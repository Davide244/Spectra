using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// A node in the scene graph: a named element with a local <see cref="Transform"/>,
/// an optional renderable, and a parent/child hierarchy. World transforms are
/// derived by composing local transforms down the tree and are cached until the
/// node (or an ancestor) changes. All members are render-thread-only, like the
/// <see cref="Scene"/> that owns the graph.
/// </summary>
public class SceneNode
{
    private readonly List<SceneNode> _children = [];
    private Transform _localTransform = Transform.Identity;
    private Matrix4x4 _worldMatrix = Matrix4x4.Identity;
    private bool _worldDirty = true;
    private Bsp.Brush? _brush;
    private BrushKind _brushKind = BrushKind.World;
    private MeshRenderer? _meshRenderer;
    private Light? _light;
    private PhysicsFlags _physicsFlags = PhysicsFlags.Default;
    private byte _collisionGroup;

    // Two lanes, one writer. Both count brushes in this node's subtree (itself
    // included) and are maintained on the whole ancestor chain by the Brush
    // setter, the BrushKind setter and reparenting, so both reads are O(1).
    //
    // They answer DIFFERENT questions and neither can be derived from the
    // other, which is why the split is not a rename:
    //
    //   _subtreeBrushCount            "is there a brush of ANY kind below me?"
    //                                 — rigidity. A scale written anywhere above
    //                                   a brush makes its placement non-rigid,
    //                                   and that is true of part brushes too, so
    //                                   ScaleGizmo's group refusal must read it.
    //   _subtreeStaticWorldBrushCount "is there a brush below me that the CSG
    //                                 compile can SEE?" — dirtying. A transform
    //                                   edit only costs a recompile when this is
    //                                   non-zero.
    //
    // Deliberately two int fields rather than two lanes packed into one long:
    // packing buys nothing here (render-thread only, adjacent fields, same cache
    // line) and costs a real hazard — a decrement that borrows across the lane
    // boundary corrupts BOTH counts silently. What actually makes desync
    // impossible is that AdjustSubtreeBrushCounts is the only writer of either.
    private int _subtreeBrushCount;
    private int _subtreeStaticWorldBrushCount;

    public SceneNode(string name = "Node")
    {
        Name = name;
        Id = Guid.NewGuid();
    }

    /// <summary>
    /// Creates a node that re-uses an existing identity instead of minting a
    /// fresh one. This is how undo resurrects a deleted node: edit history is
    /// addressed by <see cref="Id"/>, so a node recreated by an undo must come
    /// back under the id the recorded commands still name, or every command
    /// behind the delete would target a node that no longer exists.
    /// Deserialization will use the same door.
    /// </summary>
    public SceneNode(string name, Guid id)
    {
        Name = name;
        Id = id;
    }

    /// <summary>
    /// The node's identity, assigned at construction and stable for the node's
    /// entire lifetime — reparenting, renaming, and moving between scenes never
    /// change it. This is the reference that serialization and undo/redo use to
    /// name nodes across saves and edit history: editor commands store this id
    /// rather than an object reference, because undoing a delete produces a new
    /// instance with the same id (see <see cref="SceneNode(string, Guid)"/>),
    /// and <see cref="Scene.TryFindById"/> resolves it back to the live node.
    /// The id is immutable after construction.
    /// </summary>
    public Guid Id { get; }

    public string Name { get; set; }

    public SceneNode? Parent { get; private set; }

    /// <summary>
    /// The scene whose graph this node is attached to, or null while detached.
    /// Set for whole subtrees at once: the <see cref="Scene"/> constructor
    /// claims its root, and (re)parenting propagates the new parent's owner to
    /// every node in the moved subtree. Used to mark the owning scene's static
    /// world dirty automatically on brush-affecting edits.
    /// </summary>
    internal Scene? Owner { get; private set; }

    public IReadOnlyList<SceneNode> Children => _children;

