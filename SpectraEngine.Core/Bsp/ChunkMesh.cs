using System.Collections.Generic;
using SpectraEngine.Core.Assets;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A contiguous slice of a mesh's index array that wears one material.
/// </summary>
/// <param name="Material">The interned material reference the slice's triangles carry.</param>
/// <param name="IndexStart">First index of the slice.</param>
/// <param name="IndexCount">Number of indices in the slice (always a multiple of 3).</param>
/// <remarks>
/// The attribution of the MONOLITHIC <see cref="CsgWorld.BuildMesh"/> — runs
/// partition its index array in emission order and never reorder it, which is
/// what keeps that oracle's arrays bit-identical to what they were before
/// materials existed. The render path does not use runs: the per-cell artifacts
/// carry real per-material geometry instead (<see cref="ChunkSubmesh"/>), so the
/// render thread never has to slice an index array at upload time.
/// </remarks>
public readonly record struct MaterialRun(MaterialRef Material, int IndexStart, int IndexCount);

/// <summary>
/// The geometry of one chunk cell that wears a single material: its own
/// position+normal+uv vertex array and index array (the engine's standard
/// 8-float layout, see <c>VertexAttribute.StandardLayout</c>), self-contained
/// and zero-based, ready to become one GPU mesh and one draw call.
/// </summary>
/// <param name="Material">
/// The interned material reference every triangle here carries, resolved to a
/// real <see cref="Graphics.Material"/> on the render thread at upload time via
/// <see cref="AssetManager.ResolveMaterial"/> — never during the compile.
/// </param>
/// <param name="Vertices">Interleaved vertex data, 8 floats per vertex. Treat as immutable.</param>
/// <param name="Indices">
/// Index data, three entries per triangle, based at this submesh's own first
/// vertex (not at the cell's). Never empty — a material group that emits no
/// triangle produces no submesh at all. Treat as immutable.
/// </param>
public readonly record struct ChunkSubmesh(MaterialRef Material, float[] Vertices, uint[] Indices);

/// <summary>
/// The render mesh of one occupied chunk cell, split per material: one
/// <see cref="ChunkSubmesh"/> per distinct face material among the cell's OWNED
/// snapped+welded surfaces, plus the AABB culling must test the cell against.
/// Produced per cell by <see cref="ChunkMeshBuilder"/> during a static-world
/// compile; the render thread uploads each submesh to the GPU when the compile
/// lands, and reference identity of the artifact is the swap path's "this cell
/// is unchanged" signal — a reused artifact means the existing GPU meshes keep
/// serving.
/// </summary>
/// <remarks>
/// <b>Why per-material ARRAYS and not per-material index ranges.</b> A cell
/// draws one material per draw call either way; the question is only whether
/// the split lives in the geometry or in the draw arguments. Splitting the
/// arrays keeps every consumer downstream of the compile unchanged: the GPU
/// swap stays a per-entry create/destroy with the existing create-before-destroy
/// rollback (just keyed by cell AND material now), a render item stays
/// "one mesh, one material, one matrix", and all six backend pipelines keep
/// calling <c>mesh.Draw()</c> — no range-draw API to add to three backends'
/// mesh classes, and no offset arithmetic to get wrong per backend. Index
/// ranges would save GPU buffers for multi-material cells, but they would buy
/// that with a new API surface across every backend for a case (a cell wearing
/// several materials) that is already a handful of draws either way. The
/// single-material cell — every default brush world — has exactly one submesh
/// whose arrays are bit-identical to the pre-material ones, so nothing is
/// duplicated or paid for in the common case.
/// <para>
/// Immutable by contract, like every other compiled CSG artifact: the arrays
/// are exposed directly for zero-copy GPU upload and must never be mutated.
/// Only cells that own render geometry get an artifact — resident-only cells
/// (and cells whose owned brushes were carved away entirely) have nothing to
/// draw and produce none.
/// </para>
/// </remarks>
public sealed class ChunkMesh
{
    internal ChunkMesh(ChunkCoord coord, ChunkSubmesh[] submeshes, Aabb renderBounds)
    {
        Coord = coord;
        Submeshes = submeshes;
        RenderBounds = renderBounds;
    }

    /// <summary>The cell this mesh belongs to.</summary>
    public ChunkCoord Coord { get; }

    /// <summary>
    /// The cell's geometry, one entry per distinct material, in ASCENDING
    /// material id — a total order over a value key, so two compiles of the
    /// same cell emit the same submeshes in the same order (the bit-identity
    /// contract extends to the split). Surfaces keep their compile emission
    /// order within a submesh. Empty only for a cell whose owned surfaces are
    /// all degenerate; treat as immutable.
    /// </summary>
    public IReadOnlyList<ChunkSubmesh> Submeshes { get; }

    /// <summary>
    /// The union AABB of the cell's owned welded surfaces — the box frustum
    /// culling must test, and deliberately NOT the cell's own bounds
    /// (<see cref="ChunkCoord.Bounds"/>): a border-spanning brush is owned by
    /// exactly one cell, so its owned surfaces routinely stick out of that
    /// cell, and culling by cell bounds would make the overhang vanish while
    /// visible. Culling stays per CELL, not per submesh: a cell's materials
    /// interleave across its whole extent, so per-submesh boxes would be near
    /// copies of this one at the cost of testing each several times.
    /// </summary>
    public Aabb RenderBounds { get; }
}
