using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Input;
using SpectraEngine.Core.Input;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Viewport;
using System.Linq;
using System.Numerics;
using CursorMode = SpectraEngine.Core.Input.CursorMode;
using PointerButtons = SpectraEngine.Core.Input.PointerButtons;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The cursor lock, from both ends: the editor camera raising and clearing the
/// <em>request</em> exactly in step with the look gesture, and the input
/// manager's latch turning that request into a real captured cursor on the one
/// thread allowed to do it — including putting the pointer back afterwards and
/// letting go when the window loses focus.
/// </summary>
/// <remarks>
/// <b>The failure mode is not subtle and it is not recoverable from inside the
/// app:</b> a lock that is taken and never released leaves the user with an
/// invisible pointer trapped in a window, and if it happens on a focus loss they
/// cannot even click their way out. Every path that ends a look — the button
/// coming up, the arbiter withholding the frame, the host resetting, the window
/// going away — is asserted to clear it.
/// </remarks>
public sealed class CursorLockTests
{
    // --- The camera's half: request lifecycle --------------------------------

    [Fact]
    public void The_look_button_locks_the_cursor_and_releasing_it_gives_it_back()
    {
        var harness = new ViewportHarness();
        harness.CursorLock.CursorMode.ShouldBe(CursorMode.Normal);

        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel, down: PointerButtons.Right, pressed: PointerButtons.Right));

        harness.EditorCamera.IsCursorLockRequested.ShouldBeTrue();
        harness.CursorLock.Requested.ShouldBe(CursorMode.Locked);
        // Not yet a fact: the request has to cross to the window thread.
        harness.CursorLock.IsCursorLocked.ShouldBeFalse();

        harness.CursorLock.Pump();
        harness.CursorLock.IsCursorLocked.ShouldBeTrue();

        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel, released: PointerButtons.Right, locked: true));

        harness.EditorCamera.IsCursorLockRequested.ShouldBeFalse();
        harness.CursorLock.Requested.ShouldBe(CursorMode.Normal);
        harness.CursorLock.Pump();
        harness.CursorLock.IsCursorLocked.ShouldBeFalse();
    }

    [Fact]
    public void Holding_the_look_button_does_not_re_request_the_lock_every_frame()
    {
        var harness = new ViewportHarness();

        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel, down: PointerButtons.Right, pressed: PointerButtons.Right));
        for (int i = 0; i < 20; i++)
        {
            harness.EditorCamera.Update(harness.Frame(
                harness.CenterPixel, down: PointerButtons.Right, cursorDelta: new Vector2(1f, 0f), locked: true));
        }

        harness.CursorLock.Requests.Count.ShouldBe(1);
        harness.CursorLock.Requests.Single().ShouldBe(CursorMode.Locked);
    }

    [Theory]
    [InlineData(PointerButtons.Middle, KeyModifiers.None)]
    [InlineData(PointerButtons.Left, KeyModifiers.Alt)]
    [InlineData(PointerButtons.Right, KeyModifiers.Alt)]
    public void Only_freelook_captures_the_cursor(PointerButtons button, KeyModifiers modifiers)
    {
        // Pan and orbit still need to see where the pointer is, and hiding it
        // during them would be gratuitous.
        var harness = new ViewportHarness();

        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel, down: button, pressed: button, modifiers: modifiers));
        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel + new Vector2(20f, 10f), down: button, modifiers: modifiers));

        harness.EditorCamera.IsFreeLooking.ShouldBeFalse();
        harness.EditorCamera.IsCursorLockRequested.ShouldBeFalse();
        harness.CursorLock.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void Suspending_navigation_releases_the_cursor()
    {
        // The focus-loss path as the host sees it: whatever the buttons say, the
        // camera stops running and must not keep the pointer.
        var harness = new ViewportHarness();
        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel, down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.CursorLock.Pump();
        harness.CursorLock.IsCursorLocked.ShouldBeTrue();

        harness.EditorCamera.SuspendNavigation();

        harness.CursorLock.Requested.ShouldBe(CursorMode.Normal);
        harness.CursorLock.Pump();
        harness.CursorLock.IsCursorLocked.ShouldBeFalse();
    }

    [Fact]
    public void Resetting_the_viewport_releases_the_cursor()
    {
        var harness = new ViewportHarness();
        harness.Viewport.Update(harness.Frame(
            harness.CenterPixel, down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.CursorLock.Pump();
        harness.CursorLock.IsCursorLocked.ShouldBeTrue();

        harness.Viewport.Reset();

        harness.CursorLock.Requested.ShouldBe(CursorMode.Normal);
    }

    [Fact]
    public void A_button_that_vanishes_with_the_window_focus_releases_the_cursor()
    {
        // Losing focus drops every held button (see InputManager below), so the
        // very next frame the camera simply sees the look button up.
        var harness = new ViewportHarness();
        harness.Viewport.Update(harness.Frame(
            harness.CenterPixel, down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.CursorLock.Pump();

        harness.Viewport.Update(harness.Frame(
            harness.CenterPixel, released: PointerButtons.Right, locked: true));

        harness.CursorLock.Requested.ShouldBe(CursorMode.Normal);
        harness.EditorCamera.ActiveGesture.ShouldBe(EditorNavigationGesture.None);
    }

    [Fact]
    public void A_gesture_the_arbiter_withholds_never_takes_the_cursor()
    {
        // A marquee owns the pointer; the look button arriving mid-marquee is
        // withheld from the camera, so no lock may be taken behind the user's
        // back while they are still dragging a rectangle.
        var harness = new ViewportHarness();
        harness.Press(new Vector2(8f, 8f)).ShouldBe(ViewportDragMode.BoxSelect);

        harness.Viewport.Update(harness.Frame(
            new Vector2(120f, 90f),
            down: PointerButtons.Left | PointerButtons.Right,
            pressed: PointerButtons.Right));

        harness.CursorLock.Requests.ShouldBeEmpty();
        harness.CursorLock.IsCursorLocked.ShouldBeFalse();
    }

    [Fact]
    public void A_camera_without_a_cursor_lock_still_navigates()
    {
        // Headless hosts and the tests that predate the lock must keep working.
        var harness = new ViewportHarness();
        harness.EditorCamera.CursorLock = null;
        harness.EditorCamera.SetPose(Vector3.Zero, 0f, 0f);

        harness.EditorCamera.Update(harness.Frame(
            new Vector2(400f, 300f), down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.EditorCamera.Update(harness.Frame(new Vector2(430f, 300f), down: PointerButtons.Right));

        harness.EditorCamera.Yaw.ShouldBe(30f * harness.EditorCamera.LookSensitivity, 1e-6f);
        harness.EditorCamera.IsCursorLockRequested.ShouldBeTrue();
    }

    // --- The engine's half: the latch ----------------------------------------

    [Fact]
    public void A_request_only_takes_effect_when_the_window_thread_applies_it()
    {
        InputManager input = CreateInput();

        input.RequestCursorMode(CursorMode.Locked);
        input.CursorMode.ShouldBe(CursorMode.Normal);
        input.IsCursorLocked.ShouldBeFalse();

        input.ApplyPendingCursorMode();
        input.CursorMode.ShouldBe(CursorMode.Locked);
        input.IsCursorLocked.ShouldBeTrue();
    }

    [Fact]
    public void A_locked_cursor_freezes_its_reported_position_but_keeps_reporting_motion()
    {
        InputManager input = CreateInput();
        input.OnMouseMove(null!, new Vector2(100f, 120f));
        input.Update(0.016);
        input.MousePosition.ShouldBe(new Vector2(100f, 120f));

        input.RequestCursorMode(CursorMode.Locked);
        input.ApplyPendingCursorMode();

        // The backend now reports a virtual position that walks away from the
        // window. It must not reach anything that builds a picking ray.
        input.OnMouseMove(null!, new Vector2(4000f, -2500f));
        input.Update(0.016);

        input.MousePosition.ShouldBe(new Vector2(100f, 120f));
        input.MouseDelta.ShouldBe(new Vector2(3900f, -2620f));
    }

    [Fact]
    public void Unlocking_puts_the_cursor_back_where_it_was_taken_from()
    {
        InputManager input = CreateInput();
        input.OnMouseMove(null!, new Vector2(640f, 360f));
        input.Update(0.016);

        input.RequestCursorMode(CursorMode.Locked);
        input.ApplyPendingCursorMode();
        input.OnMouseMove(null!, new Vector2(9000f, 9000f));

        input.RequestCursorMode(CursorMode.Normal);
        input.ApplyPendingCursorMode();
        input.Update(0.016);

        input.MousePosition.ShouldBe(new Vector2(640f, 360f));
        // The teleport home is not motion the user made.
        input.MouseDelta.ShouldBe(Vector2.Zero);
    }

    [Fact]
    public void Losing_focus_releases_the_lock_and_overrides_a_pending_relock()
    {
        // The render thread may well still believe it is looking around. Its
        // stale request must not re-take the cursor on the next pump.
        InputManager input = CreateInput();
        input.OnMouseMove(null!, new Vector2(50f, 60f));
        input.Update(0.016);
        input.RequestCursorMode(CursorMode.Locked);
        input.ApplyPendingCursorMode();
        input.IsCursorLocked.ShouldBeTrue();

        input.OnWindowFocusChanged(false);

        input.IsCursorLocked.ShouldBeFalse();
        input.ApplyPendingCursorMode();
        input.IsCursorLocked.ShouldBeFalse();
    }

    [Fact]
    public void Losing_focus_drops_held_keys_and_turns_held_buttons_into_release_edges()
    {
        // The key-up and button-up went to whoever stole the focus, so without
        // this the look button stays "down" forever and the camera never lets go.
        InputManager input = CreateInput();
        input.OnKeyDown(null!, Key.W, 0);
        input.OnMouseDown(null!, MouseButton.Right);
        input.Update(0.016);
        input.IsKeyDown(InputKey.W).ShouldBeTrue();
        input.PointerButtonsDown.ShouldBe(PointerButtons.Right);

        input.OnWindowFocusChanged(false);
        input.Update(0.016);

        input.IsKeyDown(InputKey.W).ShouldBeFalse();
        input.PointerButtonsDown.ShouldBe(PointerButtons.None);
        input.PointerButtonsReleased.ShouldBe(PointerButtons.Right);
    }

    [Fact]
    public void Regaining_focus_changes_nothing_on_its_own()
    {
        InputManager input = CreateInput();
        input.RequestCursorMode(CursorMode.Locked);
        input.ApplyPendingCursorMode();

        input.OnWindowFocusChanged(true);

        // Re-taking the cursor is the camera's decision, made from the button
        // state it sees next frame — not something focus does behind its back.
        input.IsCursorLocked.ShouldBeTrue();
    }

    [Fact]
    public void The_lock_flag_and_the_relative_motion_cross_the_input_seam()
    {
        var input = new InputManager(NullLogger<InputManager>.Instance);
        var renderer = new StubRenderer();
        var source = new Editing.Input.EngineEditorInputSource(input, renderer);
        renderer.SetFramebufferSize(new Silk.NET.Maths.Vector2D<int>(800, 600));

        input.OnMouseMove(null!, new Vector2(400f, 300f));
        input.Update(0.016);
        input.RequestCursorMode(CursorMode.Locked);
        input.ApplyPendingCursorMode();

        input.OnMouseMove(null!, new Vector2(430f, 285f));
        input.Update(0.016);

        Editing.Input.EditorInputFrame frame = source.CaptureFrame(0.016f);
        frame.IsCursorLocked.ShouldBeTrue();
        frame.CursorDelta.ShouldBe(new Vector2(30f, -15f));
        frame.CursorPosition.ShouldBe(new Vector2(400f, 300f)); // frozen, not virtual
        frame.IsPointerUsable.ShouldBeFalse();
        frame.IsCursorInsideViewport.ShouldBeTrue(); // geometric, and still true
    }

    private static InputManager CreateInput() => new(NullLogger<InputManager>.Instance);
}
