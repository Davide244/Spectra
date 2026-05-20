using System;
using System.Collections.Generic;
using System.Numerics;

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

    private readonly Plane[] _localPlanes;
    private readonly Polygon[] _localFaces;

    public Brush(IReadOnlyList<Plane> localPlanes)
        : this(localPlanes, Matrix4x4.Identity)
    {
    }

    public Brush(IReadOnlyList<Plane> localPlanes, Matrix4x4 transform)
    {
        if (localPlanes.Count < 4)
            throw new ArgumentException("A brush needs at least 4 planes to bound a volume.", nameof(localPlanes));

        _localPlanes = new Plane[localPlanes.Count];
        for (int i = 0; i < localPlanes.Count; i++)
            _localPlanes[i] = Plane.Normalize(localPlanes[i]);

        Transform = transform;
        _localFaces = BuildFaces(_localPlanes);
        LocalBounds = ComputeBounds(_localFaces);
    }

    /// <summary>The world-from-local transform applied at render and BSP-build time.</summary>
    public Matrix4x4 Transform { get; }

    /// <summary>Outward-facing planes in the brush's local frame.</summary>
    public IReadOnlyList<Plane> LocalPlanes => _localPlanes;

    /// <summary>The clipped face polygons in the brush's local frame.</summary>
    public IReadOnlyList<Polygon> LocalFaces => _localFaces;

    /// <summary>Axis-aligned bounding box in the brush's local frame.</summary>
    public Aabb LocalBounds { get; }

    /// <summary>Axis-aligned bounding box in world space, derived from <see cref="LocalBounds"/> and <see cref="Transform"/>.</summary>
    public Aabb WorldBounds => TransformAabb(LocalBounds, Transform);

    /// <summary>
    /// Builds an axis-aligned box brush spanning <paramref name="min"/>..<paramref name="max"/>
    /// in world space. Internally the planes are stored centred on the brush's
    /// local origin so the half-extents — not the world positions — drive every
    /// clipping calculation.
    /// </summary>
    public static Brush CreateBox(Vector3 min, Vector3 max)
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
        return new Brush(localPlanes, Matrix4x4.CreateTranslation(center));
    }

    // Each face starts as a quad just large enough to cover its plane within
    // the brush's extent, then is clipped against every other plane, keeping
    // the part inside the brush (the back side).
    private static Polygon[] BuildFaces(Plane[] planes)
    {
        float seedExtent = ComputeSeedExtent(planes);
        var faces = new List<Polygon>(planes.Length);

        for (int i = 0; i < planes.Length; i++)
        {
            Polygon? face = CreatePlaneQuad(planes[i], seedExtent);

            for (int j = 0; j < planes.Length && face is not null; j++)
            {
                if (j == i) continue;
                face.Split(planes[j], out _, out Polygon? inside);
                face = inside;
            }

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

    private static Polygon CreatePlaneQuad(Plane plane, float seedExtent)
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
        return new Polygon(verts, plane);
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

    // Transforms the 8 corners of an AABB and takes the bounding box of the
    // result — exact for axis-aligned translations, tight for rotations.
    internal static Aabb TransformAabb(Aabb local, Matrix4x4 transform)
    {
        Span<Vector3> corners =
        [
            new(local.Min.X, local.Min.Y, local.Min.Z),
            new(local.Max.X, local.Min.Y, local.Min.Z),
            new(local.Min.X, local.Max.Y, local.Min.Z),
            new(local.Max.X, local.Max.Y, local.Min.Z),
            new(local.Min.X, local.Min.Y, local.Max.Z),
            new(local.Max.X, local.Min.Y, local.Max.Z),
            new(local.Min.X, local.Max.Y, local.Max.Z),
            new(local.Max.X, local.Max.Y, local.Max.Z),
        ];

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 w = Vector3.Transform(corners[i], transform);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
        return new Aabb(min, max);
    }
}
