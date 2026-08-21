using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Assets;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A convex solid defined as the intersection of half-spaces — one bounding
/// plane per face, every plane's normal pointing OUT of the solid. This is the
/// HAMMER-style brush: the authoring primitive for static world geometry.
/// </summary>
/// <remarks>
/// Planes and face polygons are stored in the brush's <em>local</em> coordinate
/// frame (centred near the origin), with a <see cref="Transform"/> placing the
/// brush in world space. CSG splits run in local frames so each brush's
/// numerical accuracy is bounded by its own extent — a brush 10 km from the
/// world origin has the same FP precision as one at the origin.
/// </remarks>
public sealed class Brush
{
    // Seed quad half-extent — chosen per-brush to comfortably cover its planes
    // without inflating the magnitudes the clipper has to work with.
    private const float SeedExtentScale = 100f;
    private const float SeedExtentFloor = 1f;

    // Tolerances for rejecting duplicate same-facing planes — identical to the
    // coplanarity tolerances Csg uses at carve time, so any plane pair the
    // carver would treat as coincident is rejected up front.
    private const float DuplicateNormalEpsilon = 1e-4f;
    private const float DuplicateOffsetEpsilon = 1e-3f;

    // Probe factor for the boundedness test: the faces are built a second time
    // with the seed extent scaled by this, and compared. Deliberately
    // non-integer so no seed-boundary vertex of one build can land exactly on
    // a lattice point of the other by coincidence.
    private const float SeedProbeFactor = 1.5f;

    // Allowed drift between the two builds, as a fraction of the seed extent.
    // A genuine corner is an intersection of brush planes and moves only by FP
    // noise (orders of magnitude below this); an unbounded face keeps vertices
    // ON the seed quad boundary, which moves by 0.5x the seed extent between
    // the builds — a wide margin on both sides of the threshold.
    private const float SeedComparisonTolerance = 1e-3f;

    private readonly Plane[] _localPlanes;
    private readonly Polygon[] _localFaces;
    private readonly FaceSurface[] _faceSurfaces;

    public Brush(IReadOnlyList<Plane> localPlanes)
        : this(localPlanes, Matrix4x4.Identity, faceSurfaces: null)
    {
    }

    public Brush(IReadOnlyList<Plane> localPlanes, Matrix4x4 transform)
        : this(localPlanes, transform, faceSurfaces: null)
    {
    }

    /// <summary>
    /// Builds a brush with an explicit per-face surface payload — material and
    /// texture axes per bounding plane (see <see cref="FaceSurface"/>).
    /// </summary>
    /// <param name="faceSurfaces">
    /// One payload per plane, index-aligned with <paramref name="localPlanes"/>,
    /// or null to give every face <see cref="FaceSurface.Default"/>. The array
    /// is copied, so the caller may reuse its buffer.
    /// </param>
    /// <remarks>
    /// Face materials are construction-time state, exactly like the planes: a
    /// brush is <b>immutable after construction</b>, and the CSG carve cache
    /// depends on that (it keys reuse on <see cref="Brush"/> reference identity,
    /// so reference identity has to imply identical carve inputs — face payload
    /// included, since the payload rides out into the carved surfaces). To
    /// change a face, build the successor brush with
    /// <see cref="WithFaceMaterial"/> or <see cref="WithFaceSurface"/> and
    /// assign it back to its scene node; the new instance is what makes the
    /// stale carve provably unreachable instead of merely unlikely.
    /// </remarks>
    public Brush(
        IReadOnlyList<Plane> localPlanes,
        Matrix4x4 transform,
        IReadOnlyList<FaceSurface>? faceSurfaces,
        BrushOperation operation = BrushOperation.Additive)
    {
        Operation = operation;
        if (localPlanes.Count < 4)
            throw new ArgumentException("A brush needs at least 4 planes to bound a volume.", nameof(localPlanes));
        if (faceSurfaces is not null && faceSurfaces.Count != localPlanes.Count)
            throw new ArgumentException(
                $"Expected one face surface per plane ({localPlanes.Count}), got {faceSurfaces.Count}.",
                nameof(faceSurfaces));

        _localPlanes = new Plane[localPlanes.Count];
        for (int i = 0; i < localPlanes.Count; i++)
            _localPlanes[i] = Plane.Normalize(localPlanes[i]);

        RejectDuplicatePlanes(_localPlanes, nameof(localPlanes));

        // Defaults are assigned deterministically — plane i gets face surface i,
        // and an unspecified face gets FaceSurface.Default (world-aligned, engine
        // default material), which reproduces the projection the engine used
        // before per-face surfaces existed.
        _faceSurfaces = new FaceSurface[_localPlanes.Length];
        for (int i = 0; i < _faceSurfaces.Length; i++)
            _faceSurfaces[i] = faceSurfaces is null ? FaceSurface.Default : faceSurfaces[i];

        Transform = transform;

        float seedExtent = ComputeSeedExtent(_localPlanes);
        Polygon?[] facesByPlane = BuildFaces(_localPlanes, _faceSurfaces, seedExtent);
        RejectUnboundedVolume(_localPlanes, _faceSurfaces, facesByPlane, seedExtent, nameof(localPlanes));
        _localFaces = CollectFaces(facesByPlane);

        if (_localFaces.Length == 0)
            throw new ArgumentException(
                "The planes clip every face away — they do not form a closed solid.", nameof(localPlanes));

        LocalBounds = ComputeBounds(_localFaces);
    }

