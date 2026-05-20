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
/// Each brush's faces are carved in <em>its own local frame</em>: the carver's
/// planes and bounds are transformed into the carved brush's space before
/// clipping. Vertex magnitudes during splits are bounded by the brush's extent,
/// so a brush 10 km from the world origin has the same numerical accuracy as
/// one at the origin. Local fragments are pushed to world coordinates only at
/// the very end, in one matrix multiply per vertex.
/// </remarks>
public static class Csg
{
    private const float NormalEpsilon = 1e-4f;
    private const float OffsetEpsilon = 1e-3f;

    /// <summary>
    /// Carves a set of brushes, returning the visible surface polygons of their
    /// union in world space.
    /// </summary>
    public static Polygon[] Carve(IReadOnlyList<Brush> brushes)
    {
        int n = brushes.Count;
        if (n == 0)
            return [];

        // Broadphase works in world space so it remains scale-independent.
        var worldBounds = new Aabb[n];
        for (int i = 0; i < n; i++)
            worldBounds[i] = brushes[i].WorldBounds;

        int[][] neighbors = BrushBroadphase.FindOverlaps(worldBounds);
        var perBrush = new List<Polygon>[n];

        Parallel.For(0, n, b =>
        {
            Brush brush = brushes[b];
            var localSurfaces = new List<Polygon>();
            var current = new List<Polygon>();
            var next = new List<Polygon>();

            // Pre-transform each neighbour into this brush's local frame once.
            var carvers = new CarverInFrame[neighbors[b].Length];
            for (int k = 0; k < neighbors[b].Length; k++)
            {
                int o = neighbors[b][k];
                carvers[k] = CarverInFrame.Build(brushes[o], brush, carverWins: o < b);
            }

            foreach (Polygon face in brush.LocalFaces)
            {
                current.Clear();
                current.Add(face);

                foreach (CarverInFrame carver in carvers)
                {
                    next.Clear();
                    foreach (Polygon fragment in current)
                        CarveFragment(fragment, carver, next);

                    (current, next) = (next, current);
                    if (current.Count == 0)
                        break;
                }

                localSurfaces.AddRange(current);
            }

            // Push this brush's local fragments out to world coordinates.
            var worldSurfaces = new List<Polygon>(localSurfaces.Count);
            foreach (Polygon local in localSurfaces)
                worldSurfaces.Add(local.Transformed(brush.Transform));

            perBrush[b] = worldSurfaces;
        });

        var all = new List<Polygon>();
        foreach (var list in perBrush)
            all.AddRange(list);
        return all.ToArray();
    }

    // Appends to `output` the parts of `fragment` that lie OUTSIDE the carver.
    private static void CarveFragment(Polygon fragment, CarverInFrame carver, List<Polygon> output)
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
                bool removeFootprint = orientation < 0 || carver.Wins;
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

    // A carver brush re-expressed in another brush's local frame so the inner
    // loop never has to think about world coordinates or two transforms at once.
    private readonly struct CarverInFrame
    {
        public Plane[] Planes { get; }
        public Aabb Bounds { get; }
        public bool Wins { get; }

        private CarverInFrame(Plane[] planes, Aabb bounds, bool wins)
        {
            Planes = planes;
            Bounds = bounds;
            Wins = wins;
        }

        public static CarverInFrame Build(Brush carver, Brush carved, bool carverWins)
        {
            // Going from carver-local → world → carved-local:
            //   v_carved = v_carver * carver.Transform * Invert(carved.Transform)
            if (!Matrix4x4.Invert(carved.Transform, out Matrix4x4 carvedInverse))
                carvedInverse = Matrix4x4.Identity;
            Matrix4x4 combined = carver.Transform * carvedInverse;

            var planes = new Plane[carver.LocalPlanes.Count];
            for (int i = 0; i < planes.Length; i++)
                planes[i] = Plane.Transform(carver.LocalPlanes[i], combined);

            Aabb bounds = Brush.TransformAabb(carver.LocalBounds, combined);
            return new CarverInFrame(planes, bounds, carverWins);
        }
    }
}
