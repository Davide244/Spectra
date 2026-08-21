using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;

// The BVH's structural invariants (and a few other scene internals) are
// verified directly by the headless scene test suite.
[assembly: InternalsVisibleTo("SpectraEngine.Bsp.Tests")]
// The editing suite drives the renderer's framebuffer latch (internal, since
// only the engine's main thread may publish to it) to prove the editor input
// adapter reads the viewport from it.
[assembly: InternalsVisibleTo("SpectraEngine.Editing.Tests")]

namespace SpectraEngine.Core.Scene;

/// <summary>
/// Result of a <see cref="Scene.Raycast"/>: the nearest spatial node the ray
/// struck, with the world-space hit point and surface normal.
/// </summary>
/// <param name="Node">The scene node whose geometry was hit.</param>
/// <param name="Distance">World-space distance from the ray origin to the hit (the ray parameter t).</param>
/// <param name="Point">World-space hit position.</param>
/// <param name="Normal">Unit world-space surface normal at the hit, facing the ray.</param>
public readonly record struct SceneRaycastHit(SceneNode Node, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>
/// A dynamic bounding-volume hierarchy over a scene's <em>spatial</em> nodes —
/// the nodes carrying a <see cref="SceneNode.MeshRenderer"/> or a
/// <see cref="SceneNode.Brush"/> — powering <see cref="Scene.Raycast"/> and
/// <see cref="Scene.QueryFrustum"/> without walking the whole graph.
/// </summary>
/// <remarks>
/// <b>Structure:</b> a binary AABB tree with one leaf per spatial node, stored
/// as struct nodes in a pooled, free-listed array (int indices, no per-node
/// heap objects). Leaves are inserted at the cheapest position by the
/// Box2b2-style surface-area heuristic descent. Leaf boxes are <em>fat</em>:
/// the node's tight world AABB expanded by <see cref="FatMargin"/> world units
/// on every side, so small movements only update the cached tight box instead
/// of restructuring the tree.
/// <para>
/// <b>Maintenance is event-driven and lazy.</b> The scene's membership events
/// insert/remove leaves; component hooks on the <c>MeshRenderer</c>/<c>Brush</c>
/// setters cover nodes that become (non-)spatial while owned; transform edits
/// and same-scene reparents mark the affected leaves dirty. Dirty leaves are
/// refit in a batch at the start of the next query — re-inserted only when the
/// true AABB escaped its fat box.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the <see cref="Scene"/> that owns
/// it — every mutation arrives through scene-graph edits, which are
/// single-threaded by the engine's threading contract, so no locks are needed.
/// </para>
/// </remarks>
internal sealed class SceneBvh
{
    /// <summary>
    /// How far (world units) a leaf's fat AABB extends beyond the node's tight
    /// world AABB on every side. Larger values tolerate more movement before a
    /// leaf must be re-inserted but make traversal visit more nodes; 0.2 suits
    /// the editor's typical drag speeds and brush sizes.
    /// </summary>
    internal const float FatMargin = 0.2f;

    private const int Null = -1;

    // A mesh created without CPU-side positions has a meaningless (default)
    // LocalBounds, so such nodes fall back to a unit box around the node
    // origin — documented on Scene.Raycast/QueryFrustum.
    private static readonly Aabb UnitFallbackBox = new(new Vector3(-0.5f), new Vector3(0.5f));

    // One tree node. Internal nodes own the union of their children's fat
    // boxes; leaves own their scene node's fat box plus the cached tight box
    // (used for exact-leaf query tests and escape detection). Struct-in-array
    // keeps the tree cache-friendly and AOT-trivial.
    private struct Node
    {
        public Aabb FatBox;
        public Aabb TightBox;   // leaves only
        public int Parent;      // doubles as nothing; Child1 doubles as the free-list next
        public int Child1;
        public int Child2;
        public SceneNode? Leaf; // null for internal and free nodes
        public bool Dirty;      // leaf is queued in _dirtyNodes
        public bool CountsAsMesh; // leaf's scene node carried a MeshRenderer when last counted
    }

    private Node[] _nodes;
    private int _freeList;
    private int _root = Null;

    // SceneNode -> leaf index. Only maintenance touches it (queries never
    // allocate or look up here except the O(1) dirty-flush check).
    private readonly Dictionary<SceneNode, int> _leaves = [];

    // Leaves marked dirty since the last flush, deduplicated by Node.Dirty.
    // Holds SceneNode references, not leaf indices: indices can be freed and
    // reused between the mark and the flush, references cannot dangle.
    private readonly List<SceneNode> _dirtyNodes = [];

    // Reused traversal stacks so steady-state queries and dirty-marking walks
    // allocate nothing (they only grow, on the rare deep tree).
    private int[] _traversalStack = new int[64];
    private readonly List<SceneNode> _subtreeStack = [];

    internal SceneBvh(Scene scene)
    {
        // Seed the pool small and chain every node onto the free list; the
        // pool doubles on demand.
        _nodes = new Node[16];
        _freeList = Null;
        for (int i = _nodes.Length - 1; i >= 0; i--)
        {
            _nodes[i].Child1 = _freeList;
            _nodes[i].Parent = Null;
            _freeList = i;
        }

        // Membership events keep the leaf set in sync with the graph; the
        // transform event marks moved leaves (and their spatial descendants,
        // whose world matrices changed too) for the lazy refit.
        scene.NodeAdded += OnNodeAdded;
        scene.NodeRemoved += OnNodeRemoved;
        scene.NodeTransformChanged += OnSubtreeMoved;
    }

    /// <summary>Number of spatial nodes currently indexed (test/diagnostic hook).</summary>
    internal int LeafCount => _leaves.Count;

    // Leaves whose scene node carries a MeshRenderer — the denominator behind
    // RenderView.TotalCount. Maintained incrementally at the three points a
    // leaf's mesh-ness can change (insert, remove, component swap) so reading
    // it per frame is O(1) instead of a walk over the leaf set.
    private int _meshLeafCount;

    /// <summary>Number of indexed nodes carrying a <see cref="SceneNode.MeshRenderer"/>.</summary>
    internal int MeshLeafCount => _meshLeafCount;

    // --- Event-driven maintenance -------------------------------------------

    private static bool IsSpatial(SceneNode node) => node.MeshRenderer is not null || node.Brush is not null;

    private void OnNodeAdded(SceneNode node)
    {
        if (IsSpatial(node))
            Insert(node);
    }

    private void OnNodeRemoved(SceneNode node)
    {
        if (_leaves.Remove(node, out int leaf))
        {
            if (_nodes[leaf].CountsAsMesh)
                _meshLeafCount--;
            RemoveLeafFromTree(leaf);
            FreeNode(leaf);
        }
    }

    // Handles both the NodeTransformChanged event and same-scene reparents
    // (Scene.OnNodeSubtreeMoved): every spatial node in the moved subtree has a
    // changed world matrix, so each of their leaves goes onto the dirty list.
    // The walk reuses one stack; the graph must not mutate mid-walk, which the
    // scene events' re-entrancy rule already guarantees.
    internal void OnSubtreeMoved(SceneNode node)
    {
        if (_leaves.Count == 0)
            return; // nothing indexed — don't pay for the walk

        _subtreeStack.Add(node);
        while (_subtreeStack.Count > 0)
        {
            int last = _subtreeStack.Count - 1;
            SceneNode current = _subtreeStack[last];
            _subtreeStack.RemoveAt(last);

            MarkDirty(current);

            IReadOnlyList<SceneNode> children = current.Children;
            for (int i = 0; i < children.Count; i++)
                _subtreeStack.Add(children[i]);
        }
    }

    /// <summary>
    /// Hook for the <see cref="SceneNode.MeshRenderer"/>/<see cref="SceneNode.Brush"/>
    /// setters (via <see cref="Scene"/>): a component was assigned, cleared, or
    /// replaced on an already-owned node, which may change whether the node is
    /// spatial at all — or just its bounds.
    /// </summary>
    internal void OnSpatialComponentChanged(SceneNode node)
    {
        bool spatial = IsSpatial(node);
        if (_leaves.TryGetValue(node, out int leaf))
        {
            if (spatial)
            {
                // Component swap — the bounds source changed, and possibly the
                // node's mesh-ness too (e.g. a brush node gaining a renderer).
                bool countsAsMesh = node.MeshRenderer is not null;
                if (countsAsMesh != _nodes[leaf].CountsAsMesh)
                {
                    _nodes[leaf].CountsAsMesh = countsAsMesh;
                    _meshLeafCount += countsAsMesh ? 1 : -1;
                }
                MarkDirty(node);
            }
            else
            {
                OnNodeRemoved(node);
            }
        }
        else if (spatial)
        {
            Insert(node);
        }
    }

    private void MarkDirty(SceneNode node)
    {
        if (_leaves.TryGetValue(node, out int leaf) && !_nodes[leaf].Dirty)
        {
            _nodes[leaf].Dirty = true;
            _dirtyNodes.Add(node);
        }
    }

    // Batch refit at the start of every query: recompute each dirty leaf's
    // tight box; when it still fits inside the leaf's fat box only the cached
    // tight box changes (ancestor boxes contain the unchanged fat box, so the
    // tree stays valid untouched) — only an escaped leaf is re-inserted.
    private void FlushDirtyLeaves()
    {
        if (_dirtyNodes.Count == 0)
            return;

        for (int i = 0; i < _dirtyNodes.Count; i++)
        {
            SceneNode sceneNode = _dirtyNodes[i];
            // A leaf removed (or removed and freshly re-inserted) since it was
            // marked either no longer exists or is already clean — skip.
            if (!_leaves.TryGetValue(sceneNode, out int leaf) || !_nodes[leaf].Dirty)
                continue;
            _nodes[leaf].Dirty = false;

            Aabb tight = ComputeWorldBounds(sceneNode);
            if (ContainsBox(_nodes[leaf].FatBox, tight))
            {
                _nodes[leaf].TightBox = tight;
                continue;
            }

            RemoveLeafFromTree(leaf);
            _nodes[leaf].TightBox = tight;
            _nodes[leaf].FatBox = tight.Expanded(FatMargin);
            InsertLeafIntoTree(leaf);
        }
        _dirtyNodes.Clear();
    }

    // --- World bounds --------------------------------------------------------

    // The tight world AABB the index maintains for a spatial node: the union of
    // its brush and mesh contributions, each transformed by the node's world
    // matrix. Mesh nodes use the mesh's CPU-side LocalBounds; a mesh without
    // CPU positions falls back to a unit box around the node origin.
    private static Aabb ComputeWorldBounds(SceneNode node)
    {
        Matrix4x4 world = node.WorldMatrix;

        bool has = false;
        Aabb result = default;
        if (node.Brush is { } brush)
        {
            result = brush.LocalBounds.Transform(world);
            has = true;
        }
        if (node.MeshRenderer is { } meshRenderer)
        {
            Aabb meshBox = MeshLocalBounds(meshRenderer.Mesh).Transform(world);
            result = has ? Union(result, meshBox) : meshBox;
            has = true;
        }
        // Only spatial nodes reach here; a degenerate point box at the node
        // origin is a safe fallback should that invariant ever break.
        return has ? result : new Aabb(world.Translation, world.Translation);
    }

    private static Aabb MeshLocalBounds(Mesh mesh) =>
        mesh.Positions.Count > 0 ? mesh.LocalBounds : UnitFallbackBox;

    // --- Node pool -----------------------------------------------------------

    private int AllocateNode()
    {
        if (_freeList == Null)
        {
            // Grow the pool and chain the new tail onto the free list.
            int oldCapacity = _nodes.Length;
            Array.Resize(ref _nodes, oldCapacity * 2);
            for (int i = _nodes.Length - 1; i >= oldCapacity; i--)
            {
                _nodes[i].Child1 = _freeList;
                _nodes[i].Parent = Null;
                _freeList = i;
            }
        }

        int index = _freeList;
        _freeList = _nodes[index].Child1;
        _nodes[index].Parent = Null;
        _nodes[index].Child1 = Null;
        _nodes[index].Child2 = Null;
        _nodes[index].Leaf = null;
        _nodes[index].Dirty = false;
        _nodes[index].CountsAsMesh = false;
        return index;
    }

    private void FreeNode(int index)
    {
        _nodes[index].Leaf = null;
        _nodes[index].Dirty = false;
        _nodes[index].CountsAsMesh = false;
        _nodes[index].Child1 = _freeList;
        _freeList = index;
    }

    // --- Tree structure ------------------------------------------------------

    private void Insert(SceneNode sceneNode)
    {
        Aabb tight = ComputeWorldBounds(sceneNode);
        int leaf = AllocateNode();
        _nodes[leaf].Leaf = sceneNode;
        _nodes[leaf].TightBox = tight;
        _nodes[leaf].FatBox = tight.Expanded(FatMargin);
        if (sceneNode.MeshRenderer is not null)
        {
            _nodes[leaf].CountsAsMesh = true;
            _meshLeafCount++;
        }
        _leaves.Add(sceneNode, leaf);
        InsertLeafIntoTree(leaf);
    }

    // Box2D-style cheapest insertion: descend toward the child whose subtree
    // grows the least (surface-area heuristic), stopping where creating a new
    // parent is cheaper than pushing the leaf further down.
    private void InsertLeafIntoTree(int leaf)
    {
        if (_root == Null)
        {
            _root = leaf;
            _nodes[leaf].Parent = Null;
            return;
        }

        Aabb leafBox = _nodes[leaf].FatBox;

        int index = _root;
        while (_nodes[index].Leaf is null)
        {
            int child1 = _nodes[index].Child1;
            int child2 = _nodes[index].Child2;

            float area = SurfaceArea(_nodes[index].FatBox);
            float combinedArea = SurfaceArea(Union(_nodes[index].FatBox, leafBox));

            // Cost of making a new parent for this node and the leaf, vs. the
            // cost of descending into either child (each descent inherits the
            // enlargement it forces on this node's box).
            float costHere = 2f * combinedArea;
            float inheritance = 2f * (combinedArea - area);
            float cost1 = DescendCost(child1, leafBox) + inheritance;
            float cost2 = DescendCost(child2, leafBox) + inheritance;

            if (costHere < cost1 && costHere < cost2)
                break;

            index = cost1 < cost2 ? child1 : child2;
        }

        int sibling = index;
        int oldParent = _nodes[sibling].Parent;
        int newParent = AllocateNode();
        _nodes[newParent].Parent = oldParent;
        _nodes[newParent].FatBox = Union(leafBox, _nodes[sibling].FatBox);
        _nodes[newParent].Child1 = sibling;
        _nodes[newParent].Child2 = leaf;
        _nodes[sibling].Parent = newParent;
        _nodes[leaf].Parent = newParent;

        if (oldParent == Null)
        {
            _root = newParent;
        }
        else if (_nodes[oldParent].Child1 == sibling)
        {
            _nodes[oldParent].Child1 = newParent;
        }
        else
        {
            _nodes[oldParent].Child2 = newParent;
        }

        RefitAncestors(oldParent);
    }

    private float DescendCost(int child, in Aabb leafBox)
    {
        float unionArea = SurfaceArea(Union(_nodes[child].FatBox, leafBox));
        // A leaf child would become a sibling (its whole box counts); an
        // internal child only charges the growth it suffers.
        return _nodes[child].Leaf is not null ? unionArea : unionArea - SurfaceArea(_nodes[child].FatBox);
    }

    // Detaches a leaf from the tree, collapsing its parent onto the sibling.
    // The leaf node itself stays allocated (the refit path re-inserts it).
    private void RemoveLeafFromTree(int leaf)
    {
        if (leaf == _root)
        {
            _root = Null;
            return;
        }

        int parent = _nodes[leaf].Parent;
        int grandParent = _nodes[parent].Parent;
        int sibling = _nodes[parent].Child1 == leaf ? _nodes[parent].Child2 : _nodes[parent].Child1;

        _nodes[sibling].Parent = grandParent;
        if (grandParent == Null)
        {
            _root = sibling;
        }
        else
        {
            if (_nodes[grandParent].Child1 == parent)
                _nodes[grandParent].Child1 = sibling;
            else
                _nodes[grandParent].Child2 = sibling;
            RefitAncestors(grandParent);
        }
        FreeNode(parent);
    }

    // Re-unions ancestor boxes bottom-up after a structural change. Every
    // internal node's box is exactly the union of its children's boxes — the
    // invariant the validation walk asserts.
    private void RefitAncestors(int index)
    {
        while (index != Null)
        {
            _nodes[index].FatBox = Union(_nodes[_nodes[index].Child1].FatBox, _nodes[_nodes[index].Child2].FatBox);
            index = _nodes[index].Parent;
        }
    }

    // --- Queries -------------------------------------------------------------

    /// <summary>
    /// Fetches the index's cached tight world AABB for <paramref name="node"/>
    /// (brush bounds, mesh bounds, or their union — exactly what the index
    /// maintains), flushing pending refits first so the box reflects the
    /// node's current transform. False when the node is not indexed (not
    /// spatial, or not in this scene). Render thread only; allocation-free —
    /// consumers like the selection highlight reuse these bounds instead of
    /// recomputing them per frame.
    /// </summary>
    internal bool TryGetWorldBounds(SceneNode node, out Aabb bounds)
    {
        FlushDirtyLeaves();

        if (_leaves.TryGetValue(node, out int leaf))
        {
            bounds = _nodes[leaf].TightBox;
            return true;
        }

        bounds = default;
        return false;
    }

    /// <summary>
    /// Casts a ray against every indexed node's geometry (exact convex test for
    /// brushes, per-triangle test for meshes with CPU-side geometry) and
    /// reports the nearest hit. See <see cref="Scene.Raycast"/> for the public
    /// contract.
    /// </summary>
    public bool Raycast(in Ray3 ray, out SceneRaycastHit hit, float maxDistance = float.PositiveInfinity)
    {
        FlushDirtyLeaves();

        hit = default;
        if (_root == Null)
            return false;

        // `best` is the nearest NARROW-PHASE hit so far. Fat/tight boxes are
        // only ever pruned against it — a box's entry t is a lower bound on any
        // geometry hit inside it, so entry > best proves the subtree is
        // farther. The reverse does not hold: a box entered before `best` can
        // still contain only farther geometry, hence no early-out on box order.
        float best = maxDistance;
        bool found = false;

        int stackTop = 0;
        _traversalStack[stackTop++] = _root;

        while (stackTop > 0)
        {
            int index = _traversalStack[--stackTop];

            if (_nodes[index].Leaf is { } sceneNode)
            {
                // Cheap filter against the tight box with the CURRENT best —
                // it may have shrunk since this leaf was pushed.
                if (!RayIntersectsBox(ray, _nodes[index].TightBox, best, out _))
                    continue;

                if (sceneNode.Brush is { } brush &&
                    RaycastBrush(sceneNode, brush, ray, best, out float tBrush, out Vector3 nBrush))
                {
                    best = tBrush;
                    hit = new SceneRaycastHit(sceneNode, tBrush, ray.PointAt(tBrush), nBrush);
                    found = true;
                }
                if (sceneNode.MeshRenderer is { } meshRenderer &&
                    RaycastMesh(sceneNode, meshRenderer.Mesh, ray, best, out float tMesh, out Vector3 nMesh))
                {
                    best = tMesh;
                    hit = new SceneRaycastHit(sceneNode, tMesh, ray.PointAt(tMesh), nMesh);
                    found = true;
                }
                continue;
            }

            int child1 = _nodes[index].Child1;
            int child2 = _nodes[index].Child2;
            bool hit1 = RayIntersectsBox(ray, _nodes[child1].FatBox, best, out float t1);
            bool hit2 = RayIntersectsBox(ray, _nodes[child2].FatBox, best, out float t2);

            if (stackTop + 2 > _traversalStack.Length)
                Array.Resize(ref _traversalStack, _traversalStack.Length * 2);

            // Near child on top of the stack: visiting it first gives the
            // narrow phase the earliest chance to shrink `best` and prune the
            // far subtree.
            if (hit1 && hit2)
            {
                if (t1 <= t2)
                {
                    _traversalStack[stackTop++] = child2;
                    _traversalStack[stackTop++] = child1;
                }
                else
                {
                    _traversalStack[stackTop++] = child1;
                    _traversalStack[stackTop++] = child2;
                }
            }
            else if (hit1)
            {
                _traversalStack[stackTop++] = child1;
            }
            else if (hit2)
            {
                _traversalStack[stackTop++] = child2;
            }
        }

        return found;
    }

    /// <summary>
    /// Appends every indexed node whose tight world AABB intersects the
    /// frustum to <paramref name="results"/>. See
    /// <see cref="Scene.QueryFrustum"/> for the public contract.
    /// </summary>
    public void QueryFrustum(in Frustum frustum, List<SceneNode> results)
    {
        FlushDirtyLeaves();

        if (_root == Null)
            return;

        int stackTop = 0;
        _traversalStack[stackTop++] = _root;

        while (stackTop > 0)
        {
            int index = _traversalStack[--stackTop];

            if (_nodes[index].Leaf is { } sceneNode)
            {
                // Leaves test their TIGHT box: internal fat boxes make the walk
                // conservative (no false negatives), but the per-node verdict
                // matches a brute-force Frustum.Intersects over true bounds.
                if (frustum.Intersects(_nodes[index].TightBox))
                    results.Add(sceneNode);
                continue;
            }

            if (!frustum.Intersects(_nodes[index].FatBox))
                continue;

            if (stackTop + 2 > _traversalStack.Length)
                Array.Resize(ref _traversalStack, _traversalStack.Length * 2);
            _traversalStack[stackTop++] = _nodes[index].Child1;
            _traversalStack[stackTop++] = _nodes[index].Child2;
        }
    }

    // --- Narrow phases -------------------------------------------------------

    // Exact convex ray test in BRUSH LOCAL space: the world ray is transformed
    // into the node's local frame, then slab-clipped against every plane — max
    // entry vs. min exit. The plane producing the final entry supplies the
    // normal, mapped back to world space. Rays starting inside the brush
    // report no hit (no defined entry face).
    //
    // The GENERAL matrix inverse is used, not a rigid shortcut: only the
    // static-world snapshot enforces rigid brush placements, and the scene
    // stays live (with picking) when it rejects one — the graph itself permits
    // scale on brush nodes, so picking must stay exact under it. As in
    // RaycastMesh, the ray parameter still measures world distance because
    // only the local direction is scaled, not t; the normal maps through the
    // inverse transpose and is re-normalized.
    private static bool RaycastBrush(
        SceneNode node, Brush brush, in Ray3 ray, float best, out float t, out Vector3 normal)
    {
        t = 0f;
        normal = default;

        if (!Matrix4x4.Invert(node.WorldMatrix, out Matrix4x4 inverse))
            return false; // degenerate transform (e.g. zero scale) — nothing to hit
        Vector3 origin = Vector3.Transform(ray.Origin, inverse);
        Vector3 direction = Vector3.TransformNormal(ray.Direction, inverse);

        float tEnter = 0f;
        float tExit = best;
        int enterPlane = -1;

        IReadOnlyList<Plane> planes = brush.LocalPlanes;
        for (int i = 0; i < planes.Count; i++)
        {
            Plane plane = planes[i]; // outward normal: inside is distance <= 0
            float distance = Plane.DotCoordinate(plane, origin);
            float denom = Vector3.Dot(plane.Normal, direction);

            if (MathF.Abs(denom) < 1e-9f)
            {
                if (distance > 0f)
                    return false; // parallel to the plane and outside its half-space
                continue;
            }

            float tPlane = -distance / denom;
            if (denom < 0f)
            {
                // Moving inward through this plane: a potential entry.
                if (tPlane > tEnter)
                {
                    tEnter = tPlane;
                    enterPlane = i;
                }
            }
            else if (tPlane < tExit)
            {
                tExit = tPlane;
            }

            if (tEnter > tExit)
                return false;
        }

        if (enterPlane < 0 || tEnter >= best)
            return false;

        t = tEnter;
        normal = WorldNormal(planes[enterPlane].Normal, inverse);
        return true;
    }

    // Möller–Trumbore over the mesh's CPU-side triangles, run in MESH LOCAL
    // space (mesh node transforms may scale, so the general inverse is used;
    // the ray parameter still measures world distance because only the local
    // direction is scaled, not t). A mesh without CPU geometry falls back to a
    // ray-vs-AABB test against its local bounds (unit box when even those are
    // missing) — documented on Scene.Raycast.
    private static bool RaycastMesh(
        SceneNode node, Mesh mesh, in Ray3 ray, float best, out float t, out Vector3 normal)
    {
        t = 0f;
        normal = default;

        Matrix4x4 world = node.WorldMatrix;
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            return false; // degenerate transform (e.g. zero scale) — nothing to hit

        Vector3 origin = Vector3.Transform(ray.Origin, inverse);
        Vector3 direction = Vector3.TransformNormal(ray.Direction, inverse);

        IReadOnlyList<Vector3> positions = mesh.Positions;
        IReadOnlyList<uint> indices = mesh.Indices;
        if (positions.Count == 0 || indices.Count == 0)
        {
            if (!RaycastLocalBox(MeshLocalBounds(mesh), origin, direction, best, out t, out Vector3 boxNormal))
                return false;
            normal = WorldNormal(boxNormal, inverse);
            return true;
        }

        float bestT = best;
        Vector3 bestLocalNormal = default;
        bool found = false;

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            Vector3 a = positions[(int)indices[i]];
            Vector3 b = positions[(int)indices[i + 1]];
            Vector3 c = positions[(int)indices[i + 2]];

            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            Vector3 pVec = Vector3.Cross(direction, edge2);
            float det = Vector3.Dot(edge1, pVec);
            if (MathF.Abs(det) < 1e-12f)
                continue; // ray parallel to the triangle plane

            float invDet = 1f / det;
            Vector3 tVec = origin - a;
            float u = Vector3.Dot(tVec, pVec) * invDet;
            if (u < 0f || u > 1f)
                continue;

            Vector3 qVec = Vector3.Cross(tVec, edge1);
            float v = Vector3.Dot(direction, qVec) * invDet;
            if (v < 0f || u + v > 1f)
                continue;

            float tTri = Vector3.Dot(edge2, qVec) * invDet;
            if (tTri < 0f || tTri >= bestT)
                continue;

            bestT = tTri;
            // No backface culling: orient the geometric normal against the ray
            // so double-sided picking always reports the facing side.
            Vector3 n = Vector3.Cross(edge1, edge2);
            bestLocalNormal = Vector3.Dot(n, direction) > 0f ? -n : n;
            found = true;
        }

        if (!found)
            return false;

        t = bestT;
        normal = WorldNormal(bestLocalNormal, inverse);
        return true;
    }

    // Ray vs. axis-aligned box in the node's LOCAL frame, tracking the entry
    // axis for the normal. Rays starting inside report no hit, mirroring the
    // brush contract.
    private static bool RaycastLocalBox(
        in Aabb box, in Vector3 origin, in Vector3 direction, float best, out float t, out Vector3 localNormal)
    {
        t = 0f;
        localNormal = default;

        float tMin = 0f;
        float tMax = best;
        int entryAxis = -1;
        float entrySign = 0f;

        for (int axis = 0; axis < 3; axis++)
        {
            float o = Component(origin, axis);
            float d = Component(direction, axis);
            float min = Component(box.Min, axis);
            float max = Component(box.Max, axis);

            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < min || o > max)
                    return false;
                continue;
            }

            float inv = 1f / d;
            float t1 = (min - o) * inv;
            float t2 = (max - o) * inv;
            if (t1 > t2)
                (t1, t2) = (t2, t1);

            if (t1 > tMin)
            {
                tMin = t1;
                entryAxis = axis;
                entrySign = d > 0f ? -1f : 1f; // entry face opposes the ray
            }
            if (t2 < tMax)
                tMax = t2;
            if (tMin > tMax)
                return false;
        }

        if (entryAxis < 0 || tMin >= best)
            return false;

        t = tMin;
        localNormal = entryAxis switch
        {
            0 => new Vector3(entrySign, 0f, 0f),
            1 => new Vector3(0f, entrySign, 0f),
            _ => new Vector3(0f, 0f, entrySign),
        };
        return true;
    }

    // Local-to-world normal transform: normals transform by the inverse
    // transpose of the linear part, i.e. n · (L⁻¹)ᵀ in the engine's row-vector
    // convention — TransformNormal against the transposed inverse.
    private static Vector3 WorldNormal(in Vector3 localNormal, in Matrix4x4 inverseWorld) =>
        Vector3.Normalize(Vector3.TransformNormal(localNormal, Matrix4x4.Transpose(inverseWorld)));

    // Slab test against a world-space box, pruned at tLimit. Entry at tEntry
    // (0 when the origin is inside). Parallel axes require the origin inside
    // the slab, keeping the test exact without infinities or NaNs.
    private static bool RayIntersectsBox(in Ray3 ray, in Aabb box, float tLimit, out float tEntry)
    {
        float tMin = 0f;
        float tMax = tLimit;

        for (int axis = 0; axis < 3; axis++)
        {
            float o = Component(ray.Origin, axis);
            float d = Component(ray.Direction, axis);
            float min = Component(box.Min, axis);
            float max = Component(box.Max, axis);

            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < min || o > max)
                {
                    tEntry = 0f;
                    return false;
                }
                continue;
            }

            float inv = 1f / d;
            float t1 = (min - o) * inv;
            float t2 = (max - o) * inv;
            if (t1 > t2)
                (t1, t2) = (t2, t1);

            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            if (tMin > tMax)
            {
                tEntry = 0f;
                return false;
            }
        }

        tEntry = tMin;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Component(in Vector3 v, int axis) =>
        axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static Aabb Union(in Aabb a, in Aabb b) =>
        new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));

    private static bool ContainsBox(in Aabb outer, in Aabb inner) =>
        outer.Min.X <= inner.Min.X && outer.Min.Y <= inner.Min.Y && outer.Min.Z <= inner.Min.Z &&
        outer.Max.X >= inner.Max.X && outer.Max.Y >= inner.Max.Y && outer.Max.Z >= inner.Max.Z;

    private static float SurfaceArea(in Aabb box)
    {
        Vector3 size = box.Size;
        return 2f * (size.X * size.Y + size.Y * size.Z + size.Z * size.X);
    }

    // --- Validation (tests) --------------------------------------------------

    /// <summary>
    /// Test/diagnostic hook: flushes pending refits, then walks the whole tree
    /// asserting every structural invariant — parent/child links are mutually
    /// consistent, every internal node's box is exactly the union of its
    /// children's, every leaf's fat box contains its up-to-date tight box, and
    /// the reachable leaves are exactly the tracked spatial nodes. Throws
    /// <see cref="InvalidOperationException"/> on the first violation.
    /// Allocates freely — never call it on a hot path.
    /// </summary>
    internal void Validate()
    {
        FlushDirtyLeaves();

        if (_root == Null)
        {
            if (_leaves.Count != 0)
                throw new InvalidOperationException($"Tree is empty but {_leaves.Count} leaves are tracked.");
            return;
        }

        if (_nodes[_root].Parent != Null)
            throw new InvalidOperationException("Root node has a parent.");

        int reachableLeaves = 0;
        var stack = new Stack<int>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            int index = stack.Pop();
            Node node = _nodes[index];

            if (node.Leaf is { } sceneNode)
            {
                if (node.Child1 != Null || node.Child2 != Null)
                    throw new InvalidOperationException($"Leaf {index} has children.");
                if (!_leaves.TryGetValue(sceneNode, out int tracked) || tracked != index)
                    throw new InvalidOperationException(
                        $"Leaf {index} ('{sceneNode.Name}') is not tracked at its own index.");
                if (!ContainsBox(node.FatBox, node.TightBox))
                    throw new InvalidOperationException($"Leaf {index} ('{sceneNode.Name}'): fat box lost its tight box.");
                Aabb expected = ComputeWorldBounds(sceneNode);
                if (node.TightBox.Min != expected.Min || node.TightBox.Max != expected.Max)
                    throw new InvalidOperationException($"Leaf {index} ('{sceneNode.Name}'): stale tight box.");
                reachableLeaves++;
                continue;
            }

            if (node.Child1 == Null || node.Child2 == Null)
                throw new InvalidOperationException($"Internal node {index} is missing a child.");
            if (_nodes[node.Child1].Parent != index || _nodes[node.Child2].Parent != index)
                throw new InvalidOperationException($"Internal node {index}: child parent links are wrong.");
            Aabb union = Union(_nodes[node.Child1].FatBox, _nodes[node.Child2].FatBox);
            if (node.FatBox.Min != union.Min || node.FatBox.Max != union.Max)
                throw new InvalidOperationException($"Internal node {index}: box is not the union of its children.");

            stack.Push(node.Child1);
            stack.Push(node.Child2);
        }

        if (reachableLeaves != _leaves.Count)
            throw new InvalidOperationException(
                $"{reachableLeaves} leaves reachable from the root, but {_leaves.Count} are tracked.");
    }
}
