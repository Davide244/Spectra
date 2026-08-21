using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// The per-cell snap+weld stage of a static-world compile: every brush's
/// carved surfaces are snapped and welded under the brush's owner cell against
/// a bounded candidate set instead of against the whole world, and brushes
/// whose weld inputs are unchanged since the previous compile reuse their
/// welded surfaces verbatim (via <see cref="CsgWeldCache"/>). The union of the
/// per-cell results is bit-identical to running the global
/// <see cref="VertexSnapper.Snap"/> + <see cref="TJunctionWelder.Weld(IReadOnlyList{Polygon})"/>
/// pipeline over the full surface list — that equivalence is this stage's
/// contract, proven below and pinned by the oracle tests.
/// </summary>
/// <remarks>
/// <b>Candidate set.</b> A brush's candidate brushes are every brush RESIDENT
/// in any cell of the brush's own footprint (the brush itself included — it is
/// resident in all of its footprint cells). Its candidate vertices are the
/// snapped carved surfaces of those brushes. Brushes welded together under one
/// owner cell share the union of their candidate sets, which is a superset for
/// each of them — harmless, because
/// <see cref="TJunctionWelder.Weld(IReadOnlyList{Polygon}, IReadOnlyList{Polygon})"/>'s
/// output is a pure function of the candidate vertices within
/// <see cref="Polygon.Epsilon"/> of each edge.
///
/// <para>
/// <b>THE BORDER-BAND INVARIANT</b> — any vertex anywhere in the world lying
/// within the weld tolerance (<see cref="Polygon.Epsilon"/>) of an edge of a
/// brush's snapped surfaces is guaranteed to be in that brush's candidate set.
/// Proof from the <see cref="ChunkGrid.WeldBand"/> inflation
/// (<c>WeldBand = 2 · max(Polygon.Epsilon, VertexSnapper.GridSize)</c>):
/// <list type="number">
/// <item><description>A brush's carved surfaces lie inside its world AABB, and
/// snapping displaces any point of any edge by at most half a grid step per
/// axis. So every point p of a snapped edge of brush b lies within
/// <c>GridSize/2</c> of b's AABB, and any cell containing p intersects b's
/// AABB inflated by <c>GridSize/2 &lt; WeldBand</c> — that cell is in b's
/// footprint.</description></item>
/// <item><description>A vertex v with <c>dist(v, p) ≤ Epsilon</c> belongs to
/// some brush j and, by the same snap bound, lies within <c>GridSize/2</c> of
/// j's AABB. The cell containing p is therefore within
/// <c>Epsilon + GridSize/2 ≤ 1.5 · max(Epsilon, GridSize)</c> of j's AABB —
/// strictly inside j's WeldBand inflation, with the remaining
/// <c>0.5 · max</c> of slack absorbing floating-point rounding in the box and
/// cell arithmetic. So j is resident in a footprint cell of b, and v is in
/// b's candidate set.</description></item>
/// </list>
/// Equivalence follows: the per-cell candidate grid is a subset of the global
/// one, so it can never insert a vertex the global weld would not; and by the
/// invariant it contains every vertex the global weld could insert into an
/// owned edge. Identical passing sets at every edge, plus the welder's
/// tie-broken insertion order, give identical output polygons bit for bit.
/// </para>
/// <para>
/// <b>Incrementality.</b> Reuse is validation-driven, not dirty-set-driven: a
/// brush re-welds exactly when its own carved array or any candidate's carved
/// array is not reference-identical to the previous compile's (see
/// <see cref="CsgWeldCache"/>). This is deliberately a superset of "brushes
/// owned by dirty cells" — a brush whose owner cell is far from an edit can
/// still need re-welding when a large neighbour spanning both regions
/// re-carves — and it makes stale reuse impossible by construction instead of
/// by dirty-diff reasoning. The per-cell BSP stage (W3, see
/// <see cref="ChunkBspBuilder"/>) and the per-cell mesh stage (W4, see
/// <see cref="ChunkMeshBuilder"/>) follow the same discipline; the recorded
/// dirty-cell set is observability data only.
/// </para>
/// </remarks>
internal static class ChunkWelder
{
    /// <summary>
    /// Snaps and welds every placement's carved surfaces per owner cell,
    /// reusing previous results where <paramref name="previousCache"/>
    /// validates. Returns the welded surfaces index-aligned with the
    /// placements; concatenating them in placement order reproduces the global
    /// pipeline's surface list bit for bit. Pure CPU work over immutable
    /// inputs — background-thread safe.
    /// </summary>
    /// <param name="produceCache">
    /// Whether to assemble <paramref name="nextCache"/> for the next compile
    /// (the caching build overloads); reuse of a welded array only ever
    /// happens through a carve-cache hit chain, so a cache-free compile has
    /// nothing to gain from producing one.
    /// </param>
    internal static Polygon[][] Weld(
        IReadOnlyList<BrushPlacement> placements,
        Polygon[][] perBrushSurfaces,
        ChunkGrid chunks,
        CsgWeldCache? previousCache,
        bool produceCache,
        out CsgWeldCache? nextCache,
        out CsgWeldStats stats)
        => Weld(placements, perBrushSurfaces, chunks, previousCache, produceCache, out nextCache, out stats, out _);

