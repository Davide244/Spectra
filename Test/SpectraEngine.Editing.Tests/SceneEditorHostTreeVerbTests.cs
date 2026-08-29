using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Maths;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The verbs a scene tree and a context menu drive: batch selection by id,
/// per-node rename, drag-and-drop reparent, and point-addressed pick/insert.
/// </summary>
/// <remarks>
/// <b>These exist because the tree holds ids, never nodes.</b> Every verb here
/// resolves ids on the render thread at apply time, so a UI whose view of the
/// graph is a frame or two behind can name a node that just left the scene and
/// get a refusal instead of a crash. The reparent tests additionally pin the
/// two silent-corruption traps of tree drags: a cycle (dropping a group onto
/// its own child) must be filtered before any command runs, because
/// <c>SceneNode.InsertChild</c> answers it with a throw that would poison an
/// open transaction; and a same-parent move must adjust its index for the slot
/// the node vacates, or every "drop below the next sibling" lands one row too
/// far.
/// </remarks>
public sealed class SceneEditorHostTreeVerbTests
{
    private static SceneEditorHost NewHost(Scene scene)
    {
        var renderer = new CompilingRenderer();
        renderer.SetFramebufferSize(new Vector2D<int>(1280, 720));

        return new SceneEditorHost(
            NullLoggerFactory.Instance,
            scene,
            renderer,
            new InputManager(NullLogger<InputManager>.Instance));
    }

    // --- Batch selection -----------------------------------------------------

    [Fact]
    public void Selecting_a_set_of_ids_raises_one_selection_change()
    {
        // One batch, not N single selects: the property panel unions the
        // selection on every change event, so a Ctrl-click spree reported as a
        // set must cost one union, not one per node.
        var scene = new Scene("Editor");
        SceneNode a = scene.Root.CreateChild("A");
        SceneNode b = scene.Root.CreateChild("B");
        SceneNode c = scene.Root.CreateChild("C");
        SceneEditorHost host = NewHost(scene);

        int raised = 0;
        scene.Selection.SelectionChanged += () => raised++;

        host.SelectByIds([a.Id, b.Id, c.Id]);

        scene.Selection.Count.ShouldBe(3);
        raised.ShouldBe(1);
    }

    [Fact]
    public void Unknown_ids_are_skipped_and_the_known_ones_still_select()
    {
        var scene = new Scene("Editor");
        SceneNode a = scene.Root.CreateChild("A");
        SceneEditorHost host = NewHost(scene);

        host.SelectByIds([Guid.NewGuid(), a.Id, Guid.NewGuid()]);

        scene.Selection.Count.ShouldBe(1);
        scene.Selection.Items[0].ShouldBeSameAs(a);
    }

    [Fact]
    public void Replacing_with_nothing_resolvable_clears_the_selection()
    {
        // The tree said "the selection is now this set"; if none of it exists
        // any more, the honest answer is an empty selection, not the old one.
        var scene = new Scene("Editor");
        SceneNode a = scene.Root.CreateChild("A");
        SceneEditorHost host = NewHost(scene);
        host.SelectById(a.Id);

        host.SelectByIds([Guid.NewGuid()]);

        scene.Selection.Count.ShouldBe(0);
    }

    [Fact]
    public void Add_and_toggle_modes_extend_and_flip()
    {
        var scene = new Scene("Editor");
        SceneNode a = scene.Root.CreateChild("A");
        SceneNode b = scene.Root.CreateChild("B");
        SceneEditorHost host = NewHost(scene);

        host.SelectByIds([a.Id]);
        host.SelectByIds([b.Id], SelectionUpdate.Add);
        scene.Selection.Count.ShouldBe(2);

        host.SelectByIds([a.Id], SelectionUpdate.Toggle);
        scene.Selection.Count.ShouldBe(1);
        scene.Selection.Items[0].ShouldBeSameAs(b);
    }

    // --- Rename --------------------------------------------------------------

    [Fact]
    public void Rename_is_one_history_entry_and_undo_restores_the_old_name()
    {
        var scene = new Scene("Editor");
        SceneNode node = scene.Root.CreateChild("Old");
        SceneEditorHost host = NewHost(scene);
        int depthBefore = host.UndoDepth;

        host.RenameById(node.Id, "New").ShouldBeTrue();

        node.Name.ShouldBe("New");
        host.UndoDepth.ShouldBe(depthBefore + 1);

        host.Apply(EditorHostCommand.Undo);
        node.Name.ShouldBe("Old");
    }

