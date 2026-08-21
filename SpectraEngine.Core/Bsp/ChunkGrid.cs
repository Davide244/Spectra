using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// The sparse spatial partition of a compiled static world into
/// <see cref="ChunkCoord.CellSize"/> cells: a dictionary keyed by integer cell
/// coordinates, holding a <see cref="WorldChunk"/> for every cell at least one
/// brush's inflated AABB touches. Sparse means no world extents — negative and
/// arbitrarily distant coordinates cost the same as cells at the origin, which
/// is what makes brushes placeable anywhere (open-world pillar).
/// </summary>
/// <remarks>
/// The partitioning substrate of the chunked compile: <see cref="CsgWorld"/>
/// builds it from the carved per-brush surfaces, the per-cell snap+weld
/// (<see cref="ChunkWelder"/>, W2) consumes its residency data and attaches
/// each cell's welded surfaces, the per-cell BSP stage
/// (<see cref="ChunkBspBuilder"/>, W3) attaches each cell's tree, and the
/// per-chunk mesh stage (W4) will consume those. Immutable once the world
/// build that created it returns, so any number of threads may read it.
/// </remarks>
public sealed class ChunkGrid
{
    /// <summary>
    /// How far a brush's world AABB is inflated before its cell coverage is
    /// computed: <c>2 * max(Polygon.Epsilon, VertexSnapper.GridSize)</c>.
    /// PINNED alongside <see cref="ChunkCoord.CellSize"/>. Rationale: geometry
    /// within one epsilon of a cell boundary can weld to (or snap onto)
    /// vertices on the far side, so any brush that close to a boundary must be
    /// resident in both cells for per-cell welds to see every candidate vertex
    /// a global weld would; twice the larger tolerance covers a snap
    /// displacement followed by a weld test. Do not change without documenting
    /// why — the W2 weld-equivalence oracle depends on this band being wide
    /// enough.
    /// </summary>
    public const float WeldBand = 2f * (Polygon.Epsilon >= VertexSnapper.GridSize ? Polygon.Epsilon : VertexSnapper.GridSize);

    // The lookup structure is layered so an incremental compile can derive a
    // grid without re-inserting every cell (an O(world) dictionary build —
    // exactly the fixed cost the open-world pillar forbids): `_base` is a full
    // dictionary SHARED with ancestor grids (immutable — never written after
    // its own build), and `_overlay` holds this grid's cumulative deltas since
    // the base was built (null value = the cell was removed). A full build has
    // no overlay; a patched grid clones its parent's small overlay and adds
    // the edit's cells; when the overlay outgrows a fraction of the base it is
    // compacted into a fresh flat dictionary (amortized O(1) per edit).
    private readonly Dictionary<ChunkCoord, WorldChunk> _base;
    private readonly Dictionary<ChunkCoord, WorldChunk?>? _overlay;
    private readonly PagedArray<WorldChunk> _orderedChunks;

    // Conservative bounding box of the occupied cells, in cell coordinates
    // (inclusive), valid only while Count > 0. Exact for full builds;
    // patched grids only ever GROW it (recomputing an exact box after a cell
    // removal would be an O(cells) sweep per edit — the fixed cost the
    // open-world pillar forbids). A superset is safe for its one consumer,
    // the ray walk's termination clip: cells outside it are guaranteed
    // unoccupied, cells inside are simply looked up.
    private readonly ChunkCoord _cellMin;
    private readonly ChunkCoord _cellMax;

    private ChunkGrid(
        Dictionary<ChunkCoord, WorldChunk> baseChunks,
        Dictionary<ChunkCoord, WorldChunk?>? overlay,
        PagedArray<WorldChunk> orderedChunks,
        ChunkCoord cellMin, ChunkCoord cellMax)
    {
        _base = baseChunks;
        _overlay = overlay;
        _orderedChunks = orderedChunks;
        _cellMin = cellMin;
        _cellMax = cellMax;
    }