    /// <summary>
    /// Like the primary <see cref="Weld(IReadOnlyList{BrushPlacement}, Polygon[][], ChunkGrid, CsgWeldCache?, bool, out CsgWeldCache?, out CsgWeldStats)"/>
    /// but also reports the per-placement candidate index sets it computed —
    /// the carry the incremental (previous-world) compile patches instead of
    /// recomputing every set (see <see cref="CsgIncrementalCompiler"/>).
    /// </summary>
    internal static Polygon[][] Weld(
        IReadOnlyList<BrushPlacement> placements,
        Polygon[][] perBrushSurfaces,
        ChunkGrid chunks,
        CsgWeldCache? previousCache,
        bool produceCache,
        out CsgWeldCache? nextCache,
        out CsgWeldStats stats,
        out int[][] candidatesOut)
    {
        int n = placements.Count;
        var welded = new Polygon[n][];
        CsgWeldCache.Entry[]? entries = produceCache ? new CsgWeldCache.Entry[n] : null;

        int[][] candidates = ComputeCandidateSets(placements, chunks);
        candidatesOut = candidates;

        // Reuse pass: brushes whose weld inputs validate keep their previous
        // welded array (and carry their entry forward unchanged); the rest are
        // grouped under their owner cell for the fresh weld below. Cells are
        // processed in first-encounter order — deterministic (ascending first
        // needing placement index), and irrelevant to the output anyway, since
        // every result lands in its own placement's slot. Candidate validation
        // is memoized per (stored carve list, current candidate set) reference
        // pair: every brush sharing a candidate set shares one check, keeping
        // a dense cell's validation linear instead of quadratic.
        var ownedByCell = new Dictionary<ChunkCoord, List<int>>();
        var cellOrder = new List<ChunkCoord>();
        var validationMemo = new Dictionary<(Polygon[][] Stored, int[] Current), bool>();
        int reused = 0;
        for (int i = 0; i < n; i++)
        {
            if (previousCache is not null &&
                previousCache.TryGet(perBrushSurfaces[i], out CsgWeldCache.Entry? cached))
            {
                (Polygon[][], int[]) memoKey = (cached.CandidateCarves, candidates[i]);
                if (!validationMemo.TryGetValue(memoKey, out bool candidatesValid))
                {
                    candidatesValid = CsgWeldCache.CandidatesMatch(cached, candidates[i], perBrushSurfaces);
                    validationMemo[memoKey] = candidatesValid;
                }

                if (candidatesValid)
                {
                    welded[i] = cached.Welded;
                    if (entries is not null)
                        entries[i] = cached;
                    reused++;
                    continue;
                }
            }

            BrushPlacement placement = placements[i];
            ChunkCoord owner = ChunkGrid.OwnerCell(in placement);
            if (!ownedByCell.TryGetValue(owner, out List<int>? needing))
            {
                ownedByCell[owner] = needing = [];
                cellOrder.Add(owner);
            }
            needing.Add(i);
        }

        // Fresh weld, one owner cell at a time. Snapped surfaces are memoized
        // per brush and computed only where needed (welded brushes and their
        // candidates), so an edit's snap cost stays proportional to its
        // neighbourhood, not the world.
        var snapped = new Polygon[]?[n];
        var distinctSets = new HashSet<int[]>();
        var unionScratch = new HashSet<int>();
        var candidateCarvesMemo = entries is not null ? new Dictionary<int[], Polygon[][]>() : null;
        var candidateSurfaces = new List<Polygon>();
        var input = new List<Polygon>();
        foreach (ChunkCoord cell in cellOrder)
        {
            List<int> needing = ownedByCell[cell];

            // The cell's candidate brushes: the union of its needing brushes'
            // candidate sets, ascending for deterministic grid construction.
            // Candidate arrays are shared per footprint box, so union over the
            // DISTINCT arrays (reference identity) — in the common case of one
            // shared set the union is that set verbatim, no per-brush rescan.
            distinctSets.Clear();
            foreach (int i in needing)
                distinctSets.Add(candidates[i]);
            int[] cellCandidates = UnionDistinctSets(distinctSets, unionScratch);

            WeldCell(
                needing, cellCandidates, SnappedOf,
                i => perBrushSurfaces[i].Length,
                candidateSurfaces, input,
                (i, slice) =>
                {
                    welded[i] = slice;
                    if (entries is not null)
                        entries[i] = new CsgWeldCache.Entry(CandidateCarvesFor(candidates[i]), slice);
                });
        }

        stats = new CsgWeldStats(reused, n - reused);
        nextCache = entries is not null ? CsgWeldCache.FromEntries(perBrushSurfaces, entries) : null;
        return welded;

        // The carve-array list an entry records for one candidate set — built
        // once per DISTINCT set and shared by every entry using it (reference-
        // keyed memo), so a dense cell's entries store one list, not one copy
        // per brush.
        Polygon[][] CandidateCarvesFor(int[] candidateIndices)
        {
            if (!candidateCarvesMemo!.TryGetValue(candidateIndices, out Polygon[][]? carves))
            {
                carves = new Polygon[candidateIndices.Length][];
                for (int k = 0; k < carves.Length; k++)
                    carves[k] = perBrushSurfaces[candidateIndices[k]];
                candidateCarvesMemo[candidateIndices] = carves;
            }
            return carves;
        }

        // Snap is per-vertex, order-independent, and deterministic, so snapping
        // a brush's carved array here yields exactly the vertices the global
        // pipeline's whole-list snap would — whatever subset of brushes ends up
        // needing it.
        Polygon[] SnappedOf(int index) => snapped[index] ??= VertexSnapper.Snap(perBrushSurfaces[index]);
    }

