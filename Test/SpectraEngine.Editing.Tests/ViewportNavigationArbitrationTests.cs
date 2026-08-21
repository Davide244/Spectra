using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Viewport;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Who gets the press now that right-drag means "look around" rather than
/// "orbit": the camera's own buttons always reach the camera, wherever the
/// cursor is; a manipulation already in progress is never taken away from the
/// tool that owns it; and a locked cursor makes every position-based path inert.
/// </summary>
/// <remarks>
/// <b>Both failures here are the kind that only show up in a user's hands.</b> A
/// right-drag that starts over a gizmo arrow and silently drags the arrow
/// instead of turning the view is a wrecked edit; an Alt+left-drag read as a box
/// select is an orbit that clears the selection every time you reach for it.
/// Neither is visible in a test of the camera alone or the manipulator alone,
/// which is why the rules live on
/// <see cref="ViewportInteractionController"/> and
/// <see cref="EditorCameraController.ClaimsPress"/> and are asserted through
/// both.
/// </remarks>
public sealed class ViewportNavigationArbitrationTests
{
    private const float AlongAxis = 0.8f;

    private static ViewportHarness Fixture()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 24f, 0.9f, -0.4f);
        return harness;
    }

    // --- The camera's buttons always reach the camera -------------------------

    [Fact]
    public void A_right_press_over_a_gizmo_handle_still_goes_to_the_camera()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 4f);
        Vector2 handlePixel = HandlePixel(harness);

        // Sanity: a LEFT press right there would grab the handle.
        harness.Viewport.ClassifyPress(harness.Frame(handlePixel))
            .ShouldBe(ViewportDragMode.Manipulate);

        // The same pixel, the right button: navigation, not manipulation.
        harness.Viewport.ClassifyPress(harness.Frame(
            handlePixel, down: PointerButtons.Right, pressed: PointerButtons.Right))
            .ShouldBe(ViewportDragMode.None);

        harness.Viewport.Update(harness.Frame(
            handlePixel, down: PointerButtons.Right, pressed: PointerButtons.Right))
            .ShouldBe(ViewportDragMode.None);
        harness.Viewport.Update(harness.Frame(handlePixel + new Vector2(40f, 0f), down: PointerButtons.Right));

        harness.Gizmos.Active.State.ShouldNotBe(GizmoInteractionState.Dragging);
        harness.EditorCamera.IsFreeLooking.ShouldBeTrue();
        harness.EditorCamera.Yaw.ShouldNotBe(0.9f);
    }

    [Fact]
    public void A_right_press_over_an_object_still_goes_to_the_camera()
    {
        var harness = Fixture();
        SceneNode node = harness.AddBrush(Vector3.Zero, 2f);
        Vector2 objectPixel = harness.WorldToScreen(Vector3.Zero);

        harness.Viewport.Update(harness.Frame(
            objectPixel, down: PointerButtons.Right, pressed: PointerButtons.Right))
            .ShouldBe(ViewportDragMode.None);

        harness.Scene.Selection.Contains(node).ShouldBeFalse();
        harness.Viewport.PressedNode.ShouldBeNull();
    }

    [Fact]
    public void Alt_and_the_left_button_orbit_instead_of_starting_a_marquee()
    {
        // The orbit modifier landing on empty space used to be swallowed as a
        // box select, which cleared the selection on every orbit.
        var harness = Fixture();
        SceneNode node = harness.AddSelectedBrush(new Vector3(30f, 0f, 0f), 1f);
        var empty = new Vector2(8f, 8f);

        harness.Viewport.ClassifyPress(harness.Frame(
            empty, down: PointerButtons.Left, pressed: PointerButtons.Left, modifiers: KeyModifiers.Alt))
            .ShouldBe(ViewportDragMode.None);

        harness.Viewport.Update(harness.Frame(
            empty, down: PointerButtons.Left, pressed: PointerButtons.Left, modifiers: KeyModifiers.Alt))
            .ShouldBe(ViewportDragMode.None);
        harness.Viewport.Update(harness.Frame(
            empty + new Vector2(40f, 0f), down: PointerButtons.Left, modifiers: KeyModifiers.Alt));

        harness.Viewport.BoxSelect.IsActive.ShouldBeFalse();
        harness.Scene.Selection.Contains(node).ShouldBeTrue();
        harness.EditorCamera.IsOrbiting.ShouldBeTrue();
    }

    [Fact]
    public void Alt_and_the_left_button_do_not_grab_a_handle_either()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 4f);
        Vector2 handlePixel = HandlePixel(harness);

        harness.Viewport.Update(harness.Frame(
            handlePixel, down: PointerButtons.Left, pressed: PointerButtons.Left, modifiers: KeyModifiers.Alt))
            .ShouldBe(ViewportDragMode.None);

        harness.Gizmos.Active.State.ShouldNotBe(GizmoInteractionState.Dragging);
        harness.EditorCamera.IsOrbiting.ShouldBeTrue();
    }

    [Fact]
    public void The_middle_button_pans_rather_than_selecting()
    {
        var harness = Fixture();
        harness.AddBrush(Vector3.Zero, 2f);

        harness.Viewport.ClassifyPress(harness.Frame(
            harness.CenterPixel, down: PointerButtons.Middle, pressed: PointerButtons.Middle))
            .ShouldBe(ViewportDragMode.None);
    }

    // --- A live manipulation is never stolen ---------------------------------

    [Fact]
    public void A_right_press_mid_gizmo_drag_cancels_the_drag_and_does_not_turn_the_view()
    {
        // The manipulator owns the pointer, so the press reaches its cancel
        // binding and not the camera. What must NOT happen is the view swinging
        // away while an edit is being abandoned.
        var harness = Fixture();
        SceneNode node = harness.AddSelectedBrush(Vector3.Zero, 1f);
        Vector3 origin = node.WorldPosition;
        float yaw = harness.EditorCamera.Yaw;

        harness.Viewport.Update(harness.Frame(
            HandlePixel(harness), down: PointerButtons.Left, pressed: PointerButtons.Left))
            .ShouldBe(ViewportDragMode.Manipulate);
        harness.Viewport.Update(harness.Frame(
            HandlePixel(harness) + new Vector2(90f, 0f), down: PointerButtons.Left))
            .ShouldBe(ViewportDragMode.Manipulate);
        node.WorldPosition.ShouldNotBe(origin);

        harness.Viewport.Update(harness.Frame(
            HandlePixel(harness) + new Vector2(200f, 60f),
            down: PointerButtons.Left | PointerButtons.Right,
            pressed: PointerButtons.Right))
            .ShouldBe(ViewportDragMode.None);

        node.WorldPosition.ShouldBe(origin);      // the edit was abandoned, not committed
        harness.EditorCamera.Yaw.ShouldBe(yaw);   // and the camera stayed put
        harness.Undo.UndoCount.ShouldBe(0);
    }

    [Fact]
    public void A_right_drag_that_follows_a_cancelled_manipulation_starts_from_scratch()
    {
        // The camera sat out the whole manipulation, so the cursor travel it
        // never saw must not arrive as one flick when it takes over.
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 1f);
        Vector2 grab = HandlePixel(harness);

        harness.Viewport.Update(harness.Frame(grab, down: PointerButtons.Left, pressed: PointerButtons.Left));
        harness.Viewport.Update(harness.Frame(grab + new Vector2(120f, 40f), down: PointerButtons.Left));
        harness.Viewport.Update(harness.Frame(
            grab + new Vector2(240f, 80f),
            down: PointerButtons.Left | PointerButtons.Right,
            pressed: PointerButtons.Right));

        float yaw = harness.EditorCamera.Yaw;

        // First idle frame with the right button still down and the cursor
        // exactly where the cancel left it.
        harness.Viewport.Update(harness.Frame(grab + new Vector2(240f, 80f), down: PointerButtons.Right));
        harness.EditorCamera.Yaw.ShouldBe(yaw);

        harness.Viewport.Update(harness.Frame(grab + new Vector2(250f, 80f), down: PointerButtons.Right));
        harness.EditorCamera.Yaw.ShouldBe(yaw + 10f * harness.EditorCamera.LookSensitivity, 1e-5f);
    }

    [Fact]
    public void A_marquee_in_progress_keeps_the_pointer_when_the_look_button_arrives()
    {
        var harness = Fixture();
        harness.AddBrush(new Vector3(40f, 0f, 0f), 1f);

        harness.Press(new Vector2(8f, 8f)).ShouldBe(ViewportDragMode.BoxSelect);
        float yaw = harness.EditorCamera.Yaw;

        harness.Viewport.Update(harness.Frame(
            new Vector2(200f, 200f),
            down: PointerButtons.Left | PointerButtons.Right,
            pressed: PointerButtons.Right))
            .ShouldBe(ViewportDragMode.BoxSelect);

        harness.EditorCamera.Yaw.ShouldBe(yaw);
        harness.EditorCamera.IsFreeLooking.ShouldBeFalse();
        harness.CursorLock.Requested.ShouldBe(CursorMode.Normal);
    }

    // --- A live camera gesture is never stolen either -------------------------

    [Fact]
    public void A_left_press_during_a_pan_neither_selects_nor_stops_the_pan()
    {
        // The press-edge test alone cannot see this: the middle button went down
        // on an EARLIER frame, so ClaimsPress answers "not mine" and the stray
        // left click used to select the brush, open a transaction and kill the
        // pan underneath it.
        var harness = Fixture();
        SceneNode node = harness.AddBrush(Vector3.Zero, 2f);
        Vector3 origin = node.WorldPosition;

        var grab = new Vector2(600f, 120f);
        harness.Viewport.Update(harness.Frame(grab, down: PointerButtons.Middle, pressed: PointerButtons.Middle))
            .ShouldBe(ViewportDragMode.None);
        Vector3 beforePan = harness.EditorCamera.Position;

        harness.Viewport.Update(harness.Frame(grab + new Vector2(20f, 0f), down: PointerButtons.Middle))
            .ShouldBe(ViewportDragMode.None);
        harness.EditorCamera.IsPanning.ShouldBeTrue();
        harness.EditorCamera.Position.ShouldNotBe(beforePan);

        // The stray click, squarely on the brush, while the pan is still live.
        Vector2 objectPixel = harness.WorldToScreen(Vector3.Zero);
        harness.Viewport.Update(harness.Frame(
            objectPixel,
            down: PointerButtons.Middle | PointerButtons.Left,
            pressed: PointerButtons.Left))
            .ShouldBe(ViewportDragMode.None);

        harness.Scene.Selection.Count.ShouldBe(0);
        harness.Viewport.PressedNode.ShouldBeNull();
        harness.Gizmos.Active.State.ShouldNotBe(GizmoInteractionState.Dragging);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
        harness.EditorCamera.IsPanning.ShouldBeTrue();

        // Drag on and let go: nothing was edited and no history was written.
        harness.Viewport.Update(harness.Frame(
            objectPixel + new Vector2(50f, 30f),
            down: PointerButtons.Middle | PointerButtons.Left));
        harness.Viewport.Update(harness.Frame(
            objectPixel + new Vector2(50f, 30f),
            released: PointerButtons.Middle | PointerButtons.Left));

        node.WorldPosition.ShouldBe(origin);
        harness.Undo.UndoCount.ShouldBe(0);
    }

    [Fact]
    public void A_left_press_during_a_freelook_is_refused_before_the_cursor_lock_lands()
    {
        // The lock is a two-thread latch, so there is at least one frame where a
        // freelook is live and IsCursorLocked is still false — the frame the
        // IsPointerUsable guard cannot cover.
        var harness = Fixture();
        SceneNode node = harness.AddSelectedBrush(Vector3.Zero, 4f);
        Vector3 origin = node.WorldPosition;
        Vector2 handlePixel = HandlePixel(harness);

        harness.Viewport.Update(harness.Frame(
            handlePixel, down: PointerButtons.Right, pressed: PointerButtons.Right))
            .ShouldBe(ViewportDragMode.None);
        harness.CursorLock.Requested.ShouldBe(CursorMode.Locked);
        harness.CursorLock.IsCursorLocked.ShouldBeFalse();   // requested, not yet applied

        harness.Viewport.Update(harness.Frame(
            handlePixel,
            down: PointerButtons.Right | PointerButtons.Left,
            pressed: PointerButtons.Left))
            .ShouldBe(ViewportDragMode.None);

        harness.Gizmos.Active.State.ShouldNotBe(GizmoInteractionState.Dragging);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
        harness.EditorCamera.IsFreeLooking.ShouldBeTrue();
        // The lock request must survive the stray press: a suspended gesture
        // would have released it and let the pointer snap back mid-look.
        harness.EditorCamera.IsCursorLockRequested.ShouldBeTrue();
        harness.CursorLock.Requested.ShouldBe(CursorMode.Locked);

        // The latch lands while the left button is still held down.
        harness.CursorLock.Pump();
        harness.Viewport.Update(harness.Frame(
            handlePixel,
            down: PointerButtons.Right | PointerButtons.Left,
            cursorDelta: new Vector2(30f, 0f),
            locked: true))
            .ShouldBe(ViewportDragMode.None);

        node.WorldPosition.ShouldBe(origin);
        harness.Undo.UndoCount.ShouldBe(0);
        harness.EditorCamera.IsFreeLooking.ShouldBeTrue();
    }

    [Fact]
    public void A_left_press_during_a_freelook_is_refused_when_the_host_never_locks_the_cursor()
    {
        // EditorCameraController documents a null CursorLock as supported, and
        // there IsCursorLocked is false for the whole gesture — so the guard has
        // to be the gesture itself, not the lock.
        var harness = Fixture();
        harness.EditorCamera.CursorLock = null;
        SceneNode node = harness.AddSelectedBrush(Vector3.Zero, 4f);
        Vector3 origin = node.WorldPosition;
        Vector2 handlePixel = HandlePixel(harness);
        float yaw = harness.EditorCamera.Yaw;

        harness.Viewport.Update(harness.Frame(
            handlePixel, down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.Viewport.Update(harness.Frame(
            handlePixel + new Vector2(10f, 0f), down: PointerButtons.Right));
        harness.EditorCamera.Yaw.ShouldNotBe(yaw);

        harness.Viewport.Update(harness.Frame(
            handlePixel,
            down: PointerButtons.Right | PointerButtons.Left,
            pressed: PointerButtons.Left))
            .ShouldBe(ViewportDragMode.None);
        harness.Viewport.Update(harness.Frame(
            handlePixel + new Vector2(120f, 0f),
            down: PointerButtons.Right | PointerButtons.Left))
            .ShouldBe(ViewportDragMode.None);

        harness.Gizmos.Active.State.ShouldNotBe(GizmoInteractionState.Dragging);
        harness.EditorCamera.IsFreeLooking.ShouldBeTrue();
        node.WorldPosition.ShouldBe(origin);
        harness.Undo.UndoCount.ShouldBe(0);
    }

    [Fact]
    public void Classifying_a_press_mid_navigation_answers_none_and_changes_nothing()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 4f);
        Vector2 handlePixel = HandlePixel(harness);

        // Idle, that pixel is a handle grab.
        harness.Viewport.ClassifyPress(harness.Frame(handlePixel)).ShouldBe(ViewportDragMode.Manipulate);

        harness.Viewport.Update(harness.Frame(
            handlePixel, down: PointerButtons.Middle, pressed: PointerButtons.Middle));
        harness.EditorCamera.IsPanning.ShouldBeTrue();

        harness.Viewport.ClassifyPress(harness.Frame(
            handlePixel,
            down: PointerButtons.Middle | PointerButtons.Left,
            pressed: PointerButtons.Left))
            .ShouldBe(ViewportDragMode.None);

        harness.EditorCamera.IsPanning.ShouldBeTrue();       // asking mutated nothing
        harness.Gizmos.Active.State.ShouldNotBe(GizmoInteractionState.Dragging);
    }

    [Fact]
    public void The_pointer_comes_back_the_moment_the_camera_gesture_ends()
    {
        // The withhold must be exactly as long as the gesture — a rule that
        // latched on would make the viewport unusable after the first pan, and
        // one that merely read the gesture latch would swallow a press landing
        // on the very frame the navigation button comes up. That frame is the
        // boundary, so it is the one asserted here.
        var harness = Fixture();
        SceneNode node = harness.AddBrush(Vector3.Zero, 2f);

        var grab = new Vector2(600f, 120f);
        harness.Viewport.Update(harness.Frame(grab, down: PointerButtons.Middle, pressed: PointerButtons.Middle));
        harness.Viewport.Update(harness.Frame(grab + new Vector2(20f, 0f), down: PointerButtons.Middle));
        harness.EditorCamera.IsPanning.ShouldBeTrue();

        // Middle up and left down on the same frame: the pan is over, so the
        // press is the user's and must be honoured.
        harness.Viewport.Update(harness.Frame(
            harness.WorldToScreen(Vector3.Zero),
            down: PointerButtons.Left,
            pressed: PointerButtons.Left,
            released: PointerButtons.Middle))
            .ShouldBe(ViewportDragMode.SelectAndMove);

        harness.Scene.Selection.Contains(node).ShouldBeTrue();
        harness.EditorCamera.IsNavigating.ShouldBeFalse();
    }

    // --- A locked cursor makes position-based paths inert ---------------------

    [Fact]
    public void Nothing_can_be_picked_while_the_cursor_is_locked()
    {
        var harness = Fixture();
        SceneNode node = harness.AddSelectedBrush(Vector3.Zero, 4f);
        // Far enough from the gizmo that the handles cannot claim this pixel.
        harness.AddBrush(new Vector3(0f, 0f, 14f), 2f, "Other");
        Vector2 objectPixel = harness.WorldToScreen(new Vector3(0f, 0f, 14f));
        Vector2 handlePixel = HandlePixel(harness);

        // Unlocked, both pixels mean something.
        harness.Viewport.ClassifyPress(harness.Frame(handlePixel)).ShouldBe(ViewportDragMode.Manipulate);
        harness.Viewport.ClassifyPress(harness.Frame(objectPixel)).ShouldBe(ViewportDragMode.SelectAndMove);
        harness.Gizmos.Active.PickAt(harness.Frame(handlePixel)).IsHit.ShouldBeTrue();

        // Locked, neither does — the reported position is a frozen leftover.
        harness.Viewport.ClassifyPress(harness.Frame(handlePixel, locked: true))
            .ShouldBe(ViewportDragMode.None);
        harness.Viewport.ClassifyPress(harness.Frame(objectPixel, locked: true))
            .ShouldBe(ViewportDragMode.None);
        harness.Gizmos.Active.PickAt(harness.Frame(handlePixel, locked: true)).IsHit.ShouldBeFalse();

        harness.Scene.Selection.Contains(node).ShouldBeTrue(); // nothing was changed by asking
    }

    [Fact]
    public void A_left_press_while_locked_starts_neither_a_drag_nor_a_marquee()
    {
        var harness = Fixture();
        SceneNode node = harness.AddSelectedBrush(Vector3.Zero, 4f);

        harness.Viewport.Update(harness.Frame(
            HandlePixel(harness),
            down: PointerButtons.Right | PointerButtons.Left,
            pressed: PointerButtons.Left,
            locked: true))
            .ShouldBe(ViewportDragMode.None);
        harness.Viewport.Update(harness.Frame(
            new Vector2(8f, 8f),
            down: PointerButtons.Right | PointerButtons.Left,
            pressed: PointerButtons.Left,
            locked: true))
            .ShouldBe(ViewportDragMode.None);

        harness.Gizmos.Active.State.ShouldNotBe(GizmoInteractionState.Dragging);
        harness.Viewport.BoxSelect.IsActive.ShouldBeFalse();
        harness.Scene.Selection.Contains(node).ShouldBeTrue();
        harness.Undo.UndoCount.ShouldBe(0);
    }

    [Fact]
    public void A_locked_frame_clears_the_gizmo_hover()
    {
        var harness = Fixture();
        harness.AddSelectedBrush(Vector3.Zero, 4f);
        Vector2 handlePixel = HandlePixel(harness);

        harness.Viewport.Update(harness.Frame(handlePixel));
        harness.Gizmos.Active.HoveredHandle.ShouldNotBe(GizmoHandle.None);

        harness.Viewport.Update(harness.Frame(handlePixel, down: PointerButtons.Right, locked: true));
        harness.Gizmos.Active.HoveredHandle.ShouldBe(GizmoHandle.None);
    }

    // --- Helpers -------------------------------------------------------------

    private static Vector2 HandlePixel(ViewportHarness harness)
    {
        float axisLength = GizmoGeometry.Build(
            harness.Scene.Camera, Vector3.Zero, Quaternion.Identity,
            harness.ViewportSize, harness.Gizmos.Active.HandlePixelSize).AxisLength;
        return harness.WorldToScreen(Vector3.UnitX * (axisLength * AlongAxis));
    }
}
