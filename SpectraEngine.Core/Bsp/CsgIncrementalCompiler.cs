using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// The incremental static-world compile: derives a new <see cref="CsgWorld"/>
/// from the previous one by re-running each pipeline stage over exactly the
/// edit's neighbourhood — changed brushes, their carve neighbours, the brushes
/// welded against those, and the cells any of them touch — while every other
/// artifact (carved and welded arrays, chunk instances with their BSP trees,
/// chunk mesh artifacts) is carried forward by reference. No step reads,
/// copies, or validates per-brush or per-cell state outside that
/// neighbourhood (the carry is paged copy-on-write, the chunk map layered),
/// which is what makes a one-part edit cost the same in a 50k-part world as
/// in a 1k-part world — the open-world pillar's requirement.
/// </summary>
/// <remarks>
/// <b>Scoping rules (mirroring the validation-driven full path exactly):</b>
/// <list type="bullet">
/// <item><description><b>Changed set C:</b> placements at dirty-cell residents
/// whose (brush, matrix) differs from the previous compile's — complete by the
/// trusted-diff contract (every changed placement's old footprint is dirty,
/// and its old residency makes it a dirty-cell resident of the previous
/// grid).</description></item>
/// <item><description><b>Re-carve set R = C ∪ neighbours(C):</b> a brush's
/// carve is a function of its placement and its ordered carver sequence, so
/// exactly the changed brushes and the brushes whose carver sequence contains
/// one re-carve — the same set the carve cache's validation would
/// miss.</description></item>
/// <item><description><b>Re-weld set W = residents of R's (old and new)
/// footprints:</b> b re-welds iff some candidate's carve array changed, and
/// r ∈ candidates(b) ⇔ footprint(r) ∩ footprint(b) ≠ ∅ ⇔ b is resident in a
/// footprint cell of r — the same set the weld cache's validation would
/// miss.</description></item>
/// <item><description><b>Rebuilt cells = footprint cells of W plus the old
/// footprints of C:</b> precisely the cells with a resident whose welded
/// array changed or whose membership changed — the BSP cache's miss set. Mesh
/// artifacts additionally survive a cell rebuild when the cell's OWNED set
/// and arrays are untouched — the mesh cache's rule.</description></item>
/// </list>
/// <para>
/// <b>Exactness.</b> Re-run stages go through the same code paths as the full
/// compile (<see cref="Csg.CarveSingle"/>, <see cref="ChunkWelder.WeldCell"/>,
/// <see cref="ChunkBspBuilder.BuildCellTree"/>,
/// <see cref="ChunkMeshBuilder.BuildArtifact"/>), so for the editing gestures
/// this path accepts the result is bit-identical to a from-scratch compile of
/// the same placements. The one assumption that is trusted rather than proven
/// is broadphase ORDER stability: carried carve results embed the clip order
/// of the original broadphase sweep, so the gates below refuse any edit that
/// could permute a surviving pair's discovery order — no new overlap pairs,
/// and no min-X rank crossing between a changed brush and any brush whose
/// neighbour list the pair could appear in: its own neighbours AND every
/// member of each surviving neighbour's carried list (the two-hop gate — a
/// changed brush crossing a non-overlapping brush that merely shares a
/// neighbour still reshuffles that neighbour's pair-discovery order). What
/// remains trusted is the sweep's swap-remove active-list bookkeeping among
/// brushes with no shared neighbour at all: a permutation there would make
/// some carried fragmentation differ bitwise from a from-scratch compile
/// while remaining semantically identical (same solid union, same queries)
/// and fully deterministic. The very next fallback compile re-validates every
/// carried artifact against a real sweep, so no such drift can persist.
/// </para>
/// <para>
/// <b>Fallback triggers</b> (TryBuild returns false and the caller runs the
/// fully validated path): placement count changed; a new overlap pair formed
/// (its clip position inside existing sequences depends on global sweep
/// history); a changed brush's min-X rank crossed — or exactly tied — that of
/// a neighbour or of any member of a surviving neighbour's carried list (the
/// pair's discovery position would move). Every trigger costs one validated
/// compile, after which patching resumes; a drag that keeps its overlap set
/// (including none) and its local rank order never falls back.
/// </para>
/// <para>
/// <b>Threading:</b> pure CPU work over the snapshot and the immutable
/// previous world — background-thread safe, and the previous world (still
/// live on the render thread) is never mutated: chunks and carry pages are
/// copy-on-write, the chunk map is layered.
/// </para>
/// </remarks>
internal static class CsgIncrementalCompiler
{
    /// <summary>
    /// Attempts the incremental compile. False means "cannot patch this edit
    /// exactly" — never an error; the caller falls back to the validated path.
    /// </summary>
    internal static bool TryBuild(
        IReadOnlyList<BrushPlacement> placements,
        IReadOnlyList<ChunkCoord> dirtyCells,
        CsgWorld previous,
        [NotNullWhen(true)] out CsgWorld? world)
    {
        world = null;
        int n = placements.Count;
        IReadOnlyList<BrushPlacement> prevPlacements = previous.Placements;
        if (n == 0 || n != prevPlacements.Count)
            return false;

        CsgWorldCarry carry = previous.Carry;
        ChunkGrid prevGrid = previous.Chunks;

        // --- Changed set C: dirty-cell residents whose placement differs. ---
        var changed = new List<int>();
        var candidateSeen = new HashSet<int>();
        foreach (ChunkCoord cell in dirtyCells)
        {
            if (!prevGrid.TryGet(cell, out WorldChunk cellChunk))
                continue;
            foreach (int i in cellChunk.ResidentBrushIndices)
            {
                if (candidateSeen.Add(i) && !SamePlacement(placements[i], prevPlacements[i]))
                    changed.Add(i);
            }
        }
        changed.Sort();

        VerifyTrustedDiff(placements, prevPlacements, changed);

        if (changed.Count == 0)
        {
            // No-op recompile: every artifact carries forward verbatim (the
            // empty mesh delta tells the GPU swap path so explicitly).
            world = CsgWorld.CreatePatched(
                placements, previous.SurfaceCount, prevGrid, previous.ChunkMeshes, dirtyCells, carry,
                previous, chunkMeshDelta: [],
                new CsgCacheStats(n, 0), new CsgWeldStats(n, 0),
                new CsgBspStats(prevGrid.Count, 0), new CsgMeshStats(previous.ChunkMeshes.Count, 0));
            return true;
        }

        // --- Bounds and residency footprints of the changed placements. ---
        var oldBounds = new Dictionary<int, Aabb>(changed.Count);
        var newBounds = new Dictionary<int, Aabb>(changed.Count);
        var oldFootprints = new Dictionary<int, ChunkCoord[]>(changed.Count);
        var newFootprints = new Dictionary<int, ChunkCoord[]>(changed.Count);
        foreach (int c in changed)
        {
            BrushPlacement prevPlacement = prevPlacements[c];
            BrushPlacement placement = placements[c];
            oldBounds[c] = prevPlacement.WorldBounds;
            newBounds[c] = placement.WorldBounds;
            oldFootprints[c] = ChunkGrid.ComputeFootprint(in prevPlacement);
            newFootprints[c] = ChunkGrid.ComputeFootprint(in placement);
        }

        // --- Overlap-pair delta. Candidates for "overlaps a changed brush":
        // previous-grid residents of every cell the old or new bounds cover,
        // plus the changed brushes themselves (their previous residency does
        // not cover where they moved to).
        var overlapCandidates = new HashSet<int>(changed);
        foreach (int c in changed)
        {
            AddResidentsOfBoundsCells(prevGrid, oldBounds[c], overlapCandidates);
            AddResidentsOfBoundsCells(prevGrid, newBounds[c], overlapCandidates);
        }

        var recarve = new HashSet<int>(changed);
        // removedPartners[i] = former neighbours pair (i, j) that no longer
        // overlaps — the only pair delta the patch path accepts (removal keeps
        // every survivor's clip order; an ADDED pair's clip position inside
        // existing sequences depends on global sweep history → fallback).
        var removedPartners = new Dictionary<int, HashSet<int>>();
        var newNeighborSet = new HashSet<int>();
        foreach (int c in changed)
        {
            Aabb boundsC = newBounds[c];
            newNeighborSet.Clear();
            foreach (int j in overlapCandidates)
            {
                if (j == c)
                    continue;
                Aabb boundsJ = newBounds.TryGetValue(j, out Aabb movedBounds)
                    ? movedBounds
                    : placements[j].WorldBounds;
                if (boundsC.Intersects(boundsJ))
                    newNeighborSet.Add(j);
            }

            int[] prevNeighbors = carry.CarveNeighbors[c];
            foreach (int j in prevNeighbors)
                recarve.Add(j);

            // Additions → fallback; removals → recorded; survivors → rank check.
            int surviving = 0;
            foreach (int j in prevNeighbors)
            {
                if (newNeighborSet.Contains(j))
                    surviving++;
                else
                {
                    GetOrAdd(removedPartners, c).Add(j);
                    GetOrAdd(removedPartners, j).Add(c);
                }
            }
            if (newNeighborSet.Count != surviving)
                return false; // at least one new overlap pair formed

            foreach (int j in newNeighborSet)
            {
                // The pair's discovery side is the later min-X rank; it must
                // not flip (and must not be ambiguous: an exact key tie makes
                // the sweep order depend on global sort internals).
                float oldMinC = oldBounds[c].Min.X;
                float newMinC = boundsC.Min.X;
                if (!RankRelationStable(c, j, oldMinC, newMinC, placements, oldBounds, newBounds))
                    return false;

                // TWO-HOP RANK GATE: j is re-carved with its carried neighbour
                // ORDER, and that order is the pairs' sweep-discovery order —
                // for a partner m, the position of whichever of {j, m} has the
                // later min-X rank (or, for partners already active at j's own
                // sweep step, their insertion order into the active list,
                // which is again min-X rank order). So c's rank crossing ANY
                // other member m of j's list — even one c never overlaps —
                // permutes where a fresh sweep would place the (j, c) pair
                // inside j's list, and a patched j carved in the carried order
                // would fragment differently from a from-scratch compile
                // (bitwise different surfaces; the pinned triangle-multiset
                // oracle breaks). Rank stability of c against every member of
                // every surviving neighbour's list closes that hole; unchanged
                // members keep their relative order among themselves by
                // construction (their keys did not move).
                foreach (int m in carry.CarveNeighbors[j])
                {
                    if (m != c && !RankRelationStable(c, m, oldMinC, newMinC, placements, oldBounds, newBounds))
                        return false;
                }
            }
        }

        // --- Patch the neighbour lists (removals only; order preserved). ---
        PagedArray<int[]> neighbors = carry.CarveNeighbors;
        if (removedPartners.Count > 0)
        {
            var replacements = new List<(int, int[])>(removedPartners.Count);
            foreach ((int i, HashSet<int> removed) in removedPartners)
            {
                int[] old = carry.CarveNeighbors[i];
                var patched = new int[old.Length - CountIn(old, removed)];
                int cursor = 0;
                foreach (int j in old)
                {
                    if (!removed.Contains(j))
                        patched[cursor++] = j;
                }
                replacements.Add((i, patched));
            }
            neighbors = neighbors.WithReplacements(replacements);
        }

        // --- Re-carve R through the same per-brush core as the full path. ---
        var recarveList = new List<int>(recarve);
        recarveList.Sort();
        var carveReplacements = new (int Index, Polygon[] Value)[recarveList.Count];
        PagedArray<int[]> neighborsFinal = neighbors;
        if (recarveList.Count <= 4)
        {
            var scratch = new Csg.CarveScratch();
            for (int k = 0; k < recarveList.Count; k++)
            {
                int b = recarveList[k];
                carveReplacements[k] = (b, Csg.CarveSingle(placements, b, neighborsFinal[b], scratch));
            }
        }
        else
        {
            Parallel.For(0, recarveList.Count,
                static () => new Csg.CarveScratch(),
                (k, _, scratch) =>
                {
                    int b = recarveList[k];
                    carveReplacements[k] = (b, Csg.CarveSingle(placements, b, neighborsFinal[b], scratch));
                    return scratch;
                },
                static _ => { });
        }
        PagedArray<Polygon[]> carved = carry.CarvedPerBrush.WithReplacements(carveReplacements);

        // --- Residency deltas per cell (only changed brushes move cells). ---
        var cellDeltas = new Dictionary<ChunkCoord, CellDelta>();
        foreach (int c in changed)
        {
            ChunkCoord[] oldFootprint = oldFootprints[c];
            ChunkCoord[] newFootprint = newFootprints[c];
            foreach (ChunkCoord cell in oldFootprint)
            {
                if (Array.IndexOf(newFootprint, cell) < 0)
                    GetOrAdd(cellDeltas, cell).Removed.Add(c);
            }
            foreach (ChunkCoord cell in newFootprint)
            {
                if (Array.IndexOf(oldFootprint, cell) < 0)
                    GetOrAdd(cellDeltas, cell).Added.Add(c);
            }
        }

        // --- Re-weld set W: current residents of R's footprints (old and new
        // for moved brushes) — exactly the weld cache's miss set.
        var weldCells = new HashSet<ChunkCoord>();
        var footprintOf = new Dictionary<int, ChunkCoord[]>(recarveList.Count);
        foreach (int r in recarveList)
        {
            ChunkCoord[] footprint;
            if (newFootprints.TryGetValue(r, out ChunkCoord[]? moved))
            {
                footprint = moved;
                foreach (ChunkCoord cell in oldFootprints[r])
                    weldCells.Add(cell);
            }
            else
            {
                BrushPlacement placement = placements[r];
                footprint = ChunkGrid.ComputeFootprint(in placement);
            }
            footprintOf[r] = footprint;
            foreach (ChunkCoord cell in footprint)
                weldCells.Add(cell);
        }

        var reweld = new HashSet<int>();
        var residentsScratch = new List<int>();
        foreach (ChunkCoord cell in weldCells)
        {
            ResidentsAfter(prevGrid, cellDeltas, cell, residentsScratch);
            foreach (int i in residentsScratch)
                reweld.Add(i);
        }
        var weldList = new List<int>(reweld);
        weldList.Sort();

        // --- Rebuilt cells: W's footprints plus the old footprints of C. ---
        var affectedCells = new HashSet<ChunkCoord>(weldCells);
        foreach (int w in weldList)
        {
            if (footprintOf.TryGetValue(w, out ChunkCoord[]? known))
            {
                foreach (ChunkCoord cell in known)
                    affectedCells.Add(cell);
            }
            else
            {
                BrushPlacement placement = placements[w];
                foreach (ChunkCoord cell in ChunkGrid.ComputeFootprint(in placement))
                    affectedCells.Add(cell);
            }
        }
        var affectedList = new List<ChunkCoord>(affectedCells);
        affectedList.Sort();

        // --- Fresh WorldChunk per affected cell (the previous instances stay
        // live in the previous world and are never touched).
        var gridChanges = new List<(ChunkCoord Coord, WorldChunk? Chunk)>(affectedList.Count);
        var ownerCellOf = new Dictionary<int, ChunkCoord>();
        foreach (ChunkCoord cell in affectedList)
        {
            ResidentsAfter(prevGrid, cellDeltas, cell, residentsScratch);
            bool existed = prevGrid.TryGet(cell, out _);
            if (residentsScratch.Count == 0)
            {
                if (existed)
                    gridChanges.Add((cell, null));
                continue;
            }

            var chunk = new WorldChunk(cell);
            foreach (int i in residentsScratch)
            {
                chunk.AddResident(i);
                if (!ownerCellOf.TryGetValue(i, out ChunkCoord owner))
                {
                    BrushPlacement placement = placements[i];
                    ownerCellOf[i] = owner = ChunkGrid.OwnerCell(in placement);
                }
                if (owner == cell)
                    chunk.AddOwned(i, carved[i]);
            }
            gridChanges.Add((cell, chunk));
        }

        ChunkGrid grid = ChunkGrid.Patch(prevGrid, gridChanges);

        // --- Weld W per owner cell through the same core as the full path,
        // with candidate sets recomputed over the patched grid (same math as
        // ChunkWelder.ComputeCandidateSets, scoped to W).
        var candidateReplacements = new List<(int, int[])>(weldList.Count);
        var candidateOf = new Dictionary<int, int[]>(weldList.Count);
        var byFootprintBox = new Dictionary<(ChunkCoord Min, ChunkCoord Max), int[]>();
        var candidateScratchSet = new HashSet<int>();
        foreach (int w in weldList)
        {
            BrushPlacement placement = placements[w];
            Aabb inflated = ChunkGrid.InflatedBounds(in placement);
            (ChunkCoord Min, ChunkCoord Max) box =
                (ChunkCoord.FromPosition(inflated.Min), ChunkCoord.FromPosition(inflated.Max));
            if (!byFootprintBox.TryGetValue(box, out int[]? set))
            {
                candidateScratchSet.Clear();
                for (int x = box.Min.X; x <= box.Max.X; x++)
                {
                    for (int y = box.Min.Y; y <= box.Max.Y; y++)
                    {
                        for (int z = box.Min.Z; z <= box.Max.Z; z++)
                        {
                            if (grid.TryGet(new ChunkCoord(x, y, z), out WorldChunk cellChunk))
                            {
                                foreach (int j in cellChunk.ResidentBrushIndices)
                                    candidateScratchSet.Add(j);
                            }
                        }
                    }
                }
                set = new int[candidateScratchSet.Count];
                candidateScratchSet.CopyTo(set);
                Array.Sort(set);
                byFootprintBox[box] = set;
            }
            candidateOf[w] = set;
            candidateReplacements.Add((w, set));
        }

        var ownedByCell = new Dictionary<ChunkCoord, List<int>>();
        var weldCellOrder = new List<ChunkCoord>();
        foreach (int w in weldList)
        {
            if (!ownerCellOf.TryGetValue(w, out ChunkCoord owner))
            {
                BrushPlacement placement = placements[w];
                ownerCellOf[w] = owner = ChunkGrid.OwnerCell(in placement);
            }
            if (!ownedByCell.TryGetValue(owner, out List<int>? needing))
            {
                ownedByCell[owner] = needing = [];
                weldCellOrder.Add(owner);
            }
            needing.Add(w);
        }

        var snappedMemo = new Dictionary<int, Polygon[]>();
        var distinctSets = new HashSet<int[]>();
        var unionScratch = new HashSet<int>();
        var candidateSurfaceScratch = new List<Polygon>();
        var inputScratch = new List<Polygon>();
        var weldReplacements = new List<(int Index, Polygon[] Value)>(weldList.Count);
        foreach (ChunkCoord cell in weldCellOrder)
        {
            List<int> needing = ownedByCell[cell];
            distinctSets.Clear();
            foreach (int i in needing)
                distinctSets.Add(candidateOf[i]);
            int[] cellCandidates = ChunkWelder.UnionDistinctSets(distinctSets, unionScratch);

            ChunkWelder.WeldCell(
                needing, cellCandidates, SnappedOf,
                i => carved[i].Length,
                candidateSurfaceScratch, inputScratch,
                (i, slice) => weldReplacements.Add((i, slice)));
        }

        PagedArray<Polygon[]> welded = carry.WeldedPerBrush.WithReplacements(weldReplacements);
        PagedArray<int[]> candidates = carry.WeldCandidates.WithReplacements(candidateReplacements);

        int surfaceCount = previous.SurfaceCount;
        foreach ((int i, Polygon[] slice) in weldReplacements)
            surfaceCount += slice.Length - carry.WeldedPerBrush[i].Length;

        // --- Attach welded surfaces and rebuild each fresh cell's tree. ---
        var freshChunks = new List<WorldChunk>(gridChanges.Count);
        foreach ((_, WorldChunk? chunk) in gridChanges)
        {
            if (chunk is null)
                continue;
            foreach (int o in chunk.OwnedBrushIndices)
                chunk.AddWeldedSurfaces(welded[o]);
            freshChunks.Add(chunk);
        }

        // Sequential below a handful of cells: thread-pool dispatch costs more
        // (and jitters more) than a few small trees; big edits still fan out.
        if (freshChunks.Count <= 4)
        {
            foreach (WorldChunk chunk in freshChunks)
                AttachTree(chunk);
        }
        else
        {
            Parallel.For(0, freshChunks.Count, k => AttachTree(freshChunks[k]));
        }

        // --- Per-cell meshes: rebuild a fresh cell's artifact only when its
        // OWNED welded input actually changed (the mesh cache's rule); a cell
        // rebuilt for resident/BSP reasons alone keeps its artifact instance,
        // which is also the GPU swap path's "cell unchanged" signal.
        PagedArray<ChunkMesh> prevMeshes = previous.ChunkMeshesPaged;
        var meshChanges = new List<(ChunkCoord Coord, ChunkMesh? Mesh)>();
        var toBuild = new List<WorldChunk>();
        var placeholderSlots = new List<int>(); // meshChanges indices awaiting a built artifact, aligned with toBuild
        foreach ((ChunkCoord coord, WorldChunk? chunk) in gridChanges)
        {
            ChunkMesh? prevMesh = FindMesh(prevMeshes, coord);
            if (chunk is null || chunk.WeldedSurfaces.Count == 0)
            {
                if (prevMesh is not null)
                    meshChanges.Add((coord, null)); // cell lost its render geometry
                continue;
            }

            if (prevMesh is not null && prevGrid.TryGet(coord, out WorldChunk prevChunk) &&
                OwnedInputUnchanged(prevChunk, chunk, carry, welded))
            {
                continue; // artifact carried forward — not a change
            }

            placeholderSlots.Add(meshChanges.Count);
            meshChanges.Add((coord, null));
            toBuild.Add(chunk);
        }

        if (toBuild.Count > 0)
        {
            var built = new ChunkMesh[toBuild.Count];
            if (toBuild.Count <= 4)
            {
                for (int k = 0; k < toBuild.Count; k++)
                    built[k] = ChunkMeshBuilder.BuildArtifact(toBuild[k]);
            }
            else
            {
                Parallel.For(0, toBuild.Count, k => built[k] = ChunkMeshBuilder.BuildArtifact(toBuild[k]));
            }
            for (int k = 0; k < placeholderSlots.Count; k++)
                meshChanges[placeholderSlots[k]] = (meshChanges[placeholderSlots[k]].Coord, built[k]);
        }

        PagedArray<ChunkMesh> chunkMeshes = SpliceMeshes(prevMeshes, meshChanges);

        var carryNext = new CsgWorldCarry(carved, welded, neighbors, candidates);
        world = CsgWorld.CreatePatched(
            placements, surfaceCount, grid, chunkMeshes, dirtyCells, carryNext,
            previous, meshChanges,
            new CsgCacheStats(n - recarveList.Count, recarveList.Count),
            new CsgWeldStats(n - weldList.Count, weldList.Count),
            new CsgBspStats(grid.Count - freshChunks.Count, freshChunks.Count),
            new CsgMeshStats(chunkMeshes.Count - toBuild.Count, toBuild.Count));
        return true;

        Polygon[] SnappedOf(int index)
        {
            if (!snappedMemo.TryGetValue(index, out Polygon[]? snapped))
                snappedMemo[index] = snapped = VertexSnapper.Snap(carved[index]);
            return snapped;
        }

        void AttachTree(WorldChunk chunk) =>
            chunk.AttachBsp(ChunkBspBuilder.BuildCellTree(chunk, i => welded[i], out _));
    }

