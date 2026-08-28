using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The chain nothing else in this suite covers end to end:
/// <b>sibling index → traversal order → placement order → compiled geometry.</b>
/// </summary>
/// <remarks>
/// Every determinism oracle here builds a literal <c>BrushPlacement[]</c> and
/// calls <c>CsgWorld.Build</c> directly, which is exactly the right shape for
/// proving that identical placements give identical floats. None of them goes
/// through the scene graph, so none of them can see the step that decides what
/// those placements ARE: <c>Scene</c> walks the graph in child-list order and
/// appends one placement per admitted brush, so a node that comes back from an
/// undo at a different sibling index shifts every later slot.
/// <para>
/// That is why <c>SceneNode.InsertChild</c> exists at all. <c>AddChild</c> only
/// appends, so a delete-then-undo built on it would rebuild a level that is
/// valid, different, and bit-unequal to the one that was there, and the whole
/// existing oracle suite would stay green while it happened.
/// </para>
/// <para>
/// The geometry assertion below is meaningful precisely because the placement
/// oracles already establish that identical placement lists give bit-identical
/// output; this suite's job is the step before that.
/// </para>
/// </remarks>
public sealed class StructuralOrderTests
{
    [Fact]
    public void Restoring_a_middle_sibling_at_its_index_rebuilds_the_world_bit_for_bit()
    {
        var scene = new Scene("Structural");
        SceneNode[] nodes = BuildOverlappingRow(scene, count: 5);

        (float[] Vertices, uint[] Indices) before = Compile(scene);
        Guid[] orderBefore = PlacementOrder(scene);

        // Take out a middle sibling and put it back exactly where it was, which
        // is what RemoveNodesCommand and its undo do to the graph.
        SceneNode removed = nodes[2];
        int index = removed.IndexInParent;
        index.ShouldBe(2);

        scene.Root.RemoveChild(removed);
        scene.Root.InsertChild(index, removed);

        PlacementOrder(scene).ShouldBe(orderBefore);

        (float[] Vertices, uint[] Indices) after = Compile(scene);
        after.Vertices.ShouldBe(before.Vertices);
        after.Indices.ShouldBe(before.Indices);
    }

    [Fact]
    public void Appending_the_same_sibling_back_puts_it_in_a_different_placement_slot()
    {
        // The negative half, and the reason InsertChild is not a convenience.
        // Asserted on the placement ORDER rather than on the float arrays,
        // because whether a given reordering also changes the geometry depends
        // on which brushes overlap: the order is the mechanism, and the
        // placement oracles own the step from order to floats.
        var scene = new Scene("Structural");
        SceneNode[] nodes = BuildOverlappingRow(scene, count: 5);
        Guid[] orderBefore = PlacementOrder(scene);

        SceneNode removed = nodes[2];
        scene.Root.RemoveChild(removed);
        scene.Root.AddChild(removed);

        Guid[] orderAfter = PlacementOrder(scene);
        orderAfter.ShouldNotBe(orderBefore);
        orderAfter[^1].ShouldBe(removed.Id);
    }

    [Fact]
    public void A_clone_inserted_at_an_index_takes_that_placement_slot()
    {
        var scene = new Scene("Structural");
        SceneNode[] nodes = BuildOverlappingRow(scene, count: 4);

        SceneNode clone = nodes[1].Clone();
        scene.Root.InsertChild(1, clone);

        Guid[] order = PlacementOrder(scene);
        order.Length.ShouldBe(5);
        order[1].ShouldBe(clone.Id);
        order[2].ShouldBe(nodes[1].Id);

        // ...and the clone carries its own brush instance, which is what buys it
        // its own carve-cache slot instead of colliding with the original's.
        clone.Brush.ShouldNotBeSameAs(nodes[1].Brush);
        clone.Brush!.LocalBounds.Min.ShouldBe(nodes[1].Brush!.LocalBounds.Min);
        clone.Brush.LocalBounds.Max.ShouldBe(nodes[1].Brush.LocalBounds.Max);
    }

    [Fact]
    public void Insert_maintains_the_subtree_counters_exactly_as_append_does()
    {
        // InsertChild is a third writer of the counter lanes (AdjustSubtreeBrushCounts
        // is documented as their only writer, reached from attach and detach), so
        // it has to move them identically or the rigidity and dirtying questions
        // both start lying.
        var scene = new Scene("Structural");
        SceneNode group = scene.Root.CreateChild("Group");

        SceneNode brush = new SceneNode("Brush") { Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f)) };
        SceneNode part = new SceneNode("Part")
        {
            Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f)),
            BrushKind = BrushKind.Part,
        };

        group.InsertChild(0, part);
        group.InsertChild(0, brush);

        group.Children[0].ShouldBeSameAs(brush);
        group.SubtreeBrushCount.ShouldBe(2);
        group.SubtreeStaticWorldBrushCount.ShouldBe(1);
        scene.Root.SubtreeBrushCount.ShouldBe(2);
        scene.Root.SubtreeStaticWorldBrushCount.ShouldBe(1);

        group.RemoveChild(brush);
        group.SubtreeBrushCount.ShouldBe(1);
        group.SubtreeStaticWorldBrushCount.ShouldBe(0);
        scene.Root.SubtreeBrushCount.ShouldBe(1);
    }

    [Fact]
    public void Attaching_a_node_under_itself_or_its_own_descendant_is_refused()
    {
        // A cycle does not surface here: it surfaces as a hang the first time
        // anything walks the graph, which is every frame.
        var scene = new Scene("Structural");
        SceneNode parent = scene.Root.CreateChild("Parent");
        SceneNode child = parent.CreateChild("Child");

        Should.Throw<ArgumentException>(() => parent.AddChild(parent));
        Should.Throw<ArgumentException>(() => child.AddChild(parent));
        Should.Throw<ArgumentException>(() => child.InsertChild(0, parent));

        // The legal direction still works, and is a reorder rather than a cycle.
        parent.InsertChild(0, child).ShouldBeSameAs(child);
    }

    // A row of unit boxes each overlapping its neighbour, so the carve genuinely
    // has to order them against each other rather than treating them as isolated.
    private static SceneNode[] BuildOverlappingRow(Scene scene, int count)
    {
        var nodes = new SceneNode[count];
        for (int i = 0; i < count; i++)
        {
            SceneNode node = scene.Root.CreateChild($"Brush{i}");
            node.LocalPosition = new Vector3(i * 1.5f, 0f, 0f);
            node.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
            nodes[i] = node;
        }

        return nodes;
    }

    // The ids of the admitted brush nodes in the order the snapshot walk finds
    // them, which IS the order their placements are appended in.
    private static Guid[] PlacementOrder(Scene scene) =>
        [.. scene.Root.Traverse().Where(n => n.IsStaticWorldBrush).Select(n => n.Id)];

    private static (float[] Vertices, uint[] Indices) Compile(Scene scene)
    {
        scene.RebuildStaticWorld(new FakeRenderer());
        scene.StaticWorld.ShouldNotBeNull();
        return scene.StaticWorld!.BuildMesh();
    }
}
