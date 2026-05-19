using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Constructive Solid Geometry over convex brushes. Carving removes the parts
/// of each brush's faces that are buried inside other brushes, leaving only the
/// visible exterior skin of the solid union — the crack-free, non-overlapping
/// surface set a BSP needs to build a correct partition.
/// </summary>
/// <remarks>
/// The carve is parallelised across brushes and uses a broadphase so each brush
/// only tests its true overlap neighbours. The per-fragment plane clipping is
/// scalar (brush faces split into small polygons); the SIMD path lives in
/// <see cref="SimdPlane"/> and is exercised by larger, many-sided faces.
/// </remarks>
public static class Csg
{
    private const float NormalEpsilon = 1e-4f;
    private const float OffsetEpsilon = 1e-3f;

    /// <summary>
    /// Carves a set of brushes, returning the visible surface polygons of their
    /// union.
    /// </summary>
    public static Polygon[] Carve(IReadOnlyList<Brush> brushes)
    {
        int n = brushes.Count;
        if (n == 0)
            return [];

        var bounds = new Aabb[n];
        for (int i = 0; i < n; i++)
            bounds[i] = brushes[i].Bounds;

        int[][] neighbors = BrushBroadphase.FindOverlaps(bounds);
        var perBrush = new List<Polygon>[n];

        Parallel.For(0, n, b =>
        {
            var surfaces = new List<Polygon>();
            var current = new List<Polygon>();
            var next = new List<Polygon>();

            foreach (Polygon face in brushes[b].Faces)
            {
                current.Clear();
                current.Add(face);

                foreach (int o in neighbors[b])
                {
                    // Lower-index brushes own surfaces they share same-facing with.
                    bool carverWins = o < b;
                    next.Clear();
                    foreach (Polygon fragment in current)
                        CarveFragment(fragment, brushes[o], carverWins, next);

                    (current, next) = (next, current);
                    if (current.Count == 0)
                        break;
                }

                surfaces.AddRange(current);
            }

            perBrush[b] = surfaces;
        });

        var all = new List<Polygon>();
        foreach (var list in perBrush)
            all.AddRange(list);
        return all.ToArray();
    }

    // Appends to `output` the parts of `fragment` that lie OUTSIDE brush `carver`.
    private static void CarveFragment(Polygon fragment, Brush carver, bool carverWins, List<Polygon> output)
    {
        // Whole fragment clear of the carver: nothing to remove.
        if (!fragment.Bounds.Intersects(carver.Bounds))
        {
            output.Add(fragment);
            return;
        }

        Polygon? remaining = fragment;
        foreach (Plane plane in carver.Planes)
        {
            if (remaining is null)
                return;

            int orientation = CoplanarOrientation(remaining.Surface, plane);
            if (orientation != 0)
            {
                // Opposite-facing coincidence is an interior interface — drop the
                // shared footprint from both brushes. Same-facing coincidence is
                // a duplicate surface — resolved by brush precedence.
                bool removeFootprint = orientation < 0 || carverWins;
                if (!removeFootprint)
                {
                    output.Add(remaining);
                    return;
                }

                // Skip the coincident plane; the remaining planes carve the
                // footprint, and whatever survives behind them is dropped.
                continue;
            }

            remaining.Split(plane, out Polygon? front, out Polygon? back);
            if (front is not null)
                output.Add(front);   // in front of an outward plane => outside the carver
            remaining = back;
        }

        // Anything still behind every plane is buried inside the carver: dropped.
    }

    // 0 = not coplanar; +1 = same geometric plane, normals agree;
    // -1 = same geometric plane, normals opposed.
    private static int CoplanarOrientation(Plane a, Plane b)
    {
        float dot = Vector3.Dot(a.Normal, b.Normal);
        if (dot > 1f - NormalEpsilon)
            return MathF.Abs(a.D - b.D) < OffsetEpsilon ? 1 : 0;
        if (dot < -1f + NormalEpsilon)
            return MathF.Abs(a.D + b.D) < OffsetEpsilon ? -1 : 0;
        return 0;
    }
}
