using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A convex solid defined as the intersection of half-spaces — one bounding
/// plane per face, every plane's normal pointing OUT of the solid. This is the
/// HAMMER-style brush: the authoring primitive for static world geometry.
/// </summary>
public sealed class Brush
{
    // Half-extent of the seed quad used to derive each face; far larger than any
    // realistic brush so the quad fully covers the plane before it is clipped.
    private const float SeedExtent = 1e4f;

    private readonly Plane[] _planes;
    private readonly Polygon[] _faces;

    public Brush(IReadOnlyList<Plane> planes)
    {
        if (planes.Count < 4)
            throw new ArgumentException("A brush needs at least 4 planes to bound a volume.", nameof(planes));

        _planes = new Plane[planes.Count];
        for (int i = 0; i < planes.Count; i++)
            _planes[i] = Plane.Normalize(planes[i]);

        _faces = BuildFaces(_planes);
        Bounds = ComputeBounds(_faces);
    }

    public IReadOnlyList<Plane> Planes => _planes;

    /// <summary>The clipped face polygons, normals pointing outward.</summary>
    public IReadOnlyList<Polygon> Faces => _faces;

    /// <summary>The brush's axis-aligned bounding box, used for broadphase culling.</summary>
    public Aabb Bounds { get; }

    /// <summary>Builds an axis-aligned box brush spanning <paramref name="min"/>..<paramref name="max"/>.</summary>
    public static Brush CreateBox(Vector3 min, Vector3 max)
    {
        Plane[] planes =
        [
            new(new Vector3(1f, 0f, 0f), -max.X),
            new(new Vector3(-1f, 0f, 0f), min.X),
            new(new Vector3(0f, 1f, 0f), -max.Y),
            new(new Vector3(0f, -1f, 0f), min.Y),
            new(new Vector3(0f, 0f, 1f), -max.Z),
            new(new Vector3(0f, 0f, -1f), min.Z),
        ];
        return new Brush(planes);
    }

    // Each face starts as a large quad on its plane, then is clipped against
    // every other plane, keeping the part inside the brush (the back side).
    private static Polygon[] BuildFaces(Plane[] planes)
    {
        var faces = new List<Polygon>(planes.Length);

        for (int i = 0; i < planes.Length; i++)
        {
            Polygon? face = CreatePlaneQuad(planes[i]);

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

    private static Polygon CreatePlaneQuad(Plane plane)
    {
        Vector3 normal = plane.Normal;
        Vector3 reference = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(reference, normal));
        Vector3 bitangent = Vector3.Cross(normal, tangent);
        Vector3 center = normal * -plane.D;

        Vector3[] verts =
        [
            center - tangent * SeedExtent - bitangent * SeedExtent,
            center + tangent * SeedExtent - bitangent * SeedExtent,
            center + tangent * SeedExtent + bitangent * SeedExtent,
            center - tangent * SeedExtent + bitangent * SeedExtent,
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
}
