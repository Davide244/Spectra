using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A convex polygon lying on a single plane. Polygons are the faces of
/// <see cref="Brush"/> solids and the primitives partitioned by the BSP builder.
/// Vertices are wound counter-clockwise around <see cref="Surface"/>'s normal.
/// </summary>
public sealed class Polygon
{
    /// <summary>Distance tolerance for treating a vertex as lying on a plane.</summary>
    public const float Epsilon = 1e-4f;

    private readonly Vector3[] _vertices;
    private Aabb? _bounds;

    public Polygon(Vector3[] vertices, Plane surface)
    {
        _vertices = vertices;
        Surface = surface;
    }

    public IReadOnlyList<Vector3> Vertices => _vertices;

    public int VertexCount => _vertices.Length;

    /// <summary>The plane this polygon lies on; preserved through splits.</summary>
    public Plane Surface { get; }

    /// <summary>The polygon's axis-aligned bounding box (computed once, then cached).</summary>
    public Aabb Bounds => _bounds ??= Aabb.FromPoints(_vertices);

    /// <summary>Classifies this polygon against a splitting plane.</summary>
    public PolygonClassification Classify(Plane splitter)
    {
        int front = 0, back = 0;

        // Many-sided faces amortise the SIMD setup cost; small faces stay scalar.
        if (_vertices.Length >= Vector<float>.Count * 2)
        {
            var distances = new float[_vertices.Length];
            SimdPlane.SignedDistances(splitter, _vertices, distances);
            foreach (float d in distances)
            {
                if (d > Epsilon) front++;
                else if (d < -Epsilon) back++;
            }
        }
        else
        {
            foreach (var v in _vertices)
            {
                float d = Plane.DotCoordinate(splitter, v);
                if (d > Epsilon) front++;
                else if (d < -Epsilon) back++;
            }
        }

        if (front > 0 && back > 0) return PolygonClassification.Spanning;
        if (front > 0) return PolygonClassification.Front;
        if (back > 0) return PolygonClassification.Back;
        return PolygonClassification.Coplanar;
    }

    /// <summary>
    /// Splits this polygon by <paramref name="splitter"/> into the parts in front
    /// of and behind the plane. An output is null when the polygon contributes
    /// nothing to that side. Coplanar polygons are reported on the front side;
    /// callers that care must classify separately.
    /// </summary>
    public void Split(Plane splitter, out Polygon? front, out Polygon? back)
    {
        switch (Classify(splitter))
        {
            case PolygonClassification.Front:
            case PolygonClassification.Coplanar:
                front = this; back = null; return;
            case PolygonClassification.Back:
                front = null; back = this; return;
        }

        var frontVerts = new List<Vector3>();
        var backVerts = new List<Vector3>();

        for (int i = 0; i < _vertices.Length; i++)
        {
            Vector3 a = _vertices[i];
            Vector3 b = _vertices[(i + 1) % _vertices.Length];
            float da = Plane.DotCoordinate(splitter, a);
            float db = Plane.DotCoordinate(splitter, b);

            if (da >= -Epsilon) frontVerts.Add(a);
            if (da <= Epsilon) backVerts.Add(a);

            // Emit an intersection vertex only when the edge strictly crosses.
            if ((da > Epsilon && db < -Epsilon) || (da < -Epsilon && db > Epsilon))
            {
                float t = da / (da - db);
                Vector3 crossing = Vector3.Lerp(a, b, t);
                frontVerts.Add(crossing);
                backVerts.Add(crossing);
            }
        }

        front = frontVerts.Count >= 3 ? new Polygon(frontVerts.ToArray(), Surface) : null;
        back = backVerts.Count >= 3 ? new Polygon(backVerts.ToArray(), Surface) : null;
    }

    /// <summary>Fan-triangulates the polygon into vertex triples.</summary>
    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Triangulate()
    {
        for (int i = 1; i + 1 < _vertices.Length; i++)
            yield return (_vertices[0], _vertices[i], _vertices[i + 1]);
    }
}
