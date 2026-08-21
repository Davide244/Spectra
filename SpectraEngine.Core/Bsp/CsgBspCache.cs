using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Reused/built counters for one compile's per-cell BSP stage:
/// <see cref="Reused"/> cells kept their previous <see cref="BspTree"/>
/// verbatim, <see cref="Built"/> had their tree rebuilt from their current
/// resident surfaces. A first compile (no previous BSP cache) reports all
/// built.
/// </summary>
public readonly record struct CsgBspStats(int Reused, int Built)
{
    /// <summary>Total occupied cells the BSP stage processed (reused + built).</summary>
    public int Total => Reused + Built;
}

/// <summary>
/// Per-cell BSP trees from one static-world compile, consumable by the next:
/// each occupied cell's coordinate maps to the tree the per-cell BSP stage
/// built for it, together with the snapped+welded surface arrays of every
/// brush that was RESIDENT in the cell — the tree's complete input. When a
/// later compile reproduces the cell with the exact same resident welded
/// arrays (by reference), it reuses the tree without rebuilding — the
/// BSP-stage analogue of <see cref="CsgWeldCache"/>, and the piece that keeps
/// a one-brush edit from re-partitioning the whole world.
/// </summary>
/// <remarks>
/// <b>Why reference identity is the right key.</b> A brush's welded surface
/// array is reference-stable across compiles only through a
/// <see cref="CsgWeldCache"/> hit — which itself requires a
/// <see cref="CsgCompileCache"/> hit — so one reference comparison inherits,
/// transitively, the whole equality contract of both earlier caches: same
/// brush, same placement matrix, same carver sequence, same weld candidates,
/// and therefore bitwise-identical welded polygons. A cell's tree is a pure
/// function of its residents' welded arrays concatenated in ascending
/// placement-index order, so identical references in identical relative order
/// mean an identical input list, and the previous tree is exactly what a
/// rebuild would produce (<see cref="BspTree.BuildFromSurfaces"/> is
/// deterministic).
///
/// <para>
/// <b>The equality contract</b> (a reuse requires ALL of):
/// <list type="bullet">
/// <item><description><b>Same cell:</b> entries are keyed by
/// <see cref="ChunkCoord"/> — a value key, so it cannot go stale.</description></item>
/// <item><description><b>Resident identity:</b> the same number of resident
/// brushes and, element-wise in ascending placement-index order, the same
/// welded surface array reference for each. Placement-index SHIFTS between
/// compiles are harmless (relative order is compared, not indices); any brush
/// entering or leaving the cell, moving, re-carving, or re-welding changes
/// the list's length or an element and forces a rebuild.</description></item>
/// </list>
/// As with the carve and weld caches, every ambiguity resolves toward a miss:
/// a false miss is wasted work, a false hit would be silently stale queries.
/// </para>
/// <para>
/// <b>Ownership and threading:</b> immutable after construction; a compile
/// consumes a previous cache read-only and produces a fresh one rebuilt from
/// the current cells only (entries whose cell emptied out are pruned by
/// construction). Like the other caches it retains the previous compile's
/// trees for as long as it lives — the memory price of making the next edit
/// cheap.
/// </para>
/// </remarks>
public sealed class CsgBspCache
{
    private readonly Dictionary<ChunkCoord, Entry> _entries;

    private CsgBspCache(Dictionary<ChunkCoord, Entry> entries) => _entries = entries;

    /// <summary>Number of cells with a cached tree.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Looks up the cached tree for the cell at <paramref name="coord"/>. The
    /// caller must still validate the resident-identity half of the equality
    /// contract via <see cref="ResidentsMatch"/> before reusing the entry.
    /// </summary>
    internal bool TryGet(ChunkCoord coord, [NotNullWhen(true)] out Entry? entry) =>
        _entries.TryGetValue(coord, out entry);

    /// <summary>
    /// The resident-identity half of the equality contract (see the class
    /// remarks): element-wise reference equality between the entry's recorded
    /// resident welded arrays and the cell's current ones, in the shared
    /// ascending placement-index order.
    /// </summary>
    internal static bool ResidentsMatch(Entry entry, WorldChunk chunk, Polygon[][] weldedPerBrush)
    {
        Polygon[][] recorded = entry.ResidentWelded;
        IReadOnlyList<int> residents = chunk.ResidentBrushIndices;
        if (recorded.Length != residents.Count)
            return false;

        for (int k = 0; k < recorded.Length; k++)
        {
            if (!ReferenceEquals(recorded[k], weldedPerBrush[residents[k]]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Builds the cache one compile produces: one entry per occupied cell,
    /// keyed by the cell's coordinate. Entries are exactly the current
    /// compile's outputs, so cells that emptied out since the previous compile
    /// are pruned by construction.
    /// </summary>
    internal static CsgBspCache FromEntries(IReadOnlyList<WorldChunk> orderedChunks, Entry[] entries)
    {
        var map = new Dictionary<ChunkCoord, Entry>(entries.Length);
        for (int c = 0; c < entries.Length; c++)
            map.Add(orderedChunks[c].Coord, entries[c]);
        return new CsgBspCache(map);
    }

    /// <summary>
    /// The cached tree of one cell: the welded surface arrays of every brush
    /// resident in the cell when the tree was built (in ascending
    /// placement-index order — the order <see cref="ResidentsMatch"/> compares
    /// in, and the order the tree's input list was concatenated in) and the
    /// tree itself. Immutable after construction; the tree is shared verbatim
    /// between the cache and every compile result that reused it (safe —
    /// <see cref="BspTree"/> and <see cref="Polygon"/> are immutable).
    /// </summary>
    internal sealed class Entry(Polygon[][] residentWelded, BspTree tree)
    {
        public readonly Polygon[][] ResidentWelded = residentWelded;
        public readonly BspTree Tree = tree;
    }
}
