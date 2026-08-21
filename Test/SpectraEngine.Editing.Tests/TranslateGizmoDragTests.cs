using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The drag state machine end to end: hovering, grabbing, moving the selection
/// along a constraint, and committing or cancelling — driven entirely through
/// simulated input frames, with no GPU anywhere.
/// </summary>
public sealed class TranslateGizmoDragTests
{
    // Well past the plane quads, so a grab aimed here can only be the shaft.
    private const float AlongAxis = 0.8f;

    // Slack for a value that made the round trip world → pixels → ray → world.
    // Two orders of magnitude below one grid step, so it can never disguise a
    // snapping or accumulation defect.
    private const float RoundTrip = 1e-3f;

    // --- The state machine ---------------------------------------------------

    [Fact]
    public void With_nothing_selected_the_gizmo_is_idle_and_invisible()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddNode(Vector3.Zero); // present but unselected

        harness.Hover(Vector3.Zero).ShouldBe(GizmoUpdateResult.None);

        harness.Gizmo.State.ShouldBe(GizmoInteractionState.Idle);
        harness.Gizmo.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public void The_state_walks_idle_to_hover_to_dragging_and_back()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(Vector3.Zero);
        float length = harness.GeometryAt(Vector3.Zero).AxisLength;

        // Cursor far from every handle: visible, but offering nothing.
        harness.Hover(new Vector3(-5f, -5f, -5f) * length).ShouldBe(GizmoUpdateResult.None);
        harness.Gizmo.State.ShouldBe(GizmoInteractionState.Idle);
        harness.Gizmo.IsVisible.ShouldBeTrue();

        harness.Hover(Vector3.UnitX * (length * AlongAxis)).ShouldBe(GizmoUpdateResult.Hovering);
        harness.Gizmo.State.ShouldBe(GizmoInteractionState.Hovering);
        harness.Gizmo.HoveredHandle.ShouldBe(GizmoHandle.AxisX);

        harness.Grab(Vector3.UnitX * (length * AlongAxis)).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmo.State.ShouldBe(GizmoInteractionState.Dragging);
        harness.Gizmo.ActiveHandle.ShouldBe(GizmoHandle.AxisX);

