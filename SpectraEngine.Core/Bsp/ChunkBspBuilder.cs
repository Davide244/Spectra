using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// The per-cell BSP stage of a static-world compile: every occupied cell gets
/// its own solid-leaf <see cref="BspTree"/>, built from the snapped+welded
/// surfaces of ALL brushes RESIDENT in the cell, and cells whose resident
/// inputs are unchanged since the previous compile reuse their tree verbatim
/// (via <see cref="CsgBspCache"/>). Queries route to a single cell's tree
/// (<see cref="CsgWorld.ContainsPoint"/> / <see cref="CsgWorld.Raycast"/>);
/// the routed answers must match a monolithic
/// <see cref="BspTree.BuildFromSurfaces"/> over the full welded surface list —
/// that equivalence is this stage's contract, argued below and pinned by the
/// oracle tests.
/// </summary>
/// <remarks>
/// <b>THE CLOSURE ARGUMENT</b> — why a cell's tree answers correctly for every
/// point in (and within <see cref="ChunkGrid.WeldBand"/> of) the cell, even
/// though it sees only a subset of the world's surfaces:
/// <list type="number">
/// <item><description>Each resident brush contributes its COMPLETE welded
/// surface set — never a clipped-to-the-cell subset — so every boundary plane
/// of every brush whose geometry reaches the cell is present with correct
/// sidedness, including the faces of brushes whose volume extends far across
/// the cell border.</description></item>
/// <item><description>Solidity inside the cell comes only from residents: a
/// brush containing a point of the cell has its world AABB (and a fortiori
/// its WeldBand-inflated AABB) touching the cell, so it is resident by
/// construction. Non-resident brushes cannot make any point of the cell
/// solid, and omitting their surfaces cannot delete a boundary the cell can
/// see.</description></item>
/// <item><description>The only difference between the cell's surface set and
/// the global one NEAR the cell is nothing at all: a carve can remove a
/// resident brush's fragment only where that fragment lies inside some carver
/// brush, and any carver reaching within WeldBand of the cell is itself
/// resident, its own faces closing the union there. So within the cell plus
/// its weld band the resident set IS the global carved surface set — the
/// union boundary is exactly as closed as the monolithic build's. Holes (a
/// resident's fragments eaten by a non-resident carver) exist only beyond the
/// band, where they can mislabel only regions no routed query ever asks this
/// cell about: point queries route by cell, and ray queries clamp each cell's
/// segment to its traversal interval.</description></item>
/// </list>
/// Note the resident sets make one brush's surfaces appear in several cells'
/// trees — intentional and correct for queries (each cell must see every
/// boundary it can reach); render ownership stays owner-only, so nothing is
/// drawn or meshed twice.
///
/// <para>
/// <b>Incrementality.</b> Reuse is validation-driven, not dirty-set-driven,
/// exactly like the weld stage: a cell rebuilds precisely when some resident's
/// welded surface array is not reference-identical to the previous compile's
/// (see <see cref="CsgBspCache"/>). This is a superset of the dirty cells —
/// and makes stale reuse impossible by construction instead of by dirty-diff
/// reasoning. The per-cell mesh stage (W4, see <see cref="ChunkMeshBuilder"/>)
/// follows the same discipline; the recorded dirty-cell set is observability
/// data only.
/// </para>
/// </remarks>
internal static class ChunkBspBuilder
{
    /// <summary>
    /// Builds (or reuses) every occupied cell's BSP tree and attaches it to
    /// the cell (<see cref="WorldChunk.Bsp"/>). Pure CPU work over immutable
    /// inputs — background-thread safe; the chunks are exclusively owned by
    /// the running compile until its world is published. Fresh cells build in
    /// parallel, which cannot affect the result: each tree is a pure function
    /// of its own cell's input list, and <see cref="BspTree.BuildFromSurfaces"/>
    /// is itself scheduling-independent.
    /// </summary>
    /// <param name="produceCache">
    /// Whether to assemble <paramref name="nextCache"/> for the next compile
    /// (the caching build overloads); tree reuse only ever happens through a
    /// weld-cache hit chain, so a cache-free compile has nothing to gain from
    /// producing one.
    /// </param>
    internal static void Build(
        ChunkGrid chunks,
        Polygon[][] weldedPerBrush,
        CsgBspCache? previousCache,
        bool produceCache,
        out CsgBspCache? nextCache,
        out CsgBspStats stats)
    {
        IReadOnlyList<WorldChunk> cells = chunks.OrderedChunks;
        CsgBspCache.Entry[]? entries = produceCache ? new CsgBspCache.Entry[cells.Count] : null;

        // Reuse pass: cells whose resident welded arrays all validate keep
        // their previous tree (and carry their entry forward unchanged); the
        // rest are collected for the fresh builds below.
        var needsBuild = new List<int>();
        int reused = 0;
        for (int c = 0; c < cells.Count; c++)
        {
            WorldChunk chunk = cells[c];
            if (previousCache is not null &&
                previousCache.TryGet(chunk.Coord, out CsgBspCache.Entry? cached) &&
                CsgBspCache.ResidentsMatch(cached, chunk, weldedPerBrush))
            {
                chunk.AttachBsp(cached.Tree);
                if (entries is not null)
                    entries[c] = cached;
                reused++;
                continue;
            }
            needsBuild.Add(c);
        }

        // Fresh builds. Each cell writes only its own chunk and entry slot, so
        // the parallel loop shares no mutable state; a single cell (the common
        // one-brush-edit case) skips the parallel machinery entirely.
        if (needsBuild.Count == 1)
        {
            BuildCell(needsBuild[0]);
        }
        else if (needsBuild.Count > 1)
        {
            Parallel.For(0, needsBuild.Count, i => BuildCell(needsBuild[i]));
        }

        stats = new CsgBspStats(reused, needsBuild.Count);
        nextCache = entries is not null ? CsgBspCache.FromEntries(cells, entries) : null;
        return;

        void BuildCell(int c)
        {
            WorldChunk chunk = cells[c];
            BspTree tree = BuildCellTree(chunk, i => weldedPerBrush[i], out Polygon[][] residentWelded);
            chunk.AttachBsp(tree);
            if (entries is not null)
                entries[c] = new CsgBspCache.Entry(residentWelded, tree);
        }
    }