    /// <summary>
    /// A conservative (possibly grown, never shrunk-below-actual) bounding box
    /// of the occupied cells in cell coordinates, inclusive on both ends.
    /// False when the grid is empty. Every occupied cell lies inside the box;
    /// the box may cover unoccupied cells after removals (see the field
    /// comment) — callers may only use it to prove a region is EMPTY.
    /// </summary>
    public bool TryGetCellBounds(out ChunkCoord min, out ChunkCoord max)
    {
        min = _cellMin;
        max = _cellMax;
        return Count > 0;
    }

    /// <summary>Number of occupied cells.</summary>
    public int Count => _orderedChunks.Count;

    /// <summary>Looks up the chunk at <paramref name="coord"/>, if that cell is occupied.</summary>
    public bool TryGet(ChunkCoord coord, out WorldChunk chunk)
    {
        if (_overlay is not null && _overlay.TryGetValue(coord, out WorldChunk? patched))
        {
            chunk = patched!;
            return patched is not null;
        }
        if (_base.TryGetValue(coord, out WorldChunk? found))
        {
            chunk = found;
            return true;
        }
        chunk = null!;
        return false;
    }

    /// <summary>
    /// Every occupied chunk, sorted ascending by <see cref="ChunkCoord"/>
    /// (lexicographic X → Y → Z). This is the enumeration consumers must use
    /// whenever order matters — combining per-cell results into an ordered
    /// whole, or comparing two grids — because dictionary order is an
    /// implementation detail. Sorted once at build time, so reading it is free.
    /// </summary>
    public IReadOnlyList<WorldChunk> OrderedChunks => _orderedChunks;

    /// <summary>
    /// The brush's world AABB inflated by <see cref="WeldBand"/> — the box
    /// whose cell coverage defines the brush's residency footprint.
    /// </summary>
    public static Aabb InflatedBounds(in BrushPlacement placement) =>
        placement.WorldBounds.Expanded(WeldBand);

    /// <summary>
    /// The single cell that OWNS the placement (holds its render surfaces):
    /// the cell containing the center of the inflated world AABB. Always a
    /// member of <see cref="ComputeFootprint"/>'s result — the center of a box
    /// lies inside the box.
    /// </summary>
    public static ChunkCoord OwnerCell(in BrushPlacement placement) =>
        ChunkCoord.FromPosition(InflatedBounds(placement).Center);

