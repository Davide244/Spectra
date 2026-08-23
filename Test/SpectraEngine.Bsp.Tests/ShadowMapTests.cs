using System;
using System.Numerics;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The two halves of a shadow map that have nothing to do with a GPU: where the
/// light's box is put, and how a world point becomes a lookup into it.
/// </summary>
/// <remarks>
/// Both are pure functions, and both fail silently. A box fitted slightly
/// differently every frame produces shadows that shimmer rather than an error,
/// and a lookup with the wrong Y sign produces a shadow that is upside down on
/// one backend and correct on the other.
/// </remarks>
public sealed class ShadowMapTests
{
    private const int Resolution = 1024;
    private const float Distance = 40f;

    private static Camera MakeCamera(Vector3 position, Vector3 lookAt)
    {
        var camera = new Camera { Position = position, AspectRatio = 16f / 9f };
        camera.LookAt(lookAt);
        return camera;
    }

    [Fact]
    public void The_light_box_moves_only_in_whole_texels_as_the_camera_walks()
    {
        // THE ANTI-SHIMMER PROPERTY, stated as what it actually is. The box does
        // not stand still while the camera moves; it steps, and every step is a
        // whole shadow texel. That is what lets a shadow edge land on the same
        // texel grid frame after frame. Without the snap the box slides
        // continuously, every edge is re-rasterised against a slightly different
        // grid, and the whole scene crawls with nothing reporting a fault.
        //
        // Measured on a FIXED WORLD POINT rather than on the matrix, because
        // that is the quantity the shimmer is visible in: where a given piece of
        // the world lands in the map.
        var probe = new Vector3(1.5f, 0.25f, -2f);
        Vector3 lightDirection = Vector3.Normalize(new Vector3(0.2f, -1f, 0.15f));

        Camera baseline = MakeCamera(new Vector3(0f, 2f, 10f), new Vector3(0f, 0f, 9f));
        ShadowMap.TryFitLightMatrix(baseline, lightDirection, Distance, Resolution, out Matrix4x4 first, out float texel)
            .ShouldBeTrue();
        float reference = TexelX(probe, first);

        // A whole texel's worth of camera travel, in twelve sub-texel steps, so
        // several of them fall inside one texel and at least one crosses a
        // boundary.
        for (int step = 1; step <= 12; step++)
        {
            float offset = texel * step / 12f;
            Camera moved = MakeCamera(new Vector3(offset, 2f, 10f), new Vector3(offset, 0f, 9f));
            ShadowMap.TryFitLightMatrix(moved, lightDirection, Distance, Resolution, out Matrix4x4 fit, out _)
                .ShouldBeTrue();

            float shift = TexelX(probe, fit) - reference;
            MathF.Abs(shift - MathF.Round(shift)).ShouldBeLessThan(1e-2f,
                $"after a {offset:0.####} unit camera move the probe shifted {shift:0.####} texels, " +
                "which is not a whole number: the light box is sliding rather than snapping");
        }
    }

    // Where a world point lands along the shadow map's x axis, in texels.
    private static float TexelX(Vector3 world, in Matrix4x4 lightViewProjection)
    {
        Vector4 clip = Vector4.Transform(world, lightViewProjection);
        return (clip.X / clip.W * 0.5f + 0.5f) * Resolution;
    }

    [Fact]
    public void Turning_the_camera_does_not_change_the_texel_size()
    {
        // The other half, and the reason the fit uses a bounding SPHERE. A box
        // around the same eight corners changes size as the camera rotates, so
        // the texel size changes, so the snap above quantises to a different
        // grid every frame and buys nothing.
        Camera facingZ = MakeCamera(new Vector3(0f, 2f, 0f), new Vector3(0f, 2f, -1f));
        Camera facingX = MakeCamera(new Vector3(0f, 2f, 0f), new Vector3(1f, 2f, 0f));
        Camera diagonal = MakeCamera(new Vector3(0f, 2f, 0f), new Vector3(1f, 2.6f, -1f));

        ShadowMap.TryFitLightMatrix(facingZ, -Vector3.UnitY, Distance, Resolution, out _, out float a).ShouldBeTrue();
        ShadowMap.TryFitLightMatrix(facingX, -Vector3.UnitY, Distance, Resolution, out _, out float b).ShouldBeTrue();
        ShadowMap.TryFitLightMatrix(diagonal, -Vector3.UnitY, Distance, Resolution, out _, out float c).ShouldBeTrue();

        b.ShouldBe(a, 1e-5f);
        c.ShouldBe(a, 1e-5f);
    }

