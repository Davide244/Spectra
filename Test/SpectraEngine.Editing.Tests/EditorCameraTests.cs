using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Viewport;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The orbit camera's felt behaviour, asserted as maths: the wheel really does
/// pull the point under the cursor toward you, framing really does fit the
/// selection, orbit and pan really do leave alone what they promise to, and
/// none of it degrades at open-world coordinates.
/// </summary>
/// <remarks>
/// <b>The invariants are stated in screen space wherever the user would state
/// them there.</b> "Zoom toward the cursor" is not a claim about a vector, it is
/// a claim that a specific world point keeps projecting to a specific pixel —
/// so that is what these tests measure, through the harness's exact projection.
/// A test written against the vector maths instead would pass on a sign error
/// that any user would spot in one wheel notch.
/// </remarks>
public sealed class EditorCameraTests
{
    // Pixel slack for a value that made the round trip world → clip → pixels.
    private const float PixelTolerance = 0.05f;

    // --- Zoom to cursor ------------------------------------------------------

    [Theory]
    [InlineData(600f, 180f)]
    [InlineData(120f, 500f)]
    [InlineData(780f, 590f)]
    [InlineData(10f, 12f)]
    public void Zooming_keeps_the_world_point_under_the_cursor_under_the_cursor(float px, float py)
    {
        var harness = new ViewportHarness();
        harness.Orbit(new Vector3(2f, 1f, -3f), 20f, 0.6f, -0.35f);
        var cursor = new Vector2(px, py);

        // A real world point: where the cursor ray crosses the focus plane.
        Vector3 anchor = FocusPlanePoint(harness, cursor);
        harness.WorldToScreen(anchor).ShouldBeCloseTo(cursor, PixelTolerance);

        harness.EditorCamera.Update(harness.Frame(cursor, scroll: new Vector2(0f, 1f)));

        // The whole point of zoom-to-cursor: it did not drift.
        harness.WorldToScreen(anchor).ShouldBeCloseTo(cursor, PixelTolerance);
    }

