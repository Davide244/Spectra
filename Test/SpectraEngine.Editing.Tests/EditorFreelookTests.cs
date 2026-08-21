using SpectraEngine.Core.Input;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Input;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The Roblox-Studio navigation model, asserted as the thing a user would
/// describe: hold the right button and the camera turns <em>where it stands</em>,
/// the movement keys fly it along what it is looking at, the wheel means speed
/// while you look and zoom while you do not, and the cursor is captured for
/// exactly as long as the gesture lasts.
/// </summary>
/// <remarks>
/// <b>"Rotates in place" is asserted exactly, not within a tolerance.</b> That is
/// deliberate and it is the regression this file exists for: an implementation
/// that turned the camera by subtracting a focus offset, rotating, and adding it
/// back would pass any reasonable tolerance at the origin and drift visibly at
/// open-world coordinates. Holding the position bit-for-bit is the only
/// statement that fails on the wrong implementation everywhere.
/// </remarks>
public sealed class EditorFreelookTests
{
    // --- Looking around ------------------------------------------------------

    [Fact]
    public void Looking_turns_the_camera_without_moving_it()
    {
        var harness = new ViewportHarness();
        var eye = new Vector3(5f, 3f, -7f);
        harness.EditorCamera.SetPose(eye, 0.4f, -0.2f);

        LookDrag(harness, new Vector2(400f, 300f), new Vector2(460f, 340f));

        float sensitivity = harness.EditorCamera.LookSensitivity;
        harness.EditorCamera.Yaw.ShouldBe(0.4f + 60f * sensitivity, 1e-6f);
        harness.EditorCamera.Pitch.ShouldBe(-0.2f - 40f * sensitivity, 1e-6f);

        // Not "close to" — the same value. See the type remarks.
        harness.Scene.Camera.Position.ShouldBe(eye);
        harness.EditorCamera.Position.ShouldBe(eye);
    }

    [Fact]
    public void Looking_leaves_the_camera_still_at_a_million_units_out()
    {
        // The precision claim, exercised where float precision actually bites: a
        // hundred look frames must not walk the eye across the level.
        var harness = new ViewportHarness();
        var distant = new Vector3(1_000_000f, 250f, -1_000_000f);
        harness.EditorCamera.SetPose(distant, 0.7f, -0.35f);

        harness.EditorCamera.Update(harness.Frame(
            new Vector2(400f, 300f), down: PointerButtons.Right, pressed: PointerButtons.Right));
        for (int i = 0; i < 100; i++)
        {
            harness.EditorCamera.Update(harness.Frame(
                new Vector2(400f + (i % 9), 300f + (i % 5)), down: PointerButtons.Right));
        }

        harness.Scene.Camera.Position.ShouldBe(distant);
        harness.Scene.Camera.Forward.Length().ShouldBe(1f, 1e-5f);
    }

    [Fact]
    public void Looking_carries_the_focus_around_with_the_camera()
    {
        // Freelook derives the focus rather than holding it, so the point the
        // Alt-orbit will swing around afterwards is whatever you ended up
        // looking at — which is what makes look-then-orbit feel continuous.
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(new Vector3(5f, 3f, -7f), 0.4f, -0.2f, distance: 14f);

        LookDrag(harness, new Vector2(400f, 300f), new Vector2(500f, 280f));

        Vector3 expected = harness.Scene.Camera.Position + harness.Scene.Camera.Forward * 14f;
        harness.EditorCamera.Focus.ShouldBeCloseTo(expected, 1e-3f);
        harness.EditorCamera.Distance.ShouldBe(14f);
    }

    [Fact]
    public void Looking_past_vertical_clamps_short_of_the_degenerate_pole()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(Vector3.Zero, 0f, 0f);

        // Far more than a quarter turn of motion, both ways.
        LookDrag(harness, new Vector2(400f, 900f), new Vector2(400f, -900f));
        harness.EditorCamera.Pitch.ShouldBeLessThan(MathF.PI / 2f);
        harness.Scene.Camera.Pitch.ShouldBe(harness.EditorCamera.Pitch);
        harness.Scene.Camera.Forward.Length().ShouldBe(1f, 1e-5f);

