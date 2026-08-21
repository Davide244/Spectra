using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Handle picking: aiming a viewport ray at a handle's world geometry picks
/// that handle, aiming at nothing picks nothing, and — the part that actually
/// decides whether a gizmo feels right — the handles that overlap on screen
/// resolve in the documented priority: centre, then planes, then axes, nearest
/// first.
/// </summary>
public sealed class GizmoHitTestingTests
{
    private const float Tolerance = TranslateGizmoHitTester.DefaultTolerancePixels;

    // Comfortably past the plane quads (which end at 0.58 of the axis length)
    // so an axis aim is unambiguously on the bare shaft.
    private const float AlongAxis = 0.8f;

    [Theory]
    [InlineData(GizmoHandle.AxisX)]
    [InlineData(GizmoHandle.AxisY)]
    [InlineData(GizmoHandle.AxisZ)]
    public void An_axis_arrow_is_picked_from_a_ray_through_its_shaft(GizmoHandle expected)
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        Vector3 onShaft = GizmoHandles.AxisDirection(expected) * (geometry.AxisLength * AlongAxis);

        GizmoPick pick = Pick(harness, geometry, onShaft);

        pick.Handle.ShouldBe(expected);
        // The ray passes exactly through the shaft, so the miss distance is
        // zero to float precision — proof the aim really is on the handle and
        // the pick is not merely inside the tolerance by luck.
        pick.PixelDistance.ShouldBeLessThan(0.01f);
    }

    [Theory]
    [InlineData(GizmoHandle.PlaneYZ)]
    [InlineData(GizmoHandle.PlaneZX)]
    [InlineData(GizmoHandle.PlaneXY)]
    public void A_plane_quad_is_picked_from_a_ray_through_its_face(GizmoHandle expected)
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);

        GizmoPick pick = Pick(harness, geometry, QuadCentre(geometry, expected));

        pick.Handle.ShouldBe(expected);
    }

    [Fact]
    public void The_centre_disc_is_picked_from_a_ray_through_the_pivot()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);

        Pick(harness, geometry, Vector3.Zero).Handle.ShouldBe(GizmoHandle.Screen);
    }

    [Fact]
    public void A_ray_into_empty_space_picks_nothing()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);

        // Far outside the gizmo in every direction, in the negative octant
        // where no handle lives at all.
        Vector3 elsewhere = new(-1f, -1f, -1f);
        Pick(harness, geometry, elsewhere * (geometry.AxisLength * 3f)).ShouldBe(GizmoPick.Miss);
    }

    [Fact]
    public void A_gizmo_behind_the_camera_picks_nothing()
    {
        var harness = GizmoHarness.FrontView();
        // 30 units behind a camera that sits at z = 10 looking down −z.
        GizmoGeometry geometry = harness.GeometryAt(new Vector3(0f, 0f, 40f));
        geometry.IsBehindCamera.ShouldBeTrue();

        var straightAhead = new Ray3(harness.Scene.Camera.Position, harness.Scene.Camera.Forward);
        TranslateGizmoHitTester.Pick(in geometry, in straightAhead, Tolerance).ShouldBe(GizmoPick.Miss);
    }

    // --- Ambiguous overlap: the priority order ------------------------------

    [Fact]
    public void The_centre_wins_over_the_axes_that_all_meet_at_the_pivot()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);

        // Every axis segment starts at the pivot, so a ray through the pivot is
        // at zero distance from all three at once. Without the centre-first
        // rule the disc — the smallest target, entirely enclosed by its
        // competitors — could never be grabbed.
        GizmoPick pick = Pick(harness, geometry, Vector3.Zero, tolerancePixels: 40f);

        pick.Handle.ShouldBe(GizmoHandle.Screen);
    }

    [Fact]
    public void A_plane_quad_wins_over_the_axes_running_beneath_it()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        Vector3 quadCentre = QuadCentre(geometry, GizmoHandle.PlaneXY);

        // A tolerance this wide puts both bounding arrows within reach of the
        // quad's centre, which is exactly the situation the priority resolves:
        // the user aimed at a filled square, so they get the square.
        GizmoPick generous = Pick(harness, geometry, quadCentre, tolerancePixels: 90f);
        generous.Handle.ShouldBe(GizmoHandle.PlaneXY);

        // Sanity check that the ambiguity was real: with the planes removed
        // from consideration the same ray would land on an axis.
        GizmoPick axisOnly = PickAxesOnly(harness, geometry, quadCentre, tolerancePixels: 90f);
        axisOnly.Handle.ShouldBeOneOf(GizmoHandle.AxisX, GizmoHandle.AxisY);
    }

    [Fact]
    public void The_nearer_plane_quad_wins_when_a_ray_pierces_two()
    {
        var harness = GizmoHarness.FrontView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        float length = geometry.AxisLength;

        // Hand-built rather than aimed through the camera: this ray travels
        // −y and +z so it pierces the z = 0 quad (PlaneXY) at 0.45 of the axis
        // length and then the y = 0 quad (PlaneZX) half an axis further on.
        var ray = new Ray3(
            new Vector3(0.45f * length, 0.9f * length, -0.45f * length),
            Vector3.Normalize(new Vector3(0f, -1f, 1f)));

        // Prove both are genuinely hit before asserting which one wins.
        geometry.TryGetPlaneQuad(GizmoHandle.PlaneXY, out Vector3 xyCorner, out Vector3 xyU, out Vector3 xyV, out float size)
            .ShouldBeTrue();
        GizmoMath.TryRayQuad(in ray, xyCorner, xyU, size, xyV, size, out float nearDistance).ShouldBeTrue();
        geometry.TryGetPlaneQuad(GizmoHandle.PlaneZX, out Vector3 zxCorner, out Vector3 zxU, out Vector3 zxV, out _)
            .ShouldBeTrue();
        GizmoMath.TryRayQuad(in ray, zxCorner, zxU, size, zxV, size, out float farDistance).ShouldBeTrue();
        nearDistance.ShouldBeLessThan(farDistance);

        TranslateGizmoHitTester.Pick(in geometry, in ray, Tolerance).Handle.ShouldBe(GizmoHandle.PlaneXY);
    }

    [Fact]
    public void The_axis_nearest_the_cursor_wins_when_another_is_viewed_end_on()
    {
        // Looking straight down −z collapses the whole z arrow onto the pivot,
        // so it is within a few pixels of everything near the centre — the
        // classic ambiguous view.
        var harness = GizmoHarness.FrontView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        Vector3 onXShaft = new(geometry.AxisLength * AlongAxis, 0f, 0f);

        // A tolerance wide enough that the end-on z arrow is also "within
        // tolerance"; proximity to the cursor is what must decide.
        GizmoPick pick = Pick(harness, geometry, onXShaft, tolerancePixels: 90f);

        pick.Handle.ShouldBe(GizmoHandle.AxisX);
        pick.PixelDistance.ShouldBeLessThan(0.01f);
    }

    // --- The tolerance is a real screen-space quantity -----------------------

    [Fact]
    public void An_axis_is_picked_inside_the_pixel_tolerance_and_missed_outside_it()
    {
        // Front view, x arrow: the arrow lies in the view plane, so a screen
        // offset converts to a world offset by exactly one scale factor and the
        // pixel distance the tester computes is the pixel distance we aimed.
        var harness = GizmoHarness.FrontView();
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        Vector2 onShaft = harness.WorldToScreen(new Vector3(geometry.AxisLength * AlongAxis, 0f, 0f));

        GizmoPick inside = PickScreen(harness, geometry, onShaft + new Vector2(0f, Tolerance - 2f), Tolerance);
        inside.Handle.ShouldBe(GizmoHandle.AxisX);
        inside.PixelDistance.ShouldBe(Tolerance - 2f, 0.1f);

        GizmoPick outside = PickScreen(harness, geometry, onShaft + new Vector2(0f, Tolerance + 4f), Tolerance);
        outside.Handle.ShouldBe(GizmoHandle.None);
    }

    // --- Helpers ------------------------------------------------------------

    private static Vector3 QuadCentre(in GizmoGeometry geometry, GizmoHandle handle)
    {
        geometry.TryGetPlaneQuad(handle, out Vector3 corner, out Vector3 first, out Vector3 second, out float size)
            .ShouldBeTrue();
        return corner + (first + second) * (size * 0.5f);
    }

    private static GizmoPick Pick(
        GizmoHarness harness,
        in GizmoGeometry geometry,
        Vector3 aimAt,
        float tolerancePixels = Tolerance) =>
        PickScreen(harness, in geometry, harness.WorldToScreen(aimAt), tolerancePixels);

    private static GizmoPick PickScreen(
        GizmoHarness harness, in GizmoGeometry geometry, Vector2 cursor, float tolerancePixels)
    {
        Ray3 ray = harness.Scene.Camera.ScreenPointToRay(cursor, harness.ViewportSize);
        return TranslateGizmoHitTester.Pick(in geometry, in ray, tolerancePixels);
    }

    // Reproduces just the axis stage of the tester, to show what the priority
    // order suppressed rather than asserting an unexplained handle.
    private static GizmoPick PickAxesOnly(
        GizmoHarness harness, in GizmoGeometry geometry, Vector3 aimAt, float tolerancePixels)
    {
        Ray3 ray = harness.Scene.Camera.ScreenPointToRay(harness.WorldToScreen(aimAt), harness.ViewportSize);
        GizmoPick best = GizmoPick.Miss;

        for (GizmoHandle handle = GizmoHandle.AxisX; handle <= GizmoHandle.AxisZ; handle++)
        {
            geometry.TryGetAxisSegment(handle, out Vector3 start, out Vector3 end).ShouldBeTrue();
            GizmoMath.ClosestApproachToSegment(
                in ray, start, end, out float distance, out Vector3 onRay, out Vector3 onAxis);

            float pixels = geometry.WorldToPixels(Vector3.Distance(onRay, onAxis));
            if (pixels <= tolerancePixels && pixels < best.PixelDistance)
                best = new GizmoPick(handle, distance, pixels, onRay);
        }

        return best;
    }
}