    /// <summary>
    /// The ascending union of a group of candidate index sets, deduplicated by
    /// array reference first (co-owned brushes share one array per footprint
    /// box, so the common case unions nothing). Shared by the full weld above
    /// and the incremental compile's scoped weld so both produce identical
    /// candidate grids for identical inputs.
    /// </summary>
    internal static int[] UnionDistinctSets(HashSet<int[]> distinctSets, HashSet<int> unionScratch)
    {
        if (distinctSets.Count == 1)
        {
            using HashSet<int[]>.Enumerator single = distinctSets.GetEnumerator();
            single.MoveNext();
            return single.Current;
        }

        unionScratch.Clear();
        foreach (int[] set in distinctSets)
        {
            foreach (int j in set)
                unionScratch.Add(j);
        }
        var union = new int[unionScratch.Count];
        unionScratch.CopyTo(union);
        Array.Sort(union);
        return union;
    }

    /// <summary>
    /// Snaps and welds one owner cell's needing brushes against the cell's
    /// candidate union and emits each brush's exact-size welded slice — the
    /// per-cell core of the weld stage, factored out so the incremental
    /// compile re-welds an edit's neighbourhood through the identical code
    /// path (same candidate grid, same insertion order, bit-identical output).
    /// <paramref name="surfaceCountOf"/> reports a brush's carved surface
    /// count (welding is per-polygon, so slice widths equal carve widths).
    /// </summary>
    internal static void WeldCell(
        List<int> needing,
        int[] cellCandidates,
        Func<int, Polygon[]> snappedOf,
        Func<int, int> surfaceCountOf,
        List<Polygon> candidateScratch,
        List<Polygon> inputScratch,
        Action<int, Polygon[]> emit)
    {
        candidateScratch.Clear();
        foreach (int j in cellCandidates)
            candidateScratch.AddRange(snappedOf(j));

        inputScratch.Clear();
        foreach (int i in needing)
            inputScratch.AddRange(snappedOf(i));

        Polygon[] output = TJunctionWelder.Weld(inputScratch, candidateScratch);

        // Slice the welder's index-aligned output back into per-placement
        // arrays (exact-size, freshly allocated — these are results and
        // cache entries, never scratch).
        int offset = 0;
        foreach (int i in needing)
        {
            var slice = new Polygon[surfaceCountOf(i)];
            Array.Copy(output, offset, slice, 0, slice.Length);
            offset += slice.Length;
            emit(i, slice);
        }
    }