    /// <summary>
    /// Renderable geometry attached to this node, if any. Assigning, clearing,
    /// or replacing it on an owned node updates the scene's spatial index
    /// automatically.
    /// </summary>
    public MeshRenderer? MeshRenderer
    {
        get => _meshRenderer;
        set
        {
            if (ReferenceEquals(_meshRenderer, value))
                return;
            _meshRenderer = value;
            // The node just became spatial, stopped being spatial, or changed
            // its renderable bounds — the owning scene's BVH must follow.
            Owner?.OnNodeSpatialComponentChanged(this);
        }
    }

    /// <summary>
    /// The light this node emits, or null. Attaching one registers the node with
    /// the owning scene's light list; detaching removes it.
    /// </summary>
    /// <remarks>
    /// <b>A light does not make a node spatial.</b> It stays out of the BVH on
    /// purpose: the BVH is what <c>Raycast</c> and the physics queries walk, and
    /// <see cref="PhysicsFlags.Default"/> includes both <c>CanCollide</c> and
    /// <c>CanQuery</c>, so admitting light-only nodes would quietly make every
    /// lamp in a level something a picking ray hits and a character walks into.
    /// Lights are collected from the scene's own small list instead, which is
    /// O(lights) rather than O(nodes) and needs no bounds at all.
    /// </remarks>
    public Light? Light
    {
        get => _light;
        set
        {
            if (ReferenceEquals(_light, value))
                return;
            _light = value;
            Owner?.UpdateLightMembership(this);
        }
    }

    /// <summary>
    /// Brush geometry this node contributes to the scene's static world, if any.
    /// Brushes are the authoring primitive: all brush nodes are carved together
    /// into one derived <see cref="Bsp.CsgWorld"/> instead of being rendered
    /// per-node. The node's world transform drives the brush's placement, so a
    /// Brush instance must not be attached to more than one node. Attaching,
    /// detaching, or replacing a brush marks the owning scene's static world
    /// dirty automatically — no manual <see cref="Scene.MarkStaticWorldDirty"/>
    /// call is needed.
    /// </summary>
    /// <remarks>
    /// <b>The node's world transform — not <see cref="Bsp.Brush.Transform"/> —
    /// places the brush.</b> Every compile snapshots the node's world matrix as
    /// the brush's placement and ignores whatever transform the brush itself
    /// carries, including the centering translation
    /// <see cref="Bsp.Brush.CreateBox"/> derives from its min/max arguments. A
    /// brush destined for a node should therefore be built with node-local
    /// (typically centred) extents; position it by moving the node, not by
    /// baking world coordinates into the brush.
    /// </remarks>
    public Bsp.Brush? Brush
    {
        get => _brush;
        set
        {
            if (ReferenceEquals(_brush, value))
                return;

            bool had = _brush is not null;
            bool has = value is not null;
            // Read the CURRENT kind, so both assignment orders are safe:
            // stamping the kind first costs nothing at all, and attaching the
            // brush first costs one dirty plus one admission bump when the kind
            // follows. Neither order can corrupt, so neither needs a convention.
            bool world = _brushKind == BrushKind.World;
            _brush = value;

            // Attach/detach changes the subtree brush population on the whole
            // ancestor chain; a brush-for-brush swap leaves the counts alone.
            // The static-world lane moves with it only for a World brush.
            if (had != has)
                AdjustSubtreeBrushCounts(this, has ? 1 : -1, world ? (has ? 1 : -1) : 0);

            // Any change to a WORLD brush — attach, detach, or replace —
            // changes the carved world. Attach/detach changes the PLACEMENT
            // COUNT, which the scene's retained snapshot cannot patch (slots
            // shift), so it goes through the conservative full-walk dirtying;
            // a brush-for-brush swap keeps the slot layout and reports just
            // this node.
            //
            // A PART brush is not in the placement list at all, so none of this
            // applies to one: attaching, swapping or detaching a part brush must
            // signal NOTHING. This is the gate the whole zero-cost claim rests
            // on — MarkStaticWorldDirty sets the force-full flag, so an ungated
            // attach makes every script-spawned part cost an O(world) walk.
            if (world)
            {
                if (had != has)
                    Owner?.MarkStaticWorldDirty();
                else
                    Owner?.MarkBrushSubtreeDirty(this);
            }

            // It also changes what (or whether) the spatial index tracks here —
            // and THIS one is deliberately kind-blind. The BVH indexes brush
            // nodes and unions their bounds regardless of kind, because part
            // brushes must still be frustum-culled and must still be pickable
            // in the editor. Gating it here would make them invisible to both.
            Owner?.OnNodeSpatialComponentChanged(this);
        }
    }