    [Fact]
    public void Zooming_slides_the_focus_along_the_cursor_ray_toward_it()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, -MathF.PI / 2f, -0.25f);
        var cursor = new Vector2(700f, 150f);

        Vector3 anchor = FocusPlanePoint(harness, cursor);
        Vector3 before = harness.EditorCamera.Focus;

        harness.EditorCamera.Update(harness.Frame(cursor, scroll: new Vector2(0f, 1f)));

        Vector3 moved = harness.EditorCamera.Focus - before;
        Vector3 toward = anchor - before;

        moved.Length().ShouldBeGreaterThan(0f);
        // Parallel to the ray's crossing of the focus plane, and pointing at it.
        Vector3.Dot(Vector3.Normalize(moved), Vector3.Normalize(toward)).ShouldBe(1f, 1e-4f);
        // Exactly the derived fraction: 1 − s, where s is the distance ratio.
        float ratio = harness.EditorCamera.Distance / 20f;
        moved.Length().ShouldBe(toward.Length() * (1f - ratio), toward.Length() * 1e-3f);
    }

    [Fact]
    public void One_notch_scales_the_distance_by_exactly_one_zoom_step()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, 0f, 0f);
        float step = harness.EditorCamera.ZoomStep;

        harness.EditorCamera.Update(harness.Frame(harness.CenterPixel, scroll: new Vector2(0f, 1f)));
        harness.EditorCamera.Distance.ShouldBe(20f / step, 1e-3f);

        harness.EditorCamera.Update(harness.Frame(harness.CenterPixel, scroll: new Vector2(0f, -1f)));
        harness.EditorCamera.Distance.ShouldBe(20f, 1e-3f);
    }

    [Fact]
    public void Zooming_at_the_screen_centre_leaves_the_focus_where_it_is()
    {
        var harness = new ViewportHarness();
        harness.Orbit(new Vector3(4f, -2f, 7f), 15f, 1.1f, 0.4f);
        Vector3 before = harness.EditorCamera.Focus;

        harness.EditorCamera.Update(harness.Frame(harness.CenterPixel, scroll: new Vector2(0f, 3f)));

        // The centre ray passes through the focus, so there is nowhere to slide.
        (harness.EditorCamera.Focus - before).Length().ShouldBe(0f, 1e-3f);
    }

    [Fact]
    public void Turning_zoom_to_cursor_off_dollies_straight_down_the_view_axis()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, -MathF.PI / 2f, -0.25f);
        harness.EditorCamera.ZoomToCursor = false;
        Vector3 before = harness.EditorCamera.Focus;

        harness.EditorCamera.Update(harness.Frame(new Vector2(700f, 150f), scroll: new Vector2(0f, 1f)));

        (harness.EditorCamera.Focus - before).Length().ShouldBe(0f, 1e-4f);
        harness.EditorCamera.Distance.ShouldBeLessThan(20f);
    }

    [Fact]
    public void Zoom_stops_sliding_the_focus_once_the_distance_hits_its_stop()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, -MathF.PI / 2f, -0.25f);
        harness.EditorCamera.MinDistance = 20f; // already at the stop

        Vector3 before = harness.EditorCamera.Focus;
        harness.EditorCamera.Update(harness.Frame(new Vector2(700f, 150f), scroll: new Vector2(0f, 5f)));

        harness.EditorCamera.Distance.ShouldBe(20f);
        // A clamped dolly that still crept toward the cursor would walk the
        // camera across the level while appearing to do nothing.
        (harness.EditorCamera.Focus - before).Length().ShouldBe(0f);
    }

    // --- Orbit ---------------------------------------------------------------

    [Fact]
    public void Orbiting_turns_the_camera_around_a_fixed_focus_at_a_fixed_distance()
    {
        var harness = new ViewportHarness();
        harness.Orbit(new Vector3(3f, 1f, -2f), 15f, 0.3f, -0.2f);
        Vector3 focus = harness.EditorCamera.Focus;
        Vector3 startPosition = harness.Scene.Camera.Position;

        OrbitDrag(harness, new Vector2(400f, 300f), new Vector2(460f, 340f));

        harness.EditorCamera.Focus.ShouldBe(focus);
        harness.EditorCamera.Distance.ShouldBe(15f);
        (harness.Scene.Camera.Position - focus).Length().ShouldBe(15f, 1e-3f);
        // The camera still looks straight at what it orbits.
        Vector3.Dot(
            Vector3.Normalize(focus - harness.Scene.Camera.Position),
            harness.Scene.Camera.Forward).ShouldBe(1f, 1e-5f);
        // And it actually moved.
        (harness.Scene.Camera.Position - startPosition).Length().ShouldBeGreaterThan(0.1f);
    }

    [Fact]
    public void The_press_frame_of_an_orbit_applies_no_delta()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 15f, 0.3f, -0.2f);

        // Cursor jumps a long way and the button goes down on the same frame:
        // that jump is where the user moved the mouse BEFORE deciding to orbit.
        harness.EditorCamera.Update(harness.Frame(new Vector2(20f, 20f)));
        harness.EditorCamera.Update(harness.Frame(
            new Vector2(700f, 500f),
            down: PointerButtons.Right,
            pressed: PointerButtons.Right,
            modifiers: KeyModifiers.Alt));

        harness.EditorCamera.Yaw.ShouldBe(0.3f);
        harness.EditorCamera.Pitch.ShouldBe(-0.2f);
    }

    [Fact]
    public void Alt_with_the_left_button_orbits_exactly_as_alt_with_the_right_one()
    {
        var left = new ViewportHarness();
        left.Orbit(Vector3.Zero, 15f, 0.3f, -0.2f);
        left.EditorCamera.Update(left.Frame(new Vector2(400f, 300f),
            down: PointerButtons.Left, modifiers: KeyModifiers.Alt));
        left.EditorCamera.Update(left.Frame(new Vector2(460f, 340f),
            down: PointerButtons.Left, modifiers: KeyModifiers.Alt));

        var right = new ViewportHarness();
        right.Orbit(Vector3.Zero, 15f, 0.3f, -0.2f);
        OrbitStep(right, new Vector2(400f, 300f), new Vector2(460f, 340f));

        left.EditorCamera.Yaw.ShouldBe(right.EditorCamera.Yaw);
        left.EditorCamera.Pitch.ShouldBe(right.EditorCamera.Pitch);
    }

    [Fact]
    public void Alt_is_what_turns_a_right_drag_from_a_look_into_an_orbit()
    {
        // The whole semantic change in one assertion: the same drag, the same
        // button, and the modifier decides whether the camera turns in place or
        // swings around the focus.
        var look = new ViewportHarness();
        look.Orbit(new Vector3(3f, 1f, -2f), 15f, 0.3f, -0.2f);
        Vector3 lookStart = look.Scene.Camera.Position;
        look.EditorCamera.Update(look.Frame(new Vector2(400f, 300f), down: PointerButtons.Right));
        look.EditorCamera.Update(look.Frame(new Vector2(460f, 340f), down: PointerButtons.Right));

        var orbit = new ViewportHarness();
        orbit.Orbit(new Vector3(3f, 1f, -2f), 15f, 0.3f, -0.2f);
        Vector3 orbitStart = orbit.Scene.Camera.Position;
        OrbitStep(orbit, new Vector2(400f, 300f), new Vector2(460f, 340f));

        look.Scene.Camera.Position.ShouldBe(lookStart);
        (orbit.Scene.Camera.Position - orbitStart).Length().ShouldBeGreaterThan(0.1f);
    }

    [Fact]
    public void The_left_button_without_alt_does_not_orbit()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 15f, 0.3f, -0.2f);

        harness.EditorCamera.Update(harness.Frame(new Vector2(400f, 300f), down: PointerButtons.Left));
        harness.EditorCamera.Update(harness.Frame(new Vector2(460f, 340f), down: PointerButtons.Left));

        harness.EditorCamera.Yaw.ShouldBe(0.3f);
    }

    [Fact]
    public void Orbiting_past_vertical_clamps_short_of_the_degenerate_pole()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 15f, 0f, 0f);

        // Far more than a quarter turn of drag, both ways.
        OrbitDrag(harness, new Vector2(400f, 900f), new Vector2(400f, -900f));
        harness.EditorCamera.Pitch.ShouldBeLessThan(MathF.PI / 2f);
        harness.Scene.Camera.Forward.Length().ShouldBe(1f, 1e-5f);

        OrbitDrag(harness, new Vector2(400f, -900f), new Vector2(400f, 900f));
        harness.EditorCamera.Pitch.ShouldBeGreaterThan(-MathF.PI / 2f);
        harness.Scene.Camera.Forward.Length().ShouldBe(1f, 1e-5f);
    }

    // --- Pan -----------------------------------------------------------------

    [Fact]
    public void Panning_drags_the_world_along_with_the_cursor()
    {
        var harness = new ViewportHarness();
        harness.Orbit(new Vector3(1f, 2f, -4f), 18f, 0.9f, -0.3f);

        var start = new Vector2(360f, 260f);
        var end = new Vector2(440f, 330f);
        Vector3 anchor = FocusPlanePoint(harness, start);

        harness.EditorCamera.Update(harness.Frame(start, down: PointerButtons.Middle, pressed: PointerButtons.Middle));
        harness.EditorCamera.Update(harness.Frame(end, down: PointerButtons.Middle));

        // The world point that was under the cursor when the pan began has
        // travelled exactly as far as the cursor did.
        harness.WorldToScreen(anchor).ShouldBeCloseTo(end, 0.25f);
    }

    [Fact]
    public void Panning_changes_neither_the_orientation_nor_the_distance()
    {
        var harness = new ViewportHarness();
        harness.Orbit(new Vector3(1f, 2f, -4f), 18f, 0.9f, -0.3f);
        Vector3 forward = harness.Scene.Camera.Forward;

        harness.EditorCamera.Update(harness.Frame(new Vector2(360f, 260f),
            down: PointerButtons.Middle, pressed: PointerButtons.Middle));
        harness.EditorCamera.Update(harness.Frame(new Vector2(440f, 330f), down: PointerButtons.Middle));

        harness.EditorCamera.Distance.ShouldBe(18f);
        harness.EditorCamera.Yaw.ShouldBe(0.9f);
        harness.EditorCamera.Pitch.ShouldBe(-0.3f);
        harness.Scene.Camera.Forward.ShouldBe(forward);
        // The focus stayed in the view plane it started in.
        Vector3.Dot(harness.EditorCamera.Focus - new Vector3(1f, 2f, -4f), forward).ShouldBe(0f, 1e-3f);
    }

    // --- Frame selection -----------------------------------------------------

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1.2f, -0.5f)]
    [InlineData(-2.7f, 0.9f)]
    [InlineData(3.4f, -1.3f)]
    public void Framing_the_selection_puts_every_corner_of_it_in_view(float yaw, float pitch)
    {
        var harness = new ViewportHarness();
        harness.Orbit(new Vector3(60f, 40f, -90f), 200f, yaw, pitch);

        harness.AddSelectedBrush(new Vector3(-3f, 0f, 2f), 1f);
        harness.AddSelectedBrush(new Vector3(4f, 2f, -3f), 0.5f);
        harness.AddSelectedBrush(new Vector3(0f, -2f, 6f), 1.5f);

        harness.EditorCamera.FrameSelection().ShouldBeTrue();
        harness.EditorCamera.SnapToTarget();

        Aabb bounds = SelectionBounds(harness);
        Frustum frustum = harness.Scene.Camera.GetFrustum();
        foreach (Vector3 corner in Corners(bounds))
            frustum.Contains(corner).ShouldBeTrue($"corner {corner} fell outside the framed view");
    }

    [Fact]
    public void Framing_centres_the_orbit_on_the_selection()
    {
        var harness = new ViewportHarness();
        harness.Orbit(new Vector3(500f, 500f, 500f), 900f, 0.4f, -0.2f);
        harness.AddSelectedBrush(new Vector3(10f, 0f, 0f), 2f);
        harness.AddSelectedBrush(new Vector3(-10f, 0f, 0f), 2f);

        harness.EditorCamera.FrameSelection().ShouldBeTrue();
        harness.EditorCamera.SnapToTarget();

        harness.EditorCamera.Focus.ShouldBeCloseTo(Vector3.Zero, 1e-4f);
        // The selection projects around the middle of the viewport.
        harness.WorldToScreen(Vector3.Zero).ShouldBeCloseTo(harness.CenterPixel, 0.5f);
    }

    [Fact]
    public void Framing_leaves_the_viewing_angle_alone()
    {
        var harness = new ViewportHarness();
        harness.Orbit(new Vector3(20f, 5f, 3f), 60f, 1.4f, -0.6f);
        harness.AddSelectedBrush(Vector3.Zero, 3f);

        harness.EditorCamera.FrameSelection().ShouldBeTrue();
        harness.EditorCamera.SnapToTarget();

        // Framing is a dolly and a re-centre, never a re-orientation: the user
        // keeps the viewpoint they had chosen.
        harness.EditorCamera.Yaw.ShouldBe(1.4f);
        harness.EditorCamera.Pitch.ShouldBe(-0.6f);
    }

    [Fact]
    public void Framing_an_empty_selection_changes_nothing()
    {
        var harness = new ViewportHarness();
        harness.Orbit(new Vector3(3f, 1f, -2f), 15f, 0.3f, -0.2f);
        harness.AddBrush(Vector3.Zero, 1f); // present but unselected

        harness.EditorCamera.FrameSelection().ShouldBeFalse();
        harness.EditorCamera.TargetFocus.ShouldBe(new Vector3(3f, 1f, -2f));
        harness.EditorCamera.TargetDistance.ShouldBe(15f);
    }

    [Fact]
    public void Framing_a_selected_group_with_no_bounds_puts_it_on_screen()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 500f, 0f, 0f);
        SceneNode group = harness.Scene.Root.CreateChild("Group");
        group.LocalPosition = new Vector3(120f, -40f, 33f);
        harness.Scene.Selection.Select(group);

        harness.EditorCamera.FrameSelection().ShouldBeTrue();
        harness.EditorCamera.SnapToTarget();

        harness.EditorCamera.Focus.ShouldBe(group.WorldPosition);
    }

    [Fact]
    public void Framing_everything_includes_what_was_off_screen()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 10f, 0f, 0f);
        harness.AddBrush(new Vector3(-40f, 0f, 0f), 1f);
        harness.AddBrush(new Vector3(40f, 0f, 0f), 1f);

        harness.EditorCamera.FrameAll().ShouldBeTrue();
        harness.EditorCamera.SnapToTarget();

        harness.EditorCamera.Focus.ShouldBeCloseTo(Vector3.Zero, 1e-4f);
        harness.EditorCamera.Distance.ShouldBeGreaterThan(40f);
    }

    [Theory]
    [InlineData("F", KeyModifiers.None, EditorCameraCommand.FrameSelection)]
    [InlineData("f", KeyModifiers.None, EditorCameraCommand.FrameSelection)]
    [InlineData("F", KeyModifiers.Shift, EditorCameraCommand.FrameAll)]
    public void The_default_keymap_resolves_the_framing_verbs(
        string key, KeyModifiers modifiers, EditorCameraCommand expected)
    {
        EditorCameraShortcuts.TryResolve(key, modifiers, out EditorCameraCommand command).ShouldBeTrue();
        command.ShouldBe(expected);
    }

    [Fact]
    public void An_unbound_key_resolves_to_nothing()
    {
        EditorCameraShortcuts.TryResolve("Q", out _).ShouldBeFalse();
        EditorCameraShortcuts.TryResolve(null, out _).ShouldBeFalse();
    }

    // --- Damping -------------------------------------------------------------

    [Fact]
    public void After_exactly_one_time_constant_the_gap_has_closed_by_one_e_fold()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SmoothingTimeConstant = 0.1f;
        harness.Orbit(Vector3.Zero, 10f, 0f, 0f);

        // Move only the focus target: a degenerate (point) bounds leaves the
        // distance alone, so this isolates the filter.
        var target = new Vector3(10f, 0f, 0f);
        harness.EditorCamera.FrameBounds(new Aabb(target, target));

        // Ten steps of 0.01 s is one 0.1 s time constant, and the discrete
        // filter is exact at any step size: (e^(−Δt/τ))^n = e^(−nΔt/τ).
        for (int i = 0; i < 10; i++)
            harness.EditorCamera.Update(harness.Frame(harness.CenterPixel, deltaTime: 0.01f));

        float remaining = (target - harness.EditorCamera.Focus).Length();
        remaining.ShouldBe(10f * MathF.Exp(-1f), 1e-3f);
        harness.EditorCamera.IsSettling.ShouldBeTrue();
    }

    [Fact]
    public void A_zero_time_constant_arrives_within_the_frame()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SmoothingTimeConstant = 0f;
        harness.Orbit(Vector3.Zero, 10f, 0f, 0f);

        var target = new Vector3(10f, 0f, 0f);
        harness.EditorCamera.FrameBounds(new Aabb(target, target));
        harness.EditorCamera.Update(harness.Frame(harness.CenterPixel, deltaTime: 0.016f));

        harness.EditorCamera.Focus.ShouldBe(target);
        harness.EditorCamera.IsSettling.ShouldBeFalse();
    }

    [Fact]
    public void A_settled_camera_reports_that_it_did_no_work()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 10f, 0f, 0f);

        harness.EditorCamera.Update(harness.Frame(harness.CenterPixel)).ShouldBeFalse();
    }

    // --- Open-world coordinates ----------------------------------------------

    [Fact]
    public void The_orbit_maths_is_identical_at_the_origin_and_a_million_units_out()
    {
        // The whole reason focus/distance/angles are the authority and the
        // position is derived: the orbit must not inherit the quantization of
        // the coordinate it happens to be sitting at.
        var near = new ViewportHarness();
        var far = new ViewportHarness();
        var origin = Vector3.Zero;
        var distant = new Vector3(1_000_000f, 0f, -1_000_000f);

        near.Orbit(origin, 12f, 0.7f, -0.35f);
        far.Orbit(distant, 12f, 0.7f, -0.35f);

        for (int i = 0; i < 120; i++)
        {
            var from = new Vector2(400f + i, 300f + (i % 7));
            var to = new Vector2(403f + i, 302f + (i % 7));
            OrbitStep(near, from, to);
            OrbitStep(far, from, to);
        }

        far.EditorCamera.Yaw.ShouldBe(near.EditorCamera.Yaw);
        far.EditorCamera.Pitch.ShouldBe(near.EditorCamera.Pitch);
        far.EditorCamera.Distance.ShouldBe(near.EditorCamera.Distance);
        far.EditorCamera.Focus.ShouldBe(distant); // orbit never moves the focus
    }

    [Fact]
    public void An_orbit_a_million_units_out_stays_finite_and_on_its_sphere()
    {
        var harness = new ViewportHarness();
        var distant = new Vector3(1_000_000f, 0f, -1_000_000f);
        harness.Orbit(distant, 12f, 0.7f, -0.35f);

        for (int i = 0; i < 200; i++)
            OrbitStep(harness, new Vector2(400f, 300f), new Vector2(407f, 296f));

        Vector3 position = harness.Scene.Camera.Position;
        float.IsFinite(position.X).ShouldBeTrue();
        float.IsFinite(position.Y).ShouldBeTrue();
        float.IsFinite(position.Z).ShouldBeTrue();
        harness.Scene.Camera.Forward.Length().ShouldBe(1f, 1e-5f);

        // A float at 1e6 resolves to about 1/16 of a unit, so the DERIVED
        // position can only be right to a few of those ulps; the orbit state
        // itself is exact.
        (position - distant).Length().ShouldBe(12f, 0.25f);
        harness.EditorCamera.Distance.ShouldBe(12f);
    }

    [Fact]
    public void A_long_orbit_keeps_the_yaw_inside_one_turn()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 12f, 0f, 0f);

        for (int i = 0; i < 2000; i++)
            OrbitStep(harness, new Vector2(400f, 300f), new Vector2(430f, 300f));

        MathF.Abs(harness.EditorCamera.Yaw).ShouldBeLessThanOrEqualTo(MathF.PI + 1e-3f);
        harness.Scene.Camera.Forward.Length().ShouldBe(1f, 1e-5f);
    }

    // --- Handover ------------------------------------------------------------

    [Fact]
    public void Adopting_a_camera_keeps_its_position_and_its_view_direction()
    {
        var harness = new ViewportHarness();
        harness.Scene.Camera.Position = new Vector3(12f, 7f, -4f);
        harness.Scene.Camera.Yaw = 1.1f;
        harness.Scene.Camera.Pitch = -0.4f;
        Vector3 position = harness.Scene.Camera.Position;
        Vector3 forward = harness.Scene.Camera.Forward;

        harness.EditorCamera.AdoptCamera(25f);

        harness.Scene.Camera.Forward.ShouldBe(forward);
        (harness.Scene.Camera.Position - position).Length().ShouldBe(0f, 1e-3f);
        // The focus lands on what the camera was already looking at.
        (harness.EditorCamera.Focus - (position + forward * 25f)).Length().ShouldBe(0f, 1e-3f);
    }

    // --- Helpers -------------------------------------------------------------

    private static Vector3 FocusPlanePoint(ViewportHarness harness, Vector2 cursor)
    {
        Camera camera = harness.Scene.Camera;
        Ray3 ray = camera.ScreenPointToRay(cursor, harness.ViewportSize);
        float travel = Vector3.Dot(harness.EditorCamera.Focus - ray.Origin, camera.Forward)
            / Vector3.Dot(ray.Direction, camera.Forward);
        return ray.PointAt(travel);
    }

    // Orbit is the MODIFIER gesture now that plain right-drag is a freelook, so
    // every orbit here holds Alt. See EditorFreelookTests for the unmodified
    // gesture.
    private static void OrbitDrag(ViewportHarness harness, Vector2 from, Vector2 to)
    {
        harness.EditorCamera.Update(harness.Frame(
            from, down: PointerButtons.Right, pressed: PointerButtons.Right, modifiers: KeyModifiers.Alt));
        harness.EditorCamera.Update(
            harness.Frame(to, down: PointerButtons.Right, modifiers: KeyModifiers.Alt));
    }

    // One held-button step: the press edge is already behind us, so both frames
    // carry the delta.
    private static void OrbitStep(ViewportHarness harness, Vector2 from, Vector2 to)
    {
        harness.EditorCamera.Update(
            harness.Frame(from, down: PointerButtons.Right, modifiers: KeyModifiers.Alt));
        harness.EditorCamera.Update(
            harness.Frame(to, down: PointerButtons.Right, modifiers: KeyModifiers.Alt));
    }

    // --- Frames the arbiter withheld -----------------------------------------

    [Fact]
    public void Cursor_travel_withheld_by_a_marquee_never_arrives_as_one_look_step()
    {
        // The controller measures its drag against the cursor position it last
        // SAW, and it only sees the frames it is given. A marquee owns the
        // pointer for its whole duration, so without being told it was skipped
        // the controller applies every withheld pixel at once on the first frame
        // it runs again — a frame on which the cursor did not move at all.
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, 0f, 0f);

        // A real look first, so the anchor the camera would snap from is a
        // position it genuinely saw rather than a default.
        harness.Viewport.Update(harness.Frame(
            new Vector2(400f, 300f), down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.Viewport.Update(harness.Frame(new Vector2(410f, 300f), down: PointerButtons.Right));

        float yaw = harness.EditorCamera.Yaw;
        float pitch = harness.EditorCamera.Pitch;
        yaw.ShouldBe(10f * harness.EditorCamera.LookSensitivity, 1e-5f); // it really was looking

        harness.Viewport.Update(harness.Frame(new Vector2(410f, 300f), released: PointerButtons.Right));
        harness.EditorCamera.IsNavigating.ShouldBeFalse();

        // Now a marquee across most of the viewport. The camera sits out every
        // frame of it — and the right button arriving mid-marquee does not take
        // the pointer back, so it sits out those frames too.
        harness.Viewport.Update(harness.Frame(
            new Vector2(410f, 300f),
            down: PointerButtons.Left,
            pressed: PointerButtons.Left)).ShouldBe(ViewportDragMode.BoxSelect);
        harness.Viewport.Update(harness.Frame(
            new Vector2(600f, 400f),
            down: PointerButtons.Left | PointerButtons.Right,
            pressed: PointerButtons.Right)).ShouldBe(ViewportDragMode.BoxSelect);
        harness.Viewport.Update(harness.Frame(
            new Vector2(700f, 500f), down: PointerButtons.Right | PointerButtons.Left));
        harness.Viewport.Update(harness.Frame(
            new Vector2(700f, 500f), down: PointerButtons.Right, released: PointerButtons.Left))
            .ShouldBe(ViewportDragMode.None);

        // The first frame the camera runs again. The cursor has not moved since
        // the release, so the camera must not move either — 290 px of withheld
        // travel is sitting in the stale anchor.
        harness.Viewport.Update(harness.Frame(new Vector2(700f, 500f), down: PointerButtons.Right));
        harness.EditorCamera.Yaw.ShouldBe(yaw);
        harness.EditorCamera.Pitch.ShouldBe(pitch);

        // And navigation resumes from where the cursor now is, not from where
        // it was before the marquee.
        harness.Viewport.Update(harness.Frame(new Vector2(710f, 500f), down: PointerButtons.Right));
        harness.EditorCamera.Yaw.ShouldBe(yaw + 10f * harness.EditorCamera.LookSensitivity, 1e-5f);
    }

    [Fact]
    public void Cursor_travel_withheld_by_a_gizmo_drag_never_arrives_as_one_look_step()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, 0f, 0f);
        harness.AddSelectedBrush(Vector3.Zero, 1f);

        harness.Viewport.Update(harness.Frame(
            new Vector2(400f, 300f), down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.Viewport.Update(harness.Frame(new Vector2(410f, 300f), down: PointerButtons.Right));
        float yaw = harness.EditorCamera.Yaw;
        float pitch = harness.EditorCamera.Pitch;

        harness.Viewport.Update(harness.Frame(new Vector2(410f, 300f), released: PointerButtons.Right));

        float axisLength = GizmoGeometry.Build(
            harness.Scene.Camera, Vector3.Zero, Quaternion.Identity,
            harness.ViewportSize, harness.Gizmos.Active.HandlePixelSize).AxisLength;
        Vector2 handlePixel = harness.WorldToScreen(Vector3.UnitX * (axisLength * 0.8f));

        // Grab the x arrow, then bring the look button down mid-drag. Right is
        // pressed while the manipulator owns the pointer, so it reaches the
        // gizmo's cancel binding and not the camera; either way the camera is
        // skipped for the whole gesture.
        harness.Viewport.Update(harness.Frame(
            handlePixel, down: PointerButtons.Left, pressed: PointerButtons.Left))
            .ShouldBe(ViewportDragMode.Manipulate);
        harness.Viewport.Update(harness.Frame(
            handlePixel + new Vector2(300f, 0f), down: PointerButtons.Left));
        harness.Viewport.Update(harness.Frame(
            handlePixel + new Vector2(300f, 0f),
            down: PointerButtons.Right | PointerButtons.Left,
            pressed: PointerButtons.Right)).ShouldBe(ViewportDragMode.None);

        harness.Viewport.Update(harness.Frame(
            handlePixel + new Vector2(300f, 0f), down: PointerButtons.Right));
        harness.EditorCamera.Yaw.ShouldBe(yaw);
        harness.EditorCamera.Pitch.ShouldBe(pitch);
    }

    [Fact]
    public void Resetting_the_viewport_also_re_anchors_the_camera()
    {
        // Same hazard through a different door: a host that resets while the
        // look button is down (a minimized window, an undo mid-gesture) skips
        // the camera for as long as it takes to come back.
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, 0f, 0f);

        harness.Viewport.Update(harness.Frame(
            new Vector2(400f, 300f), down: PointerButtons.Right, pressed: PointerButtons.Right));
        harness.Viewport.Update(harness.Frame(new Vector2(410f, 300f), down: PointerButtons.Right));
        float yaw = harness.EditorCamera.Yaw;
        harness.EditorCamera.IsFreeLooking.ShouldBeTrue();

        harness.Viewport.Reset();
        harness.EditorCamera.IsFreeLooking.ShouldBeFalse();

        harness.Viewport.Update(harness.Frame(new Vector2(700f, 500f), down: PointerButtons.Right));
        harness.EditorCamera.Yaw.ShouldBe(yaw);
    }

    private static Aabb SelectionBounds(ViewportHarness harness)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (SceneNode node in harness.Scene.Selection.Items)
        {
            harness.Scene.TryGetWorldBounds(node, out Aabb box).ShouldBeTrue();
            min = Vector3.Min(min, box.Min);
            max = Vector3.Max(max, box.Max);
        }
        return new Aabb(min, max);
    }

    private static Vector3[] Corners(in Aabb box) =>
    [
        new(box.Min.X, box.Min.Y, box.Min.Z), new(box.Max.X, box.Min.Y, box.Min.Z),
        new(box.Min.X, box.Max.Y, box.Min.Z), new(box.Max.X, box.Max.Y, box.Min.Z),
        new(box.Min.X, box.Min.Y, box.Max.Z), new(box.Max.X, box.Min.Y, box.Max.Z),
        new(box.Min.X, box.Max.Y, box.Max.Z), new(box.Max.X, box.Max.Y, box.Max.Z),
    ];
}

/// <summary>
/// Pixel-space assertions the viewport suites share — the screen-space
/// counterpart of <see cref="VectorAssertions"/>.
/// </summary>
internal static class PixelAssertions
{
    /// <summary>Distance in pixels, so a failure reads as "it drifted N pixels".</summary>
    public static void ShouldBeCloseTo(this Vector2 actual, Vector2 expected, float tolerance) =>
        (actual - expected).Length().ShouldBe(0f, tolerance,
            $"expected {expected} but was {actual}");
}