    // Operation-only successor: every plane, face, payload and bound is shared
    // verbatim rather than rebuilt. No plane moved, so BuildFaces would produce
    // provably identical geometry at the price of a full re-clip — and Polygon,
    // Plane and FaceSurface are all deeply immutable, so sharing the arrays is
    // safe. Transform is copied because it is a mutable property used by the
    // standalone Csg.Carve(IReadOnlyList<Brush>) path; both existing successor
    // factories carry it the same way, and dropping it here would silently
    // change behaviour for a flipped brush in tests and tools.
    private Brush(Brush source, BrushOperation operation)
    {
        _localPlanes = source._localPlanes;
        _localFaces = source._localFaces;
        _faceSurfaces = source._faceSurfaces;
        LocalBounds = source.LocalBounds;
        Transform = source.Transform;
        Operation = operation;
    }

    /// <summary>
    /// Whether this brush adds solid to the compiled world or removes it. See
    /// <see cref="BrushOperation"/> for the composition rule and why it is
    /// unordered.
    /// </summary>
    /// <remarks>
    /// Construction-time state like the planes, because the CSG carve cache
    /// keys reuse on <see cref="Brush"/> reference identity — so reference
    /// identity must imply identical carve inputs, and the operation changes
    /// the carve as fundamentally as a plane does. Change it with
    /// <see cref="WithOperation"/> and assign the successor back to the node.
    /// </remarks>
    public BrushOperation Operation { get; }

    /// <summary>
    /// Returns a copy of this brush with a different
    /// <see cref="BrushOperation"/> — the supported way to turn a solid into a
    /// hole, or back.
    /// </summary>
    /// <remarks>
    /// Returns <c>this</c> on an equal write, like
    /// <see cref="WithScaledExtents"/>: nothing changed, so the cached carve
    /// keyed on this instance stays valid and re-using the reference is not
    /// merely an optimisation but the correct answer.
    /// </remarks>
    public Brush WithOperation(BrushOperation operation) =>
        operation == Operation ? this : new Brush(this, operation);

    /// <summary>
    /// The world-from-local transform for <em>standalone</em> use (tests, tools,
    /// brushes carved outside the scene graph): the brush-list overloads of
    /// <see cref="Csg.Carve(IReadOnlyList{Brush})"/> and
    /// <see cref="CsgWorld.Build(IReadOnlyList{Brush})"/> capture it into
    /// placements. Node-attached brushes ignore it — the scene snapshots the
    /// node's world matrix into a <see cref="BrushPlacement"/> instead, and
    /// nothing writes this property back. Keep it rigid (rotation +
    /// translation) — the carve epsilons assume unit-length plane normals, so
    /// size belongs in the plane offsets, not in a scale here.
    /// </summary>
    public Matrix4x4 Transform { get; set; }