    /// <summary>
    /// Whether this node's brush is fused into the compiled static world, or
    /// stands alone as a movable object. Defaults to
    /// <see cref="BrushKind.World"/>, whose own documentation carries the
    /// argument for why the bit is declared rather than derived, and never
    /// inherited.
    /// </summary>
    /// <remarks>
    /// The one admission write in the graph, and deliberately conditional and
    /// idempotent: an equal write does nothing (the same exact-equality
    /// discipline the transform setters use), and a kind flip on a node
    /// carrying no brush signals nothing at all — it is a stamp for a brush
    /// that may arrive later. On a real change to a brush-bearing node it moves
    /// the node between the counter's two lanes and tells the scene that the
    /// set of admitted brushes changed, which is the one thing the incremental
    /// compile's trusted diff cannot infer for itself.
    /// </remarks>
    public BrushKind BrushKind
    {
        get => _brushKind;
        set
        {
            if (_brushKind == value)
                return;

            _brushKind = value;

            // No brush here: nothing is admitted or un-admitted, so nothing is
            // counted and nothing is dirtied.
            if (_brush is null)
                return;

            AdjustSubtreeBrushCounts(this, 0, value == BrushKind.World ? 1 : -1);
            Owner?.MarkAdmissionChanged(this);
        }
    }

    /// <summary>
    /// True when this node carries a brush that the static-world compile is
    /// allowed to see. This is the single predicate the CSG snapshot path asks;
    /// everything downstream of it — rigidity validation, placement slots, the
    /// per-cell BSP, the chunk meshes — inherits the world/part split for free
    /// by consuming the one placement list.
    /// </summary>
    public bool IsStaticWorldBrush => _brush is not null && _brushKind == BrushKind.World;

    /// <summary>
    /// The node's physics and query bits. See <see cref="Scene.PhysicsFlags"/>
    /// for what each one means and why they are a byte rather than a payload.
    /// </summary>
    /// <remarks>
    /// A plain field with no side effects, deliberately: none of these bits
    /// changes the compiled static world, the spatial index, or anything the
    /// CSG snapshot reads. They are consulted at query time and at body-creation
    /// time, so writing one must not dirty anything — which is also what makes
    /// a script toggling <c>CanCollide</c> on a world brush free.
    /// </remarks>
    public PhysicsFlags PhysicsFlags
    {
        get => _physicsFlags;
        set => _physicsFlags = value;
    }

    /// <summary>Whether this node's geometry participates in collision.</summary>
    public bool CanCollide
    {
        get => (_physicsFlags & PhysicsFlags.CanCollide) != 0;
        set => SetFlag(PhysicsFlags.CanCollide, value);
    }

    /// <summary>
    /// Whether this node's geometry is visible to spatial queries. Independent
    /// of <see cref="CanCollide"/>, and honoured for every kind of node —
    /// including static world brushes, because <see cref="Scene.Raycast"/>
    /// traverses the spatial index per node rather than the compiled BSP.
    /// </summary>
    public bool CanQuery
    {
        get => (_physicsFlags & PhysicsFlags.CanQuery) != 0;
        set => SetFlag(PhysicsFlags.CanQuery, value);
    }

    /// <summary>Whether this node generates touch and trigger events.</summary>
    public bool CanTouch
    {
        get => (_physicsFlags & PhysicsFlags.CanTouch) != 0;
        set => SetFlag(PhysicsFlags.CanTouch, value);
    }

    /// <summary>
    /// Whether this node is exempt from simulation. Default <c>true</c>; see
    /// <see cref="PhysicsFlags.Anchored"/> for why that differs from Roblox.
    /// </summary>
    public bool Anchored
    {
        get => (_physicsFlags & PhysicsFlags.Anchored) != 0;
        set => SetFlag(PhysicsFlags.Anchored, value);
    }

