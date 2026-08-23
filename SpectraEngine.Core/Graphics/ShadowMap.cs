using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// A directional light's depth map, and the transform that reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The world is unbounded, so the map cannot be fitted to it.</b> A sealed
/// level would fit one ortho box around the whole map and be done; here there is
/// no whole map, so the box is fitted to a near SLICE of the camera frustum, and
/// everything past <see cref="Distance"/> is simply unshadowed. That is the same
/// trade every open-world engine makes, and it is why cascades exist: several
/// slices at several resolutions instead of one.
/// </para>
/// <para>
/// <b>Two things make the shadow stop crawling, and both are mandatory rather
/// than polish.</b> The slice is bounded by a SPHERE rather than a box, so the
/// box's size cannot change when the camera merely turns; and the box's centre
/// is then snapped to whole shadow-map texels, so it cannot slide by a fraction
/// of a texel between frames. Without the sphere, the extents breathe as you
/// look around; without the snap, every shadow edge shimmers as you walk. Fixing
/// one and not the other fixes nothing, because the snap is only meaningful once
/// the texel size is constant.
/// </para>
/// <para>
/// <b>The map is depth-only</b>, which is what
/// <see cref="RenderTargetDesc.DepthOnly(int)"/> exists for: no colour
/// attachment to allocate, and no render target bound while it is drawn.
/// </para>
/// </remarks>
public sealed class ShadowMap : IDisposable
{
    /// <summary>Square resolution used when a caller does not say. One cascade's worth.</summary>
    public const int DefaultResolution = 2048;

    private readonly Renderer _renderer;
    private readonly RenderTarget _target;
    private bool _disposed;

