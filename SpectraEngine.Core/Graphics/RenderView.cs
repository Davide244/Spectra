using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// One draw command in a <see cref="RenderView"/>: the mesh to draw, the
/// material to draw it with, and the world matrix placing it. A plain value —
/// items carry no scene-graph references, so pipelines consuming them never
/// need to walk (or even know about) the graph.
/// </summary>
/// <param name="Mesh">The geometry to draw.</param>
/// <param name="Material">The material to draw it with; items without one are skipped.</param>
/// <param name="World">The model (local→world) matrix for this draw.</param>
public readonly record struct RenderItem(Mesh Mesh, Material? Material, Matrix4x4 World);

/// <summary>
/// A reusable per-frame draw list: the flat, frustum-culled set of mesh draws
/// for one camera, plus the frustum-culled chunks of the derived static world
/// carried in their own list (<see cref="WorldItems"/> — world-chunk meshes
/// are already in world space, so each entry draws with an identity model
/// matrix, and a chunk contributes one entry per material it wears, each with
/// that material already resolved). Built once per frame
/// by <c>Scene.BuildRenderView</c> and handed to whichever backend pipeline
/// executes — pipelines iterate <see cref="Items"/> and
/// <see cref="WorldItems"/> identically instead of walking the scene graph
/// themselves.
/// </summary>
/// <remarks>
/// <b>Threading:</b> render thread only, like the <see cref="Scene.Scene"/>
/// that fills it — built during the frame's update phase and consumed by the
/// pipeline in the same frame, so no synchronization is needed.
/// <para>
/// <b>Allocation discipline:</b> the instance is engine-owned and reused every
/// frame; <see cref="Clear"/> keeps both lists' capacity, so steady-state
/// rebuilds allocate nothing once the lists have grown to the scene's size.
/// </para>
/// </remarks>
public sealed class RenderView
{
    private readonly List<RenderItem> _items = [];
    private readonly List<RenderItem> _worldItems = [];

    /// <summary>
    /// The frustum-culled draw list, in the scene spatial index's emission
    /// order — deterministic and stable across builds of an unchanged scene.
    /// </summary>
    public IReadOnlyList<RenderItem> Items => _items;

    /// <summary>
    /// The frustum-culled static-world chunk draws, in ascending chunk-cell
    /// order and, within a chunk, ascending material id: one item per
    /// (visible chunk, material), each carrying that piece's GPU mesh, the
    /// material its faces resolved to at upload time, and an identity world
    /// matrix (chunk vertices are in world space). Culling is per CHUNK and
    /// uses the chunk's true render AABB — the union of its owned surfaces,
    /// which may extend past the cell — so a chunk with any potentially
    /// visible geometry is never dropped. Empty until the first compile lands.
    /// </summary>
    public IReadOnlyList<RenderItem> WorldItems => _worldItems;

    /// <summary>Mesh items emitted into <see cref="Items"/> this build (i.e. the survivors of culling).</summary>
    public int VisibleCount { get; internal set; }

    /// <summary>Total mesh-bearing spatial nodes registered in the scene, culled or not.</summary>
    public int TotalCount { get; internal set; }

    /// <summary>
    /// World chunks that survived chunk culling this build. NOT the length of
    /// <see cref="WorldItems"/> — a chunk expands to one item per material it
    /// wears (see <see cref="WorldMaterialBatchesVisible"/>).
    /// </summary>
    public int WorldChunksVisible { get; internal set; }

    /// <summary>Total chunks of the compiled static world with render geometry, culled or not.</summary>
    public int WorldChunksTotal { get; internal set; }

    /// <summary>
    /// Per-material world draws emitted this build — the length of
    /// <see cref="WorldItems"/>, i.e. how many draw calls the visible chunks
    /// cost. Equal to <see cref="WorldChunksVisible"/> for a single-material
    /// world; the gap between them is what per-material batching costs.
    /// </summary>
    public int WorldMaterialBatchesVisible { get; internal set; }

    /// <summary>Per-material world draws across every uploaded chunk, culled or not.</summary>
    public int WorldMaterialBatchesTotal { get; internal set; }

    /// <summary>Appends one draw to the list (build-side use only).</summary>
    internal void Add(in RenderItem item) => _items.Add(item);

    /// <summary>Appends one world-chunk draw to <see cref="WorldItems"/> (build-side use only).</summary>
    internal void AddWorldChunk(in RenderItem item) => _worldItems.Add(item);

    /// <summary>
    /// Resets the view for the next build. Retains both item lists' capacity —
    /// this is what keeps per-frame rebuilds allocation-free.
    /// </summary>
    internal void Clear()
    {
        _items.Clear();
        _worldItems.Clear();
        VisibleCount = 0;
        TotalCount = 0;
        WorldChunksVisible = 0;
        WorldChunksTotal = 0;
        WorldMaterialBatchesVisible = 0;
        WorldMaterialBatchesTotal = 0;
    }
}
