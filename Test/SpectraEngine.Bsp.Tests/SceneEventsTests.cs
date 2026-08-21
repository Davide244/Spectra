using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Scene change events and node identity. NodeAdded/NodeRemoved must track
/// scene membership exactly: one event per node entering or leaving the owned
/// graph (pre-order for subtrees), and none for a reparent within the same
/// scene — the moved subtree neither enters nor leaves. NodeTransformChanged
/// must fire only for writes that actually change an owned node's local
/// transform; equal-value writes are silent no-ops that also must not dirty
/// the static world.
/// </summary>
public sealed class SceneEventsTests
{
    // --- Node identity ------------------------------------------------------

    [Fact]
    public void Node_ids_are_assigned_at_construction_and_unique()
    {
        var a = new SceneNode("a");
        var b = new SceneNode("b");

        a.Id.ShouldNotBe(Guid.Empty);
        b.Id.ShouldNotBe(Guid.Empty);
        a.Id.ShouldNotBe(b.Id);
    }

    [Fact]
    public void Node_id_is_stable_across_reparenting_and_scene_moves()
    {
        var node = new SceneNode("n");
        Guid id = node.Id;

        var sceneA = new Scene("A");
        sceneA.Root.AddChild(node);
        node.Id.ShouldBe(id);

        var sceneB = new Scene("B");
        sceneB.Root.AddChild(node);
        node.Id.ShouldBe(id);

        sceneB.Root.RemoveChild(node);
        node.Id.ShouldBe(id);
    }

    // --- Membership events --------------------------------------------------

    [Fact]
    public void Adding_a_node_fires_NodeAdded_once()
    {
        var scene = new Scene("Test");
        List<SceneNode> added = [];
        scene.NodeAdded += added.Add;

        SceneNode node = scene.Root.CreateChild("child");

        added.ShouldBe(new[] { node });
    }

    [Fact]
    public void Attaching_a_detached_subtree_fires_NodeAdded_per_node_pre_order()
    {
        // Built while detached — no owner, so construction fires nothing —
        // then attached in one AddChild, which must announce every node.
        var group = new SceneNode("group");
        SceneNode childA = group.CreateChild("a");
        SceneNode grandchild = childA.CreateChild("a1");
        SceneNode childB = group.CreateChild("b");

        var scene = new Scene("Test");
        List<SceneNode> added = [];
        scene.NodeAdded += added.Add;

        scene.Root.AddChild(group);

        added.ShouldBe(new[] { group, childA, grandchild, childB });
    }

    [Fact]
    public void Building_a_subtree_while_detached_fires_nothing()
    {
        var scene = new Scene("Test");
        int fired = 0;
        scene.NodeAdded += _ => fired++;
        scene.NodeRemoved += _ => fired++;

        var detached = new SceneNode("detached");
        detached.CreateChild("child").CreateChild("grandchild");

        fired.ShouldBe(0);
    }

    [Fact]
    public void Removing_a_subtree_fires_NodeRemoved_per_node()
    {
        var scene = new Scene("Test");
        SceneNode group = scene.Root.CreateChild("group");
        SceneNode child = group.CreateChild("child");
        List<SceneNode> removed = [];
        scene.NodeRemoved += removed.Add;

        scene.Root.RemoveChild(group);

        removed.ShouldBe(new[] { group, child });
    }

    [Fact]
    public void Reparenting_to_a_detached_parent_fires_NodeRemoved()
    {
        // AddChild onto an unowned parent is a scene exit for the child even
        // though the child is never explicitly "removed".
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("n");
        List<SceneNode> removed = [];
        scene.NodeRemoved += removed.Add;

        var detachedParent = new SceneNode("detached");
        detachedParent.AddChild(node);

        removed.ShouldBe(new[] { node });
        node.Parent.ShouldBe(detachedParent);
    }

    [Fact]
    public void Reparenting_within_the_same_scene_fires_neither_event()
    {
        var scene = new Scene("Test");
        SceneNode oldParent = scene.Root.CreateChild("old-parent");
        SceneNode newParent = scene.Root.CreateChild("new-parent");
        SceneNode moved = oldParent.CreateChild("moved");
        moved.CreateChild("descendant");

        int fired = 0;
        scene.NodeAdded += _ => fired++;
        scene.NodeRemoved += _ => fired++;

        newParent.AddChild(moved);

        fired.ShouldBe(0);
        moved.Parent.ShouldBe(newParent);
    }

