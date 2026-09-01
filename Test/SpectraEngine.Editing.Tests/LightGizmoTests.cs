using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Gizmos;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The light's own manipulator: its handles are where they are drawn, a drag is
/// one history entry, and a cancel restores exactly.
/// </summary>
/// <remarks>
/// The tool is a standalone class rather than a fourth <c>GizmoMode</c>, which
/// is what makes this suite possible without touching the mode-dependent
/// machinery at all: no <c>GizmoGeometry</c>, no handle roster, no style.
/// </remarks>
public sealed class LightGizmoTests
{
    private static (ViewportHarness Harness, SceneNode Lamp) BuildLamp(
        LightKind kind = LightKind.Point, float range = 6f)
    {
        var harness = new ViewportHarness();

        var lamp = new SceneNode("Lamp")
        {
            Light = new Light { Kind = kind, Range = range },
        };

        harness.Scene.Root.AddChild(lamp);
        harness.Scene.Camera.Position = new Vector3(0f, 0f, 20f);
        harness.Scene.Camera.LookAt(Vector3.Zero);
        harness.Scene.Selection.Select(lamp);

        return (harness, lamp);
    }

    // Through the harness's own builder, so these frames are constructed the
    // same way every other suite's are and cannot drift from the real capture.
    private static SpectraEngine.Editing.Input.EditorInputFrame At(
        ViewportHarness harness, Vector2 cursor, bool down = false, bool pressed = false)
        => harness.Frame(
            cursor,
            down: down ? PointerButtons.Left : PointerButtons.None,
            pressed: pressed ? PointerButtons.Left : PointerButtons.None);