    // True when changed brush c's min-X rank relation to brush `other` is the
    // same, and unambiguous (no exact key tie), before and after the edit —
    // the condition under which the sweep discovers every pair involving
    // either of them on the same side and in the same relative order. `other`
    // may itself be a changed brush (its own old/new bounds are then
    // consulted); an unchanged brush keys both poses off its single placement.
    private static bool RankRelationStable(
        int c, int other, float oldMinC, float newMinC,
        IReadOnlyList<BrushPlacement> placements,
        Dictionary<int, Aabb> oldBounds, Dictionary<int, Aabb> newBounds)
    {
        float oldMinOther = (oldBounds.TryGetValue(other, out Aabb old) ? old : placements[other].WorldBounds).Min.X;
        float newMinOther = (newBounds.TryGetValue(other, out Aabb moved) ? moved : placements[other].WorldBounds).Min.X;
        int oldRelation = oldMinC.CompareTo(oldMinOther);
        int newRelation = newMinC.CompareTo(newMinOther);
        return oldRelation != 0 && newRelation != 0 && oldRelation == newRelation;
    }

    // Element-wise placement equality: brush reference plus matrix float ==,
    // the same comparison the carve cache's validation uses (see
    // CsgCompileCache for why float == is right here).
    private static bool SamePlacement(in BrushPlacement a, in BrushPlacement b) =>
        ReferenceEquals(a.Brush, b.Brush) && a.Transform == b.Transform;

