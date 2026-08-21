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
    private MeshRenderer? _meshRenderer;

    // Number of brushes attached in this node's subtree (itself included).
    // Maintained on the whole ancestor chain by the Brush setter and by
    // reparenting, so a transform edit can decide in O(1) whether it affects
    // the static world: moving a camera or a brushless prop must not trigger
    // a recompile, while moving a group node with brush descendants must.
    private int _subtreeBrushCount;

    public SceneNode(string name = "Node")
    {
        Name = name;
    }

    /// <summary>
    /// The node's identity, assigned at construction and stable for the node's
    /// entire lifetime — reparenting, renaming, and moving between scenes never
    /// change it. This is the reference that future serialization and undo/redo
    /// will use to name nodes across saves and edit history. (A setter for
    /// deserialization is deliberately deferred to the serialization arc.)
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

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
            _brush = value;

            // Attach/detach changes the subtree brush population on the whole
            // ancestor chain; a brush-for-brush swap leaves the counts alone.
            if (had != has)
                AdjustSubtreeBrushCount(this, has ? 1 : -1);

            // Any change — attach, detach, or replace — changes the carved
            // world. Attach/detach changes the PLACEMENT COUNT, which the
            // scene's retained snapshot cannot patch (slots shift), so it
            // goes through the conservative full-walk dirtying; a
            // brush-for-brush swap keeps the slot layout and reports just
            // this node.
            if (had != has)
                Owner?.MarkStaticWorldDirty();
            else
                Owner?.MarkBrushSubtreeDirty(this);
            // It also changes what (or whether) the spatial index tracks here.
            Owner?.OnNodeSpatialComponentChanged(this);
        }
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
    /// Attaches an existing node as a child, detaching it from any previous
    /// parent. When the moved subtree contains brushes, both the old and the
    /// new owning scene (if any) get their static world marked dirty — the
    /// brushes' placements changed on both sides.
    /// </summary>
    public SceneNode AddChild(SceneNode child)
    {
        Scene? previousOwner = child.Owner;

        // Detach from the old parent first, unwinding the subtree brush count
        // from the old ancestor chain before the chain is severed.
        if (child.Parent is { } oldParent)
        {
            oldParent._children.Remove(child);
            if (child._subtreeBrushCount > 0)
            {
                AdjustSubtreeBrushCount(oldParent, -child._subtreeBrushCount);
                child.Owner?.MarkStaticWorldDirty();
            }
        }

        child.Parent = this;
        _children.Add(child);
        // Invalidate the cached world matrices BEFORE announcing the node to
        // its new scene: NodeAdded handlers (the spatial index in particular)
        // read WorldMatrix, which must already reflect the new parent chain.
        child.MarkWorldDirty();
        child.SetOwner(Owner);

        if (child._subtreeBrushCount > 0)
        {
            AdjustSubtreeBrushCount(this, child._subtreeBrushCount);
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

    public void RemoveChild(SceneNode child)
    {
        if (_children.Remove(child))
        {
            child.Parent = null;
            if (child._subtreeBrushCount > 0)
            {
                // The removed subtree's brushes leave the compiled world.
                AdjustSubtreeBrushCount(this, -child._subtreeBrushCount);
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

    // Adds `delta` brushes to `node` and every ancestor's subtree count.
    private static void AdjustSubtreeBrushCount(SceneNode node, int delta)
    {
        for (SceneNode? n = node; n is not null; n = n.Parent)
            n._subtreeBrushCount += delta;
    }

    // Shared tail of every transform setter, run only after the value actually
    // changed (the setters early-out on equal writes).
    private void OnLocalTransformChanged()
    {
        MarkWorldDirty();

        // A transform edit only affects the static world when a brush sits
        // somewhere in this node's subtree — its placement derives from this
        // node's world matrix. The subtree count makes that an O(1) test.
        // Node-scoped dirtying: the scene records WHICH subtree moved, so the
        // next compile launch re-captures only it (the per-frame drag path
        // must stay O(edit neighbourhood) end to end).
        if (_subtreeBrushCount > 0)
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
