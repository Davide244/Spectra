using System.Numerics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="Camera.ScreenRectToFrustum"/>: the volume a marquee sweeps. It is
/// derived by remapping the rectangle's NDC box back onto the clip square and
/// re-running the Gribb–Hartmann extraction, so these tests exist to prove that
/// shortcut agrees with the geometry it is standing in for — the camera's own
/// frustum at full extent, and the corner rays
/// <see cref="Camera.ScreenPointToRay"/> produces at every other extent.
/// </summary>
public sealed class CameraScreenRectTests
{
    private static readonly Vector2 Viewport = new(1280f, 720f);

    private static Camera CreateCamera(float yaw = 0.6f, float pitch = -0.3f) => new()
    {
        Position = new Vector3(4f, 3f, -6f),
        Yaw = yaw,
        Pitch = pitch,
        FieldOfView = MathF.PI / 3f,
        AspectRatio = Viewport.X / Viewport.Y,
    };

    [Fact]
    public void The_whole_viewport_reproduces_the_camera_frustum()
    {
        Camera camera = CreateCamera();

        Frustum full = camera.GetFrustum();
        Frustum rect = camera.ScreenRectToFrustum(Vector2.Zero, Viewport, Viewport);

        ShouldMatch(rect.Left, full.Left);
        ShouldMatch(rect.Right, full.Right);
        ShouldMatch(rect.Bottom, full.Bottom);
        ShouldMatch(rect.Top, full.Top);
        ShouldMatch(rect.Near, full.Near);
        ShouldMatch(rect.Far, full.Far);
    }

    [Fact]
    public void The_sub_frustum_keeps_the_camera_near_and_far_planes()
    {
        Camera camera = CreateCamera();

        Frustum rect = camera.ScreenRectToFrustum(new Vector2(300f, 200f), new Vector2(700f, 500f), Viewport);

        // Pulling the sides in must not change how deep the volume reaches.
        ShouldMatch(rect.Near, camera.GetFrustum().Near);
        ShouldMatch(rect.Far, camera.GetFrustum().Far);
    }

    [Theory]
    [InlineData(300f, 200f, 700f, 500f)]
    [InlineData(0f, 0f, 40f, 30f)]
    [InlineData(1100f, 60f, 1279f, 700f)]
    public void Every_corner_ray_lies_on_the_two_side_planes_that_meet_at_it(
        float minX, float minY, float maxX, float maxY)
    {
        Camera camera = CreateCamera();
        var min = new Vector2(minX, minY);
        var max = new Vector2(maxX, maxY);
        Frustum frustum = camera.ScreenRectToFrustum(min, max, Viewport);

        // This is the cross-check the implementation's remarks promise: the
        // matrix shortcut and the corner-ray construction must describe the
        // same volume.
        AssertRayOnPlanes(camera, new Vector2(min.X, min.Y), frustum.Left, frustum.Top);
        AssertRayOnPlanes(camera, new Vector2(max.X, min.Y), frustum.Right, frustum.Top);
        AssertRayOnPlanes(camera, new Vector2(min.X, max.Y), frustum.Left, frustum.Bottom);
        AssertRayOnPlanes(camera, new Vector2(max.X, max.Y), frustum.Right, frustum.Bottom);
    }

    [Fact]
    public void Points_projecting_inside_the_rectangle_are_inside_the_volume()
    {
        Camera camera = CreateCamera();
        var min = new Vector2(320f, 180f);
        var max = new Vector2(960f, 540f);
        Frustum frustum = camera.ScreenRectToFrustum(min, max, Viewport);
        var rectangle = (Min: min, Max: max);

        // Sample the whole viewport on a coarse grid, unproject each pixel to a
        // point 20 units out, and check the volume's verdict against the plain
        // "is this pixel in the rectangle" answer.
        for (float x = 10f; x < Viewport.X; x += 37f)
        {
            for (float y = 10f; y < Viewport.Y; y += 29f)
            {
                var pixel = new Vector2(x, y);
                Vector3 point = camera.ScreenPointToRay(pixel, Viewport).PointAt(20f);
                bool insideRect =
                    pixel.X >= rectangle.Min.X && pixel.X <= rectangle.Max.X &&
                    pixel.Y >= rectangle.Min.Y && pixel.Y <= rectangle.Max.Y;

                frustum.Contains(point).ShouldBe(insideRect, $"pixel {pixel}");
            }
        }
    }

    [Fact]
    public void A_zero_sized_rectangle_widens_to_a_pixel_instead_of_going_degenerate()
    {
        Camera camera = CreateCamera();
        var point = new Vector2(640f, 360f);

        Frustum frustum = camera.ScreenRectToFrustum(point, point, Viewport);

        // No NaNs from a division by a zero half-extent...
        foreach (Plane plane in new[] { frustum.Left, frustum.Right, frustum.Bottom, frustum.Top })
        {
            float.IsFinite(plane.Normal.X).ShouldBeTrue();
            float.IsFinite(plane.D).ShouldBeTrue();
            plane.Normal.Length().ShouldBe(1f, 1e-4f);
        }

        // ...and the one pixel the user clicked really is inside.
        frustum.Contains(camera.ScreenPointToRay(point, Viewport).PointAt(20f)).ShouldBeTrue();
    }

    [Fact]
    public void A_rectangle_dragged_backwards_gives_the_same_volume()
    {
        Camera camera = CreateCamera();
        var a = new Vector2(300f, 200f);
        var b = new Vector2(700f, 500f);

        Frustum forward = camera.ScreenRectToFrustum(a, b, Viewport);
        Frustum backward = camera.ScreenRectToFrustum(b, a, Viewport);

        ShouldMatch(backward.Left, forward.Left);
        ShouldMatch(backward.Right, forward.Right);
        ShouldMatch(backward.Bottom, forward.Bottom);
        ShouldMatch(backward.Top, forward.Top);
    }

    private static void AssertRayOnPlanes(Camera camera, Vector2 pixel, Plane first, Plane second)
    {
        Ray3 ray = camera.ScreenPointToRay(pixel, Viewport);
        // Two samples far apart: a plane that merely passes near the ray's
        // origin would drift away from it further along.
        foreach (float travel in new[] { 1f, 100f })
        {
            Vector3 point = ray.PointAt(travel);
            Plane.DotCoordinate(first, point).ShouldBe(0f, 1e-2f);
            Plane.DotCoordinate(second, point).ShouldBe(0f, 1e-2f);
        }
    }

    private static void ShouldMatch(Plane actual, Plane expected)
    {
        actual.Normal.X.ShouldBe(expected.Normal.X, 1e-4f);
        actual.Normal.Y.ShouldBe(expected.Normal.Y, 1e-4f);
        actual.Normal.Z.ShouldBe(expected.Normal.Z, 1e-4f);
        actual.D.ShouldBe(expected.D, 1e-3f);
    }
}
