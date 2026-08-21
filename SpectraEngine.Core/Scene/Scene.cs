using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading.Tasks;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// The engine's spatial model, with the scene graph as the single spine: a tree
/// of <see cref="SceneNode"/> instances rooted at <see cref="Root"/>, viewed
/// through the <see cref="Camera"/>. Brush nodes in the graph are the authoring
/// primitive for static world geometry; the compiled <see cref="StaticWorld"/>
/// (carved surfaces + BSP + render mesh) is <em>derived data</em>, rebuilt from
/// the brush nodes on demand — never authored directly.
/// </summary>
/// <remarks>
/// <b>Recompiles are asynchronous.</b> Editing a brush node (or moving any node
/// with brush descendants) marks the world dirty automatically. Each frame the
/// render thread's <see cref="ProcessStaticWorldCompilation"/> snapshots the
/// brush placements, hands the immutable snapshot to a background task for the
/// pure-CPU compile (carve → snap → weld → BSP → per-chunk mesh arrays), and
/// swaps the finished world in — creating only the changed chunks' GPU meshes
/// on the render thread — when it lands. At most one compile is in flight; the
/// previous world keeps rendering
/// until its replacement is ready. <see cref="RebuildStaticWorld"/> remains the
/// synchronous path for load time and tests.
/// <para>
/// <b>Threading:</b> every member of this class is owned by the render thread
/// (scene updates and GPU resource creation live there). The only work that
/// leaves that thread is the background compile, and it reads nothing but its
/// snapshot.
/// </para>
/// </remarks>
public sealed class Scene
{
    public Scene(string name = "Scene")
    {
        Name = name;
        // The selection subscribes to NodeRemoved for auto-deselection, so it
        // must exist before any node can leave the graph.
        Selection = new SelectionSet(this);
        // The spatial index subscribes to all three change events, so it too
        // must exist before the root (and everything after it) enters the graph.
        Bvh = new SceneBvh(this);
        // The root is created by the property initializer before this runs;
        // claiming it here makes every node later attached under it inherit
        // the owner reference that powers automatic static-world dirtying.
        Root.SetOwner(this);
    }

    public string Name { get; set; }

    /// <summary>The implicit root node; never has a parent.</summary>
    public SceneNode Root { get; } = new("Root");

    public Camera Camera { get; } = new();

    /// <summary>
    /// The editor-facing selection over this scene's nodes. Render thread
    /// only, like every other scene member.
    /// </summary>
    public SelectionSet Selection { get; }

    /// <summary>
    /// The dynamic AABB tree over this scene's spatial nodes, maintained
    /// automatically through the scene change events. Internal — consumers go
    /// through <see cref="Raycast"/> and <see cref="QueryFrustum"/>. Render
    /// thread only, like every other scene member.
    /// </summary>
    internal SceneBvh Bvh { get; }

    // --- Scene change events ------------------------------------------------
    // Render thread only, like all scene state: the graph is only ever mutated
    // on the render thread, so the events fire there and handlers run there.
    // Plain C# events raised with null-check invokes — with no subscribers a
    // graph edit costs one null check, nothing more.
    //
    // RE-ENTRANCY RULE: handlers must not mutate the scene graph (add, remove,
    // or reparent nodes) from inside an event — membership events fire in the
    // middle of the ownership walk, and a structural edit there would corrupt
    // the traversal. Observe and record; defer structural edits until after
    // the event returns. (This is a stated contract, not an enforced guard.)

    /// <summary>
    /// Raised for every node that enters this scene's graph. Attaching a
    /// subtree raises it once per node, pre-order (parents before children);
    /// a reparent within this scene raises nothing — the moved nodes never
    /// left. Handlers observe the node with its <c>Owner</c> already set to
    /// this scene. Render thread only; handlers must not mutate the graph
    /// (see the re-entrancy rule above).
    /// </summary>
    public event Action<SceneNode>? NodeAdded;

    /// <summary>
    /// Raised for every node that leaves this scene's graph, whether detached
    /// outright or moved to another scene (a cross-scene move raises
    /// <see cref="NodeRemoved"/> here and <see cref="NodeAdded"/> there, per
    /// node). Handlers observe the node with its <c>Owner</c> already cleared
    /// or pointing at the destination scene. Render thread only; handlers
    /// must not mutate the graph (see the re-entrancy rule above).
    /// </summary>
    public event Action<SceneNode>? NodeRemoved;

    /// <summary>
    /// Raised when an owned node's local transform actually changes — the
    /// transform setters filter out equal-value writes, so a no-op write
    /// raises nothing. Fires for every owned node, brush-bearing or not
    /// (static-world dirtying is a separate, brush-only concern). Render
    /// thread only; handlers must not mutate the graph (see the re-entrancy
    /// rule above).
    /// </summary>
    public event Action<SceneNode>? NodeTransformChanged;

    // Raise helpers for SceneNode: the graph notifies its owning scene through
    // these instead of exposing public raise entry points. Membership and
    // reparent notifications also bump the graph-structure version — the
    // signal that the brush snapshot's TRAVERSAL ORDER may have changed, which
    // the incremental static-world compile's trusted-diff contract cannot
    // survive (see _graphStructureVersion).
    internal void OnNodeAdded(SceneNode node)
    {
        _graphStructureVersion++;
        // Index BEFORE the event so handlers (and anything they call) can
        // already resolve the arriving node through TryFindById.
        _nodesById[node.Id] = node;
        UpdatePartBrushMembership(node);
        NodeAdded?.Invoke(node);
    }

    internal void OnNodeRemoved(SceneNode node)
    {
        _graphStructureVersion++;
        // De-index before the event, mirroring how Owner is repointed before
        // the notification: a NodeRemoved handler must not be able to look the
        // departing node up in the scene it is leaving. Identity-checked so a
        // stale duplicate id (see the indexer note above) cannot unmap the
        // node that currently owns the key.
        if (_nodesById.TryGetValue(node.Id, out SceneNode? indexed) && ReferenceEquals(indexed, node))
            _nodesById.Remove(node.Id);
        // Unconditionally, unlike the membership recheck elsewhere: the node is
        // leaving whatever it carries, and its meshes are collected by the next
        // pump's sweep.
        _partBrushNodes.Remove(node);
        NodeRemoved?.Invoke(node);
    }

    internal void OnNodeTransformChanged(SceneNode node) => NodeTransformChanged?.Invoke(node);

    // Spatial-index hooks from SceneNode for changes the three public events
    // don't cover: component (MeshRenderer/Brush) edits on an already-owned
    // node, and reparents WITHIN this scene (which move the subtree's world
    // matrices but deliberately raise no membership events — yet still change
    // traversal order, hence the structure bump).
    internal void OnNodeSpatialComponentChanged(SceneNode node)
    {
        Bvh.OnSpatialComponentChanged(node);
        // The Brush setter routes through here unconditionally (it is one of
        // the two gate sites that must stay kind-blind), which makes it the
        // natural place to keep the part set honest through attach, swap and
        // detach alike.
        UpdatePartBrushMembership(node);
    }

    // Adds or drops `node` from the part set to match what it currently
    // carries. Idempotent and O(1), so every caller may just say "recheck this
    // node" rather than reasoning about which transition happened.
    internal void UpdatePartBrushMembership(SceneNode node)
    {
        if (node.Brush is not null && node.BrushKind == BrushKind.Part)
            _partBrushNodes.Add(node);
        else
            _partBrushNodes.Remove(node);
    }

    internal void OnNodeSubtreeMoved(SceneNode node)
    {
        _graphStructureVersion++;
        Bvh.OnSubtreeMoved(node);
    }

    // --- Node identity index ------------------------------------------------

    // Guid -> node for every node currently in this scene's graph, maintained
    // from the membership events above (which fire once per node of an attached
    // or detached subtree, so whole-subtree edits are covered without a walk).
    // A reparent WITHIN this scene raises no membership events and needs none:
    // the nodes never left, so their mappings stay valid.
    //
    // Written with the indexer rather than Add: these run inside the ownership
    // walk, where a throw would leave the graph half-owned. Ids are expected to
    // be unique within a scene (Guid.NewGuid, or a deliberate reuse by undo of
    // an id whose node has already left) — if two live nodes ever share one,
    // the most recently added wins rather than crashing the edit.
    private readonly Dictionary<Guid, SceneNode> _nodesById = [];

    /// <summary>
    /// Resolves a <see cref="SceneNode.Id"/> to the live node in this scene, or
    /// returns false when no node with that id is currently attached. This is
    /// the lookup editor commands use: edit history is addressed by id, not by
    /// object reference, because an undo can destroy and recreate a node — the
    /// reference goes stale, the id does not. O(1) and allocation-free; render
    /// thread only, like every other scene member.
    /// </summary>
    public bool TryFindById(Guid id, [MaybeNullWhen(false)] out SceneNode node) =>
        _nodesById.TryGetValue(id, out node);