    /// <summary>Creates the map at <paramref name="resolution"/> square. Render thread.</summary>
    public ShadowMap(Renderer renderer, int resolution = DefaultResolution)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 1);

        _renderer = renderer;
        Resolution = resolution;
        _target = renderer.CreateRenderTarget(RenderTargetDesc.DepthOnly(resolution));
    }

    /// <summary>Square resolution of the map.</summary>
    public int Resolution { get; }

    /// <summary>The target the depth pass draws into.</summary>
    public RenderTarget Target => _target;

    /// <summary>The depth map, sampled by the light pass. Never null: the target is depth-only.</summary>
    public Texture Depth => _target.DepthTexture!;

    /// <summary>
    /// How far from the camera shadows are drawn. Beyond it surfaces are lit but
    /// never shadowed, which is the open-world trade this type documents.
    /// </summary>
    /// <remarks>
    /// Not the camera's far plane, deliberately. Fitting to the far plane grows
    /// the ortho box until one texel covers metres and every shadow turns to
    /// mush, which is the single most common way a first shadow implementation
    /// is judged broken.
    /// </remarks>
    public float Distance { get; set; } = 28f;

    /// <summary>
    /// World-space distance a sample point is pushed along its surface normal
    /// before the depth comparison, in units of one shadow texel.
    /// </summary>
    /// <remarks>
    /// <b>Normal offset rather than a depth bias, and the difference matters.</b>
    /// A constant added to the compared depth has to be tuned against the clip-Z
    /// convention, which differs between backends, and trades acne for peter
    /// panning wherever the surface is steep. Offsetting along the normal is a
    /// world-space quantity: it scales with the texel footprint, needs no
    /// per-backend constant, and moves the sample sideways out of its own
    /// shadow instead of pretending the surface is nearer the light.
    /// </remarks>
    public float NormalBias { get; set; } = 1.1f;

    /// <summary>Small constant subtracted from the compared depth, to catch what the normal offset does not.</summary>
    public float DepthBias { get; set; } = 0.0015f;

    /// <summary>World-to-light-clip, for the depth pass to draw with. Valid after <see cref="Fit"/>.</summary>
    public Matrix4x4 LightViewProjection { get; private set; } = Matrix4x4.Identity;

    /// <summary>
    /// World position to shadow-map lookup: xy is the texture coordinate, z is
    /// directly comparable with what the map stores. Valid after <see cref="Fit"/>.
    /// </summary>
    /// <remarks>
    /// <b>Every convention difference between the backends is folded in here, on
    /// the CPU, so the shader has none.</b> The Y flip (render targets are
    /// bottom-left on OpenGL and top-left on D3D) and the clip-Z-to-depth-buffer
    /// mapping are both baked into this one matrix. Doing either in the shader
    /// means a shadow that is upside down or offset in depth on exactly one
    /// backend, which produces no error anywhere.
    /// </remarks>
    public Matrix4x4 WorldToShadow { get; private set; } = Matrix4x4.Identity;

    /// <summary>One texel's size in shadow-map texture coordinates. The PCF kernel's step.</summary>
    public float TexelSize => 1f / Resolution;

    /// <summary>World-space size of one shadow texel after the last <see cref="Fit"/>. Diagnostics.</summary>
    public float WorldTexelSize { get; private set; }

    /// <summary>
    /// Aims the map at the slice of <paramref name="camera"/>'s frustum that
    /// shadows are drawn for, lit from <paramref name="lightDirection"/>.
    /// </summary>
    /// <param name="camera">The camera whose view is being shadowed.</param>
    /// <param name="lightDirection">The direction the light TRAVELS, as a <see cref="RenderLight"/> carries it.</param>
    /// <returns>False when the direction is degenerate and nothing was fitted.</returns>
    public bool Fit(Camera camera, Vector3 lightDirection)
    {
        if (!TryFitLightMatrix(camera, lightDirection, Distance, Resolution,
                out Matrix4x4 lightViewProjection, out float worldTexelSize))
        {
            return false;
        }

        LightViewProjection = lightViewProjection;
        WorldToShadow = lightViewProjection
            * _renderer.ClipZCorrection
            * NdcToShadowTexture(_renderer.DepthToNdcZ, _renderer.TargetOriginIsTopLeft);
        WorldTexelSize = worldTexelSize;
        return true;
    }

    /// <summary>
    /// The fitting itself: pure geometry, no renderer and no GPU, so the two
    /// properties that make a shadow stable can be tested without one.
    /// </summary>
    /// <returns>False when there is nothing sensible to fit.</returns>
    internal static bool TryFitLightMatrix(
        Camera camera,
        Vector3 lightDirection,
        float distance,
        int resolution,
        out Matrix4x4 lightViewProjection,
        out float worldTexelSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        lightViewProjection = Matrix4x4.Identity;
        worldTexelSize = 0f;

        if (lightDirection.LengthSquared() < 1e-12f) return false;
        Vector3 forward = Vector3.Normalize(lightDirection);

        float near = camera.NearPlane;
        float far = MathF.Min(distance, camera.FarPlane);
        if (far <= near) return false;

        BoundSlice(camera, near, far, out Vector3 center, out float radius);
        if (radius <= 0f) return false;

        // Behind the visible slice by its own diameter, so a wall or a hill just
        // outside the view still casts into it. In an unbounded world this can
        // never be complete; it is a depth range, not a guarantee.
        float casterMargin = radius * 2f;

        // Any up that is not parallel to the light. A sun pointing straight down
        // is the case world-up fails on, and is also the most likely direction.
        Vector3 up = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        // LIGHT SPACE IS ANCHORED AT THE WORLD ORIGIN, not at the slice. Putting
        // the eye at the slice's centre would be the obvious choice and it
        // quietly destroys the snap below: the centre moves continuously with
        // the camera, so light space itself would slide, and quantising a
        // coordinate inside a sliding frame quantises nothing. The frame has to
        // be the same every frame for whole-texel steps to mean anything.
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(Vector3.Zero, forward, up);

        // THE SNAP. The slice's centre wanders by fractions of a texel as the
        // camera moves; quantising it to whole texels is what stops every shadow
        // edge from shimmering. It is only correct because the radius above is
        // rotation-independent, so the texel size is constant.
        float diameter = radius * 2f;
        float texelsPerUnit = resolution / diameter;
        Vector3 centerInLight = Vector3.Transform(center, lightView);
        float snappedX = MathF.Floor(centerInLight.X * texelsPerUnit) / texelsPerUnit;
        float snappedY = MathF.Floor(centerInLight.Y * texelsPerUnit) / texelsPerUnit;

        // CreateLookAt is right-handed, so anything in front of the light lies at
        // NEGATIVE light-space z while the ortho's near and far are distances
        // measured forward from the eye: hence the negation. Depth is
        // deliberately not snapped. A uniform shift in z moves the caster's
        // stored depth and the receiver's computed depth by the same amount,
        // because both go through this same matrix, so it cancels in the
        // comparison and cannot shimmer.
        float zNear = -(centerInLight.Z + radius + casterMargin);
        float zFar = -(centerInLight.Z - radius);

        Matrix4x4 lightProjection = Matrix4x4.CreateOrthographicOffCenter(
            snappedX - radius, snappedX + radius,
            snappedY - radius, snappedY + radius,
            zNear, zFar);

        lightViewProjection = lightView * lightProjection;
        worldTexelSize = diameter / resolution;
        return true;
    }

    /// <summary>
    /// Clip space to a shadow-map lookup: NDC xy to texture coordinates, NDC z
    /// to whatever the depth buffer actually stores.
    /// </summary>
    /// <remarks>
    /// The z row is the exact inverse of <see cref="Renderer.DepthToNdcZ"/>, so
    /// the two directions cannot drift apart. The y row carries the sign of the
    /// backend's target origin, which is the same flip
    /// <see cref="FullscreenTriangle"/> bakes into its vertices and for the same
    /// reason.
    /// </remarks>
    internal static Matrix4x4 NdcToShadowTexture(Vector2 depthToNdc, bool topLeftOrigin)
    {
        float zScale = 1f / depthToNdc.X;
        float zBias = -depthToNdc.Y / depthToNdc.X;
        float ySign = topLeftOrigin ? -0.5f : 0.5f;

        return new Matrix4x4(
            0.5f, 0f, 0f, 0f,
            0f, ySign, 0f, 0f,
            0f, 0f, zScale, 0f,
            0.5f, 0.5f, zBias, 1f);
    }

    // The eight corners of the frustum slice, reduced to the sphere that
    // contains them. A sphere rather than a box because a box built from the
    // same corners changes size when the camera turns, and a shadow map whose
    // extents change every frame cannot be texel-snapped into stability.
    private static void BoundSlice(Camera camera, float near, float far, out Vector3 center, out float radius)
    {
        float tanHalfFov = MathF.Tan(camera.FieldOfView * 0.5f);
        Vector3 position = camera.Position;
        Vector3 forward = camera.Forward;
        Vector3 right = camera.Right;
        Vector3 up = camera.Up;

        Span<Vector3> corners = stackalloc Vector3[8];
        int c = 0;
        for (int end = 0; end < 2; end++)
        {
            float distance = end == 0 ? near : far;
            float halfHeight = distance * tanHalfFov;
            float halfWidth = halfHeight * camera.AspectRatio;
            Vector3 middle = position + forward * distance;

            corners[c++] = middle - right * halfWidth - up * halfHeight;
            corners[c++] = middle + right * halfWidth - up * halfHeight;
            corners[c++] = middle - right * halfWidth + up * halfHeight;
            corners[c++] = middle + right * halfWidth + up * halfHeight;
        }

        center = Vector3.Zero;
        for (int i = 0; i < corners.Length; i++)
            center += corners[i];
        center /= corners.Length;

        float furthest = 0f;
        for (int i = 0; i < corners.Length; i++)
            furthest = MathF.Max(furthest, Vector3.DistanceSquared(corners[i], center));

        // Rounded up a little: the snap below moves the box by up to a texel, and
        // a radius that exactly touches the corners would clip them afterwards.
        radius = MathF.Sqrt(furthest) * 1.02f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.DestroyRenderTarget(_target);
    }
}
