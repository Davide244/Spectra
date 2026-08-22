using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Physics.Character;

/// <summary>
/// Builds the face polygons of a convex solid from its outward half-space
/// planes.
/// </summary>
/// <remarks>
/// <para>
/// The same seed-and-clip a brush uses when it turns authored planes into
/// faces: give every plane a quad far larger than the solid, clip it by every
/// other plane, and whatever survives is that plane's face. It is written here
/// rather than reused because a cover element's plane set is not a brush — it
/// routinely contains a plane exactly coincident with one it already has, which
/// brush construction rejects outright and which is <em>normal</em> for a
/// flush cut.
/// </para>
/// <para>
/// <b>It doubles as the exact emptiness test, which is why the cover is
/// affordable.</b> A combination of half-spaces that describes nothing produces
/// no surviving faces, and a solid needs at least four. So pruning empty cover
/// elements costs nothing beyond building the ones that are real — and the
/// alternative, a linear program per candidate, is what makes people reach for
/// approximations instead.
/// </para>
/// </remarks>
public static class ConvexFaceBuilder
{
    /// <summary>Below this area a face is treated as a sliver and dropped.</summary>
    private const float MinFaceExtent = 1e-4f;

    /// <summary>
    /// The face polygons of the solid bounded by <paramref name="planes"/>, or
    /// an empty array when the planes bound nothing.
    /// </summary>
    public static Polygon[] Build(ReadOnlySpan<Plane> planes, float minThickness)
    {
        float seedExtent = SeedExtent(planes);
        if (seedExtent <= 0f)
            return [];

        var faces = new List<Polygon>(planes.Length);

        for (int i = 0; i < planes.Length; i++)
        {
            Polygon? face = SeedQuad(planes[i], seedExtent);

            for (int j = 0; j < planes.Length && face is not null; j++)
            {
                if (i == j)
                    continue;

                // A cover element's plane list routinely contains the SAME
                // directed plane twice — a cut flush with the brush's own face
                // makes the flipped negative plane identical to it. Clipping a
                // face by its own plane annihilates it (Split reports coplanar
                // on the front and the inside is empty), which silently deletes
                // a real surface: the floor keeps its planes but loses its top,
                // and a character walks off the end of it into thin air.
                // Brush construction refuses duplicate planes outright; a cover
                // element has to tolerate them instead.
                if (SameDirectedPlane(planes[i], planes[j]))
                    continue;

                // Split keeps the part BEHIND the outward plane, i.e. inside.
                // A face exactly coincident with another plane survives on the
                // front side and is therefore clipped away here — which is what
                // makes a flush cut produce no sliver rather than a phantom.
                face.Split(planes[j], out _, out Polygon? inside);
                face = inside;
            }

            if (face is null || face.VertexCount < 3)
                continue;

            if (Extent(face) < MathF.Max(minThickness, MinFaceExtent))
                continue;

            faces.Add(face);
        }

        return faces.Count >= 4 ? [.. faces] : [];
    }

    /// <summary>Whether two planes are the same plane facing the same way.</summary>
    public static bool SameDirectedPlane(Plane a, Plane b) =>
        Vector3.Dot(a.Normal, b.Normal) > 1f - 1e-4f && MathF.Abs(a.D - b.D) < 1e-4f;

    /// <summary>The axis-aligned bounds of a face set.</summary>
    public static Aabb Bounds(ReadOnlySpan<Polygon> faces)
    {
        if (faces.Length == 0)
            return new Aabb(Vector3.Zero, Vector3.Zero);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int f = 0; f < faces.Length; f++)
        {
            ReadOnlySpan<Vector3> verts = faces[f].VertexSpan;
            for (int v = 0; v < verts.Length; v++)
            {
                min = Vector3.Min(min, verts[v]);
                max = Vector3.Max(max, verts[v]);
            }
        }

        return new Aabb(min, max);
    }

    // A seed large enough to contain the solid: the largest plane offset,
    // generously scaled. Undersizing it would clip real geometry away and
    // report a solid as empty, which is why the margin is not tight.
    private static float SeedExtent(ReadOnlySpan<Plane> planes)
    {
        float largest = 0f;
        for (int i = 0; i < planes.Length; i++)
        {
            float offset = MathF.Abs(planes[i].D);
            if (!float.IsFinite(offset))
                return 0f;
            largest = MathF.Max(largest, offset);
        }

        return MathF.Max(largest * 4f, 16f);
    }

    private static Polygon? SeedQuad(Plane plane, float extent)
    {
        Vector3 normal = plane.Normal;
        float lengthSquared = normal.LengthSquared();
        if (lengthSquared < 1e-12f)
            return null;

        // Any axis not parallel to the normal gives a stable tangent frame.
        Vector3 reference = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(reference, normal));
        Vector3 bitangent = Vector3.Cross(normal, tangent);

        Vector3 origin = normal * -plane.D;
        Vector3 u = tangent * extent;
        Vector3 v = bitangent * extent;

        // Wound counter-clockwise about the outward normal, matching every
        // other polygon in the engine.
        var verts = new[]
        {
            origin - u - v,
            origin + u - v,
            origin + u + v,
            origin - u + v,
        };

        return new Polygon(verts, plane);
    }

    private static float Extent(Polygon face)
    {
        ReadOnlySpan<Vector3> verts = face.VertexSpan;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int i = 0; i < verts.Length; i++)
        {
            min = Vector3.Min(min, verts[i]);
            max = Vector3.Max(max, verts[i]);
        }

        Vector3 size = max - min;
        return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
    }
}