    [Fact]
    public void What_the_camera_can_see_lands_inside_the_light_box()
    {
        // The fit is only useful if the shadowed region actually covers the
        // view. A box that is too small shadows nothing at the edges of the
        // screen, which reads as shadows that stop halfway across the floor.
        Camera camera = MakeCamera(new Vector3(0f, 3f, 12f), Vector3.Zero);
        ShadowMap.TryFitLightMatrix(
            camera, Vector3.Normalize(new Vector3(0.3f, -1f, 0.2f)), Distance, Resolution,
            out Matrix4x4 lightViewProjection, out _).ShouldBeTrue();

        // Points along the view axis, from just in front of the camera to the
        // shadow distance, plus the frustum's own corners at that distance.
        float tan = MathF.Tan(camera.FieldOfView * 0.5f);
        foreach (float depth in new[] { 1f, Distance * 0.5f, Distance * 0.98f })
        {
            float halfHeight = depth * tan;
            float halfWidth = halfHeight * camera.AspectRatio;
            Vector3 middle = camera.Position + camera.Forward * depth;

            foreach (int sx in new[] { -1, 1 })
            foreach (int sy in new[] { -1, 1 })
            {
                Vector3 corner = middle + camera.Right * (halfWidth * sx) + camera.Up * (halfHeight * sy);
                Vector4 clip = Vector4.Transform(corner, lightViewProjection);

                clip.X.ShouldBeInRange(-1.0001f, 1.0001f);
                clip.Y.ShouldBeInRange(-1.0001f, 1.0001f);
                clip.Z.ShouldBeInRange(-0.0001f, 1.0001f);
            }
        }
    }

    [Fact]
    public void A_light_with_no_direction_fits_nothing()
    {
        Camera camera = MakeCamera(new Vector3(0f, 2f, 5f), Vector3.Zero);
        ShadowMap.TryFitLightMatrix(camera, Vector3.Zero, Distance, Resolution, out _, out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void A_light_pointing_straight_down_still_fits()
    {
        // Straight down is what a sun usually is, and it is the direction whose
        // obvious up-reference is parallel to it. A NaN basis here would produce
        // a matrix full of NaN and a shadow map full of nothing.
        Camera camera = MakeCamera(new Vector3(0f, 2f, 5f), Vector3.Zero);
        ShadowMap.TryFitLightMatrix(camera, -Vector3.UnitY, Distance, Resolution, out Matrix4x4 m, out _)
            .ShouldBeTrue();

        float sum = m.M11 + m.M22 + m.M33 + m.M44 + m.M41 + m.M42 + m.M43;
        float.IsNaN(sum).ShouldBeFalse();
    }

    public static TheoryData<bool, float, float> OriginConventions() => new()
    {
        // topLeftOrigin, ndc y, expected v
        { false, -1f, 0f },   // OpenGL: ndc -1 is the bottom, and v = 0 is the bottom
        { false, 1f, 1f },
        { true, -1f, 1f },    // D3D: ndc -1 is the bottom, and v = 1 is the bottom
        { true, 1f, 0f },
    };

    [Theory]
    [MemberData(nameof(OriginConventions))]
    public void The_lookup_flips_v_on_the_backends_whose_targets_start_at_the_top(
        bool topLeftOrigin, float ndcY, float expectedV)
    {
        // A render target's row zero is its bottom on OpenGL and its top on D3D,
        // so a shadow lookup computed from clip space has to flip on exactly one
        // of them. Getting it wrong mirrors every shadow vertically on that
        // backend, which no debug layer reports because nothing about it is
        // invalid.
        Matrix4x4 m = ShadowMap.NdcToShadowTexture(new Vector2(2f, -1f), topLeftOrigin);
        Vector4 mapped = Vector4.Transform(new Vector4(0f, ndcY, 0f, 1f), m);

        mapped.Y.ShouldBe(expectedV, 1e-5f);
    }

    [Theory]
    // OpenGL: clip z runs -1..1 and the buffer stores 0..1.
    [InlineData(2f, -1f, -1f, 0f)]
    [InlineData(2f, -1f, 1f, 1f)]
    // D3D: clip z already runs 0..1, so the lookup passes it through.
    [InlineData(1f, 0f, 0f, 0f)]
    [InlineData(1f, 0f, 1f, 1f)]
    public void The_lookup_undoes_exactly_what_the_depth_texel_conversion_does(
        float scale, float bias, float ndcZ, float expectedDepth)
    {
        // The z row of the lookup matrix is the inverse of Renderer.DepthToNdcZ,
        // which is what keeps the two directions from drifting: the light pass
        // converts a texel to NDC to reconstruct position, and this converts NDC
        // to a texel to compare against.
        Matrix4x4 m = ShadowMap.NdcToShadowTexture(new Vector2(scale, bias), topLeftOrigin: true);
        Vector4 mapped = Vector4.Transform(new Vector4(0f, 0f, ndcZ, 1f), m);

        mapped.Z.ShouldBe(expectedDepth, 1e-5f);
    }
}