    /// <summary>Outward-facing planes in the brush's local frame.</summary>
    public IReadOnlyList<Plane> LocalPlanes => _localPlanes;

    /// <summary>The clipped face polygons in the brush's local frame.</summary>
    /// <remarks>
    /// Index-aligned with nothing in particular: planes whose face clipped away
    /// entirely contribute no polygon. Read a face's material and texture axes
    /// from the polygon itself (<see cref="Polygon.Face"/>); use
    /// <see cref="FaceSurfaces"/> when you need the PLANE-indexed view.
    /// </remarks>
    public IReadOnlyList<Polygon> LocalFaces => _localFaces;

    /// <summary>
    /// The per-face surface payloads, index-aligned with
    /// <see cref="LocalPlanes"/> (so this is the index
    /// <see cref="WithFaceMaterial"/> takes). Immutable, like everything else
    /// about a constructed brush.
    /// </summary>
    public IReadOnlyList<FaceSurface> FaceSurfaces => _faceSurfaces;

    /// <summary>Axis-aligned bounding box in the brush's local frame.</summary>
    public Aabb LocalBounds { get; }

    /// <summary>Axis-aligned bounding box in world space, derived from <see cref="LocalBounds"/> and <see cref="Transform"/>.</summary>
    public Aabb WorldBounds => LocalBounds.Transform(Transform);

    /// <summary>
    /// Builds an axis-aligned box brush spanning <paramref name="min"/>..<paramref name="max"/>
    /// in world space. Internally the planes are stored centred on the brush's
    /// local origin so the half-extents — not the world positions — drive every
    /// clipping calculation; the centering translation goes into <see cref="Transform"/>.
    /// </summary>
    /// <remarks>
    /// <b>When the brush is attached to a scene node</b>, that stored centering
    /// translation is ignored: every static-world compile snapshots the node's
    /// world matrix as the brush's placement (placement comes from the node —
    /// see <see cref="BrushPlacement"/>). In that case pass node-local extents
    /// (typically centred, e.g. <c>-half..+half</c>) so the box actually sits
    /// where the node places it.
    /// </remarks>
    public static Brush CreateBox(Vector3 min, Vector3 max)
        => CreateBox(min, max, MaterialRef.Default);

    /// <summary>
    /// <see cref="CreateBox(Vector3, Vector3)"/> with every face wearing
    /// <paramref name="material"/> and world-aligned texture axes — the Hammer
    /// default for a freshly-drawn block.
    /// </summary>
    /// <remarks>
    /// Face order is the plane order below (+X, −X, +Y, −Y, +Z, −Z) and is
    /// fixed, so <see cref="WithFaceMaterial"/> indices mean the same thing for
    /// every box brush the engine ever builds — that determinism is what lets an
    /// editor (and the tests) address "the top face" as index 2.
    /// </remarks>
    public static Brush CreateBox(Vector3 min, Vector3 max, MaterialRef material)
    {
        Vector3 halfExtent = (max - min) * 0.5f;
        Vector3 center = (min + max) * 0.5f;

        Plane[] localPlanes =
        [
            new(new Vector3(1f, 0f, 0f), -halfExtent.X),
            new(new Vector3(-1f, 0f, 0f), -halfExtent.X),
            new(new Vector3(0f, 1f, 0f), -halfExtent.Y),
            new(new Vector3(0f, -1f, 0f), -halfExtent.Y),
            new(new Vector3(0f, 0f, 1f), -halfExtent.Z),
            new(new Vector3(0f, 0f, -1f), -halfExtent.Z),
        ];

        // Deterministic default assignment: one world-aligned payload per plane,
        // in plane order. Skipped entirely for the default material so the
        // common case allocates nothing extra and goes through the same
        // "faceSurfaces is null" path as every pre-existing caller.
        FaceSurface[]? faces = null;
        if (!material.IsDefault)
        {
            faces = new FaceSurface[localPlanes.Length];
            for (int i = 0; i < faces.Length; i++)
                faces[i] = new FaceSurface(material);
        }

        return new Brush(localPlanes, Matrix4x4.CreateTranslation(center), faces);
    }

