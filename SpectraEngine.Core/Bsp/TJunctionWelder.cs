using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Eliminates T-junctions in a polygon set by inserting any vertex that lies
/// on another polygon's edge into that edge's vertex list.
/// <para>
/// CSG carves often emit two surfaces that meet along the same geometric line
/// but with different vertex counts along it — e.g. a floor frame's edge has
/// corners where a pillar's footprint was cut, but the pillar's side face only
/// has its own two bottom corners along that same line. Without welding, the
/// rasteriser drops sub-pixel columns at the unmatched vertices and a 1-pixel
/// crack appears along the seam.
/// </para>
/// <para>
/// Candidate vertices are bucketed into a uniform spatial hash so each edge
/// only tests vertices near its own line instead of every vertex in the world
/// (the naive scan is O(edges × vertices), which is quadratic in world size).
/// </para>
/// </summary>
public static class TJunctionWelder
{
    private const float Epsilon = Polygon.Epsilon;
    private const float EpsilonSq = Epsilon * Epsilon;

    /// <summary>
    /// Cell size of the candidate-vertex spatial hash, in world units.
    /// Correctness never depends on this value — the traversal visits every
    /// cell the inflated edge could touch — only performance does. It must sit
    /// far above the weld tolerance (1e-4) so the epsilon-inflated edge is a
    /// hairline through the grid, and near typical brush-edge scale so an edge
    /// crosses few cells while each cell holds few vertices. 0.5 world units
    /// keeps metre-scale architecture at a handful of cells per edge; being a
    /// power of two also makes the coordinate→cell mapping (multiply by 2,
    /// floor) exact in floating point, so bucketing and traversal can never
    /// disagree about which cell a vertex landed in.
    /// </summary>
    private const float CellSize = 0.5f;
    private const float InvCellSize = 1f / CellSize;

    // Traversal boxes are inflated by a whole extra cell beyond the weld
    // tolerance. The candidate tests themselves are exact (identical to the
    // pre-hash implementation); this padding only buys slack against rounding
    // in the slab/box arithmetic, so no cell a passing candidate lives in can
    // ever be skipped, even at large world coordinates where one ULP
    // approaches the weld epsilon.
    private const float QueryInflation = Epsilon + CellSize;

    /// <summary>
    /// Returns a new polygon array with T-junction vertices welded into every
    /// edge, drawing candidates from the polygons' own vertices. Inputs are
    /// not modified; polygons with no T-junctions are reused as-is.
    /// </summary>
    public static Polygon[] Weld(IReadOnlyList<Polygon> polygons)
        => Weld(polygons, polygons);

    /// <summary>
    /// Candidate-set entry point, used by the per-cell weld: like
    /// <see cref="Weld(IReadOnlyList{Polygon})"/>, but candidate vertices come
    /// from <paramref name="candidateSurfaces"/> instead of the welded set
    /// itself. The output is a pure function of each input polygon and the SET
    /// of candidate vertices lying within <see cref="Polygon.Epsilon"/> of its
    /// edges: candidate ordering, duplicate occurrences, and extra vertices
    /// beyond the tolerance cannot change it (the per-edge candidate tests are
    /// exact and the insertion sort is fully tie-broken). That purity is what
    /// lets a per-cell weld against a bounded candidate superset reproduce the
    /// global weld bit for bit — see <c>ChunkWelder</c> for the superset
    /// argument.
    /// </summary>
    public static Polygon[] Weld(IReadOnlyList<Polygon> polygons, IReadOnlyList<Polygon> candidateSurfaces)
    {
        // Every vertex appearing anywhere in the candidate set is a candidate
        // weld point. Duplicates are harmless — the per-edge dedupe catches
        // them.
        var grid = new Dictionary<(int X, int Y, int Z), List<Vector3>>();
        foreach (Polygon poly in candidateSurfaces)
        {
            foreach (Vector3 v in poly.VertexSpan)
            {
                var key = CellOf(v);
                if (!grid.TryGetValue(key, out List<Vector3>? cell))
                    grid[key] = cell = [];
                cell.Add(v);
            }
        }

        // WeldOne is pure and the grid is read-only past this point, so each
        // polygon's output depends only on its own input and the shared
        // candidate set — the parallel loop stays deterministic. The
        // thread-local overload hands each worker one WeldScratch reused for
        // every polygon it processes instead of two fresh lists per polygon.
        var result = new Polygon[polygons.Count];
        Parallel.For(0, polygons.Count,
            static () => new WeldScratch(),
            (i, _, scratch) =>
            {
                result[i] = WeldOne(polygons[i], grid, scratch);
                return scratch;
            },
            static _ => { });
        return result;
    }

