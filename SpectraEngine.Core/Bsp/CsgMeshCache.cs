using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Reused/built counters for one compile's per-cell mesh stage:
/// <see cref="Reused"/> cells kept their previous <see cref="ChunkMesh"/>
/// verbatim, <see cref="Built"/> had their vertex/index arrays rebuilt from
/// their current owned surfaces. Only cells that own render geometry are
/// counted (see <see cref="ChunkMesh"/>). A first compile (no previous mesh
/// cache) reports all built.
/// </summary>
public readonly record struct CsgMeshStats(int Reused, int Built)
{
    /// <summary>Total geometry-owning cells the mesh stage processed (reused + built).</summary>
    public int Total => Reused + Built;
}

/// <summary>
/// Per-cell render meshes from one static-world compile, consumable by the
/// next: each geometry-owning cell's coordinate maps to the
/// <see cref="ChunkMesh"/> the per-cell mesh stage built for it, together with
/// the snapped+welded surface arrays of every brush the cell OWNED — the
/// mesh's complete input. When a later compile reproduces the cell with the
/// exact same owned welded arrays (by reference), it reuses the artifact
/// without rebuilding — the mesh-stage analogue of <see cref="CsgBspCache"/>,
/// and the piece that keeps a one-brush edit from re-meshing (and, downstream,
/// re-uploading) the whole world.
/// </summary>
/// <remarks>
/// <b>Why reference identity is the right key.</b> Exactly the
/// <see cref="CsgBspCache"/> argument: a brush's welded surface array is
/// reference-stable across compiles only through a <see cref="CsgWeldCache"/>
/// hit — which itself requires a <see cref="CsgCompileCache"/> hit — so one
/// reference comparison inherits, transitively, the whole equality contract of
/// both earlier caches: same brush, same placement matrix, same carver
/// sequence, same weld candidates, and therefore bitwise-identical welded
/// polygons. A cell's mesh is a pure function of its owned welded arrays
/// concatenated in ascending placement-index order, so identical references in
/// identical relative order mean an identical input list, and the previous
/// artifact is exactly what a rebuild would produce.
///
/// <para>
/// <b>The equality contract</b> (a reuse requires ALL of):
/// <list type="bullet">
/// <item><description><b>Same cell:</b> entries are keyed by
/// <see cref="ChunkCoord"/> — a value key, so it cannot go stale.</description></item>
/// <item><description><b>Owner identity:</b> the same number of owned brushes
/// and, element-wise in ascending placement-index order, the same welded
/// surface array reference for each. Placement-index SHIFTS between compiles
/// are harmless (relative order is compared, not indices); any brush entering
/// or leaving the cell's ownership, moving, re-carving, or re-welding changes
/// the list's length or an element and forces a rebuild.</description></item>
/// </list>
/// As with the earlier caches, every ambiguity resolves toward a miss: a false
/// miss is wasted work, a false hit would be silently stale render geometry.
/// </para>
/// <para>
/// <b>Ownership and threading:</b> immutable after construction; a compile
/// consumes a previous cache read-only and produces a fresh one rebuilt from
/// the current cells only (entries whose cell lost its geometry are pruned by
/// construction). Like the other caches it retains the previous compile's
/// artifacts for as long as it lives — the memory price of making the next
/// edit cheap.
/// </para>
/// </remarks>
public sealed class CsgMeshCache
{
    private readonly Dictionary<ChunkCoord, Entry> _entries;

    private CsgMeshCache(Dictionary<ChunkCoord, Entry> entries) => _entries = entries;

    /// <summary>Number of cells with a cached mesh artifact.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Looks up the cached mesh for the cell at <paramref name="coord"/>. The
    /// caller must still validate the owner-identity half of the equality
    /// contract via <see cref="OwnersMatch"/> before reusing the entry.
    /// </summary>
    internal bool TryGet(ChunkCoord coord, [NotNullWhen(true)] out Entry? entry) =>
        _entries.TryGetValue(coord, out entry);

    /// <summary>
    /// The owner-identity half of the equality contract (see the class
    /// remarks): element-wise reference equality between the entry's recorded
    /// owned welded arrays and the cell's current ones, in the shared
    /// ascending placement-index order.
    /// </summary>
    internal static bool OwnersMatch(Entry entry, WorldChunk chunk, Polygon[][] weldedPerBrush)
    {
        Polygon[][] recorded = entry.OwnedWelded;
        IReadOnlyList<int> owned = chunk.OwnedBrushIndices;
        if (recorded.Length != owned.Count)
            return false;

        for (int k = 0; k < recorded.Length; k++)
        {
            if (!ReferenceEquals(recorded[k], weldedPerBrush[owned[k]]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Builds the cache one compile produces: one entry per geometry-owning
    /// cell, keyed by the cell's coordinate. Entries are exactly the current
    /// compile's outputs, so cells that lost their geometry since the previous
    /// compile are pruned by construction.
    /// </summary>
    internal static CsgMeshCache FromEntries(IReadOnlyList<WorldChunk> geometryCells, Entry[] entries)
    {
        var map = new Dictionary<ChunkCoord, Entry>(entries.Length);
        for (int c = 0; c < entries.Length; c++)
            map.Add(geometryCells[c].Coord, entries[c]);
        return new CsgMeshCache(map);
    }

    /// <summary>
    /// The cached mesh of one cell: the welded surface arrays of every brush
    /// the cell owned when the mesh was built (in ascending placement-index
    /// order — the order <see cref="OwnersMatch"/> compares in, and the order
    /// the mesh's input list was concatenated in) and the artifact itself.
    /// Immutable after construction; the artifact is shared verbatim between
    /// the cache and every compile result that reused it (safe —
    /// <see cref="ChunkMesh"/> and <see cref="Polygon"/> are immutable).
    /// </summary>
    internal sealed class Entry(Polygon[][] ownedWelded, ChunkMesh mesh)
    {
        public readonly Polygon[][] OwnedWelded = ownedWelded;
        public readonly ChunkMesh Mesh = mesh;
    }
}