    /// <summary>
    /// Which collision group this node belongs to — an id from the scene's
    /// <see cref="Scene.CollisionGroups"/> registry. Zero
    /// (<see cref="Scene.CollisionGroups.DefaultGroup"/>) unless assigned, so a
    /// world that never mentions groups behaves as one without the feature.
    /// </summary>
    /// <remarks>
    /// Stored as a byte and validated only against the 64-group ceiling here:
    /// the registry that gives ids their meaning belongs to a scene, and a node
    /// may be assigned its group before it is attached to one.
    /// </remarks>
    public int CollisionGroup
    {
        get => _collisionGroup;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, CollisionGroups.MaxGroups);
            _collisionGroup = (byte)value;
        }
    }

    private void SetFlag(PhysicsFlags flag, bool on)
    {
        if (on)
            _physicsFlags |= flag;
        else
            _physicsFlags &= ~flag;
    }

    /// <summary>The node's transform relative to its parent.</summary>
    /// <remarks>
    /// All transform setters (this one and the component properties below)
    /// early-out when the written value exactly equals the current one: a
    /// no-op write invalidates nothing, dirties no static world, and raises no
    /// <see cref="Scene.NodeTransformChanged"/>.
    /// </remarks>
    public Transform LocalTransform
    {
        get => _localTransform;
        set
        {
            // Transform is a plain mutable struct without equality operators,
            // so compare field-wise (component-exact, like the setters below).
            if (value.Position == _localTransform.Position &&
                value.Rotation == _localTransform.Rotation &&
                value.Scale == _localTransform.Scale)
                return;
            _localTransform = value;
            OnLocalTransformChanged();
        }
    }

    public Vector3 LocalPosition
    {
        get => _localTransform.Position;
        set
        {
            if (value == _localTransform.Position)
                return;
            _localTransform.Position = value;
            OnLocalTransformChanged();
        }
    }

    public Quaternion LocalRotation
    {
        get => _localTransform.Rotation;
        set
        {
            if (value == _localTransform.Rotation)
                return;
            _localTransform.Rotation = value;
            OnLocalTransformChanged();
        }
    }

    public Vector3 LocalScale
    {
        get => _localTransform.Scale;
        set
        {
            if (value == _localTransform.Scale)
                return;
            _localTransform.Scale = value;
            OnLocalTransformChanged();
        }
    }

    /// <summary>
    /// How many brushes are attached in this node's subtree, this node's own
    /// <see cref="Brush"/> included. Maintained incrementally by the brush
    /// setter and by reparenting, so reading it is O(1).
    /// </summary>
    /// <remarks>
    /// <b>Rigidity is a subtree property, not a node property.</b> A brush's
    /// placement is the world matrix of the node it hangs under, so a scale
    /// written <em>anywhere</em> above a brush makes that brush's placement
    /// non-rigid and the static-world compile rejects the whole snapshot — not
    /// just that brush. Any tool that is about to write
    /// <see cref="LocalScale"/> must therefore ask this, not
    /// <c>node.Brush is not null</c>: a group node carrying no brush of its own
    /// can still be the root of a subtree full of them.
    /// </remarks>
    public int SubtreeBrushCount => _subtreeBrushCount;

    /// <summary>
    /// How many brushes in this node's subtree are admitted to the static world
    /// — that is, how many of the <see cref="SubtreeBrushCount"/> are
    /// <see cref="BrushKind.World"/>. Always between zero and that total.
    /// </summary>
    /// <remarks>
    /// This is the <em>dirtying</em> question, and it is the one the transform
    /// path asks: a subtree full of part brushes can be moved every frame for
    /// free, because nothing in it is in the placement list. It is deliberately
    /// NOT the question a tool about to write <see cref="LocalScale"/> asks —
    /// see <see cref="SubtreeBrushCount"/>, which is about rigidity and stays
    /// kind-blind, because a scale above a <em>part</em> brush is just as
    /// illegal as a scale above a world one.
    /// </remarks>
    public int SubtreeStaticWorldBrushCount => _subtreeStaticWorldBrushCount;

    /// <summary>The node's accumulated world matrix (local composed with all ancestors).</summary>
    public Matrix4x4 WorldMatrix
    {
        get
        {
            if (_worldDirty)
            {
                var local = _localTransform.Model;
                _worldMatrix = Parent is null ? local : local * Parent.WorldMatrix;
                _worldDirty = false;
            }
            return _worldMatrix;
        }
    }

    public Vector3 WorldPosition => WorldMatrix.Translation;

    /// <summary>
    /// This node's position among its parent's children, or −1 when it has no
    /// parent. The coordinate a structural edit has to record to be reversible.
    /// </summary>
    /// <remarks>
    /// <b>Sibling index is not cosmetic here.</b> Traversal order is child-list
    /// order, traversal order is the static world's placement-slot order, and
    /// placement order breaks ties in the carve's overlap ordering, so a node
    /// that comes back from an undo at a different index produces geometry that
    /// is valid, different, and bit-unequal to what was there before. Linear in
    /// the sibling count, which is why it is read at gesture time and stored,
    /// never consulted per frame.
    /// </remarks>
    public int IndexInParent => Parent?._children.IndexOf(this) ?? -1;

    /// <summary>
    /// Attaches an existing node as a child at the end of the child list,
    /// detaching it from any previous parent. See
    /// <see cref="InsertChild(int, SceneNode)"/>, which this is the append case
    /// of.
    /// </summary>
    public SceneNode AddChild(SceneNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return InsertChild(_children.Count, child);
    }

    /// <summary>
    /// Attaches an existing node as a child at a chosen position, detaching it
    /// from any previous parent. When the moved subtree contains brushes, both
    /// the old and the new owning scene (if any) get their static world marked
    /// dirty — the brushes' placements changed on both sides.
    /// </summary>
    /// <param name="index">
    /// Where in the child list the node lands. Clamped to the list's length
    /// <em>after</em> the detach, so a node moved within its own parent may name
    /// the end of the list, and an undo may restore into a parent that has since
    /// lost other siblings.
    /// </param>
    /// <param name="child">The node to attach.</param>
    /// <exception cref="ArgumentException">
    /// The node is this node, or an ancestor of it: either would make the graph
    /// a cycle, and every walk over it non-terminating.
    /// </exception>
    /// <remarks>
    /// <b>Restoring the index is the whole reason this exists.</b> Appending is
    /// the right answer when a node is first created and the wrong one when a
    /// node is coming back: see <see cref="IndexInParent"/> for what re-ordering
    /// costs.
    /// </remarks>
    public SceneNode InsertChild(int index, SceneNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        // A cycle is not a bug that surfaces here: it surfaces as a hang the
        // first time anything walks the graph, which is every frame. Reparenting
        // is the operation that can reach it (dragging a parent onto its own
        // child in a tree view is an ordinary slip), so the guard belongs on the
        // one attach path rather than in each caller.
        for (SceneNode? ancestor = this; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, child))
            {
                throw new ArgumentException(
                    $"Cannot attach '{child.Name}' under '{Name}': it is that node or an ancestor of it, " +
                    "which would make the graph a cycle.",
                    nameof(child));
            }
        }

        Scene? previousOwner = child.Owner;

        // Detach from the old parent first, unwinding the subtree brush count
        // from the old ancestor chain before the chain is severed.
        if (child.Parent is { } oldParent)
        {
            oldParent._children.Remove(child);
            if (child._subtreeBrushCount > 0)
            {
                AdjustSubtreeBrushCounts(oldParent, -child._subtreeBrushCount, -child._subtreeStaticWorldBrushCount);
                // Both lanes move, but only admitted brushes changed the
                // compiled world: a folder of parts can be reparented for free.
                if (child._subtreeStaticWorldBrushCount > 0)
                    child.Owner?.MarkStaticWorldDirty();
            }
        }

        // Clamped after the detach, never before: moving a node to the end of
        // its own parent's list names an index the list only has once the node
        // has left it, and an undo restores into a parent that may have lost
        // other siblings in the same gesture.
        if (index > _children.Count)
            index = _children.Count;

        child.Parent = this;
        _children.Insert(index, child);
        // Invalidate the cached world matrices BEFORE announcing the node to
        // its new scene: NodeAdded handlers (the spatial index in particular)
        // read WorldMatrix, which must already reflect the new parent chain.
        child.MarkWorldDirty();
        child.SetOwner(Owner);

        if (child._subtreeBrushCount > 0)
        {
            AdjustSubtreeBrushCounts(this, child._subtreeBrushCount, child._subtreeStaticWorldBrushCount);
            if (child._subtreeStaticWorldBrushCount > 0)
                Owner?.MarkStaticWorldDirty();
        }

        // A reparent WITHIN one scene raises no membership events (the subtree
        // never left), yet it still moves every node in it — tell the scene so
        // the spatial index can refit the affected leaves.
        if (previousOwner is not null && ReferenceEquals(previousOwner, Owner))
            previousOwner.OnNodeSubtreeMoved(child);

        return child;
    }

    /// <summary>Creates a new child node and attaches it.</summary>
    public SceneNode CreateChild(string name = "Node")
    {
        var node = new SceneNode(name);
        return AddChild(node);
    }

    /// <summary>
    /// A detached copy of this node under a <b>fresh identity</b>, ready to be
    /// attached wherever the caller wants it.
    /// </summary>
    /// <param name="deep">
    /// True (the default) to copy the whole subtree; false for this node's
    /// payloads alone, which leaves a group node empty.
    /// </param>
    /// <remarks>
    /// <b>Each of the three payloads a node can carry is copied differently, and
    /// the differences are not stylistic.</b>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="MeshRenderer"/> is <em>shared by reference</em>. It is
    ///     immutable and its GPU resources are owned by the renderer, so a
    ///     thousand duplicates of a prop cost one mesh.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Brush"/> gets its own instance through
    ///     <c>Brush.CloneShape()</c>. Sharing would be geometrically correct but
    ///     the CSG carve cache keys on reference identity and holds one entry
    ///     per instance, so every duplicate past the first would re-carve on
    ///     every compile forever.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Light"/> gets a copy, because it is the one payload that is
    ///     MUTABLE. Sharing it would make dimming the copy dim the original.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Returned detached, deliberately.</b> The caller decides the parent and
    /// the sibling index, and attaching is what raises the membership events,
    /// indexes the new ids and dirties the static world, so a clone that
    /// attached itself would take that decision away from the one place that
    /// has to record it for undo.
    /// </para>
    /// <para>
    /// <see cref="PhysicsFlags.HasBody"/> is stripped: it is owned by the
    /// physics layer and says a body exists in its side table for the ORIGINAL
    /// node. A copy that claimed it would send every body lookup for the
    /// duplicate to a table entry that is not there.
    /// </para>
    /// </remarks>
    public SceneNode Clone(bool deep = true)
    {
        var copy = new SceneNode(Name);

        copy._localTransform = _localTransform;
        copy._physicsFlags = _physicsFlags & ~PhysicsFlags.HasBody;
        copy._collisionGroup = _collisionGroup;

        // The kind is stamped before the brush arrives, so the brush setter
        // counts the new node into the right subtree lane on the first write
        // rather than counting it twice through an admission change.
        copy._brushKind = _brushKind;

        // Through the properties from here down: they are what maintain the
        // subtree counters. The scene-side hooks they also call are all
        // null-guarded on Owner, which a detached copy does not have.
        copy.MeshRenderer = _meshRenderer;
        copy.Brush = _brush?.CloneShape();
        copy.Light = _light?.Clone();

        if (deep)
        {
            for (int i = 0; i < _children.Count; i++)
                copy.AddChild(_children[i].Clone(deep: true));
        }

        return copy;
    }

    public void RemoveChild(SceneNode child)
    {
        if (_children.Remove(child))
        {
            child.Parent = null;
            if (child._subtreeBrushCount > 0)
            {
                // The removed subtree's admitted brushes leave the compiled
                // world; its part brushes were never in it.
                AdjustSubtreeBrushCounts(this, -child._subtreeBrushCount, -child._subtreeStaticWorldBrushCount);
                if (child._subtreeStaticWorldBrushCount > 0)
                    Owner?.MarkStaticWorldDirty();
            }
            child.SetOwner(null);
            child.MarkWorldDirty();
        }
    }

    /// <summary>
    /// Enumerates this node and all of its descendants depth-first, in
    /// pre-order with children visited in list order.
    /// </summary>
    public IEnumerable<SceneNode> Traverse()
    {
        // Explicit stack instead of nested iterators: recursive `yield` chains
        // cost O(depth) per element and allocate an enumerator per node, and
        // debug visualisations walk the whole tree several times per frame.
        var stack = new Stack<SceneNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;

            // Push in reverse so the first child is popped (visited) first,
            // preserving the visit order of the old recursive version.
            var children = node._children;
            for (int i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }
    }

    // Propagates a scene-ownership change to the whole subtree, raising the
    // scenes' membership events as it goes: every node whose owner actually
    // changes fires NodeRemoved on the scene it leaves and NodeAdded on the
    // scene it enters (both, in that order, on a cross-scene move), pre-order
    // so parents are announced before their children. Descendants always share
    // their root's owner (only construction and (re)parenting change it, and
    // both keep subtrees consistent), so an unchanged owner means the entire
    // subtree is already correct and the walk can stop — which is also exactly
    // what makes a reparent WITHIN one scene fire no membership events: the
    // moved subtree neither enters nor leaves the scene.
    internal void SetOwner(Scene? owner)
    {
        if (Owner == owner)
            return;

        Scene? previous = Owner;
        // Owner is updated before the events fire, so handlers observe the
        // node's new membership (a NodeRemoved handler sees Owner as null or
        // as the destination scene, never as the scene raising the event).
        Owner = owner;
        previous?.OnNodeRemoved(this);
        owner?.OnNodeAdded(this);

        for (int i = 0; i < _children.Count; i++)
            _children[i].SetOwner(owner);
    }

    // The ONLY writer of either subtree lane. Walks `node` and every ancestor
    // once, moving both counts together — which is what makes it structurally
    // impossible for the two to disagree about the same subtree.
    private static void AdjustSubtreeBrushCounts(SceneNode node, int totalDelta, int worldDelta)
    {
        for (SceneNode? n = node; n is not null; n = n.Parent)
        {
            n._subtreeBrushCount += totalDelta;
            n._subtreeStaticWorldBrushCount += worldDelta;
        }
    }

    // Shared tail of every transform setter, run only after the value actually
    // changed (the setters early-out on equal writes).
    private void OnLocalTransformChanged()
    {
        MarkWorldDirty();

        // A transform edit only affects the static world when an ADMITTED
        // brush sits somewhere in this node's subtree — its placement derives
        // from this node's world matrix. The subtree count makes that an O(1)
        // test. Node-scoped dirtying: the scene records WHICH subtree moved, so
        // the next compile launch re-captures only it (the per-frame drag path
        // must stay O(edit neighbourhood) end to end).
        //
        // The static-world lane, not the total: a subtree of part brushes may
        // be moved by physics every tick and must cost nothing here, which is
        // the entire reason the kind exists.
        if (_subtreeStaticWorldBrushCount > 0)
            Owner?.MarkBrushSubtreeDirty(this);

        // The change event, by contrast, fires for every owned node — editors
        // track cameras and props too, not just brush geometry.
        Owner?.OnNodeTransformChanged(this);
    }

    // Eagerly propagates the dirty flag. A child's cached world matrix depends on
    // its parent's, so any change must invalidate the whole subtree. Cheap for the
    // shallow trees we have today; revisit if hierarchies grow deep.
    private void MarkWorldDirty()
    {
        _worldDirty = true;
        for (int i = 0; i < _children.Count; i++)
            _children[i].MarkWorldDirty();
    }
}