    // Worker-local scratch for Weld's Parallel.For. OWNERSHIP RULES: owned by
    // exactly one worker at a time (Parallel.For never shares thread-local
    // state across concurrently-running workers); contents are cleared before
    // each use and must never escape into results — a welded polygon always
    // gets a freshly-allocated exact-size vertex array.
    private sealed class WeldScratch
    {
        public readonly List<Vector3> NewVerts = [];
        public readonly List<(float T, Vector3 V)> Insertions = [];
    }

    private static Polygon WeldOne(Polygon poly, Dictionary<(int X, int Y, int Z), List<Vector3>> grid, WeldScratch scratch)
    {
        ReadOnlySpan<Vector3> oldVerts = poly.VertexSpan;
        List<Vector3> newVerts = scratch.NewVerts;
        List<(float T, Vector3 V)> insertions = scratch.Insertions;
        newVerts.Clear();

        for (int i = 0; i < oldVerts.Length; i++)
        {
            Vector3 a = oldVerts[i];
            Vector3 b = oldVerts[(i + 1) % oldVerts.Length];
            newVerts.Add(a);

            Vector3 ab = b - a;
            float abLengthSq = ab.LengthSquared();
            if (abLengthSq < EpsilonSq)
                continue;

            insertions.Clear();
            CollectInsertions(a, b, ab, abLengthSq, grid, insertions);

            if (insertions.Count == 0)
                continue;

            // Ordered along the edge; ties on t (only possible when two
            // distinct candidates project to bitwise-equal parameters) break
            // deterministically on position. Without the tie-break, List.Sort's
            // instability would let the candidate list's ORDER pick which of
            // two eps-close equal-t vertices survives the dedupe below — and
            // the per-cell weld hands this method a differently-ordered subset
            // of the global candidate list, so weld output must be a function
            // of the candidate set alone for the per-cell/global equivalence
            // oracle to hold.
            insertions.Sort(static (x, y) =>
            {
                int c = x.T.CompareTo(y.T);
                if (c != 0) return c;
                c = x.V.X.CompareTo(y.V.X);
                if (c != 0) return c;
                c = x.V.Y.CompareTo(y.V.Y);
                return c != 0 ? c : x.V.Z.CompareTo(y.V.Z);
            });

            // Dedupe insertions that coincide (two different polygons can both
            // contribute the same T-vertex).
            Vector3 last = a;
            foreach (var (_, v) in insertions)
            {
                if (Vector3.DistanceSquared(v, last) < EpsilonSq)
                    continue;
                newVerts.Add(v);
                last = v;
            }
        }

        // No insertions: return the SAME INSTANCE, not a copy. Downstream
        // stages (weld/BSP/mesh caches) validate reuse by array and polygon
        // reference identity, so a gratuitous copy here would invalidate them
        // world-wide on every compile. The face payload is part of the
        // instance, so it survives this path by construction.
        if (newVerts.Count == oldVerts.Length)
            return poly;

        // Copy out of the scratch list into an exact-size array the new
        // polygon owns — scratch storage must never escape into a result. The
        // payload is carried over unchanged: welding adds collinear vertices to
        // an edge, which changes neither the material nor the mapping.
        var welded = new Vector3[newVerts.Count];
        newVerts.CopyTo(welded);
        return new Polygon(welded, poly.Surface, poly.Face);
    }

