using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// One GPU-resident, single-material piece of a static-world chunk: the
/// compile's per-material geometry (<see cref="Source"/>), the GPU mesh the
/// render thread created from it, and the material that geometry's
/// <see cref="ChunkSubmesh.Material"/> id resolved to at swap time.
/// </summary>
/// <param name="Source">The compile-produced per-material arrays (immutable).</param>
/// <param name="Mesh">The GPU mesh uploaded from them; destroyed when its chunk is replaced or removed.</param>
/// <param name="Material">
/// The resolved material, or null when nothing could be resolved (no asset
/// manager and no <see cref="Scene.StaticWorldMaterial"/> fallback) — a null
/// material makes the pipelines skip the draw rather than guess.
/// </param>
/// <remarks>
/// Resolution happens exactly once per upload, on the render thread, and the
/// result is cached here for the life of the entry: the per-frame draw-list
/// build must stay allocation- and lookup-free, so it may only copy this
/// reference into its render items.
/// </remarks>
public readonly record struct StaticWorldSubmesh(ChunkSubmesh Source, Mesh Mesh, Material? Material);

/// <summary>
/// One GPU-resident chunk of a scene's compiled static world: the CPU-side
/// per-cell artifact the compile produced (<see cref="Artifact"/> — coords,
/// per-material geometry, render AABB) paired with the GPU submeshes the render
/// thread created from it, one per material the cell wears. Owned by
/// <see cref="Scene"/> in its per-chunk mesh map; the artifact reference doubles
/// as the swap path's change detector — a landing compile that carries the same
/// artifact instance for a cell keeps this entry (every GPU mesh included)
/// untouched.
/// </summary>
/// <param name="Artifact">The compile-produced per-cell mesh data (immutable).</param>
/// <param name="Submeshes">
/// The chunk's GPU pieces, index-aligned with <see cref="ChunkMesh.Submeshes"/>
/// and therefore in ascending material id. Never null; empty only for the
/// degenerate cell whose artifact has no drawable geometry.
/// </param>
public readonly record struct StaticWorldChunkMesh(ChunkMesh Artifact, StaticWorldSubmesh[] Submeshes);
