using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Physics.Box3D.Native;

namespace SpectraEngine.Physics.Box3D;

/// <summary>Why a brush could not become a collision hull.</summary>
public enum HullRefusal
{
    /// <summary>It did.</summary>
    None = 0,

    /// <summary>Fewer than four distinct vertices — no volume to bound.</summary>
    Degenerate,

    /// <summary>More unique vertices than the library accepts.</summary>
    TooManyVertices,

    /// <summary>More faces than the library accepts.</summary>
    TooManyFaces,

    /// <summary>
    /// Vertices plus faces exceed the implied edge limit. The one that actually
    /// binds in practice, and the one nothing documents.
    /// </summary>
    TooManyEdges,

    /// <summary>The library rejected the point set for a reason of its own.</summary>
    LibraryRejected,
}

/// <summary>
/// Turns a Spectra <see cref="Brush"/> into a Box3D convex hull.
/// </summary>
/// <remarks>
/// <para>
/// <b>Box3D takes a POINT CLOUD, not planes.</b> There is no half-space or
/// face-polygon hull builder anywhere in its API, so a brush's
/// <see cref="Brush.LocalPlanes"/> — the thing a brush actually is — cannot be
/// handed over. Its <see cref="Brush.LocalFaces"/> vertices are the input, and
/// the brush constructor has already clipped the half-spaces into those faces
/// and rejected unbounded volumes, so a hull reaching here is provably closed.
/// </para>
/// <para>
/// <b>The output is brush-local, exactly like the render mesh.</b> Placement is
/// the transform passed to <c>CreateTransformedHullShape</c>, so one hull backs
/// every placement of the same brush and moving a body is a transform write
/// rather than a rebuild.
/// </para>
/// <para>
/// <b><see cref="Brush.Transform"/> is deliberately IGNORED here, and this
/// catches people out.</b> <c>Brush.CreateBox(min, max)</c> centres the solid on
/// its own origin and stores the centring translation in that property — so a
/// floor authored as <c>y ∈ [−1, 0]</c> has local faces spanning
/// <c>y ∈ [−0.5, +0.5]</c>, and a hull built from it sits half a unit higher
/// than the authoring numbers suggest. That is correct and matches the render
/// path: the engine's rule is that the NODE's world matrix places a brush and
/// the brush's own transform is ignored for node-attached brushes. Place the
/// body or the shape; do not expect the hull to carry the offset. (This exact
/// mistake showed up as a test box resting at y = 1.0 instead of 0.5 — the
/// solver was right and the expectation was wrong.)
/// </para>
/// <para>
/// <b>Over a limit, this REFUSES — it never simplifies.</b> The library would
/// happily hand back a reduced hull if asked, and a simplified collision hull is
/// a player clipping through a wall that renders correctly: a bug that looks
/// like a network problem or a CSG problem and is neither. The refusal is a
/// pre-check on the counts, because by the time the library declines it has
/// only written a log line nobody reads.
/// </para>
/// </remarks>
public static class BrushHullBuilder
{
    /// <summary>Maximum unique vertices the library accepts.</summary>
    public const int MaxVertices = 128;

    /// <summary>Maximum faces the library accepts.</summary>
    public const int MaxFaces = 128;

    /// <summary>Maximum full edges the library accepts.</summary>
    public const int MaxEdges = 128;

    /// <summary>
    /// The limit that actually binds: for a convex polyhedron Euler gives
    /// <c>V − E + F = 2</c>, so <c>E = V + F − 2</c>, and the library's check is
    /// on edges.
    /// </summary>
    /// <remarks>
    /// <b>Far tighter than the vertex and face caps taken separately, and
    /// documented nowhere.</b> A 128-vertex brush is impossible; a 64-vertex
    /// brush caps at 66 faces. A pre-check written only against
    /// <see cref="MaxVertices"/> would let brushes through that fail inside the
    /// library — which is exactly the silent path this class exists to close.
    /// (For scale: a box is V=8, F=6, E=12. Nothing authored today comes near.)
    /// </remarks>
    public const int MaxVerticesPlusFaces = MaxEdges + 2;

    /// <summary>
    /// Distance below which two vertices are the same point. Matches the
    /// polygon epsilon the CSG pipeline classifies with, so "distinct here"
    /// means the same thing as "distinct there".
    /// </summary>
    public const float WeldEpsilon = 1e-4f;

    /// <summary>
    /// Collects a brush's unique face vertices, in the brush's own frame.
    /// </summary>
    /// <remarks>
    /// Adjacent faces share corners, so the raw vertex stream repeats each
    /// corner once per face meeting there. The library de-duplicates internally,
    /// but the pre-check has to count <em>unique</em> vertices to be right, so
    /// the welding happens here and serves both.
    /// </remarks>
    public static List<B3Vec3> CollectPoints(Brush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        var points = new List<B3Vec3>();
        IReadOnlyList<Polygon> faces = brush.LocalFaces;

        for (int f = 0; f < faces.Count; f++)
        {
            ReadOnlySpan<Vector3> verts = faces[f].VertexSpan;
            for (int v = 0; v < verts.Length; v++)
            {
                if (!ContainsWithin(points, verts[v], WeldEpsilon))
                    points.Add(B3Vec3.From(verts[v]));
            }
        }

        return points;
    }