    [Fact]
    public void Rename_trims_and_refuses_empty_unchanged_and_unknown()
    {
        // An empty name is a row in the tree with nothing to click, and an
        // unchanged one would fill the history with entries that undo to
        // themselves - the same two refusals the property panel's Name field
        // already makes.
        var scene = new Scene("Editor");
        SceneNode node = scene.Root.CreateChild("Kept");
        SceneEditorHost host = NewHost(scene);
        int depthBefore = host.UndoDepth;

        host.RenameById(node.Id, "   ").ShouldBeFalse();
        host.RenameById(node.Id, "Kept").ShouldBeFalse();
        host.RenameById(Guid.NewGuid(), "Anything").ShouldBeFalse();
        host.RenameById(node.Id, "  Spaced  ").ShouldBeTrue();

        node.Name.ShouldBe("Spaced");
        host.UndoDepth.ShouldBe(depthBefore + 1);
    }

    // --- Reparent ------------------------------------------------------------

    [Fact]
    public void Reparent_moves_under_the_new_parent_and_nothing_appears_to_move()
    {
        var scene = new Scene("Editor");
        SceneNode target = scene.Root.CreateChild("Target");
        target.LocalPosition = new Vector3(2f, 0f, 0f);
        SceneNode node = scene.Root.CreateChild("Mover");
        node.LocalPosition = new Vector3(5f, 1f, 0f);
        SceneEditorHost host = NewHost(scene);

        host.ReparentByIds([node.Id], target.Id, -1);

        node.Parent.ShouldBeSameAs(target);
        node.WorldMatrix.Translation.X.ShouldBe(5f, 0.0001f, "the world position is preserved");
        node.WorldMatrix.Translation.Y.ShouldBe(1f, 0.0001f);
        node.LocalPosition.X.ShouldBe(3f, 0.0001f, "the local transform was re-expressed under the new parent");
    }

    [Fact]
    public void Undo_of_a_reparent_restores_placement_and_transform()
    {
        var scene = new Scene("Editor");
        SceneNode target = scene.Root.CreateChild("Target");
        target.LocalPosition = new Vector3(2f, 0f, 0f);
        scene.Root.CreateChild("Spacer");
        SceneNode node = scene.Root.CreateChild("Mover");
        node.LocalPosition = new Vector3(5f, 0f, 0f);
        SceneEditorHost host = NewHost(scene);
        int index = node.IndexInParent;

        host.ReparentByIds([node.Id], target.Id, -1);
        host.Apply(EditorHostCommand.Undo);

        node.Parent.ShouldBeSameAs(scene.Root);
        node.IndexInParent.ShouldBe(index, "sibling index is traversal order is placement order");
        // Absolute-value commands make exact equality the right assertion.
        node.LocalPosition.ShouldBe(new Vector3(5f, 0f, 0f));
    }

    [Fact]
    public void Dropping_a_node_onto_its_own_descendant_is_refused_not_thrown()
    {
        // The ordinary slip of every tree drag. InsertChild answers it with a
        // throw; reached from inside an open transaction that would leave the
        // history open and the scene half-moved, so the verb must filter it
        // out before any command runs.
        var scene = new Scene("Editor");
        SceneNode parent = scene.Root.CreateChild("Parent");
        SceneNode child = parent.CreateChild("Child");
        SceneEditorHost host = NewHost(scene);
        int depthBefore = host.UndoDepth;

        Should.NotThrow(() => host.ReparentByIds([parent.Id], child.Id, -1));

        parent.Parent.ShouldBeSameAs(scene.Root);
        host.UndoDepth.ShouldBe(depthBefore, "a refused drop records nothing");
    }

    [Fact]
    public void A_mixed_drag_still_moves_the_legal_nodes()
    {
        var scene = new Scene("Editor");
        SceneNode parent = scene.Root.CreateChild("Parent");
        SceneNode child = parent.CreateChild("Child");
        SceneNode free = scene.Root.CreateChild("Free");
        SceneEditorHost host = NewHost(scene);

        // Parent cannot legally move under its own child; Free can.
        host.ReparentByIds([parent.Id, free.Id], child.Id, -1);

        parent.Parent.ShouldBeSameAs(scene.Root);
        free.Parent.ShouldBeSameAs(child);
    }

