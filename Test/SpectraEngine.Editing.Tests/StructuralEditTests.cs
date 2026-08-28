using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Undo;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Duplicate, delete, group and ungroup: the verbs that turn a manipulator into
/// an editor, and the undo behaviour that makes them safe to use.
/// </summary>
/// <remarks>
/// <b>The claim under all of these is that a structural undo restores the
/// PLACEMENT, not merely the node.</b> Child-list order is traversal order is
/// the static world's placement-slot order, so a node that comes back at the
/// wrong sibling index rebuilds a level that is valid, different, and bit-unequal
/// to the one that was there. <c>StructuralOrderTests</c> in the Bsp suite owns
/// the geometry half of that chain; this suite owns the graph half.
/// <para>
/// The second claim is that each verb is exactly ONE history entry however many
/// commands it is composed of, because a user who groups forty parts expects one
/// Ctrl+Z to undo it.
/// </para>
/// </remarks>
public sealed class StructuralEditTests
{
    private const float Tolerance = 1e-4f;

    // --- Delete --------------------------------------------------------------

    [Fact]
    public void Undoing_a_delete_puts_the_node_back_at_its_own_sibling_index()
    {
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode[] row = Row(scene, 5);
        SceneNode middle = row[2];

        StructuralEditor.TryDelete(scene, undo, [middle]).ShouldBeTrue();

        scene.Root.Children.Count.ShouldBe(4);
        scene.TryFindById(middle.Id, out _).ShouldBeFalse();

        undo.Undo().ShouldBeTrue();

        scene.Root.Children.Count.ShouldBe(5);
        middle.IndexInParent.ShouldBe(2);
        // The same INSTANCE, under the same id, so every history entry behind
        // the delete still resolves to the node it names.
        scene.TryFindById(middle.Id, out SceneNode? restored).ShouldBeTrue();
        restored.ShouldBeSameAs(middle);
    }

    [Fact]
    public void A_delete_of_a_whole_selection_is_one_history_entry()
    {
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode[] row = Row(scene, 5);

        StructuralEditor.TryDelete(scene, undo, [row[0], row[2], row[4]]).ShouldBeTrue();

        scene.Root.Children.Count.ShouldBe(2);
        undo.Count.ShouldBe(1);
        undo.UndoName.ShouldBe("Delete");

        undo.Undo().ShouldBeTrue();

        // All three back, each at its own index, in the original order.
        scene.Root.Children.Count.ShouldBe(5);
        for (int i = 0; i < row.Length; i++)
            scene.Root.Children[i].ShouldBeSameAs(row[i]);
    }

    [Fact]
    public void Deleting_a_parent_and_its_child_together_removes_the_parent_once()
    {
        // The effective-selection rule: a node an also-selected ancestor already
        // carries must not be recorded separately, or its undo would name a
        // parent that is itself still deleted.
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode parent = scene.Root.CreateChild("Parent");
        SceneNode child = parent.CreateChild("Child");

        StructuralEditor.TryDelete(scene, undo, [child, parent]).ShouldBeTrue();

        scene.Root.Children.Count.ShouldBe(0);
        undo.Undo().ShouldBeTrue();

        scene.Root.Children.Count.ShouldBe(1);
        scene.Root.Children[0].ShouldBeSameAs(parent);
        parent.Children.Count.ShouldBe(1);
        parent.Children[0].ShouldBeSameAs(child);
    }

    [Fact]
    public void Undoing_a_delete_relights_a_light_node()
    {
        // Scene.OnNodeRemoved drops a departing node from the light list
        // unconditionally, and the Light setter only registers a node that
        // already has an Owner. Without OnNodeAdded rechecking light membership,
        // a deleted-and-undone light is gone for good: nothing throws, nothing
        // logs, and the scene is simply darker.
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode lamp = scene.Root.CreateChild("Lamp");
        lamp.Light = new Light { Kind = LightKind.Point, Intensity = 3f };

        scene.LightNodes.ShouldContain(lamp);

        StructuralEditor.TryDelete(scene, undo, [lamp]).ShouldBeTrue();
        scene.LightNodes.ShouldNotContain(lamp);

        undo.Undo().ShouldBeTrue();
        scene.LightNodes.ShouldContain(lamp);
    }

    // --- Duplicate -----------------------------------------------------------