    /// <summary>
    /// Returns a copy of this brush with plane <paramref name="planeIndex"/>'s
    /// face wearing <paramref name="material"/> — the supported way to retexture
    /// a face, since a brush is immutable after construction.
    /// </summary>
    /// <remarks>
    /// The result is a NEW <see cref="Brush"/> instance, which is exactly what
    /// makes the change visible to the compile pipeline: <see cref="Brush"/>
    /// reference identity is the carve cache's validity key, so assigning the
    /// copy to its scene node invalidates that brush's cached carve (and only
    /// that brush's, plus the overlap neighbours whose carver sequence names
    /// it). Geometry is unchanged, so the copy re-runs the same plane
    /// validation and produces the same faces.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="planeIndex"/> is outside <see cref="LocalPlanes"/>.
    /// </exception>
    public Brush WithFaceMaterial(int planeIndex, MaterialRef material)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(planeIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(planeIndex, _faceSurfaces.Length);
        return WithFaceSurface(planeIndex, _faceSurfaces[planeIndex].WithMaterial(material));
    }

    /// <summary>
    /// Returns a copy of this brush with plane <paramref name="planeIndex"/>'s
    /// entire face payload replaced — material and texture axes together. See
    /// <see cref="WithFaceMaterial"/> for why this returns a new instance.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="planeIndex"/> is outside <see cref="LocalPlanes"/>.
    /// </exception>
    public Brush WithFaceSurface(int planeIndex, FaceSurface face)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(planeIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(planeIndex, _faceSurfaces.Length);

        var faces = new FaceSurface[_faceSurfaces.Length];
        Array.Copy(_faceSurfaces, faces, faces.Length);
        faces[planeIndex] = face;
        // Operation must ride along, or retexturing a hole turns it into a
        // solid block — silently, and only once somebody edits a face.
        return new Brush(_localPlanes, Transform, faces, Operation);
    }

