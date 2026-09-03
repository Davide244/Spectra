using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// One cell of an adopted compiled world: where it is, the box culling tests it
/// against, and the flat solid-leaf tree queries walk.
/// </summary>
/// <param name="Coord">The cell.</param>
/// <param name="RenderBounds">The cell's true render bounds, straight out of <c>CHDR</c>.</param>
/// <param name="Bsp">
/// The cell's tree, read over the map's own bytes, or null for a cell the bake
/// gave no tree. A cell with geometry and no tree is legal: solid and empty are
/// different answers and a cell that was carved away to nothing has neither.
/// </param>
/// <param name="TriangleCount">Triangles the cell's GPU submeshes were uploaded with.</param>
public readonly record struct CompiledStaticWorldChunk(
    ChunkCoord Coord,
    Aabb RenderBounds,
    FlatBspTree? Bsp,
    int TriangleCount);

/// <summary>
/// A static world that ARRIVED baked: per-cell geometry already on the GPU and
/// per-cell BSP trees read straight off the compiled map, with no carve anywhere
/// in its history.
/// </summary>
/// <remarks>
/// <para><b>This is the runtime half of the double-geometry guard.</b> A scene
/// holding one has chunks that already contain every world brush's surfaces, so a
/// live carve on top of them draws every wall twice - and the symptom is
/// z-fighting, which every graphics programmer's instinct attributes to depth
/// precision or a pipeline state bug rather than to a map loader. While this
/// object is installed, <c>Scene.RebuildStaticWorld</c> and every automatic
/// dirty mark refuse and say so.</para>
/// <para><b>It holds the map's <c>ContentBlob</c> for its whole life, and that
/// blob holds the pack.</b> The BSP nodes are a window into a memory-mapped view;
/// unmapping a view under a live span is an access violation with no managed
/// stack, so the reference that keeps the mapping alive has to travel with the
/// thing that reads it. The GPU meshes need no such protection - their bytes were
/// copied into buffers at load - which is exactly why the blob is released by
/// <see cref="Dispose"/> and not before.</para>
/// <para><b>It is deliberately NOT a <c>CsgWorld</c>.</b> A <c>CsgWorld</c> is
/// the output of a compile and carries its placements, its per-brush surface
/// arrays and its four incremental caches, none of which a baked map has or wants;
/// a subclass or an adapter would have to fabricate them, and the first consumer
/// to read one would get an empty list where the truthful answer is "this world
/// was never compiled in this process". The consequences are named rather than
/// papered over: see the remarks on <see cref="ContainsPoint"/>.</para>
/// </remarks>
public sealed class CompiledStaticWorld : IDisposable
{
    private readonly CompiledStaticWorldChunk[] _chunks;
    private ContentBlob? _file;

    /// <param name="source">What to call this map in a message.</param>
    /// <param name="chunks">
    /// The cells, in ascending <see cref="ChunkCoord.CompareTo"/> order - which is
    /// the order <c>CHDR</c> is written and validated in, so the binary search
    /// below is over the file's own sort rather than over a second one.
    /// </param>
    /// <param name="file">The map's bytes, whose lifetime this object now owns.</param>
    public CompiledStaticWorld(string source, CompiledStaticWorldChunk[] chunks, ContentBlob? file)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunks);

        Source = source;
        _chunks = chunks;
        _file = file;

        int triangles = 0;
        for (int i = 0; i < chunks.Length; i++) triangles += chunks[i].TriangleCount;
        TriangleCount = triangles;
    }

    /// <summary>What to call this map in a message: a logical asset path.</summary>
    public string Source { get; }

    /// <summary>The cells, in ascending cell order.</summary>
    public IReadOnlyList<CompiledStaticWorldChunk> Chunks => _chunks;

    /// <summary>Triangles across every cell, as uploaded.</summary>
    /// <remarks>
    /// <b>The number the double-geometry guard is graded on.</b> A loader that
    /// re-carved would leave the scene drawing twice this many, which nothing else
    /// in a running frame reports.
    /// </remarks>
    public int TriangleCount { get; }

    /// <summary>Cells that carry a queryable tree.</summary>
    public int BspChunkCount
    {
        get
        {
            int trees = 0;
            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunks[i].Bsp is not null) trees++;
            }

            return trees;
        }
    }

    /// <summary>
    /// True when <paramref name="point"/> lies inside the baked solid.
    /// </summary>
    /// <remarks>
    /// <para>Routed exactly as <c>CsgWorld.ContainsPoint</c> routes: the cell
    /// containing the point, then that cell's tree. The answer is identical
    /// because the tree is identical - the bake flattened the compile's own trees
    /// and this walks the flattened form, calling the same
    /// <c>Plane.DotCoordinate</c> on the same values.</para>
    /// <para><b>SCOPE, and the gap it names.</b> This answers about the compiled
    /// authored static world and nothing else - not part brushes, not mesh nodes -
    /// the same scope <c>CsgWorld.ContainsPoint</c> has. What a compiled world
    /// does NOT offer is the placement list, so
    /// <c>BrushPlaneCollisionSource</c> finds no plane sets and the character
    /// mover has nothing to walk on. That is a named gap of this stage rather than
    /// a defect here: cooked collision is the <c>COLL</c> section's job, and until
    /// it exists a compiled map is a level you can look at and not one you can
    /// walk in.</para>
    /// </remarks>
    public bool ContainsPoint(Vector3 point) =>
        TryGetChunk(ChunkCoord.FromPosition(point), out CompiledStaticWorldChunk chunk)
        && chunk.Bsp is { } tree
        && tree.ContainsPoint(point);

    /// <summary>Finds the cell at <paramref name="coord"/>, if this map has one.</summary>
    public bool TryGetChunk(ChunkCoord coord, out CompiledStaticWorldChunk chunk)
    {
        int lo = 0, hi = _chunks.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int order = _chunks[mid].Coord.CompareTo(coord);
            if (order == 0)
            {
                chunk = _chunks[mid];
                return true;
            }

            if (order < 0) lo = mid + 1;
            else hi = mid - 1;
        }

        chunk = default;
        return false;
    }

    /// <summary>
    /// Releases the map's bytes. Idempotent.
    /// </summary>
    /// <remarks>
    /// <b>Every tree in this world stops working here, deliberately.</b> The nodes
    /// are a window into the released blob, so a query afterwards throws
    /// <see cref="ObjectDisposedException"/> naming the blob rather than reading
    /// address space the pack no longer owns. Call it when the world leaves the
    /// scene, never while it is installed.
    /// </remarks>
    public void Dispose()
    {
        ContentBlob? file = _file;
        _file = null;
        file?.Dispose();
    }
}