    [Fact]
    public void A_duplicate_is_a_new_node_with_its_own_brush_and_a_shared_mesh()
    {
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode original = scene.Root.CreateChild("Part");
        original.LocalPosition = new Vector3(3f, 4f, 5f);
        original.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
        original.MeshRenderer = new MeshRenderer(BoxMesh.Centred(Vector3.One), new Material(null));
        original.Light = new Light { Intensity = 2f };
        original.CollisionGroup = 3;

        StructuralEditor.TryDuplicate(scene, undo, [original]).ShouldBeTrue();

        scene.Selection.Count.ShouldBe(1);
        SceneNode clone = scene.Selection.Items[0];
        clone.ShouldNotBeSameAs(original);
        clone.Id.ShouldNotBe(original.Id);
        clone.Name.ShouldBe(original.Name);
        clone.LocalPosition.ShouldBe(original.LocalPosition);
        clone.CollisionGroup.ShouldBe(3);

        // A brush of its own, so it gets its own carve-cache slot instead of
        // colliding with the original's on every compile.
        clone.Brush.ShouldNotBeSameAs(original.Brush);
        clone.Brush!.LocalBounds.Max.ShouldBe(original.Brush!.LocalBounds.Max);

        // A mesh shared by reference: immutable, and its GPU resources are
        // renderer-owned, so a thousand duplicates cost one mesh.
        clone.MeshRenderer.ShouldBeSameAs(original.MeshRenderer);

        // A light COPIED, because it is the one mutable payload: sharing it
        // would make dimming the copy dim the original.
        clone.Light.ShouldNotBeSameAs(original.Light);
        clone.Light!.Intensity.ShouldBe(2f);
        clone.Light.Intensity = 0.5f;
        original.Light!.Intensity.ShouldBe(2f);
    }

    [Fact]
    public void A_duplicate_does_not_claim_the_original_s_physics_body()
    {
        // HasBody is owned by the physics layer and means "a body exists in the
        // side table for THIS node". A copy that claimed it would send every
        // body lookup for the duplicate to an entry that is not there.
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode original = scene.Root.CreateChild("Part");
        original.PhysicsFlags |= PhysicsFlags.HasBody;
        original.Anchored = false;

        StructuralEditor.TryDuplicate(scene, undo, [original]).ShouldBeTrue();

        SceneNode clone = scene.Selection.Items[0];
        (clone.PhysicsFlags & PhysicsFlags.HasBody).ShouldBe(PhysicsFlags.None);
        clone.Anchored.ShouldBeFalse();
        clone.CanCollide.ShouldBeTrue();
    }

    [Fact]
    public void Duplicating_a_group_copies_the_whole_subtree()
    {
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode group = scene.Root.CreateChild("Group");
        SceneNode a = group.CreateChild("A");
        a.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
        group.CreateChild("B").CreateChild("C");

        StructuralEditor.TryDuplicate(scene, undo, [group]).ShouldBeTrue();

        SceneNode clone = scene.Selection.Items[0];
        clone.Children.Count.ShouldBe(2);
        clone.Children[0].Name.ShouldBe("A");
        clone.Children[1].Children[0].Name.ShouldBe("C");
        clone.SubtreeBrushCount.ShouldBe(1);
        clone.Children[0].Brush.ShouldNotBeSameAs(a.Brush);

        // Every clone is indexed, so a command can address any of them.
        scene.TryFindById(clone.Children[1].Children[0].Id, out _).ShouldBeTrue();
    }

    [Fact]
    public void Undoing_a_duplicate_removes_exactly_the_copies()
    {
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode[] row = Row(scene, 3);

        StructuralEditor.TryDuplicate(scene, undo, row).ShouldBeTrue();
        scene.Root.Children.Count.ShouldBe(6);
        undo.Count.ShouldBe(1);

        undo.Undo().ShouldBeTrue();

        scene.Root.Children.Count.ShouldBe(3);
        for (int i = 0; i < row.Length; i++)
            scene.Root.Children[i].ShouldBeSameAs(row[i]);

        undo.Redo().ShouldBeTrue();
        scene.Root.Children.Count.ShouldBe(6);
    }

    // --- Group and ungroup ---------------------------------------------------

    [Fact]
    public void Grouping_pivots_on_the_selection_and_leaves_every_child_where_it_was()
    {
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode left = BrushNode(scene, new Vector3(-4f, 0f, 0f));
        SceneNode right = BrushNode(scene, new Vector3(6f, 0f, 0f));

        StructuralEditor.TryGroup(scene, undo, [left, right]).ShouldBeTrue();

        SceneNode group = scene.Selection.Items[0];
        group.Name.ShouldBe("Group");
        group.Children.Count.ShouldBe(2);

        // The pivot is the centre of the box around what it contains, which is
        // what every later rotate and resize of the group turns about. Close to,
        // not exactly: the box comes from the brushes' plane-derived bounds,
        // whose centre sits a few tens of nanometres off the nominal one.
        group.LocalPosition.ShouldBeCloseTo(new Vector3(1f, 0f, 0f), Tolerance);

        // ...and nothing moved in the world.
        left.WorldPosition.ShouldBeCloseTo(new Vector3(-4f, 0f, 0f), Tolerance);
        right.WorldPosition.ShouldBeCloseTo(new Vector3(6f, 0f, 0f), Tolerance);
        left.LocalPosition.X.ShouldBe(-5f, Tolerance);
        right.LocalPosition.X.ShouldBe(5f, Tolerance);
    }