    // DEBUG-only verification of the trusted-diff contract's order half: every
    // placement outside the changed set must be bitwise identical to the
    // previous compile's at the same index. Compiled out in Release — the
    // whole point of the trusted diff is not paying an O(world) sweep per
    // edit — so dev builds (tests, the demo) catch contract violations loudly
    // while release builds trust their callers.
    [Conditional("DEBUG")]
    private static void VerifyTrustedDiff(
        IReadOnlyList<BrushPlacement> placements, IReadOnlyList<BrushPlacement> prevPlacements, List<int> changed)
    {
        var changedSet = new HashSet<int>(changed);
        for (int i = 0; i < placements.Count; i++)
        {
            if (!changedSet.Contains(i) && !SamePlacement(placements[i], prevPlacements[i]))
            {
                throw new InvalidOperationException(
                    $"Incremental compile contract violated: placement {i} changed but is not covered by the " +
                    "dirty-cell set (or the placement list was reordered). The caller must dirty every changed " +
                    "placement's old and new footprint, or compile through the validated path.");
            }
        }
    }

    // Adds every previous-grid resident of every cell the (uninflated) bounds
    // cover. Any brush intersecting the bounds is resident in one of these
    // cells (residency covers a brush's own AABB cells), so the union is a
    // complete overlap-candidate set.
    private static void AddResidentsOfBoundsCells(ChunkGrid grid, in Aabb bounds, HashSet<int> into)
    {
        ChunkCoord min = ChunkCoord.FromPosition(bounds.Min);
        ChunkCoord max = ChunkCoord.FromPosition(bounds.Max);
        for (int x = min.X; x <= max.X; x++)
        {
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    if (grid.TryGet(new ChunkCoord(x, y, z), out WorldChunk chunk))
                    {
                        foreach (int i in chunk.ResidentBrushIndices)
                            into.Add(i);
                    }
                }
            }
        }
    }

    // The cell's resident indices AFTER the edit: the previous residents minus
    // changed brushes that left, plus changed brushes that entered — ascending,
    // like every resident list. Fills `into` (cleared first).
    private static void ResidentsAfter(
        ChunkGrid prevGrid, Dictionary<ChunkCoord, CellDelta> deltas, ChunkCoord cell, List<int> into)
    {
        into.Clear();
        bool hasDelta = deltas.TryGetValue(cell, out CellDelta? delta);
        if (prevGrid.TryGet(cell, out WorldChunk chunk))
        {
            foreach (int i in chunk.ResidentBrushIndices)
            {
                if (!hasDelta || !delta!.Removed.Contains(i))
                    into.Add(i);
            }
        }
        if (hasDelta && delta!.Added.Count > 0)
        {
            into.AddRange(delta.Added);
            into.Sort();
        }
    }

    private static int CountIn(int[] values, HashSet<int> set)
    {
        int count = 0;
        foreach (int v in values)
        {
            if (set.Contains(v))
                count++;
        }
        return count;
    }

    private static TValue GetOrAdd<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (!map.TryGetValue(key, out TValue? value))
            map[key] = value = new TValue();
        return value;
    }

    // Binary search over the ascending previous mesh list.
    private static ChunkMesh? FindMesh(IReadOnlyList<ChunkMesh> meshes, ChunkCoord coord)
    {
        int lo = 0, hi = meshes.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int cmp = meshes[mid].Coord.CompareTo(coord);
            if (cmp == 0)
                return meshes[mid];
            if (cmp < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return null;
    }

    // The mesh cache's owner-identity rule, expressed over the carry: same
    // owned index list and, per owned brush, the same welded array instance
    // (o ∉ W ⇔ the paged slot still holds the carried reference).
    private static bool OwnedInputUnchanged(
        WorldChunk prevChunk, WorldChunk newChunk, CsgWorldCarry carry, PagedArray<Polygon[]> welded)
    {
        IReadOnlyList<int> prevOwned = prevChunk.OwnedBrushIndices;
        IReadOnlyList<int> newOwned = newChunk.OwnedBrushIndices;
        if (prevOwned.Count != newOwned.Count)
            return false;
        for (int k = 0; k < newOwned.Count; k++)
        {
            int o = newOwned[k];
            if (prevOwned[k] != o || !ReferenceEquals(welded[o], carry.WeldedPerBrush[o]))
                return false;
        }
        return true;
    }

    // Sorted splice of the ascending previous mesh list with the (ascending)
    // per-cell mesh changes: null removes, non-null replaces or inserts. The
    // steady-state edit (artifacts replaced, no cell gaining or losing its
    // mesh) derives by paged copy-on-write — O(changed pages); cell
    // insertions/removals re-pack via binary-searched block copies
    // (memcpy-cheap, and rare next to in-place edits).
    private static PagedArray<ChunkMesh> SpliceMeshes(
        PagedArray<ChunkMesh> previous, List<(ChunkCoord Coord, ChunkMesh? Mesh)> changes)
    {
        if (changes.Count == 0)
            return previous;

        int sizeDelta = 0;
        bool replaceOnly = true;
        var replacements = new List<(int Index, ChunkMesh Value)>(changes.Count);
        foreach ((ChunkCoord coord, ChunkMesh? mesh) in changes)
        {
            int pos = LowerBound(previous, coord, 0);
            bool existed = pos < previous.Count && previous[pos].Coord == coord;
            sizeDelta += (mesh is not null ? 1 : 0) - (existed ? 1 : 0);
            if (existed && mesh is not null)
                replacements.Add((pos, mesh));
            else
                replaceOnly = false;
        }

        if (replaceOnly)
            return previous.WithReplacements(replacements);

        var merged = new ChunkMesh[previous.Count + sizeDelta];
        int src = 0, dst = 0;
        foreach ((ChunkCoord coord, ChunkMesh? mesh) in changes)
        {
            int pos = LowerBound(previous, coord, src);
            previous.CopyTo(src, merged, dst, pos - src);
            dst += pos - src;
            src = pos < previous.Count && previous[pos].Coord == coord ? pos + 1 : pos;
            if (mesh is not null)
                merged[dst++] = mesh;
        }
        previous.CopyTo(src, merged, dst, previous.Count - src);
        return PagedArray<ChunkMesh>.From(merged);
    }

    // First index in [from, count) whose coordinate is >= coord.
    private static int LowerBound(PagedArray<ChunkMesh> ordered, ChunkCoord coord, int from)
    {
        int lo = from, hi = ordered.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (ordered[mid].Coord.CompareTo(coord) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // Per-cell residency delta of the changed brushes: who left, who entered.
    private sealed class CellDelta
    {
        public readonly List<int> Removed = [];
        public readonly List<int> Added = [];
    }
}
