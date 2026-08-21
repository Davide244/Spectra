using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The scene's id index (<see cref="Scene.TryFindById"/>): maintained purely
/// from the membership events, so it must stay exact across subtree attach and
/// detach, reparenting within a scene (which raises no membership events at
/// all), cross-scene moves, and a node recreated under an id whose previous
/// instance has left — the case undo of a delete produces.
/// </summary>
public sealed class SceneNodeIndexTests
{
    [Fact]
    public void The_root_is_indexed_from_construction()
    {
        var scene = new Scene("S");

        scene.TryFindById(scene.Root.Id, out SceneNode? found).ShouldBeTrue();
        found.ShouldBeSameAs(scene.Root);
        scene.NodeCount.ShouldBe(1);
    }

    [Fact]
    public void An_added_node_is_findable_and_a_removed_one_is_not()
    {
        var scene = new Scene("S");
        var node = scene.Root.CreateChild("A");

        scene.TryFindById(node.Id, out SceneNode? found).ShouldBeTrue();
        found.ShouldBeSameAs(node);

        scene.Root.RemoveChild(node);

        scene.TryFindById(node.Id, out _).ShouldBeFalse();
        scene.NodeCount.ShouldBe(1);
    }

    [Fact]
    public void Attaching_and_detaching_a_subtree_indexes_every_node_in_it()
    {
        var scene = new Scene("S");
        var parent = new SceneNode("Parent");
        var child = parent.CreateChild("Child");
        var grandchild = child.CreateChild("Grandchild");

        scene.Root.AddChild(parent);

        scene.NodeCount.ShouldBe(4); // root + 3
        scene.TryFindById(parent.Id, out _).ShouldBeTrue();
        scene.TryFindById(child.Id, out _).ShouldBeTrue();
        scene.TryFindById(grandchild.Id, out _).ShouldBeTrue();

        scene.Root.RemoveChild(parent);

        scene.NodeCount.ShouldBe(1);
        scene.TryFindById(parent.Id, out _).ShouldBeFalse();
        scene.TryFindById(child.Id, out _).ShouldBeFalse();
        scene.TryFindById(grandchild.Id, out _).ShouldBeFalse();
    }

    [Fact]
    public void Reparenting_within_one_scene_leaves_the_index_intact()
    {
        var scene = new Scene("S");
        var a = scene.Root.CreateChild("A");
        var b = scene.Root.CreateChild("B");
        var child = a.CreateChild("Child");

        b.AddChild(child);

        // A reparent raises no membership events (the node never left), and the
        // index must not need any: it is still the same node in the same scene.
        child.Parent.ShouldBeSameAs(b);
        scene.TryFindById(child.Id, out SceneNode? found).ShouldBeTrue();
        found.ShouldBeSameAs(child);
        scene.NodeCount.ShouldBe(4);
    }

    [Fact]
    public void A_cross_scene_move_reindexes_on_both_sides()
    {
        var source = new Scene("Source");
        var destination = new Scene("Destination");
        var node = source.Root.CreateChild("A");
        var child = node.CreateChild("Child");

        destination.Root.AddChild(node);

        source.TryFindById(node.Id, out _).ShouldBeFalse();
        source.TryFindById(child.Id, out _).ShouldBeFalse();
        source.NodeCount.ShouldBe(1);

        destination.TryFindById(node.Id, out SceneNode? found).ShouldBeTrue();
        found.ShouldBeSameAs(node);
        destination.TryFindById(child.Id, out _).ShouldBeTrue();
        destination.NodeCount.ShouldBe(3);
    }

    [Fact]
    public void A_node_recreated_under_a_departed_id_takes_over_the_mapping()
    {
        var scene = new Scene("S");
        var original = scene.Root.CreateChild("Box");
        Guid id = original.Id;

        scene.Root.RemoveChild(original);
        var recreated = new SceneNode("Box", id);
        scene.Root.AddChild(recreated);

        // This is what undo of a delete produces: a new instance under the id
        // the recorded commands still name.
        scene.TryFindById(id, out SceneNode? found).ShouldBeTrue();
        found.ShouldBeSameAs(recreated);
        found.ShouldNotBeSameAs(original);
        scene.NodeCount.ShouldBe(2);
    }

    [Fact]
    public void Removing_a_stale_duplicate_does_not_unmap_the_live_node()
    {
        var scene = new Scene("S");
        var first = scene.Root.CreateChild("Box");
        Guid id = first.Id;

        // Two live nodes deliberately sharing an id: the later add wins the
        // mapping rather than throwing inside the ownership walk.
        var second = new SceneNode("Box", id);
        scene.Root.AddChild(second);
        scene.TryFindById(id, out SceneNode? found).ShouldBeTrue();
        found.ShouldBeSameAs(second);

        // Detaching the one that does NOT own the mapping must leave it alone.
        scene.Root.RemoveChild(first);
        scene.TryFindById(id, out found).ShouldBeTrue();
        found.ShouldBeSameAs(second);

        scene.Root.RemoveChild(second);
        scene.TryFindById(id, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_node_arriving_is_already_findable_from_the_NodeAdded_handler()
    {
        var scene = new Scene("S");
        SceneNode? resolved = null;
        scene.NodeAdded += node =>
        {
            // Handlers (the id-addressed editing layer above all) must be able
            // to resolve the node they are being told about.
            if (scene.TryFindById(node.Id, out SceneNode? hit))
                resolved = hit;
        };

        var added = scene.Root.CreateChild("A");

        resolved.ShouldBeSameAs(added);
    }

    [Fact]
    public void A_node_leaving_is_already_gone_from_the_NodeRemoved_handler()
    {
        var scene = new Scene("S");
        var node = scene.Root.CreateChild("A");
        bool stillFindable = true;
        scene.NodeRemoved += removed => stillFindable = scene.TryFindById(removed.Id, out _);

        scene.Root.RemoveChild(node);

        stillFindable.ShouldBeFalse();
    }
}
