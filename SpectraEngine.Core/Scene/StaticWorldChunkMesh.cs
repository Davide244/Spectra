using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// One GPU-resident, single-material piece of a static-world chunk: the material
/// id its geometry carries (<see cref="SourceMaterial"/>), the GPU mesh the
/// render thread created from it, and the material that id resolved to at swap
/// time.
/// </summary>
/// <param name="SourceMaterial">
/// The interned material reference every triangle here wears, kept so the entry
/// can be re-resolved without its geometry. The ID rather than the
/// <see cref="ChunkSubmesh"/> it came from, because a chunk adopted from a
/// compiled map has no CPU-side arrays at all: its vertices are a span into a
/// memory-mapped file that went straight to the GPU.
/// </param>
/// <param name="Mesh">The GPU mesh uploaded from them; destroyed when its chunk is replaced or removed.</param>
/// <param name="Material">
/// The resolved material, or null when nothing could be resolved (no asset
/// manager and no <see cref="Scene.StaticWorldMaterial"/> fallback) - a null
/// material makes the pipelines skip the draw rather than guess.
/// </param>
/// <remarks>
/// Resolution happens exactly once per upload, on the render thread, and the
/// result is cached here for the life of the entry: the per-frame draw-list
/// build must stay allocation- and lookup-free, so it may only copy this
/// reference into its render items.
/// </remarks>
public readonly record struct StaticWorldSubmesh(MaterialRef SourceMaterial, Mesh Mesh, Material? Material);

/// <summary>
/// One GPU-resident chunk of a scene's static world: where the cell is, the box
/// culling tests it against, the GPU submeshes the render thread created for it
/// (one per material the cell wears), and, for a cell that came from a live
/// compile, the CPU artifact it was uploaded from.
/// </summary>
/// <param name="Coord">The cell this entry belongs to.</param>
/// <param name="RenderBounds">
/// The box frustum culling tests, and deliberately NOT
/// <see cref="ChunkCoord.Bounds"/>: a border-spanning brush is owned by exactly
/// one cell and its surfaces routinely overhang, so culling by cell bounds makes
/// the overhang vanish while it is plainly visible.
/// </param>
/// <param name="Artifact">
/// The compile-produced per-cell mesh data, or NULL for a chunk adopted from a
/// compiled map.
/// <para>
/// <b>It is here as the swap path's change detector, not as data anything
/// draws.</b> A landing compile that carries the same artifact instance for a
/// cell keeps this entry, GPU meshes included, untouched. A compiled map is never
/// recompiled - the whole point of one is that no carve runs - so an adopted
/// chunk has no artifact to detect a change against and says so with a null
/// rather than with a fabricated one holding empty arrays, which would report
/// "this cell draws nothing" to everything that reads it.
/// </para>
/// </param>
/// <param name="Submeshes">
/// The chunk's GPU pieces, one per material the cell wears. Never null; empty
/// only for the degenerate cell whose artifact has no drawable geometry.
/// </param>
/// <remarks>
/// <b><see cref="Coord"/> and <see cref="RenderBounds"/> are on the ENTRY rather
/// than read off the artifact.</b> Culling, the cluster boxes, the ordered list's
/// binary search and the per-cell map key are all properties of a GPU-resident
/// chunk and not of the compile that happened to produce one, and reading them
/// through a nullable artifact would make every one of those sites either
/// null-check or crash on the adopted path.
/// </remarks>
public readonly record struct StaticWorldChunkMesh(
    ChunkCoord Coord,
    Aabb RenderBounds,
    ChunkMesh? Artifact,
    StaticWorldSubmesh[] Submeshes)
{
    /// <summary>Builds an entry for a cell a live compile produced.</summary>
    public StaticWorldChunkMesh(ChunkMesh artifact, StaticWorldSubmesh[] submeshes)
        : this(artifact.Coord, artifact.RenderBounds, artifact, submeshes)
    {
    }
}