    /// <summary>
    /// The number of nodes currently indexed by id — i.e. the node count of
    /// this scene's graph, root included. Render thread only.
    /// </summary>
    public int NodeCount => _nodesById.Count;

    // --- Spatial queries ----------------------------------------------------

    /// <summary>
    /// Casts a world-space ray against the scene's spatial nodes (nodes with a
    /// <see cref="SceneNode.MeshRenderer"/> or <see cref="SceneNode.Brush"/>)
    /// and reports the nearest hit within <paramref name="maxDistance"/>.
    /// Brush nodes use an exact convex test against their planes; mesh nodes
    /// use per-triangle intersection over the mesh's CPU-side geometry (a mesh
    /// without CPU positions degrades to its bounding box — a unit box around
    /// the node origin when even bounds are missing). Rays starting inside a
    /// solid report no hit for that solid. Render thread only, allocation-free
    /// in steady state.
    /// </summary>
    public bool Raycast(in Ray3 ray, out SceneRaycastHit hit, float maxDistance = float.PositiveInfinity) =>
        Bvh.Raycast(in ray, out hit, maxDistance);

    /// <summary>
    /// Appends every spatial node whose world AABB intersects
    /// <paramref name="frustum"/> to <paramref name="results"/> (the list is
    /// NOT cleared — reuse one list across frames and clear it yourself).
    /// Conservative exactly like <see cref="Frustum.Intersects"/>: per-box
    /// false positives are possible, false negatives are not. Render thread
    /// only, allocation-free in steady state once the list has grown.
    /// </summary>
    public void QueryFrustum(in Frustum frustum, List<SceneNode> results) =>
        Bvh.QueryFrustum(in frustum, results);

