using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Maths;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using System;
using System.Linq;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The editor's verb surface: the calls a toolbar makes instead of a key press.
/// </summary>
/// <remarks>
/// <b>What is being pinned is that there is ONE path, not two.</b> Every verb
/// here was previously reachable only as a key chord inside the host's own
/// shortcut handler. A shell needs the same verbs, and the tempting shortcut is
/// to synthesise fake key presses at it; that is a second input path free to
/// drift from the real one, and it would drift silently because both look right
/// in isolation. These tests assert the public verbs do what the chords do.
/// <para>
/// The reporting half matters just as much: a three-button tool row needs to
/// know which button is lit, and the label it reads has to be a value rather
/// than a string somebody splits.
/// </para>
/// </remarks>
public sealed class SceneEditorHostCommandTests
{
    private static SceneEditorHost NewHost(Scene scene)
    {
        var renderer = new CompilingRenderer();

        // The host measures its viewport from the renderer's latch in its
        // constructor, and a zero-sized one makes every later pick undefined.
        renderer.SetFramebufferSize(new Vector2D<int>(1280, 720));

        return new SceneEditorHost(
            NullLoggerFactory.Instance,
            scene,
            renderer,
            new InputManager(NullLogger<InputManager>.Instance));
    }

    private static SceneNode AddChild(Scene scene, string name)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.LocalPosition = new Vector3(1f, 0f, 0f);
        return node;
    }

    // --- Reporting -----------------------------------------------------------

    [Fact]
    public void The_tool_and_its_handle_style_are_reported_separately()
    {
        // They used to be one combined label ("move/Studio"), which reads fine
        // in a log line and is useless to a toolbar: three buttons need to know
        // which one is lit, and splitting a string to find out is a contract
        // nobody wrote down.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.GizmoModeName.ShouldBe("move");
        host.GizmoStyleName.ShouldBe("Studio");

        host.Apply(GizmoCommand.UseRotate);
        host.GizmoModeName.ShouldBe("rotate");

        host.Apply(GizmoCommand.ToggleStyle);
        host.GizmoStyleName.ShouldBe("Classic");
        host.GizmoModeName.ShouldBe("rotate", "the style does not change the tool");
    }

    [Fact]
    public void The_labels_are_interned_constants_rather_than_formatted_strings()
    {
        // The periodic stats line reads these on an otherwise allocation-free
        // path, so a formatted enum here is a per-frame allocation nothing
        // reports.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        ReferenceEquals(host.GizmoModeName, host.GizmoModeName).ShouldBeTrue();
        ReferenceEquals(host.GizmoStyleName, host.GizmoStyleName).ShouldBeTrue();
        ReferenceEquals(host.GizmoOrientationName, host.GizmoOrientationName).ShouldBeTrue();
        ReferenceEquals(host.NavigationModeName, host.NavigationModeName).ShouldBeTrue();
    }

    [Fact]
    public void The_orientation_label_follows_the_frame_toggle()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.GizmoOrientationName.ShouldBe("world");
        host.Apply(GizmoCommand.ToggleOrientation);
        host.GizmoOrientationName.ShouldBe("local");
    }

    [Fact]
    public void The_snap_increment_is_the_live_tools_own_unit()
    {
        // All three snaps are absolute quantities of the thing being edited,
        // never a multiplier, so the number means world units under move and
        // degrees under rotate. A UI showing one must show which tool is live.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.SnapEnabled.ShouldBeTrue();
        float moveIncrement = host.SnapIncrement;

        host.Apply(GizmoCommand.UseRotate);
        host.SnapIncrement.ShouldNotBe(moveIncrement, "degrees are not world units");
        host.SnapIncrement.ShouldBe(15f);
    }

    [Fact]
    public void Toggling_snap_reports_through()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.Apply(GizmoCommand.ToggleSnap);
        host.SnapEnabled.ShouldBeFalse();

        host.Apply(GizmoCommand.ToggleSnap);
        host.SnapEnabled.ShouldBeTrue();
    }

    // --- Idempotent state verbs ----------------------------------------------
    //
    // The Use*/Enable*/Disable* verbs exist for controls that name a state
    // rather than flip one: a toggle sent against a snapshot one publish stale
    // flips the wrong way exactly when the user clicks fastest. What is pinned
    // is that naming the current state changes nothing and says so.

    [Fact]
    public void An_idempotent_verb_names_a_state_and_reports_whether_it_changed()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.Apply(GizmoCommand.UseLocalOrientation).ShouldBeTrue();
        host.GizmoOrientationName.ShouldBe("local");

        host.Apply(GizmoCommand.UseLocalOrientation).ShouldBeFalse("already local");
        host.GizmoOrientationName.ShouldBe("local");

        host.Apply(GizmoCommand.UseStudioStyle).ShouldBeFalse("Studio is the default");
        host.Apply(GizmoCommand.UseClassicStyle).ShouldBeTrue();
        host.GizmoStyleName.ShouldBe("Classic");

        host.Apply(GizmoCommand.EnableSnap).ShouldBeFalse("snap starts on");
        host.Apply(GizmoCommand.DisableSnap).ShouldBeTrue();
        host.SnapEnabled.ShouldBeFalse();
    }

    // --- Snap increments -----------------------------------------------------

    [Fact]
    public void Every_tools_increment_is_readable_without_switching_tools()
    {
        // A command surface shows the move grid and the rotate angle side by
        // side, so the per-tool values are named properties rather than
        // whatever the live tool happens to be.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.MoveSnapIncrement.ShouldBe(1f);
        host.RotateSnapIncrement.ShouldBe(15f);
        host.ResizeSnapIncrement.ShouldBe(1f);
        host.GizmoModeName.ShouldBe("move", "reading them switched nothing");
    }

    [Fact]
    public void Setting_an_increment_targets_one_tool_and_leaves_the_others_alone()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.SetSnapIncrement(GizmoMode.Rotate, 22.5f);

        host.RotateSnapIncrement.ShouldBe(22.5f);
        host.MoveSnapIncrement.ShouldBe(1f);
        host.ResizeSnapIncrement.ShouldBe(1f);
    }

    [Fact]
    public void A_bad_increment_is_refused_before_anything_is_written()
    {
        // The property panel's rule: a value the setting would throw on is
        // refused up front, because clamping writes a number nobody asked for
        // and reports nothing.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        Should.NotThrow(() => host.SetSnapIncrement(GizmoMode.Translate, 0f));
        Should.NotThrow(() => host.SetSnapIncrement(GizmoMode.Translate, -2f));
        Should.NotThrow(() => host.SetSnapIncrement(GizmoMode.Translate, float.NaN));

        host.MoveSnapIncrement.ShouldBe(1f);
    }

    // --- Select all / clear --------------------------------------------------

    [Fact]
    public void Select_all_takes_the_top_level_not_the_whole_graph()
    {
        // Moving the top level moves everything anyway, and a selection of
        // every descendant would make the property union scale with the graph
        // instead of with what the user can see.
        var scene = new Scene("Editor");
        SceneNode a = AddChild(scene, "A");
        a.CreateChild("Grandchild");
        SceneNode b = AddChild(scene, "B");

        SceneEditorHost host = NewHost(scene);
        host.Apply(EditorHostCommand.SelectAll);

        scene.Selection.Count.ShouldBe(2);
        scene.Selection.Items.ShouldContain(a);
        scene.Selection.Items.ShouldContain(b);

        host.Apply(EditorHostCommand.ClearSelection);
        scene.Selection.Count.ShouldBe(0);
    }

    // --- Insert --------------------------------------------------------------

    [Fact]
    public void Insert_creates_selects_and_is_one_undo_entry()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.Insert(InsertKind.WorldBrush);

        scene.Root.Children.Count.ShouldBe(1);
        SceneNode node = scene.Root.Children[0];
        node.Name.ShouldBe("Brush");
        node.Brush.ShouldNotBeNull();
        node.BrushKind.ShouldBe(BrushKind.World);
        scene.Selection.Items.ShouldBe([node]);
        host.UndoDepth.ShouldBe(1);

        Guid id = node.Id;
        host.Apply(EditorHostCommand.Undo);
        scene.Root.Children.ShouldBeEmpty();

        // Redo brings it back under the same id, like every structural verb,
        // so a shell holding the id keeps working.
        host.Apply(EditorHostCommand.Redo);
        scene.Root.Children.Count.ShouldBe(1);
        scene.Root.Children[0].Id.ShouldBe(id);
    }

    [Fact]
    public void An_inserted_hole_is_subtractive_and_world_kind()
    {
        // The one pairing that cancels: a subtractive PART carves nothing and
        // draws nothing, so the insert must never produce one.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.Insert(InsertKind.SubtractiveBrush);

        SceneNode node = scene.Root.Children[0];
        node.Brush.ShouldNotBeNull();
        node.Brush.Operation.ShouldBe(SpectraEngine.Core.Bsp.BrushOperation.Subtractive);
        node.BrushKind.ShouldBe(BrushKind.World);
    }

    [Fact]
    public void An_inserted_part_leaves_the_carve()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.Insert(InsertKind.PartBrush);

        scene.Root.Children[0].BrushKind.ShouldBe(BrushKind.Part);
    }

    [Fact]
    public void An_inserted_light_carries_a_valid_point_light()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.Insert(InsertKind.PointLight);

        SceneNode node = scene.Root.Children[0];
        node.Light.ShouldNotBeNull();
        node.Light.Kind.ShouldBe(LightKind.Point);
        node.Light.Intensity.ShouldBeGreaterThan(0f);
        node.Light.Range.ShouldBeGreaterThan(0f);
    }

    [Fact]
    public void A_brush_rests_on_the_aimed_surface_and_a_hole_bites_into_it()
    {
        // The two clearances are one decision each: an additive brush pushed
        // out by its half extent rests flush, while a subtractive one pushed
        // out the same way would share only the boundary plane with the solid
        // and the carve treats a resting negative as a no-op - a hole that
        // never cuts. The hole's centre therefore lands ON the surface,
        // half-buried, and the snap must not disturb either (it aligns along
        // the surface and never along the normal).
        var scene = new Scene("Editor");
        SceneNode plate = scene.Root.CreateChild("Plate");
        plate.Brush = SpectraEngine.Core.Bsp.Brush.CreateBox(
            new System.Numerics.Vector3(-8f, -1f, -8f), new System.Numerics.Vector3(8f, 1f, 8f));

        // The centre ray hits exactly the look target when the target sits on
        // the plate's top plane (y = 1).
        scene.Camera.Position = new System.Numerics.Vector3(0.5f, 8f, 4f);
        scene.Camera.LookAt(new System.Numerics.Vector3(0.5f, 1f, 0.5f));

        SceneEditorHost host = NewHost(scene);

        host.Insert(InsertKind.SubtractiveBrush);
        SceneNode hole = scene.Root.Children[^1];
        hole.LocalPosition.Y.ShouldBe(1f, 0.001f, "a hole starts half-buried in the surface");

        // Undone first, because the placement ray sees every pickable node -
        // deliberately, that is the fix this test pins - and the second
        // insert would otherwise rest on the first one.
        host.Apply(EditorHostCommand.Undo);

        host.Insert(InsertKind.WorldBrush);
        SceneNode brush = scene.Root.Children[^1];
        brush.LocalPosition.Y.ShouldBe(2f, 0.001f, "an additive brush rests flush on the surface");
    }

    [Fact]
    public void Inserts_land_on_the_move_grid_while_snap_is_on()
    {
        // Snap defaults on with a grid of one world unit, so a fresh insert
        // starts life aligned instead of needing a corrective nudge.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);
        host.SnapEnabled.ShouldBeTrue();

        host.Insert(InsertKind.WorldBrush);

        System.Numerics.Vector3 position = scene.Root.Children[0].LocalPosition;
        position.X.ShouldBe(MathF.Round(position.X));
        position.Y.ShouldBe(MathF.Round(position.Y));
        position.Z.ShouldBe(MathF.Round(position.Z));
    }

    // --- Insert entity -------------------------------------------------------

    [Fact]
    public void Inserting_an_entity_is_one_history_entry_and_leaves_it_selected()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.InsertEntity("logic_relay");

        scene.Root.Children.Count.ShouldBe(1);
        SceneNode node = scene.Root.Children[0];
        node.Entity.ShouldNotBeNull();
        node.Entity.ClassName.ShouldBe("logic_relay");
        scene.Selection.Items.ShouldBe([node]);
        host.UndoDepth.ShouldBe(1);

        Guid id = node.Id;
        host.Apply(EditorHostCommand.Undo);
        scene.Root.Children.ShouldBeEmpty();

        host.Apply(EditorHostCommand.Redo);
        scene.Root.Children[0].Id.ShouldBe(id);
        scene.Root.Children[0].Entity.ShouldNotBeNull();
    }

    [Fact]
    public void A_fresh_entity_carries_no_keyvalues_at_all()
    {
        // OMIT AT DEFAULT, which is the rule the map format already keeps. The
        // panel shows the schema's declared defaults for keys nobody has
        // authored, and a commit that produces the value the entity already
        // effectively has records nothing - so a key appears in the file
        // exactly when somebody changed it. Seeding the declared defaults here
        // would write the whole schema into every map, and a later change to a
        // default would then reach no level ever saved.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.InsertEntity("logic_timer");

        scene.Root.Children[0].Entity!.Keyvalues.ShouldBeEmpty();
        scene.Root.Children[0].Entity!.Connections.ShouldBeEmpty();
    }

    [Fact]
    public void An_inserted_entity_is_named_after_its_class()
    {
        // The name IS the targetname; there is no second identity to invent one
        // from. Duplicates are legal and MEAN something - firing at a name
        // fires every match - and every other insert produces a duplicate name
        // too, so numbering this one alone would make it behave unlike the
        // other six for no reason a user could predict.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.InsertEntity("logic_relay");
        host.InsertEntity("logic_relay");

        scene.Root.Children.Select(n => n.Name).ShouldBe(["logic_relay", "logic_relay"]);
    }

    [Fact]
    public void An_entity_with_no_class_is_refused()
    {
        // A class with no name resolves in no catalogue and would save as an
        // entity nothing can bind: a node that reads as an entity in the tree
        // and is not one anywhere else.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.InsertEntity("   ");

        scene.Root.Children.ShouldBeEmpty();
        host.UndoDepth.ShouldBe(0);
    }

    [Fact]
    public void Inserting_an_entity_is_refused_while_play_mode_owns_the_scene()
    {
        // The same RefuseEdit gate every other mutating verb goes through: a
        // shell gates its buttons on a snapshot up to a publish interval old,
        // so a click landing in that window arrives at a suspended editor.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);
        host.Suspend();

        host.InsertEntity("logic_relay");

        scene.Root.Children.ShouldBeEmpty();
        host.UndoDepth.ShouldBe(0);

        host.Resume();
        host.InsertEntity("logic_relay");
        scene.Root.Children.Count.ShouldBe(1);
    }

    // --- Structural verbs ----------------------------------------------------

    [Fact]
    public void Duplicate_copies_the_selection_and_undo_takes_the_copy_back()
    {
        var scene = new Scene("Editor");
        SceneNode original = AddChild(scene, "Original");
        SceneEditorHost host = NewHost(scene);
        scene.Selection.Select(original);

        host.Apply(EditorHostCommand.Duplicate);

        scene.Root.Children.Count.ShouldBe(2);
        host.UndoDepth.ShouldBe(1);

        host.Apply(EditorHostCommand.Undo);

        scene.Root.Children.Count.ShouldBe(1);
        host.UndoDepth.ShouldBe(0);
        host.RedoDepth.ShouldBe(1);
    }

    [Fact]
    public void Delete_removes_the_selection_and_redo_puts_it_back_under_the_same_id()
    {
        // Structural commands address nodes by id precisely so an undone delete
        // can recreate one; a shell holding that id has to keep working.
        var scene = new Scene("Editor");
        SceneNode target = AddChild(scene, "Doomed");
        Guid id = target.Id;
        SceneEditorHost host = NewHost(scene);
        scene.Selection.Select(target);

        host.Apply(EditorHostCommand.Delete);
        scene.TryFindById(id, out _).ShouldBeFalse();

        host.Apply(EditorHostCommand.Undo);
        scene.TryFindById(id, out SceneNode? restored).ShouldBeTrue();
        restored!.Name.ShouldBe("Doomed");

        host.Apply(EditorHostCommand.Redo);
        scene.TryFindById(id, out _).ShouldBeFalse();
    }

    [Fact]
    public void Group_and_ungroup_are_one_history_entry_each()
    {
        var scene = new Scene("Editor");
        SceneNode a = AddChild(scene, "A");
        SceneNode b = AddChild(scene, "B");
        SceneEditorHost host = NewHost(scene);
        scene.Selection.SetRange([a, b]);

        host.Apply(EditorHostCommand.Group);
        host.UndoDepth.ShouldBe(1);
        scene.Root.Children.Count.ShouldBe(1, "both nodes moved under one new parent");

        host.Apply(EditorHostCommand.Ungroup);
        host.UndoDepth.ShouldBe(2);
        scene.Root.Children.Count.ShouldBe(2);
    }

    [Fact]
    public void A_verb_with_nothing_selected_is_a_no_op_rather_than_a_throw()
    {
        // A toolbar button is always clickable; refusing has to be ordinary.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        Should.NotThrow(() => host.Apply(EditorHostCommand.Duplicate));
        Should.NotThrow(() => host.Apply(EditorHostCommand.Delete));
        Should.NotThrow(() => host.Apply(EditorHostCommand.Group));
        Should.NotThrow(() => host.Apply(EditorHostCommand.Undo));
        Should.NotThrow(() => host.Apply(EditorHostCommand.Redo));

        host.UndoDepth.ShouldBe(0);
    }

    // --- Selection by id -----------------------------------------------------

    [Fact]
    public void Selecting_by_id_replaces_the_selection()
    {
        var scene = new Scene("Editor");
        SceneNode a = AddChild(scene, "A");
        SceneNode b = AddChild(scene, "B");
        SceneEditorHost host = NewHost(scene);
        scene.Selection.Select(a);

        host.SelectById(b.Id);

        scene.Selection.Count.ShouldBe(1);
        scene.Selection.Contains(b).ShouldBeTrue();
        scene.Selection.Contains(a).ShouldBeFalse();
    }

    [Fact]
    public void Selecting_by_id_can_extend_and_toggle()
    {
        var scene = new Scene("Editor");
        SceneNode a = AddChild(scene, "A");
        SceneNode b = AddChild(scene, "B");
        SceneEditorHost host = NewHost(scene);

        host.SelectById(a.Id);
        host.SelectById(b.Id, SelectionUpdate.Add);
        scene.Selection.Count.ShouldBe(2);

        host.SelectById(b.Id, SelectionUpdate.Toggle);
        scene.Selection.Count.ShouldBe(1);
        scene.Selection.Contains(a).ShouldBeTrue();
    }

    [Fact]
    public void An_id_the_scene_no_longer_has_is_ordinary_rather_than_exceptional()
    {
        // A UI's view of the graph is a frame or two behind, so it can honestly
        // ask for a node that has just been deleted. Replacing with nothing is
        // the right answer; throwing into a click handler is not.
        var scene = new Scene("Editor");
        SceneNode a = AddChild(scene, "A");
        SceneEditorHost host = NewHost(scene);
        scene.Selection.Select(a);

        Should.NotThrow(() => host.SelectById(Guid.NewGuid()));
        scene.Selection.Count.ShouldBe(0);

        // ...and an extend against a missing id leaves what was there alone.
        scene.Selection.Select(a);
        host.SelectById(Guid.NewGuid(), SelectionUpdate.Add);
        scene.Selection.Count.ShouldBe(1);
    }

    // --- Camera and navigation ----------------------------------------------

    [Fact]
    public void The_navigation_toggle_swaps_the_reported_camera()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        string first = host.NavigationModeName;
        host.Apply(EditorHostCommand.ToggleNavigation);
        host.NavigationModeName.ShouldNotBe(first);

        host.Apply(EditorHostCommand.ToggleNavigation);
        host.NavigationModeName.ShouldBe(first);
    }

    [Fact]
    public void Framing_an_empty_selection_does_not_move_the_camera()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);
        Vector3 before = scene.Camera.Position;

        host.Apply(EditorCameraCommand.FrameSelection);

        scene.Camera.Position.ShouldBe(before);
    }
}