        harness.DragBy(Vector3.UnitX * 3f).ShouldBe(GizmoUpdateResult.DragUpdated);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);
        harness.Gizmo.State.ShouldBe(GizmoInteractionState.Idle);
        harness.Gizmo.ActiveHandle.ShouldBe(GizmoHandle.None);
    }

    [Fact]
    public void A_press_away_from_every_handle_starts_no_drag()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        float length = harness.GeometryAt(Vector3.Zero).AxisLength;

        harness.Grab(new Vector3(-4f, -4f, -4f) * length).ShouldBe(GizmoUpdateResult.None);

        harness.Gizmo.State.ShouldBe(GizmoInteractionState.Idle);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
        node.LocalPosition.ShouldBe(Vector3.Zero);
    }

    // --- Axis drags ----------------------------------------------------------

    [Theory]
    [InlineData(GizmoHandle.AxisX)]
    [InlineData(GizmoHandle.AxisY)]
    [InlineData(GizmoHandle.AxisZ)]
    public void An_axis_drag_moves_the_node_exactly_the_dragged_distance(GizmoHandle handle)
    {
        const float distance = 5.37f;

        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false; // measuring the raw mapping
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);

        Vector3 axis = GizmoHandles.AxisDirection(handle);
        float length = harness.GeometryAt(Vector3.Zero).AxisLength;

        harness.Grab(axis * (length * AlongAxis)).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmo.ActiveHandle.ShouldBe(handle);

        harness.DragBy(axis * distance);
        harness.Release();

        ShouldBeClose(node.LocalPosition, axis * distance);
    }

    [Fact]
    public void An_axis_drag_moves_along_no_other_axis()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        float length = harness.GeometryAt(Vector3.Zero).AxisLength;

        harness.Grab(Vector3.UnitX * (length * AlongAxis));
        // Cursor movement with components on every axis; the constraint must
        // throw away everything but x.
        harness.DragBy(new Vector3(4f, 9f, -7f));
        harness.Release();

        // Exact, not approximate: an axis constraint solves on the line
        // pivot + t·x, whose y and z components are the pivot's own untouched
        // floats — so a leak on those axes is a defect, never rounding.
        node.LocalPosition.Y.ShouldBe(0f);
        node.LocalPosition.Z.ShouldBe(0f);
        node.LocalPosition.X.ShouldNotBe(0f);
    }

    [Fact]
    public void A_long_wandering_drag_leaves_no_residue_when_the_cursor_comes_home()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;
        var start = new Vector3(3f, -2f, 1f);
        SceneNode node = harness.AddSelectedNode(start);
        float length = harness.GeometryAt(start).AxisLength;

        harness.Grab(start + Vector3.UnitY * (length * AlongAxis));

        // Three hundred frames wandering back and forth over a wide range,
        // then back to the exact pixel the gesture was grabbed at.
        for (int i = 0; i < 300; i++)
            harness.DragBy(Vector3.UnitY * (MathF.Sin(i * 0.37f) * 12f));
        harness.DragBy(Vector3.Zero);

        // BIT-FOR-BIT equal to the captured start: the final delta is exactly
        // zero and the write is the captured local position itself, so a drag
        // that comes home is perfectly reversible however long it wandered.
        // (This is the round-trip identity, not an accumulation probe — a
        // telescoping delta implementation can also land exactly here. What
        // catches accumulation is
        // The_result_of_a_drag_depends_only_on_where_the_cursor_ended_up.)
        node.LocalPosition.ShouldBe(start);

        // A gesture that ends where it started is a click, not an edit.
        harness.Release().ShouldBe(GizmoUpdateResult.DragCancelled);
        harness.Undo.Count.ShouldBe(0);
    }

    [Fact]
    public void The_result_of_a_drag_depends_only_on_where_the_cursor_ended_up()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;
        SceneNode wandering = harness.AddSelectedNode(Vector3.Zero, "Wandering");
        float length = harness.GeometryAt(Vector3.Zero).AxisLength;

        harness.Grab(Vector3.UnitX * (length * AlongAxis));
        for (int i = 0; i < 300; i++)
            harness.DragBy(Vector3.UnitX * (MathF.Sin(i * 0.37f) * 12f));
        harness.DragBy(Vector3.UnitX * 2.5f);
        harness.Release();
        Vector3 afterWandering = wandering.LocalPosition;

        // The same gesture in a single frame must land in exactly the same
        // place: the path taken is not part of the answer.
        var direct = GizmoHarness.ThreeQuarterView();
        direct.Translate.Snap.Enabled = false;
        SceneNode straight = direct.AddSelectedNode(Vector3.Zero, "Straight");
        direct.Grab(Vector3.UnitX * (length * AlongAxis));
        direct.DragBy(Vector3.UnitX * 2.5f);
        direct.Release();

        afterWandering.ShouldBe(straight.LocalPosition);
    }

    // --- Plane and screen drags ---------------------------------------------

    [Theory]
    [InlineData(GizmoHandle.PlaneYZ)]
    [InlineData(GizmoHandle.PlaneZX)]
    [InlineData(GizmoHandle.PlaneXY)]
    public void A_plane_drag_stays_in_its_plane(GizmoHandle handle)
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;
        var start = new Vector3(1f, 2f, 3f);
        SceneNode node = harness.AddSelectedNode(start);

        GizmoGeometry geometry = harness.GeometryAt(start);
        geometry.TryGetPlaneQuad(handle, out Vector3 corner, out Vector3 first, out Vector3 second, out float size)
            .ShouldBeTrue();

        harness.Grab(corner + (first + second) * (size * 0.5f)).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmo.ActiveHandle.ShouldBe(handle);

        // Push in all three directions; only the two the quad spans may answer.
        harness.DragBy(new Vector3(6f, -4f, 5f));
        harness.Release();

        Vector3 normal = GizmoHandles.PlaneNormal(handle);
        Vector3 moved = node.LocalPosition - start;

        // No movement along the plane's normal...
        Vector3.Dot(moved, normal).ShouldBe(0f, RoundTrip);
        // ...and real movement within it, so the test cannot pass by standing still.
        (moved - normal * Vector3.Dot(moved, normal)).Length().ShouldBeGreaterThan(1f);
    }

    [Fact]
    public void The_screen_handle_drags_in_the_camera_facing_plane()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);

        harness.Grab(Vector3.Zero).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmo.ActiveHandle.ShouldBe(GizmoHandle.Screen);

        harness.DragBy(new Vector3(5f, 4f, -3f));
        harness.Release();

        // The constraint plane is the camera's, frozen at the grab: the
        // movement is perpendicular to the view axis, and the selection keeps
        // its depth rather than sliding toward or away from the viewer.
        Vector3 forward = harness.Scene.Camera.Forward;
        Vector3.Dot(node.LocalPosition, forward).ShouldBe(0f, RoundTrip);
        node.LocalPosition.Length().ShouldBeGreaterThan(1f);
    }

    // --- Cancel --------------------------------------------------------------

    [Fact]
    public void Escape_during_a_drag_restores_the_exact_starting_transform()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        var start = new Vector3(1.7f, -0.35f, 4.125f);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f);
        SceneNode node = harness.AddSelectedNode(start);
        node.LocalRotation = rotation;

        float length = harness.GeometryAt(start).AxisLength;
        harness.Grab(start + Vector3.UnitX * (length * AlongAxis));
        harness.DragBy(Vector3.UnitX * 9f);
        node.LocalPosition.ShouldNotBe(start); // it really did move first

        harness.PressEscape().ShouldBe(GizmoUpdateResult.DragCancelled);

        // Exact equality: cancelling replays the absolute values captured at
        // the grab, so an off-grid start comes back off-grid, unrounded.
        node.LocalPosition.ShouldBe(start);
        node.LocalRotation.ShouldBe(rotation);
        harness.Undo.Count.ShouldBe(0);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
        harness.Gizmo.State.ShouldBe(GizmoInteractionState.Idle);
    }

    [Fact]
    public void A_right_click_during_a_drag_cancels_it_too()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        var start = new Vector3(2f, 0f, 0f);
        SceneNode node = harness.AddSelectedNode(start);
        float length = harness.GeometryAt(start).AxisLength;

        harness.Grab(start + Vector3.UnitZ * (length * AlongAxis));
        harness.DragBy(Vector3.UnitZ * 6f);
        harness.RightClick().ShouldBe(GizmoUpdateResult.DragCancelled);

        node.LocalPosition.ShouldBe(start);
        harness.Undo.Count.ShouldBe(0);
    }

    [Fact]
    public void Cancelling_out_of_band_restores_the_transform_as_well()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        var start = new Vector3(-3f, 1f, 2f);
        SceneNode node = harness.AddSelectedNode(start);
        float length = harness.GeometryAt(start).AxisLength;

        harness.Grab(start + Vector3.UnitX * (length * AlongAxis));
        harness.DragBy(Vector3.UnitX * 4f);

        // The host's escape hatch for a lost focus or a scene reload — no input
        // frame involved.
        harness.Gizmo.CancelDrag().ShouldBeTrue();

        node.LocalPosition.ShouldBe(start);
        harness.Gizmo.CancelDrag().ShouldBeFalse(); // nothing left to cancel
    }

    // --- Undo ----------------------------------------------------------------

    [Fact]
    public void A_multi_frame_drag_lands_as_exactly_one_undo_entry()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;
        var start = new Vector3(4f, 0f, 0f);
        SceneNode node = harness.AddSelectedNode(start);
        float length = harness.GeometryAt(start).AxisLength;

        harness.Grab(start + Vector3.UnitX * (length * AlongAxis));
        for (int i = 1; i <= 60; i++)
            harness.DragBy(Vector3.UnitX * (i * 0.1f));
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);

        harness.Undo.Count.ShouldBe(1);
        harness.Undo.UndoName.ShouldBe("Move");
        ShouldBeClose(node.LocalPosition, start + new Vector3(6f, 0f, 0f));

        // One undo unwinds the whole sixty-frame gesture, back to the exact
        // captured start.
        harness.Undo.Undo().ShouldBeTrue();
        node.LocalPosition.ShouldBe(start);
        harness.Undo.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void Two_gestures_stay_two_undo_entries()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);

        DragAlongX(harness, node, 3f);
        DragAlongX(harness, node, 2f);

        harness.Undo.Count.ShouldBe(2);
    }

    // --- Multi-selection -----------------------------------------------------

    [Fact]
    public void A_multi_selection_moves_rigidly_and_keeps_its_relative_offsets()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;

        var startA = new Vector3(-2f, 0f, 1f);
        var startB = new Vector3(4f, 3f, -1f);
        var startC = new Vector3(1f, -3f, 2f);
        SceneNode a = harness.AddSelectedNode(startA, "A");
        SceneNode b = harness.AddSelectedNode(startB, "B");
        SceneNode c = harness.AddSelectedNode(startC, "C");

        // The gizmo sits at the average of the selection, not on any one node.
        Vector3 pivot = (startA + startB + startC) / 3f;
        harness.Hover(pivot);
        ShouldBeClose(harness.Gizmo.Pivot, pivot);

        float length = harness.GeometryAt(pivot).AxisLength;
        harness.Grab(pivot + Vector3.UnitY * (length * AlongAxis));
        harness.DragBy(Vector3.UnitY * 8f);
        harness.Release();

        var expected = new Vector3(0f, 8f, 0f);
        ShouldBeClose(a.LocalPosition, startA + expected);
        ShouldBeClose(b.LocalPosition, startB + expected);
        ShouldBeClose(c.LocalPosition, startC + expected);

        // Rigid: every pairwise offset survives the move unchanged.
        ShouldBeClose(b.LocalPosition - a.LocalPosition, startB - startA);
        ShouldBeClose(c.LocalPosition - a.LocalPosition, startC - startA);

        // Three nodes, one gesture, one history entry.
        harness.Undo.Count.ShouldBe(1);
        harness.Undo.Undo().ShouldBeTrue();
        a.LocalPosition.ShouldBe(startA);
        b.LocalPosition.ShouldBe(startB);
        c.LocalPosition.ShouldBe(startC);
    }

    [Fact]
    public void A_selected_child_of_a_selected_parent_is_not_moved_twice()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;

        SceneNode parent = harness.AddSelectedNode(new Vector3(0f, 0f, 0f), "Parent");
        SceneNode child = parent.CreateChild("Child");
        child.LocalPosition = new Vector3(2f, 0f, 0f);
        harness.Scene.Selection.Add(child);

        harness.Hover(new Vector3(1f, 0f, 0f)); // average of (0,0,0) and (2,0,0)
        float length = harness.GeometryAt(harness.Gizmo.Pivot).AxisLength;

        harness.Grab(harness.Gizmo.Pivot + Vector3.UnitY * (length * AlongAxis));
        harness.Gizmo.DragTargetCount.ShouldBe(1); // the parent carries the child
        harness.DragBy(Vector3.UnitY * 5f);
        harness.Release();

        // The parent moved by five; the child rode along and its LOCAL position
        // is untouched. Moving both would have put the child ten units up.
        ShouldBeClose(parent.LocalPosition, new Vector3(0f, 5f, 0f));
        child.LocalPosition.ShouldBe(new Vector3(2f, 0f, 0f));
        ShouldBeClose(child.WorldPosition, new Vector3(2f, 5f, 0f));
    }

    [Fact]
    public void A_child_of_a_rotated_parent_moves_by_the_world_delta()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Translate.Snap.Enabled = false;

        SceneNode parent = harness.AddNode(Vector3.Zero, "Parent");
        // A quarter turn about y maps the parent's local +x onto world −z.
        parent.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        SceneNode child = parent.CreateChild("Child");
        child.LocalPosition = new Vector3(1f, 0f, 0f);
        harness.Scene.Selection.Add(child);

        Vector3 startWorld = child.WorldPosition;
        float length = harness.GeometryAt(startWorld).AxisLength;

        harness.Grab(startWorld + Vector3.UnitX * (length * AlongAxis));
        harness.DragBy(Vector3.UnitX * 4f);
        harness.Release();

        // The user dragged along WORLD x, so the child must end four units
        // along world x — the parent's rotation is the gizmo's problem, not the
        // user's.
        ShouldBeClose(child.WorldPosition, startWorld + new Vector3(4f, 0f, 0f));
    }

    // --- Grazing constraints -------------------------------------------------

    [Fact]
    public void The_plane_and_line_projections_refuse_at_the_same_angle()
    {
        // Both guards are a squared sine of the angle between the ray and the
        // constraint surface, so the same direction must be accepted by both or
        // refused by both. A plane guard written on |cos| instead of cos² would
        // be ~30x looser in angle, and the loose side amplifies a one-pixel
        // cursor move by up to 1/|cos| — the "selection flew to the horizon"
        // report this pair exists to prevent.
        ShouldAgreeAtDegrees(1.9f, accepted: true);
        ShouldAgreeAtDegrees(1.7f, accepted: false);

        static void ShouldAgreeAtDegrees(float degrees, bool accepted)
        {
            float radians = degrees * MathF.PI / 180f;
            var direction = new Vector3(MathF.Cos(radians), MathF.Sin(radians), 0f);
            var ray = new Ray3(new Vector3(0f, -1f, 0f), direction);

            // Plane y = 0: the ray meets it at exactly `degrees`.
            GizmoMath.TryRayPlane(in ray, Vector3.Zero, Vector3.UnitY, out _).ShouldBe(accepted);
            // Line along x: the ray meets it at exactly `degrees` too.
            GizmoMath.TryClosestPointOnLine(in ray, Vector3.Zero, Vector3.UnitX, out _).ShouldBe(accepted);
        }
    }

    [Fact]
    public void Dragging_a_floor_quad_toward_the_horizon_cannot_fling_the_selection()
    {
        // A completely ordinary view: the camera just above the floor plane,
        // looking along it. Sliding the cursor up toward the horizon makes the
        // ray graze the constraint, and the projection runs away as 1/|cos| —
        // measured at 745 units before this guard was tightened, from where the
        // drag froze and the user could not get back.
        var harness = new GizmoHarness(new Vector3(0f, 2f, 40f), new Vector3(0f, 1.9f, 0f));
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        harness.Translate.Snap.Enabled = false;

        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        geometry.TryGetPlaneQuad(GizmoHandle.PlaneZX, out Vector3 corner, out Vector3 u, out Vector3 v, out float size)
            .ShouldBeTrue();
        Vector3 grabAim = corner + (u + v) * (size * 0.5f);

        harness.Grab(grabAim).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmo.ActiveHandle.ShouldBe(GizmoHandle.PlaneZX);

        Vector2 grabPixel = harness.WorldToScreen(grabAim);
        float furthest = 0f;
        for (int dy = 1; dy <= 120; dy++)
        {
            harness.Gizmos.Update(harness.Frame(
                grabPixel - new Vector2(0f, dy), down: PointerButtons.Left));
            furthest = MathF.Max(furthest, node.WorldPosition.Length());
        }

        // The bound is geometric, not a magic number: the eye sits 2 units above
        // the constraint plane and the refusal bites at sin ≈ 0.0316, so no
        // accepted intersection can be further than 2 / 0.0316 ≈ 63 units along
        // the ray — barely more than the 40-unit camera distance itself.
        // Measured: 23.37 units here, against 746.01 before the guard was
        // brought into line with the line projection's.
        furthest.ShouldBeLessThan(30f);

        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);
        harness.Undo.UndoName.ShouldBe("Move");
    }

    // --- Helpers -------------------------------------------------------------

    private static void DragAlongX(GizmoHarness harness, SceneNode node, float distance)
    {
        float length = harness.GeometryAt(node.WorldPosition).AxisLength;
        harness.Hover(node.WorldPosition + Vector3.UnitX * (length * AlongAxis));
        harness.Grab(node.WorldPosition + Vector3.UnitX * (length * AlongAxis));
        harness.DragBy(Vector3.UnitX * distance);
        harness.Release();
    }

    private static void ShouldBeClose(Vector3 actual, Vector3 expected)
    {
        actual.X.ShouldBe(expected.X, RoundTrip);
        actual.Y.ShouldBe(expected.Y, RoundTrip);
        actual.Z.ShouldBe(expected.Z, RoundTrip);
    }
}