    /// <summary>
    /// Checks a brush against the library's limits without calling it.
    /// </summary>
    /// <returns><see cref="HullRefusal.None"/> when the brush is buildable.</returns>
    public static HullRefusal CheckLimits(int uniqueVertexCount, int faceCount)
    {
        if (uniqueVertexCount < 4 || faceCount < 4)
            return HullRefusal.Degenerate;
        if (uniqueVertexCount > MaxVertices)
            return HullRefusal.TooManyVertices;
        if (faceCount > MaxFaces)
            return HullRefusal.TooManyFaces;
        if (uniqueVertexCount + faceCount > MaxVerticesPlusFaces)
            return HullRefusal.TooManyEdges;

        return HullRefusal.None;
    }

    /// <summary>
    /// Builds a convex hull for <paramref name="brush"/>, or explains why not.
    /// </summary>
    /// <param name="brush">The brush to build from. Its local frame is the hull's.</param>
    /// <param name="hull">
    /// The native hull on success, or zero. <b>The caller owns it</b> and must
    /// release it through <see cref="Destroy"/> — never let it reach the library
    /// as null, which is an access violation rather than a no-op.
    /// </param>
    /// <param name="detail">A message naming the counts and the limit, ready to log.</param>
    public static HullRefusal TryCreate(Brush brush, out nint hull, out string detail)
    {
        ArgumentNullException.ThrowIfNull(brush);

        hull = 0;
        List<B3Vec3> points = CollectPoints(brush);
        int faceCount = brush.LocalFaces.Count;

        HullRefusal refusal = CheckLimits(points.Count, faceCount);
        if (refusal != HullRefusal.None)
        {
            detail = Describe(refusal, points.Count, faceCount);
            return refusal;
        }

        // maxVertexCount is the LIBRARY's cap as a constant, deliberately, not
        // this brush's own count: the library spends the difference as a
        // simplification budget, so passing the constant means "never simplify"
        // for anything the pre-check accepted — which is the property the
        // refusal exists to guarantee.
        unsafe
        {
            Span<B3Vec3> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points);
            fixed (B3Vec3* p = span)
            {
                hull = B3.CreateHull(p, points.Count, MaxVertices);
            }
        }

        if (hull == 0)
        {
            detail =
                $"Box3D declined a hull for a brush with {points.Count} unique vertices and " +
                $"{faceCount} faces, having passed the pre-check. The library logs its own " +
                "reason; the brush is not collidable.";
            return HullRefusal.LibraryRejected;
        }

        detail = string.Empty;
        return HullRefusal.None;
    }

    /// <summary>Releases a hull built by <see cref="TryCreate"/>. Null-safe, unlike the library.</summary>
    public static void Destroy(nint hull)
    {
        // The library dereferences its argument before checking it, so a null
        // reaching it is an access violation. Guarding here is what lets callers
        // treat "no hull" as an ordinary state.
        if (hull != 0)
            B3.DestroyHull(hull);
    }

    private static string Describe(HullRefusal refusal, int vertices, int faces) => refusal switch
    {
        HullRefusal.Degenerate =>
            $"Brush has {vertices} unique vertices and {faces} faces — too few to bound a volume " +
            "(a convex solid needs at least four of each).",
        HullRefusal.TooManyVertices =>
            $"Brush has {vertices} unique vertices; Box3D accepts at most {MaxVertices}. " +
            "Split it into simpler convex pieces — it will not be simplified for you, because a " +
            "simplified collision hull is a player clipping through a wall that renders correctly.",
        HullRefusal.TooManyFaces =>
            $"Brush has {faces} faces; Box3D accepts at most {MaxFaces}. Split it into simpler " +
            "convex pieces.",
        HullRefusal.TooManyEdges =>
            $"Brush has {vertices} unique vertices plus {faces} faces = {vertices + faces}, over the " +
            $"limit of {MaxVerticesPlusFaces} implied by Box3D's {MaxEdges}-edge cap (Euler: " +
            "E = V + F - 2). This binds long before the vertex and face caps do. Split the brush " +
            "into simpler convex pieces.",
        _ => string.Empty,
    };

    private static bool ContainsWithin(List<B3Vec3> points, Vector3 candidate, float epsilon)
    {
        float epsilonSquared = epsilon * epsilon;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 delta = points[i].ToVector3() - candidate;
            if (Vector3.Dot(delta, delta) <= epsilonSquared)
                return true;
        }

        return false;
    }
}