    [Fact]
    public void Moving_a_subtree_between_scenes_fires_removed_on_old_and_added_on_new()
    {
        var sceneA = new Scene("A");
        var sceneB = new Scene("B");
        SceneNode group = sceneA.Root.CreateChild("group");
        SceneNode child = group.CreateChild("child");

        List<SceneNode> removedFromA = [];
        List<SceneNode> addedToB = [];
        int wrongSceneEvents = 0;
        sceneA.NodeRemoved += removedFromA.Add;
        sceneB.NodeAdded += addedToB.Add;
        // The move must not masquerade as an add on A or a remove on B.
        sceneA.NodeAdded += _ => wrongSceneEvents++;
        sceneB.NodeRemoved += _ => wrongSceneEvents++;

        sceneB.Root.AddChild(group);

        removedFromA.ShouldBe(new[] { group, child });
        addedToB.ShouldBe(new[] { group, child });
        wrongSceneEvents.ShouldBe(0);
    }

    [Fact]
    public void NodeRemoved_handlers_observe_the_node_as_already_outside_the_scene()
    {
        // Documented contract: ownership is updated before the event fires, so
        // an auto-cleanup handler (like the selection's) can rely on the node
        // no longer claiming membership in the scene announcing the loss.
        // Owner itself is internal, so observe it through the selection's
        // ownership check: re-adding the departing node to the selection must
        // already be rejected while the handler runs.
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("n");
        bool handlerRan = false;
        scene.NodeRemoved += n =>
        {
            handlerRan = true;
            Should.Throw<ArgumentException>(() => scene.Selection.Add(n));
        };

        scene.Root.RemoveChild(node);

        handlerRan.ShouldBeTrue();
        node.Parent.ShouldBeNull();
    }

    // --- Transform events ---------------------------------------------------

    [Fact]
    public void Changing_local_position_fires_NodeTransformChanged()
    {
        var scene = new Scene("Test");
        // Deliberately brushless: the change event covers every owned node,
        // not just brush geometry (static-world dirtying is separate).
        SceneNode node = scene.Root.CreateChild("plain");
        List<SceneNode> changed = [];
        scene.NodeTransformChanged += changed.Add;

        node.LocalPosition = new Vector3(1f, 2f, 3f);

        changed.ShouldBe(new[] { node });
    }

    [Fact]
    public void Every_transform_setter_fires_on_a_real_change()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("n");
        int fired = 0;
        scene.NodeTransformChanged += _ => fired++;

        node.LocalPosition = new Vector3(1f, 0f, 0f);
        node.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        node.LocalScale = new Vector3(2f, 2f, 2f);
        node.LocalTransform = Transform.Identity;

        fired.ShouldBe(4);
    }

    [Fact]
    public void Equal_value_transform_writes_fire_nothing()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("n");
        node.LocalPosition = new Vector3(1f, 2f, 3f);
        node.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.25f);
        int fired = 0;
        scene.NodeTransformChanged += _ => fired++;

        node.LocalPosition = node.LocalPosition;
        node.LocalRotation = node.LocalRotation;
        node.LocalScale = node.LocalScale;
        node.LocalTransform = node.LocalTransform;

        fired.ShouldBe(0);
    }

    [Fact]
    public void Equal_value_write_does_not_dirty_the_static_world()
    {
        // The early-out sits in front of the brush-dirty logic too: writing
        // back the identical placement must not trigger a CSG recompile.
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("brush");
        node.Brush = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        scene.RebuildStaticWorld(new FakeRenderer());
        scene.StaticWorldDirty.ShouldBeFalse();

        node.LocalPosition = node.LocalPosition;

        scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void Transform_change_on_a_detached_node_fires_nothing()
    {
        var scene = new Scene("Test");
        int fired = 0;
        scene.NodeTransformChanged += _ => fired++;

        var detached = new SceneNode("detached");
        detached.LocalPosition = new Vector3(5f, 0f, 0f);

        fired.ShouldBe(0);
    }
}