    /// <summary>
    /// Every cell the placement is resident in: all cells its inflated world
    /// AABB touches. Sorted ascending by construction (X outer, Y middle, Z
    /// inner matches <see cref="ChunkCoord.CompareTo"/>), so footprints of
    /// equal placements are element-wise identical — the property the scene's
    /// dirty-cell diffing relies on.
    /// </summary>
    public static ChunkCoord[] ComputeFootprint(in BrushPlacement placement)
    {
        Aabb inflated = InflatedBounds(placement);
        ChunkCoord min = ChunkCoord.FromPosition(inflated.Min);
        ChunkCoord max = ChunkCoord.FromPosition(inflated.Max);

        var cells = new ChunkCoord[(max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1)];
        int i = 0;
        for (int x = min.X; x <= max.X; x++)
        {
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                    cells[i++] = new ChunkCoord(x, y, z);
            }
        }
        return cells;
    }

    /// <summary>
    /// Buckets every placement (and its carved surfaces) into the sparse grid:
    /// each placement becomes resident in every cell of its footprint and
    /// owned — surfaces included — by its owner cell. Placements are visited
    /// in index order, making every chunk's index lists ascending and the
    /// whole build a deterministic function of its inputs.
    /// </summary>
    internal static ChunkGrid Build(IReadOnlyList<BrushPlacement> placements, IReadOnlyList<Polygon[]> perBrushSurfaces)
    {
        var chunks = new Dictionary<ChunkCoord, WorldChunk>();

        for (int i = 0; i < placements.Count; i++)
        {
            BrushPlacement placement = placements[i];
            Aabb inflated = InflatedBounds(in placement);
            ChunkCoord owner = ChunkCoord.FromPosition(inflated.Center);
            ChunkCoord min = ChunkCoord.FromPosition(inflated.Min);
            ChunkCoord max = ChunkCoord.FromPosition(inflated.Max);

            for (int x = min.X; x <= max.X; x++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        var coord = new ChunkCoord(x, y, z);
                        if (!chunks.TryGetValue(coord, out WorldChunk? chunk))
                            chunks[coord] = chunk = new WorldChunk(coord);

                        chunk.AddResident(i);
                        if (coord == owner)
                            chunk.AddOwned(i, perBrushSurfaces[i]);
                    }
                }
            }
        }

        var ordered = new WorldChunk[chunks.Count];
        chunks.Values.CopyTo(ordered, 0);
        // Coord is unique per chunk, so this sort has no equal keys and its
        // instability cannot introduce nondeterminism.
        Array.Sort(ordered, static (a, b) => a.Coord.CompareTo(b.Coord));
        (ChunkCoord cellMin, ChunkCoord cellMax) = ComputeCellBounds(ordered);
        return new ChunkGrid(chunks, overlay: null, PagedArray<WorldChunk>.From(ordered), cellMin, cellMax);
    }

    // Exact cell-coordinate bounds of the occupied cells (full builds only —
    // patched grids grow their parent's box instead, see the field comment).
    private static (ChunkCoord Min, ChunkCoord Max) ComputeCellBounds(IReadOnlyList<WorldChunk> chunks)
    {
        if (chunks.Count == 0)
            return (default, default);

        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (WorldChunk chunk in chunks)
        {
            ChunkCoord c = chunk.Coord;
            if (c.X < minX) minX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.Z < minZ) minZ = c.Z;
            if (c.X > maxX) maxX = c.X;
            if (c.Y > maxY) maxY = c.Y;
            if (c.Z > maxZ) maxZ = c.Z;
        }
        return (new ChunkCoord(minX, minY, minZ), new ChunkCoord(maxX, maxY, maxZ));
    }

    /// <summary>
    /// Derives a grid from <paramref name="previous"/> with the given cells
    /// replaced, added, or (null chunk) removed — the incremental compile's
    /// grid construction, O(changes + cells-as-one-memcpy) instead of a full
    /// re-bucketing. <paramref name="changes"/> must be sorted ascending by
    /// coordinate with no duplicates; every non-removed entry must be a fresh
    /// <see cref="WorldChunk"/> (the previous grid's chunks stay live in the
    /// previous world and are never mutated). <paramref name="previous"/> is
    /// read-only here and remains fully valid.
    /// </summary>
    internal static ChunkGrid Patch(ChunkGrid previous, IReadOnlyList<(ChunkCoord Coord, WorldChunk? Chunk)> changes)
    {
        // Splice the ordered enumeration. The steady-state edit (chunks
        // replaced in place, no cell added or removed) derives by paged
        // copy-on-write — O(changed pages), nothing proportional to the cell
        // count. Cell insertions/removals re-pack via binary-searched block
        // copies — memcpy-cheap, and rare next to in-place edits.
        PagedArray<WorldChunk> prevOrdered = previous._orderedChunks;
        int sizeDelta = 0;
        bool replaceOnly = true;
        var replacements = new List<(int Index, WorldChunk Value)>(changes.Count);
        foreach ((ChunkCoord coord, WorldChunk? chunk) in changes)
        {
            int pos = LowerBound(prevOrdered, coord, 0);
            bool existed = pos < prevOrdered.Count && prevOrdered[pos].Coord == coord;
            sizeDelta += (chunk is not null ? 1 : 0) - (existed ? 1 : 0);
            if (existed && chunk is not null)
                replacements.Add((pos, chunk));
            else
                replaceOnly = false;
        }

        PagedArray<WorldChunk> ordered;
        if (replaceOnly)
        {
            ordered = prevOrdered.WithReplacements(replacements);
        }
        else
        {
            var packed = new WorldChunk[prevOrdered.Count + sizeDelta];
            int src = 0, dst = 0;
            foreach ((ChunkCoord coord, WorldChunk? chunk) in changes)
            {
                int pos = LowerBound(prevOrdered, coord, src);
                prevOrdered.CopyTo(src, packed, dst, pos - src);
                dst += pos - src;
                src = pos < prevOrdered.Count && prevOrdered[pos].Coord == coord ? pos + 1 : pos;
                if (chunk is not null)
                    packed[dst++] = chunk;
            }
            prevOrdered.CopyTo(src, packed, dst, prevOrdered.Count - src);
            ordered = PagedArray<WorldChunk>.From(packed);
        }

        // Cell bounds: grow the parent's box over the added cells. A previously
        // empty grid (Count == 0) contributes no box, so the first added cell
        // seeds it. Removals deliberately never shrink it (see the field
        // comment on _cellMin).
        ChunkCoord cellMin = previous._cellMin;
        ChunkCoord cellMax = previous._cellMax;
        bool hasBounds = previous.Count > 0;
        foreach ((ChunkCoord coord, WorldChunk? chunk) in changes)
        {
            if (chunk is null)
                continue;
            if (!hasBounds)
            {
                cellMin = cellMax = coord;
                hasBounds = true;
                continue;
            }
            cellMin = new ChunkCoord(
                Math.Min(cellMin.X, coord.X), Math.Min(cellMin.Y, coord.Y), Math.Min(cellMin.Z, coord.Z));
            cellMax = new ChunkCoord(
                Math.Max(cellMax.X, coord.X), Math.Max(cellMax.Y, coord.Y), Math.Max(cellMax.Z, coord.Z));
        }

        // Layered lookup: clone the (small) parent overlay, apply the changes,
        // compact into a flat dictionary once the overlay stops being small —
        // the one amortized O(cells) step, paid every ~base/8 edits, which
        // also caps the lookup cost at exactly two probes forever.
        Dictionary<ChunkCoord, WorldChunk?> overlay = previous._overlay is not null
            ? new Dictionary<ChunkCoord, WorldChunk?>(previous._overlay)
            : [];
        foreach ((ChunkCoord coord, WorldChunk? chunk) in changes)
            overlay[coord] = chunk;

        if (overlay.Count > Math.Max(64, previous._base.Count / 8))
        {
            var flat = new Dictionary<ChunkCoord, WorldChunk>(ordered.Count);
            foreach (WorldChunk chunk in ordered)
                flat.Add(chunk.Coord, chunk);
            // Compaction visits every cell anyway, so take the opportunity to
            // re-tighten the conservative box for free.
            (ChunkCoord exactMin, ChunkCoord exactMax) = ComputeCellBounds(ordered);
            return new ChunkGrid(flat, overlay: null, ordered, exactMin, exactMax);
        }

        return new ChunkGrid(previous._base, overlay, ordered, cellMin, cellMax);
    }

    // First index in [from, count) whose coordinate is >= coord.
    private static int LowerBound(PagedArray<WorldChunk> ordered, ChunkCoord coord, int from)
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

    /// <summary>
    /// Attaches each placement's snapped+welded surfaces to its owner cell,
    /// in placement order — the same visitation order the owned carve buckets
    /// were filled in, so <see cref="WorldChunk.WeldedSurfaces"/> and
    /// <see cref="WorldChunk.Surfaces"/> stay per-brush aligned. Called once
    /// by the world assembly after the per-cell weld; the chunks are immutable
    /// from then on.
    /// </summary>
    internal void AttachWeldedSurfaces(IReadOnlyList<BrushPlacement> placements, Polygon[][] weldedPerBrush)
    {
        for (int i = 0; i < placements.Count; i++)
        {
            BrushPlacement placement = placements[i];
            // The owner cell is always occupied — the grid build created a
            // chunk for every footprint cell of every placement.
            if (TryGet(OwnerCell(in placement), out WorldChunk chunk))
                chunk.AddWeldedSurfaces(weldedPerBrush[i]);
        }
    }
}