        LookDrag(harness, new Vector2(400f, -900f), new Vector2(400f, 900f));
        harness.EditorCamera.Pitch.ShouldBeGreaterThan(-MathF.PI / 2f);
        harness.Scene.Camera.Forward.Length().ShouldBe(1f, 1e-5f);
    }

    [Fact]
    public void The_press_frame_of_a_look_applies_no_delta()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(Vector3.Zero, 0.3f, -0.2f);

        // The cursor moved a long way BEFORE the user decided to look.
        harness.EditorCamera.Update(harness.Frame(new Vector2(20f, 20f)));
        harness.EditorCamera.Update(harness.Frame(
            new Vector2(700f, 500f), down: PointerButtons.Right, pressed: PointerButtons.Right));

        harness.EditorCamera.Yaw.ShouldBe(0.3f);
        harness.EditorCamera.Pitch.ShouldBe(-0.2f);
    }

    [Fact]
    public void Pressing_alt_mid_look_restarts_the_gesture_rather_than_snapping()
    {
        // The gesture changed kind under the user's hand: whatever cursor travel
        // the look had accumulated must not land on the orbit as one step.
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(new Vector3(2f, 1f, 3f), 0.3f, -0.2f);

        harness.EditorCamera.Update(harness.Frame(
            new Vector2(400f, 300f), down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.EditorCamera.Update(harness.Frame(new Vector2(430f, 300f), down: PointerButtons.Right));
        float yaw = harness.EditorCamera.Yaw;

        // Alt goes down; the cursor sits still. Nothing may move.
        harness.EditorCamera.Update(harness.Frame(
            new Vector2(430f, 300f), down: PointerButtons.Right, modifiers: KeyModifiers.Alt));

        harness.EditorCamera.ActiveGesture.ShouldBe(EditorNavigationGesture.Orbit);
        harness.EditorCamera.Yaw.ShouldBe(yaw);
    }

    // --- Flying --------------------------------------------------------------

    [Fact]
    public void The_forward_key_moves_the_camera_along_what_it_is_looking_at()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(new Vector3(1f, 2f, 3f), 0.6f, -0.25f);
        Vector3 start = harness.Scene.Camera.Position;
        Vector3 forward = harness.Scene.Camera.Forward;
        harness.EditorCamera.FlySpeed = 10f;

        Fly(harness, EditorNavigationInput.FromKeys(
            forward: true, back: false, left: false, right: false, up: false, down: false), 0.5f);

        // 10 units/s for half a second, straight ahead.
        harness.Scene.Camera.Position.ShouldBeCloseTo(start + forward * 5f, 1e-4f);
        // Looking is unaffected by moving.
        harness.EditorCamera.Yaw.ShouldBe(0.6f);
        harness.EditorCamera.Pitch.ShouldBe(-0.25f);
    }

    [Theory]
    [InlineData(true, false, false, false, 1f, 0f)]   // D — camera right
    [InlineData(false, true, false, false, -1f, 0f)]  // A — camera left
    [InlineData(false, false, true, false, 0f, 1f)]   // W — camera forward
    [InlineData(false, false, false, true, 0f, -1f)]  // S — camera back
    public void Each_movement_key_moves_along_its_own_camera_axis(
        bool right, bool left, bool forward, bool back, float rightAmount, float forwardAmount)
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(new Vector3(4f, -1f, 8f), 1.1f, -0.4f);
        Vector3 start = harness.Scene.Camera.Position;
        Vector3 cameraRight = harness.Scene.Camera.Right;
        Vector3 cameraForward = harness.Scene.Camera.Forward;
        harness.EditorCamera.FlySpeed = 8f;

        Fly(harness, EditorNavigationInput.FromKeys(forward, back, left, right, up: false, down: false), 0.25f);

        Vector3 expected = start + (cameraRight * rightAmount + cameraForward * forwardAmount) * (8f * 0.25f);
        harness.Scene.Camera.Position.ShouldBeCloseTo(expected, 1e-4f);
    }

    [Theory]
    [InlineData(true, false, 1f)]
    [InlineData(false, true, -1f)]
    public void The_rise_and_fall_keys_move_along_WORLD_up_however_the_camera_is_pitched(
        bool up, bool down, float sign)
    {
        // A camera-relative up would make "go up" mean "go up and backwards" the
        // moment you looked down. Pitched steeply, so a tilted axis would show.
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(new Vector3(3f, 5f, -2f), 0.9f, -1.2f);
        Vector3 start = harness.Scene.Camera.Position;
        harness.EditorCamera.FlySpeed = 6f;

        Fly(harness, EditorNavigationInput.FromKeys(
            forward: false, back: false, left: false, right: false, up: up, down: down), 0.5f);

        harness.Scene.Camera.Position.ShouldBeCloseTo(start + Vector3.UnitY * (sign * 3f), 1e-4f);
    }

    [Fact]
    public void Holding_two_keys_is_no_faster_than_holding_one()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(Vector3.Zero, 0f, 0f);
        harness.EditorCamera.FlySpeed = 10f;

        Fly(harness, EditorNavigationInput.FromKeys(
            forward: true, back: false, left: false, right: true, up: false, down: false), 0.5f);

        harness.Scene.Camera.Position.Length().ShouldBe(5f, 1e-4f);
    }

    [Fact]
    public void Opposing_keys_cancel_instead_of_jittering()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(Vector3.Zero, 0f, 0f);

        EditorNavigationInput both = EditorNavigationInput.FromKeys(
            forward: true, back: true, left: true, right: true, up: true, down: true);
        both.IsIdle.ShouldBeTrue();

        Fly(harness, both, 0.5f);
        harness.Scene.Camera.Position.ShouldBe(Vector3.Zero);
    }

    [Fact]
    public void The_boost_modifier_multiplies_the_distance_travelled()
    {
        var plain = new ViewportHarness();
        plain.EditorCamera.SetPose(Vector3.Zero, 0f, 0f);
        plain.EditorCamera.FlySpeed = 10f;
        Fly(plain, EditorNavigationInput.FromKeys(
            forward: true, back: false, left: false, right: false, up: false, down: false), 0.5f);

        var boosted = new ViewportHarness();
        boosted.EditorCamera.SetPose(Vector3.Zero, 0f, 0f);
        boosted.EditorCamera.FlySpeed = 10f;
        Fly(boosted, EditorNavigationInput.FromKeys(
            forward: true, back: false, left: false, right: false, up: false, down: false, boost: true), 0.5f);

        float multiplier = boosted.EditorCamera.BoostMultiplier;
        boosted.Scene.Camera.Position.Length()
            .ShouldBe(plain.Scene.Camera.Position.Length() * multiplier, 1e-3f);
    }

    [Fact]
    public void Flying_carries_the_focus_along_so_the_orbit_pivot_stays_ahead()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(new Vector3(1f, 2f, 3f), 0.6f, -0.25f, distance: 20f);
        Vector3 offset = harness.EditorCamera.Focus - harness.EditorCamera.Position;
        harness.EditorCamera.FlySpeed = 10f;

        Fly(harness, EditorNavigationInput.FromKeys(
            forward: true, back: false, left: false, right: false, up: false, down: false), 0.5f);

        (harness.EditorCamera.Focus - harness.EditorCamera.Position).ShouldBeCloseTo(offset, 1e-3f);
    }

    // --- The wheel -----------------------------------------------------------

    [Fact]
    public void The_wheel_while_looking_trims_the_fly_speed_and_does_not_zoom()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(Vector3.Zero, 0f, 0f, distance: 20f);
        harness.EditorCamera.FlySpeed = 10f;
        float step = harness.EditorCamera.FlySpeedStep;

        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel, down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel, down: PointerButtons.Right, scroll: new Vector2(0f, 2f)));

        harness.EditorCamera.FlySpeed.ShouldBe(10f * step * step, 1e-3f);
        harness.EditorCamera.Distance.ShouldBe(20f);
    }

    [Fact]
    public void The_trimmed_fly_speed_persists_after_the_look_ends()
    {
        // Studio users navigate large places by setting this once and expecting
        // it to stay set — so releasing the button, and even suspending
        // navigation entirely, must leave it alone.
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(Vector3.Zero, 0f, 0f);
        harness.EditorCamera.FlySpeed = 10f;

        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel, down: PointerButtons.Right, pressed: PointerButtons.Right,
            scroll: new Vector2(0f, 3f)));
        float trimmed = harness.EditorCamera.FlySpeed;
        trimmed.ShouldBeGreaterThan(10f);

        harness.EditorCamera.Update(harness.Frame(harness.CenterPixel, released: PointerButtons.Right));
        harness.EditorCamera.SuspendNavigation();

        harness.EditorCamera.FlySpeed.ShouldBe(trimmed);
    }

    [Fact]
    public void The_wheel_without_the_look_button_still_zooms_and_leaves_the_speed_alone()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, 0f, 0f);
        harness.EditorCamera.FlySpeed = 10f;

        harness.EditorCamera.Update(harness.Frame(harness.CenterPixel, scroll: new Vector2(0f, 1f)));

        harness.EditorCamera.Distance.ShouldBe(20f / harness.EditorCamera.ZoomStep, 1e-3f);
        harness.EditorCamera.FlySpeed.ShouldBe(10f);
    }

    [Fact]
    public void The_fly_speed_trim_is_clamped_at_both_ends()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.MinFlySpeed = 1f;
        harness.EditorCamera.MaxFlySpeed = 100f;
        harness.EditorCamera.FlySpeed = 10f;

        harness.EditorCamera.AdjustFlySpeed(500f);
        harness.EditorCamera.FlySpeed.ShouldBe(100f);

        harness.EditorCamera.AdjustFlySpeed(-500f);
        harness.EditorCamera.FlySpeed.ShouldBe(1f);
    }

    // --- Relative motion while locked ----------------------------------------

    [Fact]
    public void A_locked_look_reads_relative_motion_and_ignores_the_frozen_position()
    {
        // Once the OS captures the cursor its reported position stops changing
        // (the input manager freezes it deliberately), so a controller that
        // differenced positions would sit still forever.
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(Vector3.Zero, 0f, 0f);
        Vector2 frozen = harness.CenterPixel;

        harness.EditorCamera.Update(harness.Frame(
            frozen, down: PointerButtons.Right, pressed: PointerButtons.Right));
        // The lock lands: this frame's motion is the backend's own teleport.
        harness.EditorCamera.Update(harness.Frame(
            frozen, down: PointerButtons.Right, cursorDelta: new Vector2(999f, 999f), locked: true));
        harness.EditorCamera.Yaw.ShouldBe(0f);

        harness.EditorCamera.Update(harness.Frame(
            frozen, down: PointerButtons.Right, cursorDelta: new Vector2(40f, -20f), locked: true));

        float sensitivity = harness.EditorCamera.LookSensitivity;
        harness.EditorCamera.Yaw.ShouldBe(40f * sensitivity, 1e-6f);
        harness.EditorCamera.Pitch.ShouldBe(20f * sensitivity, 1e-6f);
        harness.Scene.Camera.Position.ShouldBe(Vector3.Zero);
    }

    [Fact]
    public void Releasing_a_locked_look_does_not_read_the_restored_cursor_as_a_flick()
    {
        // Unlocking teleports the pointer back to where it started. If that
        // showed up as motion, every look would end with a jolt.
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(Vector3.Zero, 0f, 0f);

        harness.EditorCamera.Update(harness.Frame(
            new Vector2(400f, 300f), down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.EditorCamera.Update(harness.Frame(
            new Vector2(400f, 300f), down: PointerButtons.Right, cursorDelta: new Vector2(50f, 0f), locked: true));
        float yaw = harness.EditorCamera.Yaw;

        // Released and unlocked on the same frame, cursor restored elsewhere.
        harness.EditorCamera.Update(harness.Frame(new Vector2(120f, 640f), released: PointerButtons.Right));

        harness.EditorCamera.Yaw.ShouldBe(yaw);
        harness.EditorCamera.ActiveGesture.ShouldBe(EditorNavigationGesture.None);
    }

    // --- Helpers -------------------------------------------------------------

    // Press, then move: the press frame is deliberately deltaless, so the whole
    // travel lands on the second frame.
    private static void LookDrag(ViewportHarness harness, Vector2 from, Vector2 to)
    {
        harness.EditorCamera.Update(
            harness.Frame(from, down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.EditorCamera.Update(harness.Frame(to, down: PointerButtons.Right));
    }

    // One movement frame with the look button held, which is how the demo host
    // gates the movement keys.
    private static void Fly(ViewportHarness harness, EditorNavigationInput navigation, float seconds)
    {
        harness.EditorCamera.Update(harness.Frame(
            harness.CenterPixel,
            down: PointerButtons.Right,
            pressed: PointerButtons.Right,
            deltaTime: seconds,
            navigation: navigation));
    }
}
