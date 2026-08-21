using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// What the rotate and resize gizmos put on screen, and what that same shape is
/// pickable as. The two are asserted together on purpose: a manipulator whose
/// drawn shape and pickable shape disagree is worse than one that draws nothing,
/// because it silently does the wrong thing under the cursor.
/// </summary>
public sealed class RotateScaleGizmoShapeTests
{
    // Interleaved position + colour, six floats per vertex, two per line —
    // DebugDraw's layout.
    private const int FloatsPerVertex = 6;

    private const float Tolerance = GizmoHitTesting.DefaultTolerancePixels;

    // --- Rotate --------------------------------------------------------------

    [Fact]
    public void The_rotate_gizmo_draws_four_rings()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(Vector3.Zero);
        harness.Use(GizmoMode.Rotate);
        harness.Hover(new Vector3(40f, 40f, 40f)); // off every handle

        var output = new DebugDraw();
        harness.Gizmo.Draw(output);

        // Three axis rings plus the view ring, each a closed polygon of the
        // shared chord count.
        output.VertexCount.ShouldBe(4 * GizmoHitTesting.RingSegments * 2);
    }

    [Theory]
    [InlineData(GizmoHandle.AxisX)]
    [InlineData(GizmoHandle.AxisY)]
    [InlineData(GizmoHandle.AxisZ)]
    public void Each_rotate_ring_is_pickable_all_the_way_round(GizmoHandle handle)
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        geometry.AxisPerpendiculars(handle, out Vector3 u, out Vector3 v);

        // Sixteen stations round the circle, including the parts of the ring
        // furthest from face-on — the ones a plane-intersection hit test would
        // lose. Points that coincide with another ring (every 90°, where two
        // rings share an axis) are skipped: there the answer is legitimately
        // ambiguous and depth decides.
        for (int i = 0; i < 16; i++)
        {
            float angle = MathF.Tau * i / 16f;
            if (i % 4 == 0)
                continue;

            Vector3 point = (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * geometry.RingRadius;
            Ray3 ray = harness.Scene.Camera.ScreenPointToRay(harness.WorldToScreen(point), harness.ViewportSize);

            RotateGizmoHitTester.Pick(in geometry, in ray, Tolerance).Handle
                .ShouldBe(handle, $"at {angle:F2} rad");
        }
    }

    [Fact]
    public void The_rotate_gizmo_is_missed_away_from_every_ring()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);

        // Dead centre: inside all four rings, on none of them.
        Ray3 centre = harness.Scene.Camera.ScreenPointToRay(
            harness.WorldToScreen(Vector3.Zero), harness.ViewportSize);
        RotateGizmoHitTester.Pick(in geometry, in centre, Tolerance).ShouldBe(GizmoPick.Miss);

        // Well outside the largest ring.
        Ray3 far = harness.Scene.Camera.ScreenPointToRay(
            harness.WorldToScreen(Vector3.UnitY * (geometry.ScreenRingRadius * 3f)), harness.ViewportSize);
        RotateGizmoHitTester.Pick(in geometry, in far, Tolerance).ShouldBe(GizmoPick.Miss);
    }

    [Fact]
    public void The_view_ring_sits_outside_the_axis_rings_so_both_stay_grabbable()
    {
        var harness = GizmoHarness.FrontView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);

        geometry.ScreenRingRadius.ShouldBeGreaterThan(geometry.RingRadius);

        // Face-on, the z ring and the view ring are concentric circles far
        // enough apart that a cursor on one is nowhere near the other.
        float gapPixels = geometry.WorldToPixels(geometry.ScreenRingRadius - geometry.RingRadius);
        gapPixels.ShouldBeGreaterThan(Tolerance * 2f);
    }

    [Fact]
    public void The_dragged_ring_is_highlighted_and_draws_a_sweep()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(Vector3.Zero);
        RotateGizmo rotate = (RotateGizmo)harness.Use(GizmoMode.Rotate);
        rotate.Snap.Enabled = false;

        var idle = new DebugDraw();
        harness.Hover(new Vector3(40f, 40f, 40f));
        harness.Gizmo.Draw(idle);

        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        geometry.AxisPerpendiculars(GizmoHandle.AxisZ, out Vector3 u, out Vector3 v);
        Vector3 grabAt = (u * MathF.Cos(MathF.PI / 4f) + v * MathF.Sin(MathF.PI / 4f)) * geometry.RingRadius;
        harness.Grab(grabAt).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.DragTo((u * MathF.Cos(1.5f) + v * MathF.Sin(1.5f)) * geometry.RingRadius);

        var dragging = new DebugDraw();
        harness.Gizmo.Draw(dragging);

        // The protractor is extra geometry on top of the four rings.
        dragging.VertexCount.ShouldBeGreaterThan(idle.VertexCount);

        // And the active ring changed colour rather than being drawn twice.
        CountColour(dragging, GizmoColors.For(GizmoHandle.AxisZ, GizmoHandle.None)).ShouldBe(0);
        CountColour(dragging, GizmoColors.Highlight)
            .ShouldBeGreaterThan(GizmoHitTesting.RingSegments * 2);
    }

    // --- Scale ---------------------------------------------------------------

    [Fact]
    public void The_scale_gizmo_draws_three_capped_shafts_and_a_centre_cube()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(Vector3.Zero);
        harness.Use(GizmoMode.Scale);
        harness.Hover(new Vector3(40f, 40f, 40f));

        var output = new DebugDraw();
        harness.Gizmo.Draw(output);

        // Three shafts of one line each, three cubes of twelve edges, and one
        // more twelve-edge cube at the centre.
        const int expectedLines = (3 * 1) + (3 * 12) + 12;
        output.VertexCount.ShouldBe(expectedLines * 2);
    }

    [Theory]
    [InlineData(GizmoHandle.AxisX)]
    [InlineData(GizmoHandle.AxisY)]
    [InlineData(GizmoHandle.AxisZ)]
    public void A_scale_cube_and_its_shaft_both_pick_their_axis(GizmoHandle handle)
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        Vector3 axis = geometry.Axis(handle);

        Pick(harness, in geometry, axis * geometry.AxisLength).Handle.ShouldBe(handle);
        Pick(harness, in geometry, axis * (geometry.AxisLength * 0.75f)).Handle.ShouldBe(handle);
    }

    [Fact]
    public void The_centre_cube_wins_at_the_pivot()
    {
        // The uniform handle is surrounded by all three shafts; if it lost ties
        // it could never be grabbed at all.
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);

        Pick(harness, in geometry, Vector3.Zero).Handle.ShouldBe(GizmoHandle.Screen);
    }

    [Fact]
    public void The_scale_gizmo_is_missed_beyond_its_handles()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);

        Pick(harness, in geometry, Vector3.UnitX * (geometry.AxisLength * 4f)).ShouldBe(GizmoPick.Miss);
    }

    // --- Shared ---------------------------------------------------------------

    [Theory]
    [InlineData(GizmoMode.Rotate)]
    [InlineData(GizmoMode.Scale)]
    public void A_gizmo_behind_the_camera_neither_draws_nor_picks(GizmoMode mode)
    {
        var harness = GizmoHarness.FrontView();
        harness.AddSelectedNode(new Vector3(0f, 0f, 40f)); // 30 units behind the camera
        harness.Use(mode);
        harness.Hover(Vector3.Zero);

        var output = new DebugDraw();
        harness.Gizmo.Draw(output);
        output.VertexCount.ShouldBe(0);
        harness.Gizmo.HoveredHandle.ShouldBe(GizmoHandle.None);
    }

    [Theory]
    [InlineData(GizmoMode.Rotate)]
    [InlineData(GizmoMode.Scale)]
    public void Every_handle_stays_inside_the_constant_screen_footprint(GizmoMode mode)
    {
        // The whole point of the shared geometry: all three tools are the same
        // size on screen at any camera distance, so switching mode never makes
        // the manipulator jump.
        foreach (float distance in new[] { 3f, 50f, 5_000f })
        {
            var harness = GizmoHarness.FrontView(distance);
            harness.AddSelectedNode(Vector3.Zero);
            harness.Use(mode);
            harness.Hover(Vector3.Zero);

            var output = new DebugDraw();
            harness.Gizmo.Draw(output);
            output.VertexCount.ShouldBeGreaterThan(0);

            Vector2 centre = harness.WorldToScreen(Vector3.Zero);
            float furthest = 0f;
            foreach (Vector3 position in Positions(output))
                furthest = MathF.Max(furthest, Vector2.Distance(harness.WorldToScreen(position), centre));

            furthest.ShouldBeGreaterThan(harness.Gizmo.HandlePixelSize * 0.7f);
            furthest.ShouldBeLessThan(harness.Gizmo.HandlePixelSize * 1.3f);
        }
    }

    // --- Helpers -------------------------------------------------------------

    private static GizmoPick Pick(GizmoHarness harness, in GizmoGeometry geometry, Vector3 aimAt)
    {
        Ray3 ray = harness.Scene.Camera.ScreenPointToRay(
            harness.WorldToScreen(aimAt), harness.ViewportSize);
        return ScaleGizmoHitTester.Pick(in geometry, in ray, Tolerance);
    }

    private static Vector3[] Positions(DebugDraw output)
    {
        ReadOnlySpan<float> data = output.Vertices;
        var positions = new Vector3[output.VertexCount];
        for (int i = 0; i < positions.Length; i++)
        {
            int b = i * FloatsPerVertex;
            positions[i] = new Vector3(data[b], data[b + 1], data[b + 2]);
        }
        return positions;
    }

    private static int CountColour(DebugDraw output, Vector3 colour)
    {
        ReadOnlySpan<float> data = output.Vertices;
        int count = 0;
        for (int i = 0; i < output.VertexCount; i++)
        {
            int b = i * FloatsPerVertex;
            if (data[b + 3] == colour.X && data[b + 4] == colour.Y && data[b + 5] == colour.Z)
                count++;
        }
        return count;
    }
}
