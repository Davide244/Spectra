using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="Frustum"/> extraction and containment. The culling guarantee
/// under test: <see cref="Frustum.Intersects"/> may report a not-quite-visible
/// box as visible (a false positive merely costs a draw call), but must NEVER
/// reject a box that contains a visible point — a false negative would make
/// geometry pop out of existence.
/// </summary>
public sealed class FrustumTests
{
    /// <summary>Camera at an arbitrary offset looking down -Z (yaw -π/2, pitch 0).</summary>
    private static Camera CreateCamera() => new()
    {
        Position = new Vector3(1f, 2f, 3f),
        Yaw = -MathF.PI / 2f,
        Pitch = 0f,
        FieldOfView = MathF.PI / 3f,
        AspectRatio = 16f / 9f,
    };

    [Fact]
    public void Point_on_forward_axis_is_contained()
    {
        Camera camera = CreateCamera();

        camera.GetFrustum().Contains(camera.Position + camera.Forward * 5f).ShouldBeTrue();
    }

    [Fact]
    public void Point_behind_the_camera_is_excluded()
    {
        Camera camera = CreateCamera();

        camera.GetFrustum().Contains(camera.Position - camera.Forward * 1f).ShouldBeFalse();
    }

    [Fact]
    public void Point_beyond_the_far_plane_is_excluded()
    {
        Camera camera = CreateCamera();

        camera.GetFrustum().Contains(camera.Position + camera.Forward * (camera.FarPlane + 10f))
            .ShouldBeFalse();
    }

    [Fact]
    public void Point_closer_than_the_near_plane_is_excluded()
    {
        // 0.07 with near = 0.1 sits between the true near plane and the plane a
        // wrong GL-style [-1, 1] extraction would yield (~near/2 in front of the
        // eye), so this probe pins the [0, 1] clip-depth adaptation specifically.
        Camera camera = CreateCamera();
        camera.NearPlane.ShouldBe(0.1f);

        camera.GetFrustum().Contains(camera.Position + camera.Forward * 0.07f).ShouldBeFalse();
    }

    [Fact]
    public void Intersects_never_rejects_a_box_containing_a_visible_point()
    {
        Camera camera = CreateCamera();
        Frustum frustum = camera.GetFrustum();
        float halfTan = MathF.Tan(camera.FieldOfView * 0.5f);

        // Probe points constructed to be inside the view pyramid (fractions of
        // the half-extents at each depth, staying clear of the ±1 boundary),
        // spanning near-plane-adjacent to almost-far.
        float[] depths = [0.2f, 1f, 5f, 20f, 100f, 900f];
        float[] fractions = [-0.9f, -0.5f, 0f, 0.5f, 0.9f];
        float[] halfSizes = [0.01f, 1f, 50f];

        foreach (float depth in depths)
        foreach (float fx in fractions)
        foreach (float fy in fractions)
        {
            float halfHeight = depth * halfTan;
            float halfWidth = halfHeight * camera.AspectRatio;
            Vector3 point = camera.Position
                + camera.Forward * depth
                + camera.Right * (fx * halfWidth)
                + camera.Up * (fy * halfHeight);
            frustum.Contains(point).ShouldBeTrue(
                $"probe at depth {depth}, fx {fx}, fy {fy} should be visible by construction");

            // Boxes containing that point — tiny to frustum-dwarfing, centered
            // on it and with it sitting exactly on opposite corners.
            foreach (float half in halfSizes)
            {
                var extent = new Vector3(half);
                AssertNotCulled(frustum, new Aabb(point - extent, point + extent), point);
                AssertNotCulled(frustum, new Aabb(point, point + extent), point);
                AssertNotCulled(frustum, new Aabb(point - extent, point), point);
            }
        }
    }

    [Fact]
    public void Intersects_rejects_boxes_fully_outside()
    {
        // Not demanded by the conservative contract (false positives are legal),
        // but it pins that the grid test above is not passing vacuously: boxes
        // well clear of the frustum on either depth end must be culled.
        Camera camera = CreateCamera();
        Frustum frustum = camera.GetFrustum();

        Vector3 behind = camera.Position - camera.Forward * 10f;
        frustum.Intersects(new Aabb(behind - new Vector3(1f), behind + new Vector3(1f)))
            .ShouldBeFalse();

        Vector3 beyondFar = camera.Position + camera.Forward * (camera.FarPlane + 100f);
        frustum.Intersects(new Aabb(beyondFar - new Vector3(1f), beyondFar + new Vector3(1f)))
            .ShouldBeFalse();
    }

    private static void AssertNotCulled(in Frustum frustum, in Aabb box, Vector3 point) =>
        frustum.Intersects(box).ShouldBeTrue(
            $"box [{box.Min} .. {box.Max}] contains visible point {point} and must not be culled");
}
