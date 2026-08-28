using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Maths;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using System;
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
