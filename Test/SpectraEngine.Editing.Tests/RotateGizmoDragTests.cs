using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// What a rotate drag actually does to the scene: the angle it derives from the
/// cursor, the orientation it writes, the way a multi-selection orbits its shared
/// pivot, and the exactness of a cancel.
/// </summary>
/// <remarks>
/// <b>Angles are constructed, not measured.</b> Every gesture aims the cursor at
/// a world point that lies exactly on the ring, at a known parametric angle in
/// the ring's own plane; the projection the gizmo runs is the inverse of the one
/// the harness runs, so "sweep 40°" really is 40° and the assertions can be tight
/// rather than eyeballed.
/// </remarks>
public sealed class RotateGizmoDragTests
{
    // Enough that a wrong sign or a swapped basis vector cannot pass, and not a
    // multiple of the default snap increment, so a test that forgot to disable
    // snapping fails loudly instead of quietly rounding into agreement.
    private const float SweepDegrees = 37f;

    private const float Tolerance = 1e-3f;

    [Theory]
    [InlineData(GizmoHandle.AxisX)]
    [InlineData(GizmoHandle.AxisY)]
    [InlineData(GizmoHandle.AxisZ)]
    public void A_drag_around_a_ring_turns_the_node_by_the_swept_angle(GizmoHandle handle)
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        RotateGizmo rotate = Rotate(harness);
        rotate.Snap.Enabled = false;

        float radians = SweepDegrees * MathF.PI / 180f;
        Ring ring = GrabRing(harness, handle);
        rotate.ActiveHandle.ShouldBe(handle);

        ring.DragToAngle(harness, radians);
        rotate.DragAngleDegrees.ShouldBe(SweepDegrees, 0.05f);
        harness.Release();