    // Gathers every candidate vertex lying on the open segment a..b (within
    // the weld tolerance) by walking only the grid cells the inflated segment
    // passes through. The walk slices the segment into one-cell slabs along
    // its dominant axis, so a long diagonal edge visits O(length / CellSize)
    // cells rather than the O(k³) cells of its whole bounding box.
    private static void CollectInsertions(
        Vector3 a, Vector3 b, Vector3 ab, float abLengthSq,
        Dictionary<(int X, int Y, int Z), List<Vector3>> grid,
        List<(float T, Vector3 V)> insertions)
    {
        int axis = DominantAxis(ab);
        int u = axis == 0 ? 1 : 0;
        int w = axis == 2 ? 1 : 2;

        // |abD| >= |ab| / sqrt(3) and the zero-length edge was rejected by the
        // caller, so the divisions below are safe.
        float aD = Component(a, axis);
        float abD = Component(ab, axis);

        int slabMin = FloorToCell(MathF.Min(aD, aD + abD) - QueryInflation);
        int slabMax = FloorToCell(MathF.Max(aD, aD + abD) + QueryInflation);

        var pad = new Vector3(QueryInflation);

        for (int c = slabMin; c <= slabMax; c++)
        {
            // The sub-range of the segment whose inflated tube can reach slab c.
            float lo = c * CellSize - QueryInflation;
            float hi = (c + 1) * CellSize + QueryInflation;
            float t0 = (lo - aD) / abD;
            float t1 = (hi - aD) / abD;
            if (t0 > t1)
                (t0, t1) = (t1, t0);
            t0 = MathF.Max(t0, 0f);
            t1 = MathF.Min(t1, 1f);
            if (t0 > t1)
                continue;

            // The sub-segment is linear, so its inflated bounds come from its
            // two endpoints; that bounds the u/w cells any candidate can be in.
            Vector3 p0 = a + ab * t0;
            Vector3 p1 = a + ab * t1;
            Vector3 boxMin = Vector3.Min(p0, p1) - pad;
            Vector3 boxMax = Vector3.Max(p0, p1) + pad;

            int uMin = FloorToCell(Component(boxMin, u));
            int uMax = FloorToCell(Component(boxMax, u));
            int wMin = FloorToCell(Component(boxMin, w));
            int wMax = FloorToCell(Component(boxMax, w));

            for (int cu = uMin; cu <= uMax; cu++)
            {
                for (int cw = wMin; cw <= wMax; cw++)
                {
                    if (!grid.TryGetValue(MakeKey(axis, c, cu, cw), out List<Vector3>? cell))
                        continue;

                    // Each vertex lives in exactly one cell and each (c,cu,cw)
                    // triple is visited once, so — like the flat scan this
                    // replaces — every candidate is tested exactly once.
                    foreach (Vector3 v in cell)
                    {
                        // Skip the edge's own endpoints.
                        if (Vector3.DistanceSquared(v, a) < EpsilonSq) continue;
                        if (Vector3.DistanceSquared(v, b) < EpsilonSq) continue;

                        // Project onto the line; reject if outside the open segment.
                        float t = Vector3.Dot(v - a, ab) / abLengthSq;
                        if (t <= 0f || t >= 1f) continue;

                        // Reject if v isn't actually on the line (off to the side).
                        Vector3 closest = a + ab * t;
                        if (Vector3.DistanceSquared(closest, v) > EpsilonSq) continue;

                        insertions.Add((t, v));
                    }
                }
            }
        }
    }

    private static (int X, int Y, int Z) CellOf(Vector3 v) =>
        (FloorToCell(v.X), FloorToCell(v.Y), FloorToCell(v.Z));

    private static int FloorToCell(float value) => (int)MathF.Floor(value * InvCellSize);

    private static int DominantAxis(Vector3 v)
    {
        float x = MathF.Abs(v.X), y = MathF.Abs(v.Y), z = MathF.Abs(v.Z);
        return x >= y ? (x >= z ? 0 : 2) : (y >= z ? 1 : 2);
    }

    private static float Component(in Vector3 v, int axis) =>
        axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    // Reassembles a cell key from a slab index on the dominant axis and the
    // two cross-axis indices (u/w follow the axis mapping in CollectInsertions).
    private static (int X, int Y, int Z) MakeKey(int axis, int d, int cu, int cw) =>
        axis switch { 0 => (d, cu, cw), 1 => (cu, d, cw), _ => (cu, cw, d) };
}
