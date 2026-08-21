using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Viewport;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The one rule that keeps three gestures from fighting over the same button:
/// a press on a handle manipulates, a press on an object selects and moves it,
/// a press on empty space box-selects — and whichever wins owns the pointer
/// until the release.
/// </summary>
/// <remarks>
/// <b>The failure this suite exists to catch is silent and infuriating:</b> a
/// grab that starts a marquee instead of moving the brush you were pointing at,
/// or an object drag that swallows a gizmo handle drawn on top of it. Both are
/// ordering bugs, both look like "the editor is janky" rather than like a
/// defect, and neither shows up in a test of any single component — so the
/// arbitration is asserted here directly, through
/// <see cref="ViewportInteractionController.ClassifyPress"/>, which is a pure
/// function of the frame.
/// </remarks>
public sealed class ViewportArbitrationTests
{
    // Well past the plane quads, so an aim here can only be the x shaft.
    private const float AlongAxis = 0.8f;

    private static ViewportHarness Fixture()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 24f, 0.9f, -0.4f);
        return harness;
    }

    // --- Classification ------------------------------------------------------

    [Fact]
    public void A_press_on_a_gizmo_handle_classifies_as_manipulate()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 4f);
        Vector3 aim = Vector3.UnitX * (AxisLength(harness) * AlongAxis);

        harness.Viewport.ClassifyPress(harness.FrameAt(aim)).ShouldBe(ViewportDragMode.Manipulate);
    }

    [Fact]
    public void The_handle_wins_over_the_object_drawn_underneath_it()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 4f);
        Vector3 aim = Vector3.UnitX * (AxisLength(harness) * AlongAxis);

        // The conflict has to be real for the test to mean anything: the very
        // same pixel does hit the brush.
        Ray3 ray = harness.Scene.Camera.ScreenPointToRay(harness.WorldToScreen(aim), harness.ViewportSize);
        harness.Scene.Raycast(in ray, out _).ShouldBeTrue();

        harness.Viewport.ClassifyPress(harness.FrameAt(aim)).ShouldBe(ViewportDragMode.Manipulate);
    }

    [Fact]
    public void A_press_on_an_object_classifies_as_select_and_move()
    {
        var harness = Fixture();
        SceneNode node = harness.AddBrush(new Vector3(3f, 0f, 0f), 1f);

        harness.Viewport.ClassifyPress(harness.FrameAt(node.WorldPosition))
            .ShouldBe(ViewportDragMode.SelectAndMove);
    }

    [Fact]
    public void A_press_on_empty_space_classifies_as_box_select()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 1f);

        harness.Viewport.ClassifyPress(harness.Frame(new Vector2(8f, 8f)))
            .ShouldBe(ViewportDragMode.BoxSelect);
    }

    [Fact]
    public void A_cursor_outside_the_viewport_claims_nothing()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 1f);

        harness.Viewport.ClassifyPress(harness.Frame(new Vector2(-40f, 300f)))
            .ShouldBe(ViewportDragMode.None);
    }

    [Fact]
    public void Classification_changes_nothing()
    {
        var harness = Fixture();
        SceneNode node = harness.AddBrush(new Vector3(3f, 0f, 0f), 1f);
        harness.AddSelectedBrush(Vector3.Zero, 1f);

        harness.Viewport.ClassifyPress(harness.FrameAt(node.WorldPosition));

        harness.Viewport.DragMode.ShouldBe(ViewportDragMode.None);
        harness.Viewport.BoxSelect.IsActive.ShouldBeFalse();
        harness.Scene.Selection.Count.ShouldBe(1);
        harness.Gizmos.Active.State.ShouldBe(GizmoInteractionState.Idle);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
    }

    // --- The gestures the classification leads to ----------------------------

    [Fact]
    public void A_handle_drag_manipulates_and_never_starts_a_marquee()
    {
        var harness = Fixture();
        SceneNode node = harness.AddSelectedBrush(Vector3.Zero, 4f);
        Vector3 aim = Vector3.UnitX * (AxisLength(harness) * AlongAxis);

        harness.Press(harness.WorldToScreen(aim)).ShouldBe(ViewportDragMode.Manipulate);
        harness.Viewport.BoxSelect.IsActive.ShouldBeFalse();
        harness.Gizmos.Active.ActiveHandle.ShouldBe(GizmoHandle.AxisX);

        harness.Drag(harness.WorldToScreen(aim + new Vector3(3f, 0f, 0f)));
        harness.Release(harness.WorldToScreen(aim + new Vector3(3f, 0f, 0f)));

        harness.Viewport.DragMode.ShouldBe(ViewportDragMode.None);
        node.LocalPosition.X.ShouldBe(3f, 1e-2f);
        node.LocalPosition.Y.ShouldBe(0f, 1e-4f); // the axis constraint held
        harness.Undo.UndoCount.ShouldBe(1);
    }

    [Fact]
    public void A_press_on_an_object_selects_it_and_then_drags_it()
    {
        var harness = Fixture();
        SceneNode node = harness.AddBrush(new Vector3(3f, 0f, 0f), 1f);
        Vector2 press = harness.WorldToScreen(node.WorldPosition);

        harness.Press(press).ShouldBe(ViewportDragMode.SelectAndMove);
        harness.Scene.Selection.Items.ShouldBe(new[] { node });
        harness.Viewport.PressedNode.ShouldBeSameAs(node);

        harness.Drag(press + new Vector2(60f, -30f));
        Vector3 moved = node.LocalPosition;
        moved.ShouldNotBe(new Vector3(3f, 0f, 0f));

        harness.Release(press + new Vector2(60f, -30f));
        harness.Viewport.DragMode.ShouldBe(ViewportDragMode.None);
        harness.Undo.UndoCount.ShouldBe(1);

        harness.Undo.Undo().ShouldBeTrue();
        node.LocalPosition.ShouldBe(new Vector3(3f, 0f, 0f));
    }

    [Fact]
    public void A_press_on_empty_space_starts_a_marquee_and_no_transaction()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 1f);

        harness.Press(new Vector2(8f, 8f)).ShouldBe(ViewportDragMode.BoxSelect);

        harness.Viewport.BoxSelect.IsActive.ShouldBeTrue();
        harness.Gizmos.Active.State.ShouldNotBe(GizmoInteractionState.Dragging);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
    }

    // --- Multi-selection ------------------------------------------------------

    [Fact]
    public void Pressing_a_member_of_a_multi_selection_does_not_collapse_it_yet()
    {
        var harness = Fixture();
        SceneNode a = harness.AddSelectedBrush(new Vector3(-8f, 0f, 0f), 1f, "A");
        SceneNode b = harness.AddSelectedBrush(new Vector3(8f, 0f, 0f), 1f, "B");

        harness.Press(harness.WorldToScreen(a.WorldPosition));

        // Collapsing here would drop B at the instant the user grabbed A to
        // drag both.
        harness.Scene.Selection.Items.ShouldBe(new[] { a, b });
    }

    [Fact]
    public void Releasing_without_moving_collapses_the_selection_to_what_was_clicked()
    {
        var harness = Fixture();
        SceneNode a = harness.AddSelectedBrush(new Vector3(-8f, 0f, 0f), 1f, "A");
        harness.AddSelectedBrush(new Vector3(8f, 0f, 0f), 1f, "B");
        Vector2 press = harness.WorldToScreen(a.WorldPosition);

        harness.Press(press);
        harness.Release(press);

        harness.Scene.Selection.Items.ShouldBe(new[] { a });
        harness.Undo.UndoCount.ShouldBe(0); // a click is not an edit
    }

    [Fact]
    public void Aborting_the_gesture_leaves_the_multi_selection_as_the_press_found_it()
    {
        var harness = Fixture();
        SceneNode a = harness.AddSelectedBrush(new Vector3(-8f, 0f, 0f), 1f, "A");
        SceneNode b = harness.AddSelectedBrush(new Vector3(8f, 0f, 0f), 1f, "B");
        Vector2 press = harness.WorldToScreen(a.WorldPosition);

        harness.Press(press);
        harness.Viewport.Update(
            harness.Frame(press, down: PointerButtons.Left), cancelRequested: true);

        // Escape means "forget this gesture happened" — including the pending
        // collapse a release would have applied.
        harness.Scene.Selection.Items.ShouldBe(new[] { a, b });
        harness.Viewport.DragMode.ShouldBe(ViewportDragMode.None);
        harness.Undo.UndoCount.ShouldBe(0);
    }

    [Fact]
    public void Dragging_a_member_of_a_multi_selection_moves_the_whole_selection()
    {
        var harness = Fixture();
        SceneNode a = harness.AddSelectedBrush(new Vector3(-8f, 0f, 0f), 1f, "A");
        SceneNode b = harness.AddSelectedBrush(new Vector3(8f, 0f, 0f), 1f, "B");
        Vector2 press = harness.WorldToScreen(a.WorldPosition);

        harness.Press(press);
        harness.Drag(press + new Vector2(50f, 20f));
        harness.Release(press + new Vector2(50f, 20f));

        harness.Scene.Selection.Items.ShouldBe(new[] { a, b }); // still both
        a.LocalPosition.ShouldNotBe(new Vector3(-8f, 0f, 0f));
        b.LocalPosition.ShouldNotBe(new Vector3(8f, 0f, 0f));
        // Rigid: they moved by the same world delta.
        (a.LocalPosition - new Vector3(-8f, 0f, 0f)).ShouldBeCloseTo(
            b.LocalPosition - new Vector3(8f, 0f, 0f), 1e-3f);
        harness.Undo.UndoCount.ShouldBe(1);
    }

    // --- Modifiers ------------------------------------------------------------

    [Fact]
    public void Shift_pressing_an_object_adds_it_to_the_selection()
    {
        var harness = Fixture();
        SceneNode a = harness.AddSelectedBrush(new Vector3(-8f, 0f, 0f), 1f, "A");
        SceneNode b = harness.AddBrush(new Vector3(8f, 0f, 0f), 1f, "B");

        harness.Press(harness.WorldToScreen(b.WorldPosition), KeyModifiers.Shift);

        harness.Scene.Selection.Items.ShouldBe(new[] { a, b });
    }

    [Fact]
    public void Ctrl_pressing_a_selected_object_removes_it()
    {
        var harness = Fixture();
        SceneNode a = harness.AddSelectedBrush(new Vector3(-8f, 0f, 0f), 1f, "A");
        SceneNode b = harness.AddSelectedBrush(new Vector3(8f, 0f, 0f), 1f, "B");

        harness.Press(harness.WorldToScreen(b.WorldPosition), KeyModifiers.Control);

        harness.Scene.Selection.Items.ShouldBe(new[] { a });
    }

    // --- Tools without a free-move handle ------------------------------------

    [Theory]
    [InlineData(GizmoMode.Rotate)]
    [InlineData(GizmoMode.Scale)]
    public void In_a_tool_with_no_free_move_handle_an_object_press_only_selects(GizmoMode mode)
    {
        var harness = Fixture();
        harness.Gizmos.Mode = mode;
        SceneNode node = harness.AddBrush(new Vector3(3f, 0f, 0f), 1f);
        Vector2 press = harness.WorldToScreen(node.WorldPosition);

        harness.Press(press).ShouldBe(ViewportDragMode.None);

        harness.Scene.Selection.Items.ShouldBe(new[] { node });
        harness.Drag(press + new Vector2(60f, -30f));
        // Dragging an unselected object must never silently spin or stretch it.
        node.LocalPosition.ShouldBe(new Vector3(3f, 0f, 0f));
        harness.Undo.UndoCount.ShouldBe(0);
    }

    [Fact]
    public void Object_dragging_can_be_turned_off_leaving_a_plain_click_select()
    {
        var harness = Fixture();
        harness.Viewport.ObjectDragEnabled = false;
        SceneNode node = harness.AddBrush(new Vector3(3f, 0f, 0f), 1f);
        Vector2 press = harness.WorldToScreen(node.WorldPosition);

        harness.Press(press).ShouldBe(ViewportDragMode.None);

        harness.Scene.Selection.Items.ShouldBe(new[] { node });
        harness.Drag(press + new Vector2(60f, -30f));
        node.LocalPosition.ShouldBe(new Vector3(3f, 0f, 0f));
    }

    [Fact]
    public void With_object_dragging_off_a_press_on_a_selected_node_collapses_immediately()
    {
        var harness = Fixture();
        harness.Viewport.ObjectDragEnabled = false;
        SceneNode a = harness.AddSelectedBrush(new Vector3(-8f, 0f, 0f), 1f, "A");
        harness.AddSelectedBrush(new Vector3(8f, 0f, 0f), 1f, "B");

        // No drag will ever come back to resolve the deferred collapse, so it
        // has to happen on the press instead of being lost.
        harness.Press(harness.WorldToScreen(a.WorldPosition));

        harness.Scene.Selection.Items.ShouldBe(new[] { a });
    }

    // --- Camera arbitration ---------------------------------------------------

    [Fact]
    public void The_camera_runs_only_while_nothing_owns_the_pointer()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 1f);

        // Free: a right-drag orbits.
        harness.Viewport.Update(harness.Frame(new Vector2(400f, 300f), down: PointerButtons.Right));
        harness.Viewport.Update(harness.Frame(new Vector2(440f, 300f), down: PointerButtons.Right));
        harness.EditorCamera.Yaw.ShouldNotBe(0.9f);
        float afterOrbit = harness.EditorCamera.Yaw;

        // Claimed by a marquee: the same right-drag does nothing to the camera.
        harness.Press(new Vector2(8f, 8f)).ShouldBe(ViewportDragMode.BoxSelect);
        harness.Viewport.Update(harness.Frame(new Vector2(200f, 200f),
            down: PointerButtons.Left | PointerButtons.Right));
        harness.Viewport.Update(harness.Frame(new Vector2(300f, 260f),
            down: PointerButtons.Left | PointerButtons.Right));

        harness.EditorCamera.Yaw.ShouldBe(afterOrbit);
    }

    // --- Teardown -------------------------------------------------------------

    [Fact]
    public void Resetting_abandons_whichever_gesture_was_live()
    {
        var harness = Fixture();
        SceneNode node = harness.AddBrush(new Vector3(3f, 0f, 0f), 1f);
        Vector2 press = harness.WorldToScreen(node.WorldPosition);
        harness.Press(press);
        harness.Drag(press + new Vector2(60f, -30f));

        harness.Viewport.Reset();

        harness.Viewport.DragMode.ShouldBe(ViewportDragMode.None);
        harness.Viewport.BoxSelect.IsActive.ShouldBeFalse();
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
        harness.Undo.UndoCount.ShouldBe(0);
        node.LocalPosition.ShouldBe(new Vector3(3f, 0f, 0f)); // restored exactly
    }

    [Fact]
    public void The_mode_change_event_names_what_took_the_pointer()
    {
        var harness = Fixture();
        harness.AddBrush(new Vector3(3f, 0f, 0f), 1f);
        var seen = new System.Collections.Generic.List<ViewportDragMode>();
        harness.Viewport.DragModeChanged += seen.Add;

        harness.Press(new Vector2(8f, 8f));
        harness.Release(new Vector2(8f, 8f));

        seen.ShouldBe(new[] { ViewportDragMode.BoxSelect, ViewportDragMode.None });
    }

    // --- A click is a click, even on an off-grid object -----------------------

    [Fact]
    public void A_click_held_for_a_frame_neither_moves_an_off_grid_object_nor_lands_history()
    {
        // The shape of every human click: press, at least one held frame with
        // the cursor perfectly still, release. With grid snapping on (the
        // default) the held frame used to quantize the object's absolute
        // position, so clicking to select an off-grid brush moved it, wrote a
        // "Move" into the history, and recompiled the cells it landed in.
        var harness = Fixture();
        var start = new Vector3(3.7f, 2.2f, -0.4f);
        SceneNode node = harness.AddSelectedBrush(start, 1f, "OffGrid");
        harness.Gizmos.Translate.Snap.Enabled.ShouldBeTrue();

        var renderer = new CompilingRenderer();
        harness.Scene.RebuildStaticWorld(renderer);
        harness.Scene.StaticWorldDirty.ShouldBeFalse();

        // A single selection puts the gizmo's centre disc over the node's own
        // pixel, so this press grabs the free-move handle directly rather than
        // through the object route — the same Screen handle either way. The
        // object route is covered by the multi-selection test below, whose
        // shared pivot sits away from the node.
        Vector2 pixel = harness.WorldToScreen(node.WorldPosition);
        harness.Press(pixel).ShouldBe(ViewportDragMode.Manipulate);
        harness.Drag(pixel).ShouldBe(ViewportDragMode.Manipulate);
        harness.Release(pixel).ShouldBe(ViewportDragMode.None);

        node.LocalPosition.ShouldBe(start);
        harness.Undo.UndoCount.ShouldBe(0);
        harness.Scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void Clicking_one_member_of_an_off_grid_multi_selection_still_isolates_it()
    {
        // The second face of the same bug: the deferred collapse only runs when
        // the gesture reports as a click, so a snap-manufactured edit made
        // click-to-isolate stop working — and moved both nodes on the way.
        var harness = Fixture();
        var startA = new Vector3(-3.4f, 0.2f, 0f);
        var startB = new Vector3(3.1f, 0.2f, 0f);
        SceneNode a = harness.AddSelectedBrush(startA, 1f, "A");
        SceneNode b = harness.AddSelectedBrush(startB, 1f, "B");

        Vector2 pixel = harness.WorldToScreen(a.WorldPosition);
        harness.Press(pixel);
        harness.Drag(pixel);
        harness.Release(pixel);

        harness.Scene.Selection.Items.ShouldBe(new[] { a });
        a.LocalPosition.ShouldBe(startA);
        b.LocalPosition.ShouldBe(startB);
        harness.Undo.UndoCount.ShouldBe(0);
    }

    // --- Cancelling a gesture reaches every node it touched -------------------

    [Fact]
    public void Escape_restores_even_a_node_that_left_the_scene_mid_drag()
    {
        // A cancel discards its commands, so a node removed while the gesture
        // was open gets exactly one chance to be put back. Missing it strands
        // the node at a mid-drag value that nothing in the history can undo.
        var harness = Fixture();
        var startA = new Vector3(-3f, 0f, 0f);
        var startB = new Vector3(3f, 0f, 0f);
        SceneNode a = harness.AddSelectedBrush(startA, 1f, "A");
        SceneNode b = harness.AddSelectedBrush(startB, 1f, "B");

        Vector2 pixel = harness.WorldToScreen(a.WorldPosition);
        harness.Press(pixel).ShouldBe(ViewportDragMode.SelectAndMove);
        harness.Drag(pixel + new Vector2(70f, -40f));
        a.LocalPosition.ShouldNotBe(startA);
        b.LocalPosition.ShouldNotBe(startB);

        // The host deletes B while the drag is live; SelectionSet drops it, so
        // nothing else would ever notice it was left behind.
        harness.Scene.Root.RemoveChild(b);

        harness.Viewport.Update(
            harness.Frame(pixel + new Vector2(70f, -40f), down: PointerButtons.Left), cancelRequested: true);

        harness.Undo.IsTransactionOpen.ShouldBeFalse();
        harness.Undo.Count.ShouldBe(0);
        a.LocalPosition.ShouldBe(startA);
        b.LocalPosition.ShouldBe(startB);
    }

    private static float AxisLength(ViewportHarness harness) =>
        GizmoGeometry.Build(
            harness.Scene.Camera,
            harness.Gizmos.Active.Pivot == Vector3.Zero ? Vector3.Zero : harness.Gizmos.Active.Pivot,
            Quaternion.Identity,
            harness.ViewportSize,
            harness.Gizmos.Active.HandlePixelSize).AxisLength;
}
