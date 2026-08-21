using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Mode and orientation switching: which tool is live, that switching away from
/// a half-finished gesture abandons it cleanly, and that the world/local toggle
/// really re-aims the handles.
/// </summary>
/// <remarks>
/// <b>The leak this suite exists to catch is an orphaned undo transaction.</b>
/// Transactions do not nest, so a tool abandoned mid-drag would leave one open
/// and the next tool's first grab would throw — a bug that only shows up when a
/// user presses E while still holding the mouse, which is exactly the moment
/// nobody tests by hand.
/// </remarks>
public sealed class GizmoModeSwitchingTests
{
    private const float Tolerance = 1e-4f;

    [Fact]
    public void The_controller_starts_on_move_in_world_orientation()
    {
        var harness = GizmoHarness.ThreeQuarterView();

        harness.Gizmos.Mode.ShouldBe(GizmoMode.Translate);
        harness.Gizmos.Active.ShouldBeSameAs(harness.Translate);
        harness.Gizmos.Orientation.ShouldBe(GizmoOrientation.World);
    }

    [Theory]
    [InlineData(GizmoCommand.UseTranslate, GizmoMode.Translate)]
    [InlineData(GizmoCommand.UseRotate, GizmoMode.Rotate)]
    [InlineData(GizmoCommand.UseScale, GizmoMode.Scale)]
    public void Each_mode_verb_selects_its_tool(GizmoCommand command, GizmoMode expected)
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.Gizmos.Mode = GizmoMode.Rotate; // somewhere other than the default

        harness.Gizmos.Apply(command);