    /// <summary>
    /// The tight world AABB the spatial index already maintains for
    /// <paramref name="node"/> — brush bounds, mesh bounds, or their union —
    /// reflecting any transform change made since the last query. False when
    /// the node is not spatial or does not belong to this scene.
    /// </summary>
    /// <remarks>
    /// The editor's selection highlight, box select and frame-selection all
    /// need a node's bounds every frame; handing them the index's cached box
    /// keeps them from recomputing what culling and raycasts already paid for,
    /// and guarantees all four agree on where a node is. Render thread only,
    /// allocation-free.
    /// </remarks>
    public bool TryGetWorldBounds(SceneNode node, out Aabb bounds)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Bvh.TryGetWorldBounds(node, out bounds);
    }

    // Scratch list reused by BuildRenderView's frustum query — capacity is
    // retained across frames so steady-state builds allocate nothing.
    private readonly List<SceneNode> _renderViewScratch = [];

    /// <summary>
    /// Fills <paramref name="view"/> with this frame's draw list for
    /// <paramref name="camera"/>: refits the spatial index, frustum-culls the
    /// scene's spatial nodes, and emits one <see cref="RenderItem"/> per
    /// visible mesh node (world matrix from the node). The derived static
    /// world rides along as per-chunk items in
    /// <see cref="RenderView.WorldItems"/>, frustum-culled per chunk against
    /// each chunk's true render AABB (see <see cref="ChunkMesh.RenderBounds"/>)
    /// in one linear pass — chunks are few relative to nodes, so no
    /// acceleration structure is warranted. A surviving chunk emits one item
    /// per material it wears (see <see cref="ChunkMesh.Submeshes"/>), each
    /// carrying the material resolved when that chunk was uploaded. Stats:
    /// <see cref="RenderView.VisibleCount"/>/<see cref="RenderView.TotalCount"/>
    /// count mesh items and registered mesh nodes;
    /// <see cref="RenderView.WorldChunksVisible"/>/<see cref="RenderView.WorldChunksTotal"/>
    /// count the world CHUNKS beside them, and
    /// <see cref="RenderView.WorldMaterialBatchesVisible"/>/<see cref="RenderView.WorldMaterialBatchesTotal"/>
    /// the per-material draws those chunks expand to. Item order follows the
    /// spatial index's emission order (world chunks: ascending cell order, then
    /// ascending material id) — deterministic and stable while the scene is
    /// unchanged. Render thread only, like every other scene member;
    /// allocation-free in steady state (the view and the internal scratch list
    /// retain their capacity).
    /// </summary>
    public void BuildRenderView(Camera camera, RenderView view)
    {
        view.Clear();

        Frustum frustum = camera.GetFrustum();
        _renderViewScratch.Clear();
        Bvh.QueryFrustum(in frustum, _renderViewScratch);

        // The query yields every visible spatial node, brush nodes included.
        // Mesh-bearing nodes become draws, and so do PART brush nodes: world
        // brush geometry renders through the compiled static world and never
        // per-node, but a part is by definition not in that world, so its own
        // mesh is drawn here under the node's world matrix. This is the one
        // place the two kinds diverge in the render path.
        // Counted apart from view.Items.Count, which now holds both
        // populations: VisibleCount stays "mesh nodes that survived culling",
        // so the two numbers keep meaning what their names say and neither can
        // read as larger than its own total.
        int meshItems = 0;
        int partBrushes = 0;
        for (int i = 0; i < _renderViewScratch.Count; i++)
        {
            SceneNode node = _renderViewScratch[i];
            if (node.MeshRenderer is { } meshRenderer)
            {
                meshItems++;
                view.Add(new RenderItem(meshRenderer.Mesh, meshRenderer.Material, node.WorldMatrix));
            }

            if (node.BrushKind == BrushKind.Part &&
                node.Brush is { } partBrush &&
                _partBrushMeshes.TryGet(partBrush, out BrushSubmesh[] submeshes))
            {
                partBrushes++;
                Matrix4x4 world = node.WorldMatrix;
                for (int s = 0; s < submeshes.Length; s++)
                    view.Add(new RenderItem(submeshes[s].Mesh, submeshes[s].Material, world));
            }
        }

        // The static world's chunks: cull against the RENDER bounds (owned
        // surfaces of border-spanning brushes stick out of their cell, so cell
        // bounds would wrongly drop visible overhangs). The AABB test is
        // conservative (see Frustum.Intersects), so a chunk containing any
        // visible geometry always survives; vertices are already in world
        // space, hence the identity model matrix.
        //
        // Culling stays PER CHUNK — one box test per cell, whatever its
        // material count — and a surviving chunk then contributes one item per
        // material it wears, with the material resolved back at swap time (a
        // per-frame asset lookup here would allocate and would put asset state
        // on the hot path). Both the entry list and each entry's submesh array
        // are engine-owned and indexed, so this whole pass touches no
        // allocator.
        int visibleChunks = 0;
        int totalBatches = 0;
        for (int i = 0; i < _staticWorldChunkList.Count; i++)
        {
            StaticWorldChunkMesh chunk = _staticWorldChunkList[i];
            StaticWorldSubmesh[] submeshes = chunk.Submeshes;
            totalBatches += submeshes.Length;

            if (!frustum.Intersects(chunk.Artifact.RenderBounds))
                continue;

            visibleChunks++;
            for (int s = 0; s < submeshes.Length; s++)
            {
                StaticWorldSubmesh submesh = submeshes[s];
                view.AddWorldChunk(new RenderItem(submesh.Mesh, submesh.Material, Matrix4x4.Identity));
            }
        }

        view.VisibleCount = meshItems;
        view.TotalCount = Bvh.MeshLeafCount;
        view.PartBrushesVisible = partBrushes;
        view.PartBrushesTotal = _partBrushNodes.Count;
        view.WorldChunksVisible = visibleChunks;
        view.WorldChunksTotal = _staticWorldChunkList.Count;
        view.WorldMaterialBatchesVisible = view.WorldItems.Count;
        view.WorldMaterialBatchesTotal = totalBatches;
    }

    /// <summary>
    /// The compiled static world (carved surfaces + BSP tree for spatial
    /// queries), derived from every brush node in the graph. Null until the
    /// first compile completes, or when the scene has no brush nodes. Treat as
    /// a build artifact: to change the world, edit the brush nodes — the edit
    /// marks the world dirty and the engine recompiles in the background.
    /// </summary>
    public CsgWorld? StaticWorld { get; private set; }

    // The per-chunk GPU mesh map derived from StaticWorld.ChunkMeshes: one
    // entry per world chunk with render geometry. The dictionary answers
    // coordinate lookups (swap diffing, tests); the list is its
    // ascending-cell-order snapshot, rebuilt at swap time so the per-frame
    // culling pass is a plain indexed loop. Render thread only, like all
    // scene state; both containers always describe the same entries.
    private readonly Dictionary<ChunkCoord, StaticWorldChunkMesh> _staticWorldChunkMeshes = [];
    private readonly List<StaticWorldChunkMesh> _staticWorldChunkList = [];

    // Every owned node carrying a BrushKind.Part brush, and the GPU meshes
    // those brushes draw with.
    //
    // The set exists so the per-frame pump has an exact work list instead of a
    // graph walk: parts are the population that moves every tick, so anything
    // O(world) here would reintroduce exactly the cost the kind was invented to
    // remove. It is plain membership — no ancestor walk, no subtree invariant —
    // maintained from the same four places that already learn about a brush
    // arriving, leaving or changing kind, and it is deliberately NOT a third
    // counter (see SceneNode's two lanes for why that distinction matters).
    private readonly HashSet<SceneNode> _partBrushNodes = [];
    private readonly PartBrushMeshCache _partBrushMeshes = new();

    // Cached so the per-brush upload path can hand the resolver down without
    // allocating a delegate per call.
    private Func<MaterialRef, Material?>? _resolveWorldMaterial;

    /// <summary>
    /// The static world's GPU meshes, one entry per chunk with render geometry
    /// (each holding one GPU submesh per material that chunk wears), in
    /// ascending chunk-cell order — the per-chunk successor of the old single
    /// static-world mesh. Chunk vertices are in world space (identity model
    /// matrix); a landing recompile replaces only the entries whose cells
    /// actually changed. Empty until the first compile lands, or when the
    /// scene has no brush nodes. Render thread only.
    /// </summary>
    public IReadOnlyList<StaticWorldChunkMesh> StaticWorldChunkMeshes => _staticWorldChunkList;

    /// <summary>
    /// Looks up the static world's GPU mesh entry for the chunk cell at
    /// <paramref name="coord"/>, if that cell currently has render geometry.
    /// Render thread only.
    /// </summary>
    public bool TryGetStaticWorldChunkMesh(ChunkCoord coord, out StaticWorldChunkMesh chunk) =>
        _staticWorldChunkMeshes.TryGetValue(coord, out chunk);

    /// <summary>
    /// Material for static-world faces that carry no material of their own
    /// (<see cref="MaterialRef.Default"/>) — the fallback the whole world used
    /// to be drawn with. Assign before the first rebuild; it survives rebuilds
    /// (only the geometry is derived). Changing it later affects chunks
    /// uploaded from then on; call <see cref="RefreshStaticWorldMaterials"/> to
    /// re-resolve the ones already on the GPU.
    /// </summary>
    public Material? StaticWorldMaterial { get; set; }

    /// <summary>
    /// Asset manager used to turn the material ids compiled into the static
    /// world's faces into real materials. Optional: without one every face
    /// falls back to <see cref="StaticWorldMaterial"/>, which is all a
    /// geometry-only scene (or a headless test) needs. Assign before the first
    /// rebuild, on the render thread — resolution happens there, at GPU-upload
    /// time, never during the background compile.
    /// </summary>
    public AssetManager? Assets { get; set; }

    /// <summary>
    /// Re-resolves every uploaded chunk's materials from its compiled material
    /// ids, without touching a single GPU mesh. The escape hatch for the two
    /// cases that change what a face should be drawn with while its geometry
    /// stays valid: assigning <see cref="StaticWorldMaterial"/> after the world
    /// was compiled, and an <see cref="Assets"/> reload that replaced a
    /// material instance. Render thread only; O(GPU submeshes) and no
    /// allocation beyond whatever the asset manager does on a cache miss.
    /// </summary>
    public void RefreshStaticWorldMaterials()
    {
        for (int i = 0; i < _staticWorldChunkList.Count; i++)
        {
            StaticWorldChunkMesh chunk = _staticWorldChunkList[i];
            StaticWorldSubmesh[] submeshes = chunk.Submeshes;
            for (int s = 0; s < submeshes.Length; s++)
            {
                StaticWorldSubmesh submesh = submeshes[s];
                submeshes[s] = submesh with { Material = ResolveWorldMaterial(submesh.Source.Material) };
            }
        }
    }

    // Render thread only: the compile's pure-value material id -> the asset
    // object a pipeline can actually draw with. Called once per GPU submesh at
    // upload time (and from RefreshStaticWorldMaterials), never per frame.
    //
    // An unassigned face resolves to StaticWorldMaterial, which is exactly what
    // the pre-material engine drew the entire world with — so a scene that
    // never touches materials renders identically. A face that DOES name a
    // material needs the asset manager; without one there is nothing to resolve
    // against, so it degrades to the same fallback rather than dropping the
    // geometry. The asset manager's own default material is the last resort for
    // a scene that set no fallback at all (it degrades, never throws, on a
    // missing file or an unknown id).
    /// <summary>
    /// Brings the part brushes' GPU meshes up to date: builds one for any part
    /// brush that does not have one yet, and destroys the meshes of brushes
    /// nothing references any more. Render thread only, once per frame, beside
    /// <see cref="ProcessStaticWorldCompilation"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The work is proportional to the number of distinct part <em>brushes</em>
    /// — not to the world, and not to how much anything moved. A part that only
    /// moves is a pure cache hit: its mesh is brush-local, so movement is the
    /// node's world matrix and nothing here is rebuilt. That is the entire
    /// performance claim of <see cref="BrushKind.Part"/>, and it is why this
    /// pump may run unconditionally every frame.
    /// </para>
    /// <para>
    /// A part's mesh is built from its brush alone, with no carve against its
    /// neighbours — which is the visible difference between the two kinds, not
    /// an optimisation. Two overlapping world brushes merge into one skin; two
    /// overlapping parts interpenetrate, and a part face left coplanar with a
    /// world face z-fights. Both are correct, and both are why the editor must
    /// make the kind visible.
    /// </para>
    /// </remarks>
    public void ProcessPartBrushMeshes(Renderer renderer)
    {
        if (_partBrushNodes.Count == 0 && _partBrushMeshes.Count == 0)
            return;

        _resolveWorldMaterial ??= ResolveWorldMaterial;

        _partBrushMeshes.BeginPump();
        foreach (SceneNode node in _partBrushNodes)
        {
            if (node.Brush is { } brush)
                _partBrushMeshes.Acquire(renderer, brush, _resolveWorldMaterial);
        }
        _partBrushMeshes.EndPump(renderer);
    }

    /// <summary>How many distinct part brushes currently hold GPU meshes.</summary>
    public int PartBrushMeshCount => _partBrushMeshes.Count;

    /// <summary>How many nodes in this scene carry a <see cref="BrushKind.Part"/> brush.</summary>
    public int PartBrushNodeCount => _partBrushNodes.Count;

    /// <summary>Destroys every GPU mesh the part-brush cache owns. Render thread, before renderer shutdown.</summary>
    public void ReleasePartBrushMeshes(Renderer renderer) => _partBrushMeshes.ReleaseGraphicsResources(renderer);

    private Material? ResolveWorldMaterial(MaterialRef reference)
    {
        if (!reference.IsDefault && Assets is { } assets)
            return assets.ResolveMaterial(reference);

        return StaticWorldMaterial ?? Assets?.ResolveMaterial(MaterialRef.Default);
    }

    // --- Static-world compile state -----------------------------------------
    // Single-threaded contract: every field below is read and written by the
    // render thread ONLY (MarkStaticWorldDirty included — node edits happen
    // during scene updates, which the render thread owns). The background
    // compile task never touches them; it receives an immutable snapshot and
    // returns a result, and the render thread harvests that result here. No
    // locks or volatiles are needed because Task.Run / Task completion provide
    // the necessary happens-before edges for the snapshot and the result.

    // Monotonic edit counter: bumped on every dirtying edit. Compared against
    // _handledStaticWorldVersion to decide whether a new compile is needed.
    private int _staticWorldVersion;

    // Highest version a compile has been launched for (or that was handled
    // synchronously / rejected as invalid). Marking a defective snapshot as
    // handled is what stops a bad transform from retry-spamming every frame.
    private int _handledStaticWorldVersion;

    // The single in-flight background compile, if any, and the version its
    // snapshot was taken at. Exactly one compile runs at a time, so a landing
    // result is always the newest one launched — no ordering logic is needed
    // when harvesting.
    private Task<StaticWorldCompilation>? _inFlightCompile;
    private int _inFlightVersion;

    // Last defect message logged for a rejected snapshot; suppresses per-frame
    // repeats of the same defect while another brush keeps re-arming the pump.
    private string? _lastLoggedSnapshotDefect;

    // Cadence for the harvested-compile stats line. A continuously animating
    // brush lands a compile nearly every frame, and a per-harvest line through
    // synchronous sinks would throttle the render thread and grow the log file
    // without bound — so harvests accumulate below and one Debug line
    // summarizes them at a bounded rate (matching the SceneManager's 5 s
    // Information cadence). Deadline of 0 makes the very first harvest log
    // immediately, so short smoke runs still get their evidence.
    private const long CompileStatsLogIntervalMs = 5000;
    private long _nextCompileStatsLogTicks;

    // Stats accumulated since the last emitted line (render thread only, like
    // all compile state): compiles landed, and the carve cache's hit/miss
    // totals across them.
    private int _compileStatsLanded;
    private int _compileStatsCacheHits;
    private int _compileStatsCacheMisses;

    // --- Dirty-cell tracking -------------------------------------------------
    // The chunk grid's edit granularity: which cells' brush contents changed
    // since the last compile. Maintained entirely inside the snapshot/pump on
    // the render thread — no SceneNode surgery: each successful snapshot's
    // per-node footprints are remembered here and diffed against the next
    // snapshot's. A node edit sends two signals: the version bump that arms
    // the pump, and (for placement-preserving edits) the edited node's
    // identity, which is what lets the snapshot and this diff run over the
    // edit's neighbourhood instead of the whole graph.

    // Per-node record of the last successfully captured snapshot: the brush
    // reference and placement matrix the footprint was computed from, plus the
    // footprint itself (sorted, from ChunkGrid.ComputeFootprint). Replaced
    // wholesale at every snapshot; the footprint array is reused when the
    // node's placement is unchanged, so an idle node costs one dictionary move
    // per compile, no cell math.
    private readonly record struct NodeFootprint(Brush Brush, Matrix4x4 Transform, ChunkCoord[] Footprint);

    // Footprints of the previous successful snapshot (empty before the first
    // one, which is exactly what makes the first compile all-dirty: every node
    // diffs as newly added).
    private Dictionary<SceneNode, NodeFootprint> _lastSnapshotFootprints = [];

    // Cells dirtied but not yet handed to a compile. Normally drained at every
    // launch; a faulted background compile merges its cell set back in so the
    // next compile re-covers what the failed one was supposed to refresh.
    private readonly HashSet<ChunkCoord> _pendingDirtyCells = [];

    // The dirty-cell set the in-flight background compile was launched with;
    // null when nothing is in flight. Kept so a fault can restore it (see
    // _pendingDirtyCells).
    private ChunkCoord[]? _inFlightDirtyCells;

    // --- Incremental snapshot state -----------------------------------------
    // The per-edit compile LAUNCH must be O(edit neighbourhood) like the
    // compile itself: re-walking the whole graph (and re-validating every
    // placement, and re-diffing every footprint) per launch was the last
    // O(world) stage in the edit loop — ~10 ms and ~15 MiB of garbage per
    // drag frame at 50k parts, dwarfing the sub-millisecond background
    // compile. So the last snapshot is RETAINED (nodes, slot map, placements
    // in paged copy-on-write storage) and each launch merely patches the
    // slots of the nodes that actually reported an edit. The retained
    // placements are immutable (PagedArray derivations share pages), so
    // handing them to the background task and keeping them here is safe.

    // Nodes of the last successful snapshot, index-aligned with the placement
    // list handed to the compile, plus the node -> slot map. Rebuilt only by
    // a full walk.
    private readonly List<SceneNode> _snapshotNodes = [];
    private readonly Dictionary<SceneNode, int> _snapshotSlots = [];

    // The last successful snapshot's placements; successors derive by paged
    // copy-on-write replacement of the edited slots. Null before the first
    // snapshot.
    private PagedArray<BrushPlacement>? _snapshotPlacements;

    // Graph-structure version the retained snapshot's traversal order was
    // captured at; any structural edit since forces a full re-walk (the slot
    // assignment is order-derived).
    private int _snapshotStructureVersion;

    // Nodes that reported a brush-affecting edit since the last successful
    // snapshot — each entry is the EDITED node, whose subtree contains the
    // affected brush placements (a group-node move dirties the group node,
    // not each descendant). Drained by every successful snapshot; retained
    // across rejected (defective) snapshots so a defect on one node never
    // loses another node's pending change.
    private readonly HashSet<SceneNode> _dirtyBrushSubtrees = [];

    // Forces the next snapshot to re-walk the whole graph. Set initially, by
    // the parameterless MarkStaticWorldDirty (no node context — external
    // callers get the conservative full validation they always had), by
    // brush attach/detach (placement count changes shift every later slot),
    // and by any fast-path anomaly.
    private bool _snapshotForceFull = true;

    // Scratch for the fast path's changed-slot collection; valid only within
    // one snapshot call. Never handed to the background task.
    private readonly List<(int Slot, BrushPlacement Placement)> _changedSlotScratch = [];
    private readonly HashSet<int> _changedSlotSeen = [];

    /// <summary>
    /// The sorted dirty-cell set the most recent static-world compile was
    /// given (background launch, synchronous rebuild, or synchronous clear):
    /// every chunk cell whose brush contents changed since the compile before
    /// it. The first compile reports every covered cell (all cells dirty);
    /// an unchanged-scene recompile reports none. Render thread only; the
    /// incremental per-chunk stages consume this via
    /// <see cref="CsgWorld.DirtyCells"/>, and tests use it as the tracking
    /// oracle.
    /// </summary>
    public IReadOnlyList<ChunkCoord> LastCompileDirtyCells { get; private set; } = [];

    // Diffs the freshly captured snapshot against the previous snapshot's
    // per-node footprints, updates the stored footprints, and drains the
    // accumulated dirty cells into one sorted array. Dirtying rule: a node
    // present in both snapshots with an unchanged brush reference and
    // placement matrix dirties nothing; any change — moved, brush swapped,
    // attached, detached — dirties the UNION of its old and new footprints (a
    // brush moving out of a cell leaves stale geometry behind there just as
    // surely as it brings new geometry to where it lands). Two modes matching
    // the snapshot's: a non-null `changedSlots` (the fast path's edit list)
    // diffs exactly those slots in place — O(edit neighbourhood); null (a
    // full re-walk) rebuilds the footprint dictionary over all placements,
    // sweeping the old one for departed nodes. Render thread only, called
    // exactly once per handled snapshot.
    private ChunkCoord[] CollectDirtyCells(
        IReadOnlyList<BrushPlacement> placements, List<(int Slot, BrushPlacement Placement)>? changedSlots)
    {
        if (changedSlots is not null)
        {
            foreach ((int slot, BrushPlacement placement) in changedSlots)
            {
                SceneNode node = _snapshotNodes[slot];
                bool hadPrevious = _lastSnapshotFootprints.TryGetValue(node, out NodeFootprint previous);
                if (hadPrevious && ReferenceEquals(previous.Brush, placement.Brush) &&
                    previous.Transform == placement.Transform)
                {
                    // Edit-flagged but placement unchanged (e.g. a move that
                    // was reverted before the snapshot): dirty nothing.
                    continue;
                }

                ChunkCoord[] footprint = ChunkGrid.ComputeFootprint(in placement);
                _lastSnapshotFootprints[node] = new NodeFootprint(placement.Brush, placement.Transform, footprint);
                MarkCellsDirty(footprint);
                if (hadPrevious)
                    MarkCellsDirty(previous.Footprint);
            }
            return DrainPendingDirtyCells();
        }

        var newFootprints = new Dictionary<SceneNode, NodeFootprint>(placements.Count);
        for (int i = 0; i < placements.Count; i++)
        {
            SceneNode node = _snapshotNodes[i];
            BrushPlacement placement = placements[i];

            bool hadFootprint = _lastSnapshotFootprints.TryGetValue(node, out NodeFootprint recorded);
            if (hadFootprint && ReferenceEquals(recorded.Brush, placement.Brush) &&
                recorded.Transform == placement.Transform)
            {
                // Unchanged placement: same footprint by construction — reuse
                // the array, dirty nothing. (Matrix float == is safe here: the
                // snapshot already rejected non-finite transforms, so no NaN
                // can force a spurious mismatch.)
                newFootprints[node] = recorded;
                continue;
            }

            ChunkCoord[] footprint = ChunkGrid.ComputeFootprint(in placement);
            newFootprints[node] = new NodeFootprint(placement.Brush, placement.Transform, footprint);
            MarkCellsDirty(footprint);
            if (hadFootprint)
                MarkCellsDirty(recorded.Footprint);
        }

        // Nodes that left the snapshot (detached, brush removed, or moved to
        // another scene) leave stale geometry in their old cells.
        foreach (var (node, previous) in _lastSnapshotFootprints)
        {
            if (!newFootprints.ContainsKey(node))
                MarkCellsDirty(previous.Footprint);
        }

        _lastSnapshotFootprints = newFootprints;
        return DrainPendingDirtyCells();
    }

    private ChunkCoord[] DrainPendingDirtyCells()
    {
        var dirty = new ChunkCoord[_pendingDirtyCells.Count];
        _pendingDirtyCells.CopyTo(dirty);
        _pendingDirtyCells.Clear();
        // Sorted so the set is deterministic for equal edit histories —
        // ChunkCoord.CompareTo is the canonical cell order.
        Array.Sort(dirty);
        return dirty;
    }

    private void MarkCellsDirty(ChunkCoord[] cells)
    {
        foreach (ChunkCoord cell in cells)
            _pendingDirtyCells.Add(cell);
    }

    // The previous compiled world carried from the last harvested (or
    // synchronous) compile to the next launch — the incremental compile's
    // whole input: per-brush carve/weld carry, chunk grid, per-cell trees and
    // mesh artifacts, and the classic validation caches as lazily derived
    // views (see CsgWorldCarry / CsgIncrementalCompiler). OWNERSHIP: the
    // render thread holds it between compiles; at launch it is handed to the
    // background task (and this field nulled), the task reads it — never
    // mutates it — for the duration of the compile, and the harvested world
    // becomes the next carry. Single-compile-in-flight makes the handoff
    // race-free: no second launch can happen before the harvest, and the
    // synchronous RebuildStaticWorld waits out any in-flight task before
    // compiling. A faulted compile restores the carry from StaticWorld (the
    // same immutable world object) — never a correctness issue, because the
    // restored dirty-cell set re-covers everything the failed compile missed.
    private CsgWorld? _staticWorldCarry;

    // Bumped by the membership/reparent raise helpers whenever the graph's
    // structure — and therefore the brush snapshot's traversal ORDER — may
    // have changed. The incremental compile's trusted-diff contract requires
    // placement i to describe the same authoring slot as the previous
    // compile's placement i, so a launch whose structure version differs from
    // the carry's compiles through the validated cache path instead (always
    // correct for any order, just O(world) validation — structural edits are
    // rare next to per-frame drags).
    private int _graphStructureVersion;

    // The structure version the carried world's snapshot was captured at, and
    // the version captured for the in-flight compile (promoted into
    // _carryStructureVersion when the compile lands).
    private int _carryStructureVersion;
    private int _inFlightStructureVersion;

    // The structure version behind the currently published StaticWorld — what
    // a faulted compile restores _carryStructureVersion from.
    private int _staticWorldStructureVersion;

    /// <summary>
    /// Number of times a recompiled static world has been swapped in
    /// (synchronous rebuilds included). Render thread only; useful as cheap
    /// evidence that live recompiles are actually happening.
    /// </summary>
    public int StaticWorldCompileCount { get; private set; }

    /// <summary>
    /// True when brush nodes changed since the last compile was launched (or
    /// handled synchronously). A world with a compile still in flight is no
    /// longer "dirty" — those edits are already being processed.
    /// </summary>
    public bool StaticWorldDirty => _staticWorldVersion != _handledStaticWorldVersion;

    /// <summary>
    /// Flags the derived static world as stale; the engine recompiles it in
    /// the background. Called automatically by <see cref="SceneNode"/> for
    /// structural brush edits (attach/detach, membership changes); transform
    /// and brush-swap edits go through the node-scoped
    /// <see cref="MarkBrushSubtreeDirty"/> instead. Render thread only — the
    /// version counter is deliberately unsynchronized because scene edits are
    /// single-threaded.
    /// </summary>
    /// <remarks>
    /// Carrying no node context, this overload also forces the next snapshot
    /// to re-walk and re-validate the whole graph — the behaviour external
    /// callers always had. The per-frame drag path never comes through here.
    /// </remarks>
    public void MarkStaticWorldDirty()
    {
        _staticWorldVersion++;
        _snapshotForceFull = true;
    }

    // The set of brushes ADMITTED to the static world changed — a node's
    // BrushKind flipped — without the graph's shape changing at all.
    //
    // This needs its own door because it breaks two separate assumptions at
    // once, and MarkStaticWorldDirty only covers one of them. The placement
    // COUNT changed, so every slot after the converted node shifted (that is
    // the force-full half). But the snapshot's fast path also gates on the
    // graph-structure version, whose documented meaning is "the brush
    // snapshot's TRAVERSAL ORDER may have changed" — and a conversion changes
    // exactly that while adding and removing no nodes, so no membership event
    // fires and nothing would bump it. A stale structure version plus a
    // shifted slot map is a trusted diff applied to the wrong placements: the
    // corruption is silent, and it renders.
    //
    // Render thread only, like the two marks below it.
    internal void MarkAdmissionChanged(SceneNode node)
    {
        _graphStructureVersion++;
        _staticWorldVersion++;
        _snapshotForceFull = true;
        UpdatePartBrushMembership(node);
    }

    // Node-scoped dirtying for placement-preserving edits (transform changes,
    // brush-for-brush swaps): records WHICH subtree changed so the next
    // snapshot can patch exactly those slots instead of re-walking the world.
    // Render thread only, like MarkStaticWorldDirty.
    internal void MarkBrushSubtreeDirty(SceneNode node)
    {
        _staticWorldVersion++;
        _dirtyBrushSubtrees.Add(node);
    }

    /// <summary>Synchronously rebuilds the static world only if it has been marked dirty.</summary>
    public void RebuildStaticWorldIfDirty(Renderer renderer)
    {
        if (StaticWorldDirty)
            RebuildStaticWorld(renderer);
    }

    /// <summary>
    /// Synchronously recompiles the static world from the graph's brush nodes:
    /// each brush is placed by its node's world transform, then carve → snap →
    /// weld → BSP, then the per-chunk render meshes are rebuilt. Must run on
    /// the render thread — GPU resources are created and disposed here. Throws
    /// <see cref="InvalidOperationException"/> when any brush node's world
    /// transform is non-rigid. With no brush nodes present the derived world
    /// becomes null. Intended for load time and tests; frame-to-frame edits go
    /// through <see cref="ProcessStaticWorldCompilation"/> instead.
    /// Deliberately cache-free: this path always compiles fresh, exactly as it
    /// did before incremental carving existed (its contract is unchanged).
    /// </summary>
    public void RebuildStaticWorld(Renderer renderer)
    {
        // A background compile may still be in flight. Wait it out and drop
        // its result: if it stayed parked in _inFlightCompile, the frame pump
        // would later harvest its stale world and overwrite the synchronous
        // rebuild done below. (Carving itself is thread-safe — brush local
        // geometry, Polygon.Bounds included, is immutable — this wait is
        // purely about result ordering.) The dropped result's carve cache is
        // lost with it — the next background compile runs cold, which is
        // merely a full carve, never a correctness issue.
        if (_inFlightCompile is not null)
        {
            try { _inFlightCompile.Wait(); }
            catch (AggregateException) { /* superseded — its failure is irrelevant now */ }
            _inFlightCompile = null;

            // The dropped compile never landed, so the cells it was launched
            // for are still stale relative to the last COMPLETED compile —
            // fold them back so the diff below reports them again.
            if (_inFlightDirtyCells is not null)
            {
                MarkCellsDirty(_inFlightDirtyCells);
                _inFlightDirtyCells = null;
            }
        }

        IReadOnlyList<BrushPlacement>? placements = SnapshotBrushPlacements(
            out string? defectMessage, out List<(int Slot, BrushPlacement Placement)>? changedSlots);
        if (placements is null)
            throw new InvalidOperationException(defectMessage);

        _handledStaticWorldVersion = _staticWorldVersion;

        // Footprint bookkeeping runs on the synchronous path too, so async
        // compiles resumed afterwards diff against THIS snapshot, and the
        // dirty set stays observable (the build below is deliberately
        // dirty-agnostic — it compiles everything regardless).
        ChunkCoord[] dirtyCells = CollectDirtyCells(placements, changedSlots);
        LastCompileDirtyCells = dirtyCells;

        if (placements.Count == 0)
        {
            _staticWorldCarry = null;
            ReplaceStaticWorld(renderer, null);
            _staticWorldStructureVersion = _graphStructureVersion;
            return;
        }

        CsgWorld world = CsgWorld.Build(placements);
        // Publish first, commit the carry pairing only afterwards: a swap
        // failure (CreateMesh throwing) must leave the carry describing the
        // world that is actually rendering, with the consumed dirty cells
        // folded back so the next compile re-covers them.
        try
        {
            ReplaceStaticWorld(renderer, world);
        }
        catch
        {
            MarkCellsDirty(dirtyCells);
            throw;
        }

        // The synchronous world is a valid carry for the next background
        // compile: its snapshot is the one later dirty diffs run against.
        _staticWorldCarry = world;
        _carryStructureVersion = _graphStructureVersion;
        _staticWorldStructureVersion = _graphStructureVersion;
    }

    /// <summary>
    /// Drives the asynchronous static-world pipeline; the engine calls this
    /// once per frame on the render thread. First harvests a finished
    /// background compile — swapping in the new world and creating its GPU
    /// mesh here, where GPU resource creation is allowed — then, if brush
    /// nodes changed and nothing is in flight, snapshots the graph and
    /// launches the next compile on the thread pool. When edits arrive faster
    /// than compiles finish, each landing compile immediately triggers the
    /// next one, so a dragged brush yields progressive results.
    /// </summary>
    public void ProcessStaticWorldCompilation(Renderer renderer, ILogger logger)
    {
        // (a) Harvest a finished compile.
        if (_inFlightCompile is { } inFlight)
        {
            if (!inFlight.IsCompleted)
                return; // Still compiling; the previous world keeps rendering.
            _inFlightCompile = null;

            if (inFlight.IsFaulted)
            {
                // Keep the last good world. Logged once here, not per frame:
                // the version was marked handled at launch, so nothing retries
                // until the next MarkStaticWorldDirty.
                logger.LogError(inFlight.Exception,
                    "Static world compile v{Version} failed; keeping the previous world", _inFlightVersion);

                // The failed compile consumed its dirty-cell set without
                // refreshing those cells — restore it so the next compile
                // (re-armed by the next edit) covers them again. The carry it
                // consumed is restored from StaticWorld: the same immutable
                // world object, and the restored dirty set covers everything
                // that changed since ITS snapshot (last-snapshot diff plus the
                // failed compile's cells), so the trusted-diff contract holds.
                if (_inFlightDirtyCells is not null)
                    MarkCellsDirty(_inFlightDirtyCells);
                _inFlightDirtyCells = null;
                _staticWorldCarry = StaticWorld;
                _carryStructureVersion = _staticWorldStructureVersion;
            }
            else
            {
                StaticWorldCompilation result = inFlight.Result;
                // Publish FIRST, commit the compile bookkeeping only after the
                // swap succeeded. ReplaceStaticWorld can throw mid-swap
                // (CreateMesh — a survivable failure by its own contract), and
                // committing beforehand would pair the OLD published world
                // with the NEW world's carry/dirty/version state: the dropped
                // dirty set would never be re-covered, so a later faulted
                // compile would restore a carry whose dirty cells no longer
                // cover every changed placement — silently stale geometry in
                // release builds, an unrecoverable VerifyTrustedDiff fault
                // loop in debug builds.
                try
                {
                    ReplaceStaticWorld(renderer, result.World);
                }
                catch
                {
                    // Same restoration as a faulted compile: fold the consumed
                    // dirty cells back and re-pair the carry with the world
                    // that is still actually published.
                    if (_inFlightDirtyCells is not null)
                        MarkCellsDirty(_inFlightDirtyCells);
                    _inFlightDirtyCells = null;
                    _staticWorldCarry = StaticWorld;
                    _carryStructureVersion = _staticWorldStructureVersion;
                    throw;
                }

                _inFlightDirtyCells = null;
                // The landed world is the next compile's carry: its per-brush
                // and per-cell state is what lets the next edit re-run only
                // its own neighbourhood.
                _staticWorldCarry = result.World;
                _carryStructureVersion = _inFlightStructureVersion;
                _staticWorldStructureVersion = _inFlightStructureVersion;

                // Aggregate the stats and log at a bounded cadence (see the
                // field comments): the steady-state cost per harvest is three
                // integer adds and a tick read — no logging, no boxing.
                CsgCacheStats stats = result.World.CacheStats ?? default;
                _compileStatsLanded++;
                _compileStatsCacheHits += stats.Hits;
                _compileStatsCacheMisses += stats.Misses;
                long now = Environment.TickCount64;
                if (now >= _nextCompileStatsLogTicks)
                {
                    _nextCompileStatsLogTicks = now + CompileStatsLogIntervalMs;
                    logger.LogDebug(
                        "Static world compiles: {Landed} landed since last report, latest v{Version}: " +
                        "{Surfaces} surfaces in {DurationMs:0.00} ms " +
                        "(compile #{Count}, carve cache {Hits} hits / {Misses} misses)",
                        _compileStatsLanded, _inFlightVersion, result.World.SurfaceCount, result.DurationMs,
                        StaticWorldCompileCount, _compileStatsCacheHits, _compileStatsCacheMisses);
                    _compileStatsLanded = 0;
                    _compileStatsCacheHits = 0;
                    _compileStatsCacheMisses = 0;
                }
            }
        }

        // (b) Launch the next compile if edits arrived since the last launch.
        if (_staticWorldVersion == _handledStaticWorldVersion)
            return;

        int version = _staticWorldVersion;
        IReadOnlyList<BrushPlacement>? placements = SnapshotBrushPlacements(
            out string? defectMessage, out List<(int Slot, BrushPlacement Placement)>? changedSlots);

        // Handled either way: a defective snapshot must not retry-spam every
        // frame — the next MarkStaticWorldDirty re-arms the pump.
        _handledStaticWorldVersion = version;

        if (placements is null)
        {
            // A continuously animating brush re-arms the pump every frame, so a
            // persistent defect on some other node would otherwise log per
            // frame. Repeat the message only when the defect itself changes.
            if (defectMessage != _lastLoggedSnapshotDefect)
            {
                _lastLoggedSnapshotDefect = defectMessage;
                logger.LogError(
                    "Static world compile v{Version} skipped: {Defect} Keeping the last good world.",
                    version, defectMessage);
            }
            return;
        }
        _lastLoggedSnapshotDefect = null;

        // Diff this snapshot against the previous one BEFORE branching on the
        // placement count: an emptied scene must still dirty the cells the
        // departed brushes covered.
        ChunkCoord[] dirtyCells = CollectDirtyCells(placements, changedSlots);
        LastCompileDirtyCells = dirtyCells;

        if (placements.Count == 0)
        {
            // No brush nodes: the derived world is null. Cheap enough to do
            // synchronously — no carve, just releasing the old chunk meshes.
            // The carry is dropped too: every placement left the world, so
            // there is nothing to derive the next compile from.
            _staticWorldCarry = null;
            ReplaceStaticWorld(renderer, null);
            _staticWorldStructureVersion = _graphStructureVersion;
            return;
        }

        _inFlightVersion = version;
        _inFlightDirtyCells = dirtyCells;
        _inFlightStructureVersion = _graphStructureVersion;
        // The snapshot placements are immutable (paged copy-on-write — see
        // SnapshotBrushPlacements), so the task and the render thread share
        // them safely; the carried world transfers to the task, and the
        // render thread re-acquires a carry when the result is harvested (the
        // landed world) or the compile faults (StaticWorld). The dirty-cell
        // array is shared read-only: the task carries it into the built
        // world, the render thread only re-reads it on a fault.
        //
        // The trusted-diff contract (see CsgWorld.Build(placements, dirty,
        // previous)) holds by construction here: dirtyCells is the footprint
        // diff of every changed node since the carry's snapshot, and the
        // structure-version check proves the traversal order is unchanged.
        // After a structural edit the carry is still handed over, but flagged
        // untrusted — the compile then uses only its lazily derived validation
        // caches, which are safe under any placement order.
        CsgWorld? carry = _staticWorldCarry;
        bool orderStable = carry is not null && _carryStructureVersion == _graphStructureVersion;
        _staticWorldCarry = null;
        _inFlightCompile = Task.Run(
            () => CompileStaticWorld(placements, dirtyCells, carry, orderStable));
    }

    // Pure CPU compile of one snapshot; runs on a thread-pool thread. Reads
    // ONLY the snapshot: placement transforms plus each brush's immutable
    // local planes/faces/bounds (all fully computed at construction, so any
    // number of carve workers may read them) — and the immutable previous
    // world handed over at launch, which it reads but never mutates. It must
    // never touch Scene, SceneNode, Brush.Transform, or any renderer/GPU
    // object — GPU upload happens when the render thread harvests the result
    // (the per-cell mesh ARRAYS are built here, inside CsgWorld.Build; only
    // their upload is render-thread work).
    private static StaticWorldCompilation CompileStaticWorld(
        IReadOnlyList<BrushPlacement> placements, ChunkCoord[] dirtyCells, CsgWorld? previous, bool orderStable)
    {
        var stopwatch = Stopwatch.StartNew();
        CsgWorld world = orderStable
            ? CsgWorld.Build(placements, dirtyCells, previous)
            : CsgWorld.Build(
                placements, dirtyCells,
                previous?.CompileCache, previous?.WeldCache, previous?.BspCache, previous?.MeshCache);
        return new StaticWorldCompilation(world, stopwatch.Elapsed.TotalMilliseconds);
    }

    // Render thread only: swaps the derived world and updates the per-chunk
    // GPU mesh map — creating meshes ONLY for cells whose artifact changed and
    // destroying ONLY the replaced/removed cells' meshes, so a one-brush edit
    // touches a handful of GPU buffers regardless of world size. Buffers are
    // per (cell, material) since W5, which changes the constant and nothing
    // else: the change detector is still the artifact instance, so a cell is
    // carried or re-uploaded as a whole. Passing a null world clears the
    // derived data (the no-brush-nodes case).
    //
    // Two shapes. A PATCHED world whose base is exactly the currently
    // published world carries its per-cell mesh delta, and the swap applies
    // just those entries — O(changed cells) of CPU work per landed compile,
    // matching the GPU churn (the map rebuild below would otherwise cost
    // O(world chunks) of dictionary/list traffic per drag frame). Everything
    // else — full builds, validated fallbacks, clears, or a patched world
    // whose base is not what is published (possible after a failed swap) —
    // takes the full rebuild, whose per-cell instance diff is correct against
    // any previous map state.
    private void ReplaceStaticWorld(Renderer renderer, CsgWorld? world)
    {
        if (world is not null && StaticWorld is { } published &&
            world.ChunkMeshDelta is { } delta && world.PatchBaseId == published.Id)
        {
            ApplyChunkMeshDelta(renderer, world, delta);
            return;
        }

        IReadOnlyList<ChunkMesh> artifacts = world?.ChunkMeshes ?? Array.Empty<ChunkMesh>();

        // Phase 1 — create before destroy, the same atomicity stance the old
        // single-mesh swap had, now per cell: every replacement GPU mesh is
        // created before any old mesh is destroyed, so if CreateMesh throws
        // the created replacements are rolled back and the last good world —
        // every chunk of it — stays intact and renderable. The brief overlap
        // of old and new meshes in GPU memory is the price of that atomicity.
        var replacement = new StaticWorldChunkMesh[artifacts.Count];
        var createdChunks = new List<StaticWorldSubmesh[]>();
        var carriedCells = new HashSet<ChunkCoord>();
        try
        {
            for (int i = 0; i < artifacts.Count; i++)
            {
                ChunkMesh artifact = artifacts[i];
                if (_staticWorldChunkMeshes.TryGetValue(artifact.Coord, out StaticWorldChunkMesh existing) &&
                    ReferenceEquals(existing.Artifact, artifact))
                {
                    // Clean cell carried forward by the compile (the mesh
                    // cache reused the artifact instance): the existing GPU
                    // meshes — all of the cell's materials — keep serving,
                    // zero churn.
                    replacement[i] = existing;
                    carriedCells.Add(artifact.Coord);
                    continue;
                }

                StaticWorldSubmesh[] submeshes = CreateChunkSubmeshes(renderer, artifact);
                createdChunks.Add(submeshes);
                replacement[i] = new StaticWorldChunkMesh(artifact, submeshes);
            }
        }
        catch
        {
            // Roll back this swap's creations; the map, the world, and every
            // old GPU mesh are untouched, so rendering continues from the
            // previous compile. (The throwing chunk rolled its own partial
            // submeshes back before rethrowing — see CreateChunkSubmeshes.)
            foreach (StaticWorldSubmesh[] submeshes in createdChunks)
                DestroyChunkSubmeshes(renderer, submeshes);
            throw;
        }

        // Phase 2 — commit: destroy exactly the replaced/removed cells' old
        // meshes (an old entry survives iff the new world carried its artifact
        // forward), then republish the map and its ordered list. DestroyMesh,
        // not Dispose: rebuilds happen on every brush edit, and a plain
        // Dispose would leave dead meshes in the renderer's tracking list
        // until shutdown (a leak proportional to edit count).
        foreach (KeyValuePair<ChunkCoord, StaticWorldChunkMesh> stale in _staticWorldChunkMeshes)
        {
            if (!carriedCells.Contains(stale.Key))
                DestroyChunkSubmeshes(renderer, stale.Value.Submeshes);
        }

        _staticWorldChunkMeshes.Clear();
        _staticWorldChunkList.Clear();
        foreach (StaticWorldChunkMesh chunk in replacement)
        {
            _staticWorldChunkMeshes.Add(chunk.Artifact.Coord, chunk);
            // The artifact list is in ascending cell order, so the rebuilt
            // list snapshot is too — the deterministic order BuildRenderView
            // emits world items in.
            _staticWorldChunkList.Add(chunk);
        }

        StaticWorld = world;
        StaticWorldCompileCount++;
    }

    // The delta swap: applies a patched compile's per-cell mesh changes to
    // the map and ordered list in place — every untouched cell's entry (the
    // overwhelming majority of a one-brush edit) is simply never visited.
    // Same create-before-destroy atomicity as the full rebuild: all
    // replacement GPU meshes are created first, and a CreateMesh throw rolls
    // them back with the map, the list, and the published world untouched.
    private void ApplyChunkMeshDelta(
        Renderer renderer, CsgWorld world, IReadOnlyList<(ChunkCoord Coord, ChunkMesh? Mesh)> delta)
    {
        // Phase 1 — create every replacement/addition, one GPU mesh per
        // (cell, material).
        var createdChunks = new List<StaticWorldSubmesh[]>(delta.Count);
        try
        {
            foreach ((_, ChunkMesh? artifact) in delta)
            {
                if (artifact is not null)
                    createdChunks.Add(CreateChunkSubmeshes(renderer, artifact));
            }
        }
        catch
        {
            foreach (StaticWorldSubmesh[] submeshes in createdChunks)
                DestroyChunkSubmeshes(renderer, submeshes);
            throw;
        }

        // Phase 2 — commit: per delta entry, destroy the replaced/removed
        // cell's old mesh and splice the map and the ordered list. The list
        // stays ascending because each entry lands at its binary-searched
        // position (replacements in place, insertions/removals shifting —
        // rare border-crossing edits only).
        int createdIndex = 0;
        foreach ((ChunkCoord coord, ChunkMesh? artifact) in delta)
        {
            bool existed = _staticWorldChunkMeshes.TryGetValue(coord, out StaticWorldChunkMesh old);
            if (existed)
                DestroyChunkSubmeshes(renderer, old.Submeshes);

            int position = ChunkListLowerBound(coord);
            if (artifact is null)
            {
                if (existed)
                {
                    _staticWorldChunkMeshes.Remove(coord);
                    _staticWorldChunkList.RemoveAt(position);
                }
                continue;
            }

            var entry = new StaticWorldChunkMesh(artifact, createdChunks[createdIndex++]);
            _staticWorldChunkMeshes[coord] = entry;
            if (existed)
                _staticWorldChunkList[position] = entry;
            else
                _staticWorldChunkList.Insert(position, entry);
        }

        StaticWorld = world;
        StaticWorldCompileCount++;
    }

    // Render thread only: uploads one cell's artifact as one GPU mesh per
    // material it wears and resolves each one's material once, here — the
    // single point where the compile's pure-value material ids become asset
    // objects (see ResolveWorldMaterial).
    //
    // Atomicity is per CELL, mirroring the swap's per-cell stance one level
    // down: a throw partway through a cell's materials destroys that cell's
    // already-created meshes before propagating, so the caller only ever has
    // to roll back whole cells and no half-uploaded cell can leak.
    private StaticWorldSubmesh[] CreateChunkSubmeshes(Renderer renderer, ChunkMesh artifact)
    {
        IReadOnlyList<ChunkSubmesh> sources = artifact.Submeshes;
        var submeshes = new StaticWorldSubmesh[sources.Count];
        int created = 0;
        try
        {
            for (; created < submeshes.Length; created++)
            {
                ChunkSubmesh source = sources[created];
                Mesh gpuMesh = renderer.CreateMesh(source.Vertices, source.Indices, VertexAttribute.StandardLayout);
                submeshes[created] = new StaticWorldSubmesh(
                    source, gpuMesh, ResolveWorldMaterial(source.Material));
            }
        }
        catch
        {
            for (int i = 0; i < created; i++)
                renderer.DestroyMesh(submeshes[i].Mesh);
            throw;
        }

        return submeshes;
    }

    // Destroys every GPU mesh of one chunk entry. DestroyMesh, not Dispose:
    // the renderer's tracking list must lose them too (see Renderer.DestroyMesh).
    private static void DestroyChunkSubmeshes(Renderer renderer, StaticWorldSubmesh[] submeshes)
    {
        for (int i = 0; i < submeshes.Length; i++)
            renderer.DestroyMesh(submeshes[i].Mesh);
    }

    // First index in the ordered chunk list whose coordinate is >= coord.
    private int ChunkListLowerBound(ChunkCoord coord)
    {
        int lo = 0, hi = _staticWorldChunkList.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_staticWorldChunkList[mid].Artifact.Coord.CompareTo(coord) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // How a fast (retained-snapshot) capture attempt resolved.
    private enum FastSnapshotResult
    {
        Patched,          // _changedSlotScratch holds the edited slots' new placements
        Defect,           // a dirty node's world transform is non-rigid — reject the snapshot
        FullWalkRequired, // the retained snapshot cannot describe this edit — re-walk
    }

    // Captures one immutable placement per brush node — the node's world
    // matrix at this instant. The background compile reads only this
    // snapshot, so the live scene is free to keep mutating while it runs.
    // Returns null (with a message naming the node and the defect) when any
    // captured brush node's world transform is non-rigid; the retained
    // snapshot state and the pending dirty-subtree set are left untouched
    // then, so the eventual retry still covers every pending edit.
    //
    // FAST PATH (the per-frame drag shape): when the graph's structure is
    // unchanged since the retained snapshot, only the nodes that reported an
    // edit are re-visited — their subtrees re-captured, validated, and
    // patched into the retained placement list by paged copy-on-write. Cost
    // is O(edit neighbourhood), never O(world). `changedSlots` then reports
    // exactly the patched slots for the footprint diff. A full walk (first
    // snapshot, structural edit, external MarkStaticWorldDirty, or any
    // anomaly) re-captures everything and reports null `changedSlots`.
    private IReadOnlyList<BrushPlacement>? SnapshotBrushPlacements(
        out string? defectMessage, out List<(int Slot, BrushPlacement Placement)>? changedSlots)
    {
        if (!_snapshotForceFull && _snapshotPlacements is not null &&
            _snapshotStructureVersion == _graphStructureVersion)
        {
            switch (TryCollectChangedSlots(out defectMessage))
            {
                case FastSnapshotResult.Defect:
                    changedSlots = null;
                    return null;
                case FastSnapshotResult.Patched:
                    changedSlots = _changedSlotScratch;
                    if (changedSlots.Count > 0)
                        _snapshotPlacements = _snapshotPlacements.WithReplacements(changedSlots);
                    _dirtyBrushSubtrees.Clear();
                    return _snapshotPlacements;
            }
            // FullWalkRequired: fall through to the walk below.
        }

        changedSlots = null;
        return SnapshotFullWalk(out defectMessage);
    }

    // The fast path's capture: expand each dirty subtree to its brush nodes,
    // validate their world transforms, and collect their slots' replacement
    // placements into _changedSlotScratch. Mutates ONLY the scratch
    // containers, so a defect (or an anomaly) leaves every retained structure
    // untouched.
    private FastSnapshotResult TryCollectChangedSlots(out string? defectMessage)
    {
        defectMessage = null;
        _changedSlotScratch.Clear();
        _changedSlotSeen.Clear();
        foreach (SceneNode root in _dirtyBrushSubtrees)
        {
            // A dirty node that left the scene would have bumped the
            // structure version (membership events), so this never fires; it
            // guards the fast path against any future dirtying source that
            // forgets that contract.
            if (!ReferenceEquals(root.Owner, this))
                return FastSnapshotResult.FullWalkRequired;

            foreach (SceneNode node in root.Traverse())
            {
                // Admitted brushes only. Testing IsStaticWorldBrush rather
                // than `Brush is not null` is what keeps a part brush inside a
                // dirty subtree from forcing the O(world) walk on every drag
                // frame: an un-admitted node holds no slot, so it would fall
                // into the "brush appeared since the snapshot" bail below.
                if (!node.IsStaticWorldBrush)
                {
                    // A slot-holding node that is no longer an admitted brush
                    // means a detach — or a conversion — that the force-full
                    // signal did not cover: the placement count changed, every
                    // later slot shifted, so re-walk. (Both routes do signal
                    // it: detach through MarkStaticWorldDirty, conversion
                    // through MarkAdmissionChanged. This is the net.)
                    if (_snapshotSlots.ContainsKey(node))
                        return FastSnapshotResult.FullWalkRequired;
                    continue;
                }

                Bsp.Brush brush = node.Brush!;

                if (!_snapshotSlots.TryGetValue(node, out int slot))
                    return FastSnapshotResult.FullWalkRequired; // brush appeared since the snapshot

                if (!_changedSlotSeen.Add(slot))
                    continue; // nested dirty roots cover the same node once

                Matrix4x4 world = node.WorldMatrix;
                string? defect = DescribeNonRigidDefect(world);
                if (defect is not null)
                {
                    defectMessage = DescribeBrushNodeDefect(node, defect);
                    return FastSnapshotResult.Defect;
                }

                _changedSlotScratch.Add((slot, new BrushPlacement(brush, world)));
            }
        }
        return FastSnapshotResult.Patched;
    }

    // The full capture: walks the whole graph, validates every brush node,
    // and — only on success — commits the retained snapshot (node list, slot
    // map, paged placements, structure version) and drains the dirty-subtree
    // set. O(world); paid only for structural edits, external dirtying, and
    // the first snapshot — never for the per-frame drag.
    private IReadOnlyList<BrushPlacement>? SnapshotFullWalk(out string? defectMessage)
    {
        var placements = new List<BrushPlacement>();
        var nodes = new List<SceneNode>();
        foreach (SceneNode node in Nodes)
        {
            // Admitted brushes only: a part brush is not compiled, so it holds
            // no slot and contributes no placement. Rigidity validation
            // therefore stops seeing part brushes here — they stay rigid by the
            // tool-level refusal instead, which reads the kind-BLIND
            // SubtreeBrushCount precisely so that it still covers them.
            if (node.IsStaticWorldBrush)
            {
                Bsp.Brush brush = node.Brush!;
                Matrix4x4 world = node.WorldMatrix;
                string? defect = DescribeNonRigidDefect(world);
                if (defect is not null)
                {
                    defectMessage = DescribeBrushNodeDefect(node, defect);
                    return null;
                }

                // The node's world transform IS the brush's placement
                // (scene-graph-spine architecture). Whatever Transform the
                // brush itself carries — e.g. the centering translation
                // Brush.CreateBox(min, max) stores — is ignored for
                // node-attached brushes, so author them with node-local
                // (typically centred) extents.
                placements.Add(new BrushPlacement(brush, world));
                nodes.Add(node);
            }
        }

        _snapshotNodes.Clear();
        _snapshotNodes.AddRange(nodes);
        _snapshotSlots.Clear();
        for (int i = 0; i < nodes.Count; i++)
            _snapshotSlots[nodes[i]] = i;
        _snapshotPlacements = PagedArray<BrushPlacement>.From(placements);
        _snapshotStructureVersion = _graphStructureVersion;
        _snapshotForceFull = false;
        _dirtyBrushSubtrees.Clear();

        defectMessage = null;
        return _snapshotPlacements;
    }

    private static string DescribeBrushNodeDefect(SceneNode node, string defect) =>
        $"Brush node '{node.Name}' has a non-rigid world transform ({defect}). " +
        "Brush node transforms must be rigid — rotation and translation only; " +
        "size belongs in the brush planes, not in node scale.";

    /// <summary>Enumerates every node in the scene, depth-first from the root.</summary>
    public IEnumerable<SceneNode> Nodes => Root.Traverse();

    /// <summary>
    /// How far a brush node's world matrix may deviate from a rigid
    /// (rotation + translation) transform before the compile rejects it.
    /// </summary>
    private const float RigidTransformTolerance = 1e-4f;

    // The whole CSG epsilon scheme assumes rigid brush transforms: under scale
    // or shear, Plane.Transform returns non-normalized planes and every
    // distance tolerance silently changes meaning. The snapshot is the single
    // point where node transforms enter the brush pipeline, so rigidity is
    // enforced there rather than trusted.
    //
    // Returns a description of why the matrix is not rigid, or null when it is.
    // A rigid matrix is affine with an orthonormal, positively-oriented basis;
    // singular matrices necessarily fail the unit-length or orthogonality check.
    private static string? DescribeNonRigidDefect(in Matrix4x4 m)
    {
        const float tolerance = RigidTransformTolerance;

        // Finiteness must be screened FIRST and explicitly: every predicate
        // below is written "comparison > tolerance => defect", and all
        // comparisons against NaN are false, so a NaN matrix would pass every
        // later check as "rigid" and feed NaN geometry into carve/snap/weld/BSP.
        // Non-finite values propagate through addition (and Inf + -Inf is NaN),
        // so one non-finite element anywhere poisons the sum; finite elements
        // could only overflow it near float.MaxValue, far outside any sane
        // transform.
        if (!float.IsFinite(
                m.M11 + m.M12 + m.M13 + m.M14 +
                m.M21 + m.M22 + m.M23 + m.M24 +
                m.M31 + m.M32 + m.M33 + m.M34 +
                m.M41 + m.M42 + m.M43 + m.M44))
            return "non-finite elements: NaN or Infinity in the matrix";

        if (MathF.Abs(m.M14) > tolerance || MathF.Abs(m.M24) > tolerance ||
            MathF.Abs(m.M34) > tolerance || MathF.Abs(m.M44 - 1f) > tolerance)
            return "projective components";

        // Row-vector convention: the basis vectors are the upper 3x3 rows.
        var r0 = new Vector3(m.M11, m.M12, m.M13);
        var r1 = new Vector3(m.M21, m.M22, m.M23);
        var r2 = new Vector3(m.M31, m.M32, m.M33);

        if (MathF.Abs(r0.Length() - 1f) > tolerance ||
            MathF.Abs(r1.Length() - 1f) > tolerance ||
            MathF.Abs(r2.Length() - 1f) > tolerance)
            return "scale or a singular basis: basis vectors are not unit length";

        if (MathF.Abs(Vector3.Dot(r0, r1)) > tolerance ||
            MathF.Abs(Vector3.Dot(r1, r2)) > tolerance ||
            MathF.Abs(Vector3.Dot(r2, r0)) > tolerance)
            return "shear: basis vectors are not orthogonal";

        // An orthonormal basis has determinant ±1; -1 is a mirror, which turns
        // every outward brush plane inside-out.
        if (Vector3.Dot(Vector3.Cross(r0, r1), r2) < 0f)
            return "reflection: negative determinant";

        return null;
    }

    // CPU-side output of one background compile: the built world, whose
    // per-cell mesh arrays (CsgWorld.ChunkMeshes) are ready for GPU upload on
    // the render thread. Immutable by construction; crosses threads exactly
    // once, through the completed Task.
    private sealed record StaticWorldCompilation(CsgWorld World, double DurationMs);
}