    /// <summary>
    /// Returns a copy of this brush whose local extents are scaled by
    /// <paramref name="scale"/> about the brush's local origin — the supported
    /// way to <em>resize</em> a brush, since brush node transforms must stay
    /// rigid and a brush is immutable after construction.
    /// </summary>
    /// <remarks>
    /// <b>This is the exact image of the solid under the diagonal map
    /// <c>p → S·p</c>, not an approximation for boxes.</b> A half-space
    /// <c>{p : n·p + d ≤ 0}</c> maps to <c>{q : (S⁻¹n)·q + d ≤ 0}</c>, so the new
    /// bounding plane is <c>n' = normalize(S⁻¹n)</c> with
    /// <c>d' = d / ‖S⁻¹n‖</c>. Every convex shape the brush pipeline accepts —
    /// wedges, cut corners, arbitrary plane sets — comes through correctly, and
    /// for the axis-aligned box case it reduces to the obvious
    /// "multiply the half-extents": a +X plane <c>(1,0,0), d = −hx</c> becomes
    /// <c>(1,0,0), d = −hx·sx</c>.
    /// <para>
    /// <b>Why this and not node scale.</b> The whole CSG epsilon scheme assumes
    /// unit-length plane normals and rigid placements; a scale in the node
    /// transform makes <c>Plane.Transform</c> produce non-normalized planes and
    /// silently changes the meaning of every distance tolerance downstream
    /// (<c>Scene</c>'s snapshot rejects it outright). Size therefore lives in the
    /// plane offsets, which is exactly what this method edits.
    /// </para>
    /// <para>
    /// <b>The result is a new instance</b>, which is what makes the resize
    /// visible to the compile pipeline — <see cref="Brush"/> reference identity
    /// is the carve cache's validity key. Face payloads ride along unchanged, so
    /// a face with explicit texture axes keeps its projection and the texture
    /// does not stretch with the resize (Hammer's behaviour); world-aligned faces
    /// re-derive theirs as always.
    /// </para>
    /// <para>
    /// Rebuilding a brush re-runs the full plane validation and face clipping, so
    /// this is <em>not</em> a per-frame-free operation — a resize gizmo should
    /// only call it when the resulting extents actually changed.
    /// </para>
    /// </remarks>
    /// <param name="scale">
    /// Per-axis factors about the brush's local origin. Every component must be
    /// finite and strictly positive: zero collapses the solid to a plane and a
    /// negative factor mirrors it, inverting every outward normal into an
    /// inward one and turning the brush inside out.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A component of <paramref name="scale"/> is not finite or not positive.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The scaled planes no longer bound a volume — reported by the ordinary
    /// construction-time validation.
    /// </exception>
    public Brush WithScaledExtents(Vector3 scale)
    {
        ThrowIfUnusableScale(scale.X, nameof(scale));
        ThrowIfUnusableScale(scale.Y, nameof(scale));
        ThrowIfUnusableScale(scale.Z, nameof(scale));

        // Identity is common (a resize drag that has not left its starting
        // factor yet) and rebuilding would cost a full re-clip for nothing.
        // Reference-equal is also the right answer for the carve cache: nothing
        // about the geometry changed, so the cached carve stays valid.
        if (scale == Vector3.One)
            return this;

        var planes = new Plane[_localPlanes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            Plane plane = _localPlanes[i];
            var mapped = new Vector3(
                plane.Normal.X / scale.X,
                plane.Normal.Y / scale.Y,
                plane.Normal.Z / scale.Z);

            // Non-zero for any positive finite scale, because the source normal
            // is unit length and therefore has a non-zero component somewhere.
            float length = mapped.Length();
            planes[i] = new Plane(mapped / length, plane.D / length);
        }

        // Operation must ride along, or resizing a hole turns it into a solid
        // block — the same silent failure as WithFaceSurface's, reached through
        // the resize gizmo instead.
        return new Brush(planes, Transform, _faceSurfaces, Operation);
    }