        harness.Gizmos.Mode.ShouldBe(expected);
        harness.Gizmos.Active.Mode.ShouldBe(expected);
    }

    [Fact]
    public void Cycling_walks_the_toolbar_order_and_wraps()
    {
        var harness = GizmoHarness.ThreeQuarterView();

        harness.Gizmos.Apply(GizmoCommand.CycleMode);
        harness.Gizmos.Mode.ShouldBe(GizmoMode.Rotate);
        harness.Gizmos.Apply(GizmoCommand.CycleMode);
        harness.Gizmos.Mode.ShouldBe(GizmoMode.Scale);
        harness.Gizmos.Apply(GizmoCommand.CycleMode);
        harness.Gizmos.Mode.ShouldBe(GizmoMode.Translate);
    }

    [Fact]
    public void Re_selecting_the_live_mode_reports_that_nothing_changed()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        int changes = 0;
        harness.Gizmos.ModeChanged += _ => changes++;

        harness.Gizmos.Apply(GizmoCommand.UseTranslate).ShouldBeFalse();
        changes.ShouldBe(0);

        harness.Gizmos.Apply(GizmoCommand.UseRotate).ShouldBeTrue();
        changes.ShouldBe(1);
    }

    [Fact]
    public void Switching_mode_mid_drag_abandons_the_gesture_and_restores_the_node()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        harness.Translate.Snap.Enabled = false;

        float length = harness.GeometryAt(Vector3.Zero).AxisLength;
        harness.Grab(Vector3.UnitX * (length * 0.8f)).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.DragBy(Vector3.UnitX * 5f);
        node.LocalPosition.X.ShouldBeGreaterThan(1f); // really moved

        harness.Gizmos.Mode = GizmoMode.Rotate;

        node.LocalPosition.ShouldBe(Vector3.Zero);
        harness.Undo.Count.ShouldBe(0);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
    }

    [Fact]
    public void The_abandoned_tool_keeps_no_drag_state()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(Vector3.Zero);
        harness.Translate.Snap.Enabled = false;

        float length = harness.GeometryAt(Vector3.Zero).AxisLength;
        harness.Grab(Vector3.UnitY * (length * 0.8f));
        harness.DragBy(Vector3.UnitY * 3f);
        harness.Translate.DragTargetCount.ShouldBe(1);

        harness.Gizmos.Mode = GizmoMode.Scale;

        harness.Translate.State.ShouldBe(GizmoInteractionState.Idle);
        harness.Translate.ActiveHandle.ShouldBe(GizmoHandle.None);
        harness.Translate.HoveredHandle.ShouldBe(GizmoHandle.None);
        harness.Translate.DragTargetCount.ShouldBe(0);
        harness.Translate.DragDelta.ShouldBe(Vector3.Zero);
        harness.Translate.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public void The_next_tool_can_open_its_own_transaction_immediately()
    {
        // The regression this whole suite is named for: an orphaned transaction
        // would make the very next grab throw, because transactions do not nest.
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);

        float length = harness.GeometryAt(Vector3.Zero).AxisLength;
        harness.Grab(Vector3.UnitX * (length * 0.8f)).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmos.Mode = GizmoMode.Rotate;

        RotateGizmoDragTests.Sweep(harness, GizmoHandle.AxisY, MathF.PI / 2f);

        harness.Undo.Count.ShouldBe(1);
        harness.Undo.UndoName.ShouldBe("Rotate");
        Vector3.Transform(Vector3.UnitZ, node.LocalRotation).ShouldBeCloseTo(Vector3.UnitX, 1e-3f);
    }

    [Fact]
    public void Switching_orientation_mid_drag_abandons_the_gesture_too()
    {
        // Same hazard, different door: a constraint re-aimed under the cursor
        // would drag the selection somewhere the user never pointed.
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        harness.Translate.Snap.Enabled = false;

        float length = harness.GeometryAt(Vector3.Zero).AxisLength;
        harness.Grab(Vector3.UnitX * (length * 0.8f));
        harness.DragBy(Vector3.UnitX * 4f);

        harness.Gizmos.Apply(GizmoCommand.ToggleOrientation).ShouldBeTrue();

        harness.Gizmos.Orientation.ShouldBe(GizmoOrientation.Local);
        node.LocalPosition.ShouldBe(Vector3.Zero);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
    }

    [Fact]
    public void The_orientation_toggle_reaches_move_and_rotate_but_not_resize()
    {
        var harness = GizmoHarness.ThreeQuarterView();

        harness.Gizmos.Apply(GizmoCommand.ToggleOrientation);

        harness.Translate.Orientation.ShouldBe(GizmoOrientation.Local);
        harness.Rotate.Orientation.ShouldBe(GizmoOrientation.Local);
        harness.Translate.SupportsOrientation.ShouldBeTrue();
        harness.Rotate.SupportsOrientation.ShouldBeTrue();
        harness.Scale.SupportsOrientation.ShouldBeFalse();

        harness.Gizmos.Apply(GizmoCommand.ToggleOrientation);
        harness.Gizmos.Orientation.ShouldBe(GizmoOrientation.World);
    }

    [Fact]
    public void Local_orientation_aims_the_handles_along_the_reference_node()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        node.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        harness.Hover(new Vector3(50f, 50f, 50f)); // off the gizmo; just builds geometry
        harness.Gizmo.Geometry.AxisX.ShouldBeCloseTo(Vector3.UnitX, Tolerance);

        harness.Gizmos.Orientation = GizmoOrientation.Local;
        harness.Hover(new Vector3(50f, 50f, 50f));

        // A quarter turn about +y takes the node's own +x onto world −z.
        harness.Gizmo.Geometry.AxisX.ShouldBeCloseTo(-Vector3.UnitZ, Tolerance);
        harness.Gizmo.Geometry.AxisY.ShouldBeCloseTo(Vector3.UnitY, Tolerance);
    }

    [Fact]
    public void A_local_axis_drag_moves_along_the_node_s_own_axis()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        // A quarter turn about +z takes the node's own +x onto world +y.
        node.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        harness.Gizmos.Orientation = GizmoOrientation.Local;
        harness.Translate.Snap.Enabled = false;

        harness.Hover(new Vector3(50f, 50f, 50f));
        GizmoGeometry geometry = harness.Gizmo.Geometry;
        Vector3 localX = geometry.AxisX;
        localX.ShouldBeCloseTo(Vector3.UnitY, Tolerance);

        harness.Grab(localX * (geometry.AxisLength * 0.8f)).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmo.ActiveHandle.ShouldBe(GizmoHandle.AxisX);
        harness.DragBy(localX * 3f);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);

        node.LocalPosition.ShouldBeCloseTo(new Vector3(0f, 3f, 0f), 1e-2f);
    }

    [Fact]
    public void A_local_snapped_drag_quantises_the_displacement_along_the_local_axis()
    {
        // There is no absolute world grid to land on in a rotated frame, so the
        // local mode snaps the TRAVEL instead of the destination.
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(new Vector3(0.3f, 0f, 0f));
        node.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        harness.Gizmos.Orientation = GizmoOrientation.Local;
        harness.Translate.Snap.Enabled = true;
        harness.Translate.Snap.Increment = 1f;

        harness.Hover(new Vector3(50f, 50f, 50f));
        GizmoGeometry geometry = harness.Gizmo.Geometry;
        Vector3 localX = geometry.AxisX;

        harness.Grab(node.LocalPosition + localX * (geometry.AxisLength * 0.8f));
        harness.DragBy(localX * 2.4f);
        harness.Release();

        // Exactly two units of travel along local x (world +y), and the off-axis
        // sub-grid offset the node started with is preserved rather than
        // quantised away — an absolute world snap would have pulled x to 0.
        node.LocalPosition.ShouldBeCloseTo(new Vector3(0.3f, 2f, 0f), 1e-2f);
    }

    [Fact]
    public void Cancel_as_a_verb_aborts_the_live_drag_and_reports_whether_it_did()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        harness.Translate.Snap.Enabled = false;

        harness.Gizmos.Apply(GizmoCommand.Cancel).ShouldBeFalse(); // nothing to cancel

        float length = harness.GeometryAt(Vector3.Zero).AxisLength;
        harness.Grab(Vector3.UnitZ * (length * 0.8f));
        harness.DragBy(Vector3.UnitZ * 6f);

        harness.Gizmos.Apply(GizmoCommand.Cancel).ShouldBeTrue();
        node.LocalPosition.ShouldBe(Vector3.Zero);
    }

    [Fact]
    public void Resetting_the_controller_clears_every_tool()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        harness.Translate.Snap.Enabled = false;

        float length = harness.GeometryAt(Vector3.Zero).AxisLength;
        harness.Grab(Vector3.UnitX * (length * 0.8f));
        harness.DragBy(Vector3.UnitX * 2f);

        harness.Gizmos.Reset();

        node.LocalPosition.ShouldBe(Vector3.Zero);
        harness.Translate.State.ShouldBe(GizmoInteractionState.Idle);
        harness.Rotate.State.ShouldBe(GizmoInteractionState.Idle);
        harness.Scale.State.ShouldBe(GizmoInteractionState.Idle);
        harness.Undo.IsTransactionOpen.ShouldBeFalse();
    }

    // --- Shortcuts -----------------------------------------------------------

    [Theory]
    [InlineData("W", GizmoCommand.UseTranslate)]
    [InlineData("E", GizmoCommand.UseRotate)]
    [InlineData("R", GizmoCommand.UseScale)]
    [InlineData("2", GizmoCommand.UseTranslate)]
    [InlineData("3", GizmoCommand.UseScale)]
    [InlineData("4", GizmoCommand.UseRotate)]
    [InlineData("X", GizmoCommand.ToggleOrientation)]
    [InlineData("G", GizmoCommand.ToggleSnap)]
    [InlineData("Escape", GizmoCommand.Cancel)]
    public void The_default_bindings_resolve(string key, GizmoCommand expected)
    {
        GizmoShortcuts.TryResolve(key, out GizmoCommand command).ShouldBeTrue();
        command.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Number2")]
    [InlineData("D2")]
    [InlineData("Keypad2")]
    [InlineData("w")]
    public void Key_names_are_matched_across_the_spellings_hosts_actually_use(string key)
    {
        // A Silk-hosted viewport says "Number2", WinUI says "Number2", a raw
        // character host says "2" — all the same key to the person pressing it.
        GizmoShortcuts.TryResolve(key, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("F13")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unbound_key_resolves_to_nothing(string? key) =>
        GizmoShortcuts.TryResolve(key, out _).ShouldBeFalse();

    [Fact]
    public void Every_documented_default_actually_resolves_to_what_it_claims()
    {
        // Keeps the help-overlay table and the resolver from drifting apart.
        foreach ((string key, GizmoCommand expected) in GizmoShortcuts.Defaults)
        {
            GizmoShortcuts.TryResolve(key, out GizmoCommand command).ShouldBeTrue(key);
            command.ShouldBe(expected, key);
        }
    }
}
