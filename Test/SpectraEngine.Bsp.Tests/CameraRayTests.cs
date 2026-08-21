using System.Numerics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Picking-ray math on <see cref="Camera"/>: screen convention is top-left
/// origin with y-down pixels (raw window mouse coordinates), unprojection goes
/// through the inverse view-projection. Pure CPU math — no renderer, window,
/// or driver involved.
/// </summary>
public sealed class CameraRayTests
{
    private static readonly Vector2 Viewport = new(1920f, 1080f);

    private static Camera CreateCamera(float yaw, float pitch, float fov) => new()
    {
        Position = new Vector3(3f, 2f, -4f),
        Yaw = yaw,
        Pitch = pitch,
        FieldOfView = fov,
        AspectRatio = Viewport.X / Viewport.Y,
    };

    [Theory]
    [InlineData(-MathF.PI / 2f, 0f, MathF.PI / 3f)] // default orientation and fov
    [InlineData(0.7f, 0.4f, MathF.PI / 4f)]         // up-right, narrow fov
    [InlineData(2.3f, -0.9f, 1.9f)]                 // steep downward pitch, wide fov
    [InlineData(-2.9f, 1.2f, MathF.PI / 2f)]        // strong upward pitch
    public void Center_screen_ray_matches_camera_forward(float yaw, float pitch, float fov)
    {
        Camera camera = CreateCamera(yaw, pitch, fov);

        Ray3 ray = camera.ScreenPointToRay(Viewport * 0.5f, Viewport);

        ray.Direction.Length().ShouldBe(1f, 1e-5f);
        (ray.Direction - camera.Forward).Length().ShouldBe(0f, 1e-5f);
        // The documented origin contract: on the near plane, straight ahead.
        (ray.Origin - (camera.Position + camera.Forward * camera.NearPlane)).Length()
            .ShouldBe(0f, 1e-3f);
    }

    [Theory]
    [InlineData(-MathF.PI / 2f, 0f, MathF.PI / 3f)]
    [InlineData(1.1f, -0.6f, 1.2f)]
    public void Corner_rays_diverge_symmetrically(float yaw, float pitch, float fov)
    {
        Camera camera = CreateCamera(yaw, pitch, fov);

        Vector3 topLeft = camera.ScreenPointToRay(new Vector2(0f, 0f), Viewport).Direction;
        Vector3 topRight = camera.ScreenPointToRay(new Vector2(Viewport.X, 0f), Viewport).Direction;
        Vector3 bottomLeft = camera.ScreenPointToRay(new Vector2(0f, Viewport.Y), Viewport).Direction;
        Vector3 bottomRight = camera.ScreenPointToRay(Viewport, Viewport).Direction;

        // Screen y grows downward, so the top-left corner must tilt toward +Up
        // and -Right. This pins the y-flip convention — the mirror checks
        // below would pass even with a flipped y.
        Vector3.Dot(topLeft, camera.Right).ShouldBeLessThan(0f);
        Vector3.Dot(topLeft, camera.Up).ShouldBeGreaterThan(0f);

        // Horizontal mirror pairs: equal magnitude, opposite sign along Right.
        Vector3.Dot(topLeft, camera.Right).ShouldBe(-Vector3.Dot(topRight, camera.Right), 1e-5f);
        Vector3.Dot(bottomLeft, camera.Right).ShouldBe(-Vector3.Dot(bottomRight, camera.Right), 1e-5f);

        // Vertical mirror pairs: equal magnitude, opposite sign along Up.
        Vector3.Dot(topLeft, camera.Up).ShouldBe(-Vector3.Dot(bottomLeft, camera.Up), 1e-5f);
        Vector3.Dot(topRight, camera.Up).ShouldBe(-Vector3.Dot(bottomRight, camera.Up), 1e-5f);

        // All four corners make the same angle with Forward.
        float alongForward = Vector3.Dot(topLeft, camera.Forward);
        Vector3.Dot(topRight, camera.Forward).ShouldBe(alongForward, 1e-5f);
        Vector3.Dot(bottomLeft, camera.Forward).ShouldBe(alongForward, 1e-5f);
        Vector3.Dot(bottomRight, camera.Forward).ShouldBe(alongForward, 1e-5f);
    }

    [Theory]
    [InlineData(0f, 0f)]      // the point 5 units straight along Forward
    [InlineData(1.5f, -0.8f)] // off-axis, still well inside the fov at depth 5
    [InlineData(-2.0f, 1.1f)]
    public void Visible_point_round_trips_through_projection_and_picking_ray(float rightOffset, float upOffset)
    {
        Camera camera = CreateCamera(0.6f, -0.3f, MathF.PI / 3f);
        Vector3 point = camera.Position + camera.Forward * 5f
            + camera.Right * rightOffset + camera.Up * upOffset;

        camera.GetFrustum().Contains(point).ShouldBeTrue();

        // Project: world → clip → NDC → top-left y-down pixels (the inverse of
        // the mapping ScreenPointToRay documents).
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), camera.GetViewProjection());
        var screen = new Vector2(
            (clip.X / clip.W + 1f) * 0.5f * Viewport.X,
            (1f - clip.Y / clip.W) * 0.5f * Viewport.Y);

        Ray3 ray = camera.ScreenPointToRay(screen, Viewport);

        // Point-to-line distance; Direction is unit length per the Ray3 contract.
        Vector3.Cross(point - ray.Origin, ray.Direction).Length().ShouldBeLessThan(1e-3f);
    }
}