    private static Vector2 Project(ViewportHarness harness, Vector3 world)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), harness.Scene.Camera.GetViewProjection());
        return new Vector2(
            ((clip.X / clip.W) + 1f) * 0.5f * harness.ViewportSize.X,
            (1f - (clip.Y / clip.W)) * 0.5f * harness.ViewportSize.Y);
    }

    // --- Where the handles are ----------------------------------------------

    [Fact]
    public void The_range_handle_is_where_it_is_drawn()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(range: 6f);
        var tool = new LightGizmo(harness.Scene, harness.Undo);

        // The knob sits along screen RIGHT at the light's range, which is what
        // makes the drag axis and the handle agree.
        Vector2 knob = Project(harness, lamp.WorldPosition + (harness.Scene.Camera.Right * 6f));

        tool.Pick(At(harness, knob), out SceneNode? node).ShouldBe(LightHandle.Range);
        node.ShouldBeSameAs(lamp);
    }

    [Fact]
    public void A_press_well_clear_of_the_handle_grabs_nothing()
    {
        (ViewportHarness harness, _) = BuildLamp();
        var tool = new LightGizmo(harness.Scene, harness.Undo);

        tool.Pick(At(harness, new Vector2(20f, 20f)), out SceneNode? node).ShouldBe(LightHandle.None);
        node.ShouldBeNull();
    }

    [Fact]
    public void A_sun_offers_an_aim_handle_and_no_range_handle()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(LightKind.Directional);
        var tool = new LightGizmo(harness.Scene, harness.Undo);

        // Where the RANGE handle would be for a point light of this range: a
        // directional light must refuse it, because a sun has no reach.
        Vector2 rangeSpot = Project(harness, lamp.WorldPosition + (harness.Scene.Camera.Right * 6f));
        tool.Pick(At(harness, rangeSpot), out _).ShouldBe(LightHandle.None);
    }

    [Fact]
    public void Nothing_is_offered_when_the_selection_is_not_a_single_light()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp();
        var tool = new LightGizmo(harness.Scene, harness.Undo);

        Vector2 knob = Project(harness, lamp.WorldPosition + (harness.Scene.Camera.Right * 6f));
        tool.Pick(At(harness, knob), out _).ShouldBe(LightHandle.Range);

        // A second light in the selection: a drag would have to decide whether
        // the ranges move together or converge, and both answers are wrong for
        // half the cases.
        var second = new SceneNode("Lamp2") { Light = new Light { Kind = LightKind.Point } };
        harness.Scene.Root.AddChild(second);
        harness.Scene.Selection.SetRange([lamp, second]);

        tool.Pick(At(harness, knob), out _).ShouldBe(LightHandle.None);
    }

    // --- Dragging ------------------------------------------------------------

    [Fact]
    public void One_drag_is_one_history_entry()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(range: 6f);
        var tool = new LightGizmo(harness.Scene, harness.Undo) { Snap = null };

        Vector2 knob = Project(harness, lamp.WorldPosition + (harness.Scene.Camera.Right * 6f));

        tool.Update(At(harness, knob, down: true, pressed: true), cancelRequested: false).ShouldBeTrue();

        // Several frames, as a real drag produces.
        for (int i = 1; i <= 5; i++)
            tool.Update(At(harness, knob + new Vector2(i * 8f, 0f), down: true), cancelRequested: false);

        tool.Update(At(harness, knob + new Vector2(40f, 0f)), cancelRequested: false).ShouldBeFalse();

        lamp.Light!.Range.ShouldBeGreaterThan(6f);
        harness.Undo.UndoCount.ShouldBe(1);
    }

    [Fact]
    public void Undoing_the_drag_restores_the_range_exactly()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(range: 6f);
        var tool = new LightGizmo(harness.Scene, harness.Undo) { Snap = null };

        Vector2 knob = Project(harness, lamp.WorldPosition + (harness.Scene.Camera.Right * 6f));

        tool.Update(At(harness, knob, down: true, pressed: true), cancelRequested: false);
        tool.Update(At(harness, knob + new Vector2(60f, 0f), down: true), cancelRequested: false);
        tool.Update(At(harness, knob + new Vector2(60f, 0f)), cancelRequested: false);

        harness.Undo.Undo();

        // Exactly, not nearly: the command carries absolute before/after values,
        // so bit equality is the right assertion rather than a tolerance.
        lamp.Light!.Range.ShouldBe(6f);
    }

    [Fact]
    public void Escape_restores_the_range_and_records_nothing()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(range: 6f);
        var tool = new LightGizmo(harness.Scene, harness.Undo) { Snap = null };

        Vector2 knob = Project(harness, lamp.WorldPosition + (harness.Scene.Camera.Right * 6f));

        tool.Update(At(harness, knob, down: true, pressed: true), cancelRequested: false);
        tool.Update(At(harness, knob + new Vector2(60f, 0f), down: true), cancelRequested: false);
        lamp.Light!.Range.ShouldBeGreaterThan(6f);

        tool.Update(At(harness, knob + new Vector2(60f, 0f), down: true), cancelRequested: true);

        lamp.Light.Range.ShouldBe(6f);
        harness.Undo.UndoCount.ShouldBe(0);
    }

    [Fact]
    public void Dragging_past_zero_stops_at_the_minimum_rather_than_throwing()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(range: 2f);
        var tool = new LightGizmo(harness.Scene, harness.Undo) { Snap = null };

        Vector2 knob = Project(harness, lamp.WorldPosition + (harness.Scene.Camera.Right * 2f));

        tool.Update(At(harness, knob, down: true, pressed: true), cancelRequested: false);

        // Far past zero. Light.Range throws on anything at or below it, so a
        // command carrying one would throw from inside Do, halfway through an
        // open transaction - the gizmo clamps instead, because running the
        // cursor past zero means "as small as it goes".
        tool.Update(At(harness, knob - new Vector2(2000f, 0f), down: true), cancelRequested: false);

        lamp.Light!.Range.ShouldBe(LightGizmo.MinimumRange);
    }

    [Fact]
    public void Every_drag_frame_recomputes_from_the_grab_rather_than_the_last_frame()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(range: 6f);
        var tool = new LightGizmo(harness.Scene, harness.Undo) { Snap = null };

        Vector2 knob = Project(harness, lamp.WorldPosition + (harness.Scene.Camera.Right * 6f));
        tool.Update(At(harness, knob, down: true, pressed: true), cancelRequested: false);

        tool.Update(At(harness, knob + new Vector2(50f, 0f), down: true), cancelRequested: false);
        float far = lamp.Light!.Range;

        // Wander away and come back to exactly the same cursor. Accumulating
        // per-frame deltas instead would leave residue here, and a snapped drag
        // would leave a lot of it.
        tool.Update(At(harness, knob + new Vector2(120f, 0f), down: true), cancelRequested: false);
        tool.Update(At(harness, knob + new Vector2(10f, 0f), down: true), cancelRequested: false);
        tool.Update(At(harness, knob + new Vector2(50f, 0f), down: true), cancelRequested: false);

        lamp.Light.Range.ShouldBe(far);
    }

    [Fact]
    public void Aiming_a_sun_writes_the_node_rather_than_the_light()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(LightKind.Directional);

        // Aimed SIDEWAYS, not at the camera. A sun whose travel points straight
        // at the eye puts its aim knob on top of its own icon - inherent to a
        // direction handle in 3D, and the reason this fixture turns it first.
        lamp.LocalTransform = lamp.LocalTransform with
        {
            Rotation = Light.RotationForDirection(-Vector3.UnitX),
        };

        var tool = new LightGizmo(harness.Scene, harness.Undo);

        Matrix4x4 before = lamp.WorldMatrix;
        var travelBefore = Vector3.Normalize(new Vector3(before.M31, before.M32, before.M33));

        // The knob sits at a CONSTANT SCREEN distance, so its world position
        // follows from the world-per-pixel at the lamp's own depth - the same
        // expression the tool uses, restated here rather than asked for, so the
        // test would notice the placement moving.
        float depth = Vector3.Dot(lamp.WorldPosition - harness.Scene.Camera.Position, harness.Scene.Camera.Forward);
        float worldPerPixel = GizmoMath.WorldPerPixel(harness.Scene.Camera, harness.ViewportSize.Y, depth);
        Vector3 knobWorld = lamp.WorldPosition + (travelBefore * LightGizmo.AimReachPixels * worldPerPixel);
        Vector2 knob = Project(harness, knobWorld);

        tool.Pick(At(harness, knob), out SceneNode? picked).ShouldBe(LightHandle.Aim);
        picked.ShouldBeSameAs(lamp);

        tool.Update(At(harness, knob, down: true, pressed: true), cancelRequested: false).ShouldBeTrue();
        tool.Update(At(harness, knob + new Vector2(0f, 120f), down: true), cancelRequested: false);
        tool.Update(At(harness, knob + new Vector2(0f, 120f)), cancelRequested: false);

        Matrix4x4 after = lamp.WorldMatrix;
        var travelAfter = Vector3.Normalize(new Vector3(after.M31, after.M32, after.M33));

        // Dragging the knob DOWN aims the sun downward, which is the whole
        // point: RotationForDirection takes the direction light TRAVELS, and
        // getting it backwards gives a sun shining up out of the ground -
        // silent, dark, and exactly what that method's remarks warn about.
        travelAfter.Y.ShouldBeLessThan(travelBefore.Y);

        // The light PAYLOAD is untouched: aim is the node's rotation, which is
        // where the engine reads a directional light's direction from.
        lamp.Light!.Kind.ShouldBe(LightKind.Directional);
        harness.Undo.UndoCount.ShouldBe(1);
    }

    // --- The shape handles ---------------------------------------------------

    [Theory]
    [InlineData(LightKind.Spot, LightHandle.ConeOuter)]
    [InlineData(LightKind.Spot, LightHandle.ConeInner)]
    [InlineData(LightKind.Rect, LightHandle.Width)]
    [InlineData(LightKind.Rect, LightHandle.Height)]
    [InlineData(LightKind.Disc, LightHandle.Radius)]
    public void Each_shape_offers_only_its_own_handles(LightKind kind, LightHandle offered)
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(kind);
        var tool = new LightGizmo(harness.Scene, harness.Undo);

        // Turned so the light's own basis is not edge-on to the camera; a knob
        // placed along the forward axis of a lamp pointing at the eye projects
        // onto the lamp itself, which is inherent and not what this checks.
        lamp.LocalTransform = lamp.LocalTransform with
        {
            Rotation = Light.RotationForDirection(Vector3.Normalize(new Vector3(-0.4f, -0.3f, -1f))),
        };

        // Every kind is walked at every pixel of a coarse grid, and the handle
        // this kind offers must be findable somewhere while the ones it does not
        // must be findable nowhere. A test that only probed the expected spot
        // would pass just as happily if EVERY kind offered EVERY handle.
        var found = new HashSet<LightHandle>();

        for (float x = 8f; x < harness.ViewportSize.X; x += 6f)
        {
            for (float y = 8f; y < harness.ViewportSize.Y; y += 6f)
                found.Add(tool.Pick(At(harness, new Vector2(x, y)), out _));
        }

        found.ShouldContain(offered, $"{kind} should offer {offered} somewhere on screen");

        foreach (LightHandle other in Enum.GetValues<LightHandle>())
        {
            if (other is LightHandle.None or LightHandle.Range || other == offered)
                continue;

            if (kind == LightKind.Spot && other is LightHandle.ConeInner or LightHandle.ConeOuter)
                continue;

            if (kind == LightKind.Rect && other is LightHandle.Width or LightHandle.Height)
                continue;

            found.ShouldNotContain(other, $"{kind} must not offer {other}");
        }
    }

    [Fact]
    public void Dragging_the_outer_cone_handle_widens_the_cone()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(LightKind.Spot);
        lamp.Light!.InnerAngle = 10f;
        lamp.Light.OuterAngle = 20f;

        var tool = new LightGizmo(harness.Scene, harness.Undo) { Snap = null, AngleSnap = null };

        Vector2 knob = FindHandle(harness, tool, LightHandle.ConeOuter);
        Vector2 axis = FindAxis(harness, tool, knob, LightHandle.ConeOuter);

        tool.Update(At(harness, knob, down: true, pressed: true), cancelRequested: false).ShouldBeTrue();
        tool.Update(At(harness, knob + (axis * 80f), down: true), cancelRequested: false);
        tool.Update(At(harness, knob + (axis * 80f)), cancelRequested: false);

        lamp.Light.OuterAngle.ShouldBeGreaterThan(20f);
        harness.Undo.UndoCount.ShouldBe(1);
    }

    [Fact]
    public void A_cone_dragged_past_the_ceiling_stops_there_rather_than_inverting()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(LightKind.Spot);
        var tool = new LightGizmo(harness.Scene, harness.Undo) { Snap = null, AngleSnap = null };

        Vector2 knob = FindHandle(harness, tool, LightHandle.ConeOuter);
        Vector2 axis = FindAxis(harness, tool, knob, LightHandle.ConeOuter);

        tool.Update(At(harness, knob, down: true, pressed: true), cancelRequested: false);

        // Far past 89 degrees. A half-angle beyond that is a cone pointing
        // backwards, which is not a shape anybody means - and unlike range it
        // is clamped rather than refused, because a drag should stop at the end
        // rather than throw from inside an open transaction.
        tool.Update(At(harness, knob + (axis * 5000f), down: true), cancelRequested: false);

        lamp.Light!.OuterAngle.ShouldBe(Light.MaxConeAngle);
    }

    [Fact]
    public void A_rect_lights_width_and_height_are_dragged_independently()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(LightKind.Rect);
        lamp.Light!.Width = 1f;
        lamp.Light.Height = 1f;

        var tool = new LightGizmo(harness.Scene, harness.Undo) { Snap = null, AngleSnap = null };

        Vector2 knob = FindHandle(harness, tool, LightHandle.Width);
        Vector2 axis = FindAxis(harness, tool, knob, LightHandle.Width);

        tool.Update(At(harness, knob, down: true, pressed: true), cancelRequested: false);
        tool.Update(At(harness, knob + (axis * 60f), down: true), cancelRequested: false);
        tool.Update(At(harness, knob + (axis * 60f)), cancelRequested: false);

        lamp.Light.Width.ShouldBeGreaterThan(1f);

        // The other axis is untouched: "make this panel wider" must not also
        // make it taller, which is the whole reason there are two handles.
        lamp.Light.Height.ShouldBe(1f);
    }

    // The first pixel at which the tool reports this handle. A search rather
    // than a computed position, because the knob's placement is the thing under
    // test and recomputing it here would be the same expression twice.
    private static Vector2 FindHandle(ViewportHarness harness, LightGizmo tool, LightHandle wanted)
    {
        for (float x = 4f; x < harness.ViewportSize.X; x += 3f)
        {
            for (float y = 4f; y < harness.ViewportSize.Y; y += 3f)
            {
                var at = new Vector2(x, y);
                if (tool.Pick(At(harness, at), out _) == wanted)
                    return at;
            }
        }

        throw new Xunit.Sdk.XunitException($"no pixel picks {wanted}");
    }

    // Which way to drag so the scalar GROWS: probe the four cardinals and keep
    // whichever still reports the same handle furthest out. Derived rather than
    // assumed, for the same reason the tool derives it.
    private static Vector2 FindAxis(
        ViewportHarness harness, LightGizmo tool, Vector2 knob, LightHandle handle)
    {
        Vector2 best = new(1f, 0f);
        float bestDistance = -1f;

        foreach (Vector2 direction in new[]
        {
            new Vector2(1f, 0f), new Vector2(-1f, 0f),
            new Vector2(0f, 1f), new Vector2(0f, -1f),
        })
        {
            // Walk outward until the handle stops answering; the direction that
            // stays on the knob longest is the one the knob travels along.
            float distance = 0f;
            for (float step = 2f; step <= 14f; step += 2f)
            {
                if (tool.Pick(At(harness, knob + (direction * step)), out _) != handle)
                    break;

                distance = step;
            }

            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = direction;
            }
        }

        return best;
    }

    [Fact]
    public void The_light_command_coalesces_so_a_drag_keeps_one_before_state()
    {
        (ViewportHarness harness, SceneNode lamp) = BuildLamp(range: 6f);

        var first = SetLightCommand.Capture(
            lamp, SetLightCommand.Settings.From(lamp.Light!) with { Range = 7f });

        var second = new SetLightCommand(
            lamp.Id,
            SetLightCommand.Settings.From(lamp.Light!) with { Range = 7f },
            SetLightCommand.Settings.From(lamp.Light!) with { Range = 9f });

        first.TryAbsorb(second).ShouldBeTrue();

        // The absorbed command's AFTER, and the original's BEFORE. Without that
        // the drag's undo would step back one frame at a time.
        first.After.Range.ShouldBe(9f);
        first.Before.Range.ShouldBe(6f);
    }
}