    /// <summary>
    /// Builds one cell's solid-leaf tree from its residents' welded surfaces —
    /// the per-cell core of the BSP stage, factored out so the incremental
    /// compile rebuilds an edit's neighbourhood cells through the identical
    /// code path (same input order, bit-identical tree for identical input).
    /// The tree input is every resident's COMPLETE welded surface set,
    /// concatenated in ascending placement-index order — the same order the
    /// flat <see cref="CsgWorld.Surfaces"/> list uses, so a single-cell
    /// world's tree is bit-identical to the monolithic tree.
    /// <paramref name="residentWelded"/> reports the input arrays in that
    /// order — the validation record a cache entry stores.
    /// </summary>
    internal static BspTree BuildCellTree(
        WorldChunk chunk, Func<int, Polygon[]> weldedOf, out Polygon[][] residentWelded)
    {
        IReadOnlyList<int> residents = chunk.ResidentBrushIndices;

        residentWelded = new Polygon[residents.Count][];
        int total = 0;
        for (int k = 0; k < residentWelded.Length; k++)
        {
            residentWelded[k] = weldedOf(residents[k]);
            total += residentWelded[k].Length;
        }

        var input = new Polygon[total];
        int offset = 0;
        foreach (Polygon[] surfaces in residentWelded)
        {
            surfaces.CopyTo(input, offset);
            offset += surfaces.Length;
        }

        return BspTree.BuildFromSurfaces(input);
    }
}
