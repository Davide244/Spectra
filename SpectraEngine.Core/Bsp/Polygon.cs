using System;
using System.Buffers;
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

    // Vertex counts up to this hold per-vertex plane distances on the stack
    // (classification and splitting sit in the hottest CSG loop, so a heap
    // allocation per call is real cost). 128 floats is 512 bytes — trivial
    // stack load, yet far above any realistic face after carving and welding;
    // larger polygons fall back to a pooled array (rented/returned inside the
    // call, so it never escapes). The stackalloc length is the exact vertex
    // count, not this maximum: localsinit (no SkipLocalsInit in this solution)
    // zero-initialises every stackalloc, so a fixed-size buffer would pay a
    // mandatory 512-byte clear per call even for triangles.
    private const int MaxStackDistances = 128;

    private readonly Vector3[] _vertices;

    /// <summary>
    /// Creates a polygon carrying the default face payload (engine default
    /// material, world-aligned texture axes). Convenience for geometry-only
    /// call sites — tests, debug shapes, and anything that predates per-face
    /// surfaces.
    /// </summary>
    public Polygon(Vector3[] vertices, Plane surface)
        : this(vertices, surface, FaceSurface.Default)
    {
    }

    /// <summary>
    /// Creates a polygon on <paramref name="surface"/> wearing
    /// <paramref name="face"/>. Every polygon-producing operation in the CSG
    /// pipeline (split, transform, snap, weld, carve) propagates the payload
    /// through this constructor, so a fragment always wears the material and
    /// texture mapping of the brush face it came from.
    /// </summary>
    public Polygon(Vector3[] vertices, Plane surface, FaceSurface face)
    {
        _vertices = vertices;
        Surface = surface;
        Face = face;
        // Eager, not lazily cached: Csg.Carve's parallel per-placement workers
        // evaluate Bounds on shared local-face polygons whenever one Brush
        // instance backs several placements, and a lazily-written multi-word
        // Aabb? cache can tear under that concurrency (a worker observing
        // HasValue before Min/Max are fully written would carve against a
        // garbage box). Computing here makes the polygon deeply immutable, so
        // any number of threads may read it. The min/max scan is trivial next
        // to the split/transform work that accompanies every construction.
        Bounds = Aabb.FromPoints(vertices);
    }

    public IReadOnlyList<Vector3> Vertices => _vertices;

    /// <summary>
    /// The vertices as a span over the backing array. Prefer this in hot loops:
    /// enumerating <see cref="Vertices"/> through the interface allocates an
    /// enumerator per loop, a span enumerates allocation-free.
    /// </summary>
    public ReadOnlySpan<Vector3> VertexSpan => _vertices;

    public int VertexCount => _vertices.Length;

    /// <summary>The plane this polygon lies on; preserved through splits.</summary>
    public Plane Surface { get; }

    /// <summary>
    /// The face payload — material reference and Hammer-style texture axes (see
    /// <see cref="FaceSurface"/>). Preserved verbatim through splits, snapping
    /// and welding (those change vertices, never what the face wears), and
    /// mapped through the matrix by <see cref="Transformed"/>.
    /// </summary>
    public FaceSurface Face { get; }

    /// <summary>
    /// The polygon's axis-aligned bounding box, computed once at construction —
    /// safe to read from any number of threads.
    /// </summary>
    public Aabb Bounds { get; }

    /// <summary>Classifies this polygon against a splitting plane.</summary>
    public PolygonClassification Classify(Plane splitter)
    {
        int count = _vertices.Length;
        if (count <= MaxStackDistances)
        {
            Span<float> distances = stackalloc float[count];
            return ClassifyInto(splitter, distances);
        }

        // Rare oversized face: rent scratch instead of allocating. The buffer
        // never escapes this call; try/finally guarantees it goes back to the
        // pool even if classification throws.
        float[] rented = ArrayPool<float>.Shared.Rent(count);
        try
        {
            return ClassifyInto(splitter, rented.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    // Fills `distances` (one slot per vertex) with each vertex's signed
    // distance to `splitter` and classifies from them — so Split can reuse
    // the very same distances for interpolation instead of recomputing every
    // dot product a second time.
    private PolygonClassification ClassifyInto(Plane splitter, Span<float> distances)
    {
        // Many-sided faces amortise the SIMD setup cost; small faces stay scalar.
        if (_vertices.Length >= Vector<float>.Count * 2)
        {
            SimdPlane.SignedDistances(splitter, _vertices, distances);
        }
        else
        {
            for (int i = 0; i < _vertices.Length; i++)
                distances[i] = Plane.DotCoordinate(splitter, _vertices[i]);
        }

        int front = 0, back = 0;
        foreach (float d in distances)
        {
            if (d > Epsilon) front++;
            else if (d < -Epsilon) back++;
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
        // One distance computation feeds both the classification and the
        // spanning interpolation below.
        int count = _vertices.Length;
        if (count <= MaxStackDistances)
        {
            Span<float> distances = stackalloc float[count];
            SplitWithDistances(splitter, distances, out front, out back);
            return;
        }

        // Rare oversized face: rent scratch instead of allocating. The buffer
        // never escapes this call; try/finally guarantees it goes back to the
        // pool even if a split throws.
        float[] rented = ArrayPool<float>.Shared.Rent(count);
        try
        {
            SplitWithDistances(splitter, rented.AsSpan(0, count), out front, out back);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private void SplitWithDistances(Plane splitter, Span<float> distances, out Polygon? front, out Polygon? back)
    {
        switch (ClassifyInto(splitter, distances))
        {
            case PolygonClassification.Front:
            case PolygonClassification.Coplanar:
                front = this; back = null; return;
            case PolygonClassification.Back:
                front = null; back = this; return;
        }

        int count = _vertices.Length;

        // Counting pass: determine each side's exact vertex count from the
        // distances alone, so the result arrays below are allocated at final
        // size — no List growth churn, no ToArray double copy. The conditions
        // mirror the fill pass exactly, so the counts always match.
        int frontCount = 0, backCount = 0;
        for (int i = 0; i < count; i++)
        {
            float da = distances[i];
            float db = distances[(i + 1) % count];

            if (da >= -Epsilon) frontCount++;
            if (da <= Epsilon) backCount++;
            if ((da > Epsilon && db < -Epsilon) || (da < -Epsilon && db > Epsilon))
            {
                frontCount++;
                backCount++;
            }
        }

        // A side with fewer than three vertices contributes no polygon (same
        // degenerate-sliver rule as before); skip its array entirely. These
        // arrays are the results — exact-size and handed to the new Polygon,
        // never pooled.
        Vector3[]? frontVerts = frontCount >= 3 ? new Vector3[frontCount] : null;
        Vector3[]? backVerts = backCount >= 3 ? new Vector3[backCount] : null;
        int fi = 0, bi = 0;

        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            Vector3 a = _vertices[i];
            float da = distances[i];
            float db = distances[j];

            if (da >= -Epsilon && frontVerts is not null) frontVerts[fi++] = a;
            if (da <= Epsilon && backVerts is not null) backVerts[bi++] = a;

            // Emit an intersection vertex only when the edge strictly crosses.
            if ((da > Epsilon && db < -Epsilon) || (da < -Epsilon && db > Epsilon))
            {
                float t = da / (da - db);
                Vector3 crossing = Vector3.Lerp(a, _vertices[j], t);
                if (frontVerts is not null) frontVerts[fi++] = crossing;
                if (backVerts is not null) backVerts[bi++] = crossing;
            }
        }

        // Both fragments inherit the parent's face payload unchanged: a split
        // divides a face's area, never its material or its texture mapping —
        // and because the mapping is expressed in world axes rather than in
        // per-vertex UVs, the two halves stay perfectly continuous across the
        // cut with no re-derivation at all.
        front = frontVerts is not null ? new Polygon(frontVerts, Surface, Face) : null;
        back = backVerts is not null ? new Polygon(backVerts, Surface, Face) : null;
    }

    /// <summary>Fan-triangulates the polygon into vertex triples.</summary>
    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Triangulate()
    {
        for (int i = 1; i + 1 < _vertices.Length; i++)
            yield return (_vertices[0], _vertices[i], _vertices[i + 1]);
    }

    /// <summary>
    /// Returns a new polygon with every vertex, the surface plane, and the face
    /// payload transformed by <paramref name="transform"/>. Used to push a
    /// brush's local-space fragments out into world coordinates.
    /// </summary>
    /// <remarks>
    /// The payload's texture-lock semantics live in
    /// <see cref="FaceSurface.Transformed"/>: explicit axes rotate with the
    /// brush and absorb the translation into their offsets (the texture stays
    /// glued to the surface), while world-aligned faces pass through and
    /// re-derive their projection from the transformed normal.
    /// </remarks>
    public Polygon Transformed(Matrix4x4 transform)
    {
        var verts = new Vector3[_vertices.Length];
        for (int i = 0; i < _vertices.Length; i++)
            verts[i] = Vector3.Transform(_vertices[i], transform);
        return new Polygon(verts, Plane.Transform(Surface, transform), Face.Transformed(transform));
    }
}
