using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A solid-leaf BSP tree built from convex <see cref="Brush"/> solids. It
/// partitions space into solid and empty convex cells, giving fast
/// point-containment and ray queries — the static, query-oriented counterpart
/// to the dynamic scene graph.
/// </summary>
public sealed class BspTree
{
    // Small offset used to probe just across a splitting plane when deciding
    // whether a ray has entered solid space.
    private const float ProbeEpsilon = 1e-3f;

    private BspTree(BspNode root) => Root = root;

    public BspNode Root { get; }

    /// <summary>
    /// Builds a BSP tree from a set of brushes, running CSG first so the tree
    /// is partitioned from a clean, non-overlapping surface set.
    /// </summary>
    public static BspTree Build(IReadOnlyList<Brush> brushes) =>
        BuildFromSurfaces(Csg.Carve(brushes));

    /// <summary>
    /// Builds a BSP tree directly from a set of surface polygons (typically the
    /// output of <see cref="Csg.Carve"/>).
    /// </summary>
    public static BspTree BuildFromSurfaces(IReadOnlyList<Polygon> surfaces)
    {
        var polygons = new List<Polygon>(surfaces);
        return new BspTree(BuildNode(polygons, solidIfEmpty: false));
    }

    // Recursively partitions polygons. An exhausted polygon list means the
    // region is unbounded by any further face: it is solid when reached down a
    // back edge (behind a face) and empty when reached down a front edge.
    private static BspNode BuildNode(List<Polygon> polygons, bool solidIfEmpty)
    {
        if (polygons.Count == 0)
            return BspNode.Leaf(solidIfEmpty);

        Plane splitter = polygons[0].Surface;
        var front = new List<Polygon>();
        var back = new List<Polygon>();

        for (int i = 1; i < polygons.Count; i++)
        {
            Polygon poly = polygons[i];
            switch (poly.Classify(splitter))
            {
                case PolygonClassification.Front:
                    front.Add(poly);
                    break;
                case PolygonClassification.Back:
                    back.Add(poly);
                    break;
                case PolygonClassification.Coplanar:
                    // Same plane: side it by whether its normal agrees with the splitter.
                    if (Vector3.Dot(poly.Surface.Normal, splitter.Normal) >= 0f)
                        front.Add(poly);
                    else
                        back.Add(poly);
                    break;
                case PolygonClassification.Spanning:
                    poly.Split(splitter, out Polygon? f, out Polygon? b);
                    if (f is not null) front.Add(f);
                    if (b is not null) back.Add(b);
                    break;
            }
        }

        BspNode frontChild = BuildNode(front, solidIfEmpty: false);
        BspNode backChild = BuildNode(back, solidIfEmpty: true);
        return BspNode.Split(splitter, frontChild, backChild);
    }

    /// <summary>True when the point lies inside solid space.</summary>
    public bool ContainsPoint(Vector3 point)
    {
        BspNode node = Root;
        while (!node.IsLeaf)
        {
            float d = Plane.DotCoordinate(node.Plane, point);
            node = d >= 0f ? node.Front! : node.Back!;
        }
        return node.IsSolid;
    }

    /// <summary>
    /// Casts a ray against solid space. Returns true and reports the first
    /// surface entered in <paramref name="hit"/>; false if the ray stays in
    /// empty space for the whole distance.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out BspRaycastHit hit)
    {
        hit = default;
        if (direction == Vector3.Zero || maxDistance <= 0f)
            return false;

        direction = Vector3.Normalize(direction);

        // A ray that starts inside solid space hits immediately.
        if (ContainsPoint(origin))
        {
            hit = new BspRaycastHit(origin, -direction, 0f);
            return true;
        }

        Vector3 end = origin + direction * maxDistance;
        if (TraceSegment(Root, origin, end, direction, out hit))
        {
            hit = hit with { Distance = Vector3.Distance(origin, hit.Point) };
            return true;
        }
        return false;
    }

    // Walks the segment a..b through the tree, returning the point where it
    // first crosses from empty into solid space.
    private bool TraceSegment(BspNode node, Vector3 a, Vector3 b, Vector3 direction, out BspRaycastHit hit)
    {
        hit = default;
        if (node.IsLeaf)
            return false;

        float da = Plane.DotCoordinate(node.Plane, a);
        float db = Plane.DotCoordinate(node.Plane, b);

        if (da >= 0f && db >= 0f)
            return TraceSegment(node.Front!, a, b, direction, out hit);
        if (da < 0f && db < 0f)
            return TraceSegment(node.Back!, a, b, direction, out hit);

        float t = da / (da - db);
        Vector3 mid = Vector3.Lerp(a, b, t);

        BspNode near = da >= 0f ? node.Front! : node.Back!;
        BspNode far = da >= 0f ? node.Back! : node.Front!;

        // Resolve the near side first; the ray reaches it before the plane.
        if (TraceSegment(near, a, mid, direction, out hit))
            return true;

        // The near side is clear up to the plane. If solid space begins just
        // across the plane, the crossing point is the entry surface.
        if (ContainsPoint(mid + direction * ProbeEpsilon))
        {
            Vector3 normal = da >= 0f ? node.Plane.Normal : -node.Plane.Normal;
            hit = new BspRaycastHit(mid, normal, 0f);
            return true;
        }

        return TraceSegment(far, mid, b, direction, out hit);
    }
}