    [Fact]
    public void Moving_a_node_later_under_its_own_parent_lands_where_the_drop_pointed()
    {
        // Children A,B,C; "drop A below B" names index 2 in the list the user
        // saw. A leaves slot 0 first, shifting B and C down, so inserting at
        // the unadjusted index would put A after C instead.
        var scene = new Scene("Editor");
        SceneNode a = scene.Root.CreateChild("A");
        SceneNode b = scene.Root.CreateChild("B");
        SceneNode c = scene.Root.CreateChild("C");
        SceneEditorHost host = NewHost(scene);

        host.ReparentByIds([a.Id], scene.Root.Id, 2);

        scene.Root.Children[0].ShouldBeSameAs(b);
        scene.Root.Children[1].ShouldBeSameAs(a);
        scene.Root.Children[2].ShouldBeSameAs(c);
    }

    [Fact]
    public void Append_lands_at_the_end_and_the_dragged_nodes_stay_selected()
    {
        var scene = new Scene("Editor");
        SceneNode group = scene.Root.CreateChild("Group");
        group.CreateChild("Existing");
        SceneNode node = scene.Root.CreateChild("Dragged");
        SceneEditorHost host = NewHost(scene);

        host.ReparentByIds([node.Id], group.Id, -1);

        group.Children[^1].ShouldBeSameAs(node);
        scene.Selection.Count.ShouldBe(1);
        scene.Selection.Items[0].ShouldBeSameAs(node, "a drop you cannot immediately act on is not a drop");
    }

    // --- Point-addressed verbs (the viewport context menu) -------------------

    [Fact]
    public void Right_click_selection_retargets_to_an_unselected_hit_and_keeps_a_selected_one()
    {
        var scene = new Scene("Editor");
        SceneNode plate = scene.Root.CreateChild("Plate");
        plate.Brush = SpectraEngine.Core.Bsp.Brush.CreateBox(
            new Vector3(-8f, -1f, -8f), new Vector3(8f, 1f, 8f));
        SceneNode other = scene.Root.CreateChild("Other");
        scene.Camera.Position = new Vector3(0.5f, 8f, 4f);
        scene.Camera.LookAt(new Vector3(0.5f, 1f, 0.5f));
        SceneEditorHost host = NewHost(scene);
        var centre = new Vector2(640f, 360f);

        host.SelectAtPoint(centre);
        scene.Selection.Items[0].ShouldBeSameAs(plate, "an unselected hit becomes the selection");

        // With the hit already in the selection, the set is kept whole - the
        // menu about to open acts on all of it.
        host.SelectByIds([plate.Id, other.Id]);
        host.SelectAtPoint(centre);
        scene.Selection.Count.ShouldBe(2);
    }

    [Fact]
    public void Right_click_on_empty_space_keeps_the_selection()
    {
        // The menu's verbs still need their subject: Studio and every IDE keep
        // the selection on a background right-click.
        var scene = new Scene("Editor");
        SceneNode node = scene.Root.CreateChild("Kept");
        scene.Camera.Position = new Vector3(0f, 5f, 0f);
        scene.Camera.LookAt(new Vector3(0f, 5f, -10f));
        SceneEditorHost host = NewHost(scene);
        host.SelectById(node.Id);

        host.SelectAtPoint(new Vector2(640f, 360f));

        scene.Selection.Count.ShouldBe(1);
    }

    [Fact]
    public void An_insert_aimed_at_a_point_lands_under_that_point_not_the_view_centre()
    {
        var scene = new Scene("Editor");
        SceneNode plate = scene.Root.CreateChild("Plate");
        plate.Brush = SpectraEngine.Core.Bsp.Brush.CreateBox(
            new Vector3(-16f, -1f, -16f), new Vector3(16f, 1f, 16f));
        scene.Camera.Position = new Vector3(0.5f, 8f, 4f);
        scene.Camera.LookAt(new Vector3(0.5f, 1f, 0.5f));
        SceneEditorHost host = NewHost(scene);

        // Snap off so the two landing spots compare by geometry, not by grid.
        host.Apply(GizmoCommand.DisableSnap);

        host.Insert(InsertKind.WorldBrush);
        SceneNode centred = scene.Root.Children[^1];
        host.Apply(EditorHostCommand.Undo);

        host.Insert(InsertKind.WorldBrush, new Vector2(320f, 360f));
        SceneNode aimed = scene.Root.Children[^1];

        aimed.LocalPosition.X.ShouldBeLessThan(
            centred.LocalPosition.X, "a point left of centre lands left of the centre insert");
        aimed.LocalPosition.Y.ShouldBe(2f, 0.001f, "it still rests flush on the aimed surface");
    }
}