    // One candidate index set per placement: every brush resident in any cell
    // of the placement's footprint (see the class remarks for why that set is
    // sufficient). Ascending, so the sets are deterministic and comparable
    // element-wise across compiles — placement-index shifts preserve relative
    // order, which is what lets CsgWeldCache validate by position. Placements
    // sharing a footprint box share ONE array (candidates are a function of
    // the covered cells alone): without the dedupe, a dense cell of n brushes
    // would compute and store n identical n-element sets — quadratic in
    // exactly the worlds the chunk substrate degenerates on.
    private static int[][] ComputeCandidateSets(IReadOnlyList<BrushPlacement> placements, ChunkGrid chunks)
    {
        var candidates = new int[placements.Count][];
        var byFootprint = new Dictionary<(ChunkCoord Min, ChunkCoord Max), int[]>();
        var scratch = new HashSet<int>();
        for (int i = 0; i < candidates.Length; i++)
        {
            BrushPlacement placement = placements[i];
            // The footprint is the full cell box between these two corners —
            // the same math as ChunkGrid.ComputeFootprint, keyed instead of
            // materialized.
            Aabb inflated = ChunkGrid.InflatedBounds(in placement);
            (ChunkCoord Min, ChunkCoord Max) footprint =
                (ChunkCoord.FromPosition(inflated.Min), ChunkCoord.FromPosition(inflated.Max));
            if (byFootprint.TryGetValue(footprint, out int[]? shared))
            {
                candidates[i] = shared;
                continue;
            }

            scratch.Clear();
            for (int x = footprint.Min.X; x <= footprint.Max.X; x++)
            {
                for (int y = footprint.Min.Y; y <= footprint.Max.Y; y++)
                {
                    for (int z = footprint.Min.Z; z <= footprint.Max.Z; z++)
                    {
                        // Every footprint cell of a live placement is occupied
                        // (the grid build created a chunk for each); TryGet is
                        // just the grid's lookup shape.
                        if (chunks.TryGet(new ChunkCoord(x, y, z), out WorldChunk chunk))
                        {
                            foreach (int j in chunk.ResidentBrushIndices)
                                scratch.Add(j);
                        }
                    }
                }
            }

            var set = new int[scratch.Count];
            scratch.CopyTo(set);
            Array.Sort(set);
            byFootprint[footprint] = candidates[i] = set;
        }
        return candidates;
    }
}