        // The node's orientation must BE the rotation about that world axis —
        // checked by how it moves two independent directions, which pins the
        // quaternion up to nothing.
        Vector3 axis = GizmoHandles.AxisDirection(handle);
        var expected = Quaternion.CreateFromAxisAngle(axis, radians);
        ShouldRotateLike(node.LocalRotation, expected);
    }

    [Fact]
    public void A_quarter_turn_about_y_carries_forward_onto_the_x_axis()
    {
        // Pins the handedness and the composition order in a form a reader can
        // check against the right-hand rule without trusting the helper above:
        // +90° about +y takes +z to +x.
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        Rotate(harness).Snap.Enabled = false;

        Sweep(harness, GizmoHandle.AxisY, MathF.PI / 2f);

        Vector3.Transform(Vector3.UnitZ, node.LocalRotation)
            .ShouldBeCloseTo(Vector3.UnitX, Tolerance);
        Vector3.Transform(Vector3.UnitX, node.LocalRotation)
            .ShouldBeCloseTo(-Vector3.UnitZ, Tolerance);
    }

    [Fact]
    public void A_multi_selection_orbits_the_shared_pivot_as_well_as_spinning()
    {
        // Two nodes straddling the origin: their pivot is the origin, so a
        // quarter turn about y must swap them onto the z axis — the difference
        // between rotating a group and spinning each member in place.
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode left = harness.AddSelectedNode(new Vector3(-2f, 0f, 0f), "Left");
        SceneNode right = harness.AddSelectedNode(new Vector3(2f, 0f, 0f), "Right");
        Rotate(harness).Snap.Enabled = false;

        Sweep(harness, GizmoHandle.AxisY, MathF.PI / 2f);

        right.LocalPosition.ShouldBeCloseTo(new Vector3(0f, 0f, -2f), Tolerance);
        left.LocalPosition.ShouldBeCloseTo(new Vector3(0f, 0f, 2f), Tolerance);

        // And both are also turned, not merely carried.
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        ShouldRotateLike(left.LocalRotation, expected);
        ShouldRotateLike(right.LocalRotation, expected);
    }

    [Fact]
    public void A_single_selection_spins_in_place_without_drifting()
    {
        // The degenerate case of the orbit: the pivot IS the node's position, so
        // the position arithmetic must return it bit-for-bit rather than nearly.
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode node = harness.AddSelectedNode(new Vector3(3f, -1f, 4f));
        Rotate(harness).Snap.Enabled = false;

        Sweep(harness, GizmoHandle.AxisZ, 1.1f);

        node.LocalPosition.ShouldBe(new Vector3(3f, -1f, 4f));
    }

    [Fact]
    public void The_view_ring_turns_about_the_camera_axis()
    {
        var harness = GizmoHarness.FrontView();
        SceneNode node = harness.AddSelectedNode(Vector3.Zero);
        RotateGizmo rotate = Rotate(harness);
        rotate.Snap.Enabled = false;

        Ring ring = GrabRing(harness, GizmoHandle.Screen);
        rotate.ActiveHandle.ShouldBe(GizmoHandle.Screen);
        rotate.DragAxis.ShouldBeCloseTo(harness.Scene.Camera.Forward, Tolerance);

        // Swept from the camera's right vector toward its up vector — anti-
        // clockwise on screen.
        ring.DragToAngle(harness, 0.4f);

        // Which is a rotation about the axis pointing OUT of the screen, i.e.
        // −forward. The gizmo reports the same turn signed about its own axis
        // (forward), so the reported angle is the negative one.
        rotate.DragAngle.ShouldBe(-0.4f, 1e-3f);
        harness.Release();

        var expected = Quaternion.CreateFromAxisAngle(-harness.Scene.Camera.Forward, 0.4f);
        ShouldRotateLike(node.LocalRotation, expected);
    }

    [Fact]
    public void A_drag_past_half_a_turn_keeps_going_instead_of_flipping_sign()
    {
        // The winding number is the one piece of per-frame state the tool keeps;
        // without it the angle would wrap at ±180° and a three-quarter turn would
        // read as a quarter turn the other way.
        var harness = GizmoHarness.FrontView();
        harness.AddSelectedNode(Vector3.Zero);
        RotateGizmo rotate = Rotate(harness);
        rotate.Snap.Enabled = false;

        Ring ring = GrabRing(harness, GizmoHandle.AxisZ);

        // Walked in steps small enough that no single frame jumps half a turn,
        // which is exactly the assumption the unwrap makes — and exactly how a
        // real mouse arrives.
        for (int step = 1; step <= 12; step++)
            ring.DragToAngle(harness, MathF.Tau * 0.75f * step / 12f);

        rotate.DragAngleDegrees.ShouldBe(270f, 0.2f);
    }

    [Fact]
    public void A_cancelled_rotation_restores_every_node_exactly()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode a = harness.AddSelectedNode(new Vector3(-2f, 0f, 0f), "A");
        SceneNode b = harness.AddSelectedNode(new Vector3(2f, 1f, 0f), "B");
        Rotate(harness).Snap.Enabled = false;

        Transform beforeA = a.LocalTransform;
        Transform beforeB = b.LocalTransform;

        Ring ring = GrabRing(harness, GizmoHandle.AxisY);
        ring.DragToAngle(harness, 0.9f);
        a.LocalPosition.ShouldNotBe(beforeA.Position); // the drag really did move it

        harness.PressEscape().ShouldBe(GizmoUpdateResult.DragCancelled);

        // Bit-for-bit: the cancel replays the captured start values, it does not
        // rotate back by the negative angle.
        a.LocalPosition.ShouldBe(beforeA.Position);
        a.LocalRotation.ShouldBe(beforeA.Rotation);
        b.LocalPosition.ShouldBe(beforeB.Position);
        b.LocalRotation.ShouldBe(beforeB.Rotation);
        harness.Undo.Count.ShouldBe(0);
    }

    [Fact]
    public void A_committed_rotation_lands_one_history_entry_that_undoes_whole()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode a = harness.AddSelectedNode(new Vector3(-2f, 0f, 0f), "A");
        SceneNode b = harness.AddSelectedNode(new Vector3(2f, 0f, 0f), "B");
        Rotate(harness).Snap.Enabled = false;

        Vector3 beforeA = a.LocalPosition;
        Vector3 beforeB = b.LocalPosition;

        Sweep(harness, GizmoHandle.AxisY, 0.7f);

        harness.Undo.Count.ShouldBe(1);
        harness.Undo.UndoName.ShouldBe("Rotate");

        harness.Undo.Undo().ShouldBeTrue();
        a.LocalPosition.ShouldBe(beforeA);
        b.LocalPosition.ShouldBe(beforeB);
        a.LocalRotation.ShouldBe(Quaternion.Identity);
    }

    [Fact]
    public void A_grab_that_never_sweeps_commits_nothing()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(Vector3.Zero);
        Rotate(harness);

        GrabRing(harness, GizmoHandle.AxisZ);
        harness.Release().ShouldBe(GizmoUpdateResult.DragCancelled);
        harness.Undo.Count.ShouldBe(0);
    }

    [Fact]
    public void A_selected_descendant_of_a_selected_node_is_not_rotated_twice()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        SceneNode parent = harness.AddSelectedNode(Vector3.Zero, "Parent");
        SceneNode child = parent.CreateChild("Child");
        child.LocalPosition = new Vector3(0f, 0f, 2f);
        harness.Scene.Selection.Add(child);
        Rotate(harness).Snap.Enabled = false;

        Sweep(harness, GizmoHandle.AxisY, MathF.PI / 2f);

        // The parent carries the child; the child's own local transform must be
        // untouched, or the subtree would shear apart.
        child.LocalPosition.ShouldBe(new Vector3(0f, 0f, 2f));
        child.LocalRotation.ShouldBe(Quaternion.Identity);
        harness.Gizmo.DragTargetCount.ShouldBe(0); // cleared on commit
    }

    // --- Helpers -------------------------------------------------------------

    private static RotateGizmo Rotate(GizmoHarness harness)
    {
        harness.Use(GizmoMode.Rotate);
        return harness.Rotate;
    }

    /// <summary>
    /// One grabbed ring: the pivot it turns about, its in-plane basis, and its
    /// radius — everything needed to aim the cursor at a chosen angle on it.
    /// </summary>
    /// <remarks>
    /// The pivot is captured once, at the grab, which is correct because a
    /// rotation does not move it: the selection turns around where it already
    /// was.
    /// </remarks>
    internal readonly record struct Ring(Vector3 Pivot, Vector3 U, Vector3 V, float Radius)
    {
        /// <summary>The world point at <paramref name="angle"/> around this ring.</summary>
        public Vector3 PointAt(float angle) =>
            Pivot + (U * MathF.Cos(angle) + V * MathF.Sin(angle)) * Radius;

        /// <summary>Aims the cursor <paramref name="sweep"/> radians on from the grab point.</summary>
        public GizmoUpdateResult DragToAngle(
            GizmoHarness harness, float sweep, KeyModifiers modifiers = KeyModifiers.None) =>
            harness.DragTo(PointAt(GrabAngle + sweep), modifiers);
    }

    // 45° along the ring's own plane: a point no other ring passes through (each
    // pair of axis rings meets only on the axes), so the pick is unambiguous.
    private const float GrabAngle = MathF.PI / 4f;

    private static Ring GrabRing(GizmoHarness harness, GizmoHandle handle)
    {
        // One update with the cursor outside the viewport: it builds the gizmo's
        // geometry (and so publishes the selection's pivot) without claiming a
        // hover, which is what lets the aim below be computed against the pivot
        // the gizmo actually chose rather than against a guess.
        harness.Gizmos.Update(harness.Frame(new Vector2(-10f, -10f)));
        Vector3 pivot = harness.Gizmo.Pivot;
        GizmoGeometry geometry = harness.GeometryAt(pivot);

        Vector3 u, v;
        float radius;
        if (handle == GizmoHandle.Screen)
        {
            u = geometry.ViewRight;
            v = geometry.ViewUp;
            radius = geometry.ScreenRingRadius;
        }
        else
        {
            geometry.AxisPerpendiculars(handle, out u, out v);
            radius = geometry.RingRadius;
        }

        var ring = new Ring(pivot, u, v, radius);

        harness.Hover(ring.PointAt(GrabAngle));
        harness.Gizmo.HoveredHandle.ShouldBe(handle);
        harness.Grab(ring.PointAt(GrabAngle)).ShouldBe(GizmoUpdateResult.DragBegan);
        return ring;
    }

    /// <summary>Grab, sweep in one frame, release.</summary>
    internal static void Sweep(
        GizmoHarness harness, GizmoHandle handle, float radians, KeyModifiers modifiers = KeyModifiers.None)
    {
        Ring ring = GrabRing(harness, handle);
        ring.DragToAngle(harness, radians, modifiers);
        harness.Release(modifiers);
    }

    // --- Picking promises only what a press can keep -------------------------

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(180f)]
    [InlineData(270f)]
    public void An_edge_on_ring_never_steals_the_pick_from_the_ring_facing_the_camera(float degrees)
    {
        // Straight down the z axis: the z ring is face-on and the x and y rings
        // are edge-on, so each of them projects to a LINE through the pivot and
        // wins the pixel-distance tie-break wherever it crosses the z ring —
        // which is exactly at the four cardinal points a user aims at. The press
        // was then refused (the sweep is measured in the ring's own plane, which
        // the eye lies in) and fell through to click-select or to a marquee.
        var harness = GizmoHarness.FrontView(12f);
        harness.AddSelectedNode(Vector3.Zero);
        harness.Use(GizmoMode.Rotate);

        float radius = harness.GeometryAt(Vector3.Zero).RingRadius;
        float radians = degrees * MathF.PI / 180f;
        var aim = new Vector3(MathF.Cos(radians) * radius, MathF.Sin(radians) * radius, 0f);

        harness.Hover(aim).ShouldBe(GizmoUpdateResult.Hovering);
        harness.Gizmo.HoveredHandle.ShouldBe(GizmoHandle.AxisZ);

        // And what highlighted is what the press actually grabs.
        harness.Grab(aim).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmo.ActiveHandle.ShouldBe(GizmoHandle.AxisZ);
    }

    [Theory]
    // The editor's default pose: Camera.Pitch starts at 0 and FrameSelection
    // never changes it, so the eye sits exactly in the y ring's plane.
    [InlineData(0f, 0f, 12f)]
    // The Hammer "top" viewport, where the x and z rings go edge-on.
    [InlineData(0f, 12f, 0.01f)]
    [InlineData(8f, 6f, 10f)]
    [InlineData(-14f, 0.5f, 3f)]
    public void Every_ring_the_hit_test_offers_can_actually_be_grabbed(float ex, float ey, float ez)
    {
        // The property the whole defect reduces to: ClassifyPress promises
        // Manipulate from the hit test, so a hit the tool would refuse is a
        // promise the viewport cannot keep — and over empty space the refused
        // press becomes a marquee that replaces the selection.
        var harness = new GizmoHarness(new Vector3(ex, ey, ez), Vector3.Zero);
        harness.AddSelectedNode(Vector3.Zero);
        harness.Use(GizmoMode.Rotate);

        int offered = 0;
        for (float x = 200f; x <= 600f; x += 8f)
        {
            for (float y = 100f; y <= 500f; y += 8f)
            {
                var pixel = new Vector2(x, y);
                EditorInputFrame hover = harness.Frame(pixel);
                GizmoPick pick = harness.Gizmo.PickAt(in hover);
                if (!pick.IsHit)
                    continue;

                offered++;
                harness.Gizmos.Update(harness.Frame(pixel, down: PointerButtons.Left, pressed: PointerButtons.Left))
                    .ShouldBe(GizmoUpdateResult.DragBegan, $"pick {pick.Handle} at {pixel} was refused");
                harness.Gizmo.ActiveHandle.ShouldBe(pick.Handle);
                harness.Gizmo.Reset();
            }
        }

        offered.ShouldBeGreaterThan(0); // the sweep must actually have found rings
    }

    /// <summary>
    /// Two independent probe directions pin a rotation completely, and comparing
    /// what a quaternion DOES sidesteps the q ≡ −q ambiguity that makes raw
    /// component comparison unreliable.
    /// </summary>
    internal static void ShouldRotateLike(Quaternion actual, Quaternion expected)
    {
        foreach (Vector3 probe in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Vector3.Transform(probe, actual).ShouldBeCloseTo(Vector3.Transform(probe, expected), Tolerance);
    }
}

/// <summary>Vector assertions the gizmo suites share.</summary>
internal static class VectorAssertions
{
    /// <summary>Component-wise closeness, so a failure names the axis that drifted.</summary>
    public static void ShouldBeCloseTo(this Vector3 actual, Vector3 expected, float tolerance)
    {
        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
    }
}