    private static void ThrowIfUnusableScale(float component, string paramName)
    {
        if (!float.IsFinite(component) || component <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                paramName, component,
                "Brush extent scale factors must be finite and strictly positive; " +
                "zero collapses the solid and a negative factor turns it inside out.");
        }
    }

    // Two same-facing near-coincident planes make Polygon.Split's
    // coplanar-front rule silently drop BOTH faces during BuildFaces (each
    // face classifies as coplanar against the other's plane and the kept
    // "inside" back output is null), leaving an open solid that corrupts BSP
    // solid-marking. Reject the brush at construction instead.
    private static void RejectDuplicatePlanes(Plane[] planes, string paramName)
    {
        for (int i = 0; i < planes.Length; i++)
        {
            for (int j = i + 1; j < planes.Length; j++)
            {
                if (Vector3.Dot(planes[i].Normal, planes[j].Normal) > 1f - DuplicateNormalEpsilon &&
                    MathF.Abs(planes[i].D - planes[j].D) < DuplicateOffsetEpsilon)
                {
                    throw new ArgumentException(
                        $"Planes {i} and {j} are near-coplanar duplicates (same facing, similar offset); " +
                        "each brush face needs a distinct bounding plane.", paramName);
                }
            }
        }
    }

    // A bounded polytope's face vertices are intersections of the brush planes
    // and therefore do not depend on the seed quad they were clipped from; an
    // unbounded face keeps vertices ON the seed quad boundary, which move when
    // the seed grows. Building the faces a second time with a scaled seed and
    // comparing per-plane exposes both failure shapes: faces that stretch with
    // the seed, and faces that only appear (or vanish) under one seed size.
    // A vertex-magnitude threshold cannot make this distinction — a legitimate
    // sharp spire (tiny plane offsets, distant apex) sits far from the origin
    // yet is perfectly seed-independent. Construction-time-only cost (one
    // extra BuildFaces) for a guard the whole CSG pipeline depends on.
    private static void RejectUnboundedVolume(
        Plane[] planes, FaceSurface[] faceSurfaces, Polygon?[] facesByPlane, float seedExtent, string paramName)
    {
        Polygon?[] probe = BuildFaces(planes, faceSurfaces, seedExtent * SeedProbeFactor);
        float tolerance = seedExtent * SeedComparisonTolerance;

        for (int i = 0; i < facesByPlane.Length; i++)
        {
            Polygon? face = facesByPlane[i];
            Polygon? probeFace = probe[i];

            if (face is null && probeFace is null)
                continue;

            if (face is null || probeFace is null ||
                face.VertexCount != probeFace.VertexCount ||
                MathF.Abs(MaxVertexMagnitude(face) - MaxVertexMagnitude(probeFace)) > tolerance)
            {
                throw new ArgumentException(
                    "The planes do not enclose a bounded volume.", paramName);
            }
        }
    }

    private static float MaxVertexMagnitude(Polygon face)
    {
        float max = 0f;
        foreach (Vector3 v in face.Vertices)
            max = MathF.Max(max, v.Length());
        return max;
    }

    // Each face starts as a quad just large enough to cover its plane within
    // the brush's extent, then is clipped against every other plane, keeping
    // the part inside the brush (the back side). The result is indexed by
    // plane (null where the face clipped away entirely) so the boundedness
    // probe can compare the two builds face-for-face.
    private static Polygon?[] BuildFaces(Plane[] planes, FaceSurface[] faceSurfaces, float seedExtent)
    {
        var faces = new Polygon?[planes.Length];

        for (int i = 0; i < planes.Length; i++)
        {
            // The seed quad already carries plane i's payload, so every
            // fragment the clipping below leaves behind inherits it through
            // Polygon.Split — no post-hoc reattachment, no chance of a face
            // ending up with the wrong plane's material.
            Polygon? face = CreatePlaneQuad(planes[i], faceSurfaces[i], seedExtent);

            for (int j = 0; j < planes.Length && face is not null; j++)
            {
                if (j == i) continue;
                face.Split(planes[j], out _, out Polygon? inside);
                face = inside;
            }

            faces[i] = face;
        }

        return faces;
    }

    private static Polygon[] CollectFaces(Polygon?[] facesByPlane)
    {
        var faces = new List<Polygon>(facesByPlane.Length);
        foreach (Polygon? face in facesByPlane)
        {
            if (face is not null)
                faces.Add(face);
        }
        return faces.ToArray();
    }

    // Scales the seed quad to the largest plane offset present, well above the
    // brush extent but far below the 1e4 absolute we used to use. Smaller
    // magnitudes in the seed → less FP noise in every split.
    private static float ComputeSeedExtent(Plane[] planes)
    {
        float maxOffset = 0f;
        foreach (Plane p in planes)
            maxOffset = MathF.Max(maxOffset, MathF.Abs(p.D));
        return MathF.Max(maxOffset * SeedExtentScale, SeedExtentFloor);
    }

    private static Polygon CreatePlaneQuad(Plane plane, FaceSurface face, float seedExtent)
    {
        Vector3 normal = plane.Normal;
        Vector3 reference = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(reference, normal));
        Vector3 bitangent = Vector3.Cross(normal, tangent);
        Vector3 center = normal * -plane.D;

        Vector3[] verts =
        [
            center - tangent * seedExtent - bitangent * seedExtent,
            center + tangent * seedExtent - bitangent * seedExtent,
            center + tangent * seedExtent + bitangent * seedExtent,
            center - tangent * seedExtent + bitangent * seedExtent,
        ];
        return new Polygon(verts, plane, face);
    }

    private static Aabb ComputeBounds(Polygon[] faces)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Polygon face in faces)
        {
            foreach (Vector3 v in face.Vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }
        return new Aabb(min, max);
    }

}