    [Fact]
    public void A_group_takes_the_place_of_what_it_grouped_and_undoes_in_one_step()
    {
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode[] row = Row(scene, 4);

        StructuralEditor.TryGroup(scene, undo, [row[1], row[2]]).ShouldBeTrue();

        // Two originals moved inside the group, and the group took the lower of
        // their two slots rather than appearing at the bottom of the tree.
        scene.Root.Children.Count.ShouldBe(3);
        scene.Root.Children[1].ShouldBeSameAs(scene.Selection.Items[0]);
        undo.Count.ShouldBe(1);
        undo.UndoName.ShouldBe("Group");

        undo.Undo().ShouldBeTrue();

        scene.Root.Children.Count.ShouldBe(4);
        for (int i = 0; i < row.Length; i++)
        {
            scene.Root.Children[i].ShouldBeSameAs(row[i]);
            row[i].LocalPosition.ShouldBe(new Vector3(i * 1.5f, 0f, 0f));
        }
    }

    [Fact]
    public void Ungrouping_returns_the_children_to_the_group_s_slot_and_keeps_them_still()
    {
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode[] row = Row(scene, 3);
        StructuralEditor.TryGroup(scene, undo, [row[0], row[1]]).ShouldBeTrue();
        SceneNode group = scene.Selection.Items[0];

        Vector3 worldBefore = row[1].WorldPosition;

        StructuralEditor.TryUngroup(scene, undo, [group]).ShouldBeTrue();

        // The group is gone and its children took its slot, in order.
        scene.Root.Children.Count.ShouldBe(3);
        scene.Root.Children[0].ShouldBeSameAs(row[0]);
        scene.Root.Children[1].ShouldBeSameAs(row[1]);
        scene.TryFindById(group.Id, out _).ShouldBeFalse();
        row[1].WorldPosition.ShouldBe(worldBefore);

        undo.Undo().ShouldBeTrue();
        scene.TryFindById(group.Id, out _).ShouldBeTrue();
        group.Children.Count.ShouldBe(2);
        row[1].WorldPosition.ShouldBe(worldBefore);
    }

    [Fact]
    public void Ungrouping_something_that_is_not_a_group_changes_nothing()
    {
        (Scene scene, UndoStack undo) = Fixture();
        SceneNode[] row = Row(scene, 2);

        StructuralEditor.TryUngroup(scene, undo, row).ShouldBeFalse();

        scene.Root.Children.Count.ShouldBe(2);
        undo.Count.ShouldBe(0);
    }

    [Fact]
    public void Every_verb_refuses_an_empty_selection_without_touching_the_history()
    {
        (Scene scene, UndoStack undo) = Fixture();

        StructuralEditor.TryDuplicate(scene, undo, []).ShouldBeFalse();
        StructuralEditor.TryDelete(scene, undo, []).ShouldBeFalse();
        StructuralEditor.TryGroup(scene, undo, []).ShouldBeFalse();
        StructuralEditor.TryUngroup(scene, undo, []).ShouldBeFalse();

        // The scene root itself is never a target: it has no placement to
        // restore and removing it is not an operation the engine offers.
        StructuralEditor.TryDelete(scene, undo, [scene.Root]).ShouldBeFalse();

        undo.Count.ShouldBe(0);
    }

    // --- Helpers -------------------------------------------------------------

    private static (Scene Scene, UndoStack Undo) Fixture()
    {
        var scene = new Scene("Structural");
        return (scene, new UndoStack(scene));
    }

    private static SceneNode[] Row(Scene scene, int count)
    {
        var nodes = new SceneNode[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = scene.Root.CreateChild($"Node{i}");
            nodes[i].LocalPosition = new Vector3(i * 1.5f, 0f, 0f);
        }

        return nodes;
    }

    private static SceneNode BrushNode(Scene scene, Vector3 position)
    {
        SceneNode node = scene.Root.CreateChild("Brush");
        node.LocalPosition = position;
        node.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
        return node;
    }
}
