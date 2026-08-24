using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// A directional light's cascaded depth map, and the transforms that read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The world is unbounded, so the map cannot be fitted to it.</b> A sealed
/// level would fit one ortho box around the whole map and be done; here there is
/// no whole map, so boxes are fitted to SLICES of the camera frustum, and
/// everything past <see cref="Distance"/> is lit but never shadowed.
/// </para>
/// <para>
/// <b>Cascades are not a quality setting, they are what makes the near shadow
/// sharp at all.</b> One box over the whole shadow distance has to be as wide as
/// the far end of the view, and a texel of it is then centimetres across
/// wherever you are actually looking: the 3x3 filter kernel spans three of those
/// texels, so a 40 cm post gets a penumbra a third of its own width and reads as
/// a smudge rather than a shadow. Splitting the range means the slice nearest
/// the camera is metres across instead of tens of metres, and its texels shrink
/// by the same factor. The far cascades stay coarse, which is invisible because
/// they are far away.
/// </para>
/// <para>
/// <b>Two things make a cascade stop crawling, and both are mandatory rather
/// than polish.</b> Each slice is bounded by a SPHERE rather than a box, so the
/// box's size cannot change when the camera merely turns; and the box's centre
/// is then snapped to whole texels, so it cannot slide by a fraction of one
/// between frames. Fixing one and not the other fixes nothing, because the snap
/// is only meaningful once the texel size is constant.
/// </para>
/// <para>
/// <b>One depth-only texture, split into quadrants.</b> An atlas rather than N
/// textures because the light pass then samples ONE sampler with one filter
/// kernel: N textures would need the kernel written out N times, since
/// SpectraShade cannot pass a sampler to a function.
/// </para>
/// </remarks>
public sealed class ShadowMap : IDisposable
{
    /// <summary>Square resolution of the whole atlas when a caller does not say.</summary>
    /// <remarks>
    /// 2048 holds four 1024-square cascades in 16 MB. Doubling it doubles the
    /// sharpness everywhere and costs 64 MB, which is the same memory four
    /// 2048-square cascades would need and strictly better spent here.
    /// </remarks>
    public const int DefaultResolution = 2048;

    /// <summary>Cascades in the 2x2 atlas.</summary>
    public const int MaxCascades = 4;

    private readonly Renderer _renderer;
    private readonly RenderTarget _target;
    private readonly Matrix4x4[] _lightViewProjection = new Matrix4x4[MaxCascades];
    private readonly Matrix4x4[] _worldToShadow = new Matrix4x4[MaxCascades];
    private readonly Vector4[] _rects = new Vector4[MaxCascades];
    private int _cascadeCount = MaxCascades;
    private bool _disposed;

    /// <summary>Creates the atlas at <paramref name="resolution"/> square. Render thread.</summary>
    public ShadowMap(Renderer renderer, int resolution = DefaultResolution)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 2);

        _renderer = renderer;
        Resolution = resolution;
        _target = renderer.CreateRenderTarget(RenderTargetDesc.DepthOnly(resolution));

        for (int i = 0; i < MaxCascades; i++)
        {
            _lightViewProjection[i] = Matrix4x4.Identity;
            _worldToShadow[i] = Matrix4x4.Identity;
        }
    }

    /// <summary>Square resolution of the whole atlas.</summary>
    public int Resolution { get; }

    /// <summary>Square resolution of one cascade's quadrant.</summary>
    public int TileResolution => Resolution / 2;

    /// <summary>The target the depth pass draws into.</summary>
    public RenderTarget Target => _target;

    /// <summary>The depth atlas, sampled by the light pass. Never null: the target is depth-only.</summary>
    public Texture Depth => _target.DepthTexture!;

    /// <summary>How many cascades are fitted and drawn. 1 to <see cref="MaxCascades"/>.</summary>
    public int CascadeCount
    {
        get => _cascadeCount;
        set => _cascadeCount = Math.Clamp(value, 1, MaxCascades);
    }

    /// <summary>
    /// How far from the camera shadows are drawn. Beyond it surfaces are lit but
    /// never shadowed, which is the open-world trade this type documents.
    /// </summary>
    /// <remarks>
    /// Not the camera's far plane, deliberately: fitting the last cascade to a
    /// kilometre would make even the coarsest one useless. It can be much larger
    /// than a single-cascade map could afford, because only the last cascade
    /// pays for it.
    /// </remarks>
    public float Distance { get; set; } = 60f;

    /// <summary>
    /// How the range is divided between cascades: 0 splits it evenly, 1
    /// logarithmically.
    /// </summary>
    /// <remarks>
    /// Even splits waste the near cascades on slivers of view; logarithmic
    /// splits waste the far ones. The blend is the standard practical scheme,
    /// and this leans toward the log end because texel size is what matters and
    /// the log split is what equalises it.
    /// </remarks>
    public float SplitBlend { get; set; } = 0.88f;

    /// <summary>
    /// The rasterizer's depth offset while the map is drawn: THE acne fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the bias that costs nothing, and <see cref="CompareBias"/>
    /// only exists for the sliver it cannot reach.</b> Acne comes from a
    /// receiver comparing its own
    /// ramping depth against a stored depth that is constant across each texel;
    /// their difference is a sawtooth at texel frequency, and the slope-scaled
    /// term is multiplied by exactly the quantity that sets that sawtooth's
    /// height. See <see cref="Graphics.DepthBias"/>.
    /// </para>
    /// <para>
    /// The numbers are in the graphics API's own units and are the standard
    /// shadow-map starting point. They need no per-cascade scaling, unlike
    /// everything else here, because a depth-buffer unit is already a fraction
    /// of that cascade's own ortho depth range.
    /// </para>
    /// </remarks>
    public DepthBias RasterBias { get; set; } = new(Constant: 2000, SlopeScaled: 2.5f);

    /// <summary>
    /// Radius of the PCF tap circle, in texels of the chosen cascade.
    /// </summary>
    /// <remarks>
    /// Four taps, each bilinearly weighted, so sixteen fetches whatever this
    /// says: widening the filter costs nothing but softness. It is a real dial
    /// rather than a constant because what it trades is a shadow's crispness
    /// against how much of the map's own texel staircase survives, and those
    /// are worth different amounts at different map resolutions.
    /// </remarks>
    public float FilterRadius { get; set; } = 1.2f;

    /// <summary>Constant subtracted from the compared depth, for the sliver <see cref="RasterBias"/> misses.</summary>
    /// <remarks>
    /// Small, and deliberately not slope-scaled: with the raster bias doing the
    /// real work this is a floor against depth-buffer quantisation rather than a
    /// tuning dial. Raising it detaches shadows from their casters, which is the
    /// one failure the light pass cannot compensate for. Named for what it does
    /// rather than <c>DepthBias</c>, which is the TYPE the raster bias uses.
    /// </remarks>
    public float CompareBias { get; set; } = 0.0002f;

    /// <summary>World-to-light-clip for one cascade, for the depth pass to draw with.</summary>
    public Matrix4x4 LightViewProjectionAt(int cascade) => _lightViewProjection[cascade];

    /// <summary>
    /// Per cascade: world position to a lookup in that cascade's own 0..1 space,
    /// with z directly comparable against what the map stores.
    /// </summary>
    /// <remarks>
    /// <b>Every convention difference between the backends is folded in here, on
    /// the CPU, so the shader has none.</b> The Y flip (render targets are
    /// bottom-left on OpenGL and top-left on D3D) and the clip-Z-to-depth-buffer
    /// mapping are both baked in. Doing either in the shader means a shadow that
    /// is upside down or offset in depth on exactly one backend, which produces
    /// no error anywhere.
    /// </remarks>
    public ReadOnlySpan<Matrix4x4> WorldToShadow => _worldToShadow.AsSpan(0, _cascadeCount);

    /// <summary>
    /// Per cascade: the atlas quadrant as (u, v, scale), plus that cascade's
    /// world texel size in w, which is what scales the normal offset.
    /// </summary>
    public ReadOnlySpan<Vector4> CascadeRects => _rects.AsSpan(0, _cascadeCount);

    /// <summary>One texel of a cascade, in that cascade's own 0..1 space. The filter kernel's step.</summary>
    public float TexelSize => 1f / TileResolution;

    /// <summary>World-space texel size of the sharpest cascade. Diagnostics.</summary>
    public float WorldTexelSize => _rects[0].W;

    /// <summary>World-space texel size of the coarsest fitted cascade. Diagnostics.</summary>
    public float CoarsestWorldTexelSize => _rects[_cascadeCount - 1].W;

    /// <summary>The atlas rectangle cascade <paramref name="cascade"/> is drawn into, in texels.</summary>
    public (int X, int Y, int Size) TileAt(int cascade)
    {
        int tile = TileResolution;
        return ((cascade % 2) * tile, (cascade / 2) * tile, tile);
    }

    /// <summary>
    /// Fits every cascade to its slice of <paramref name="camera"/>'s frustum,
    /// lit from <paramref name="lightDirection"/>.
    /// </summary>
    /// <param name="camera">The camera whose view is being shadowed.</param>
    /// <param name="lightDirection">The direction the light TRAVELS, as a <see cref="RenderLight"/> carries it.</param>
    /// <returns>False when nothing could be fitted and no shadow should be drawn.</returns>
    public bool Fit(Camera camera, Vector3 lightDirection)
    {
        ArgumentNullException.ThrowIfNull(camera);

        float near = camera.NearPlane;
        float far = MathF.Min(Distance, camera.FarPlane);
        if (far <= near) return false;

        Span<float> splits = stackalloc float[MaxCascades];
        ComputeSplits(near, far, _cascadeCount, SplitBlend, splits);

        Matrix4x4 ndcToTexture = NdcToShadowTexture(
            _renderer.DepthToNdcZ, _renderer.TargetOriginIsTopLeft);

        float sliceNear = near;
        for (int i = 0; i < _cascadeCount; i++)
        {
            float sliceFar = splits[i];

            // Slices OVERLAP slightly at their boundary. Without it a receiver
            // sitting exactly on a split can fall outside both cascades for a
            // pixel or two and flicker as the camera moves.
            float overlappedNear = i == 0 ? sliceNear : sliceNear * 0.96f;

            if (!TryFitLightMatrix(
                    camera, lightDirection, overlappedNear, sliceFar, TileResolution,
                    out Matrix4x4 lightViewProjection, out float worldTexel))
            {
                return false;
            }

            _lightViewProjection[i] = lightViewProjection;
            _worldToShadow[i] = lightViewProjection * _renderer.ClipZCorrection * ndcToTexture;

            (int x, int y, int size) = TileAt(i);
            _rects[i] = new Vector4(
                x / (float)Resolution, y / (float)Resolution, size / (float)Resolution, worldTexel);

            sliceNear = sliceFar;
        }

        return true;
    }

    /// <summary>
    /// Where each cascade ends, from <paramref name="near"/> to
    /// <paramref name="far"/>.
    /// </summary>
    internal static void ComputeSplits(float near, float far, int count, float blend, Span<float> splits)
    {
        for (int i = 1; i <= count; i++)
        {
            float fraction = i / (float)count;
            float logarithmic = near * MathF.Pow(far / near, fraction);
            float uniform = near + (far - near) * fraction;
            splits[i - 1] = blend * logarithmic + (1f - blend) * uniform;
        }

        // The last one is the shadow distance exactly, whatever the blend
        // rounded it to: it is the number the caller set and the number the
        // documentation quotes.
        splits[count - 1] = far;
    }

    /// <summary>
    /// The fitting itself: pure geometry, no renderer and no GPU, so the two
    /// properties that make a shadow stable can be tested without one.
    /// </summary>
    /// <returns>False when there is nothing sensible to fit.</returns>
    internal static bool TryFitLightMatrix(
        Camera camera,
        Vector3 lightDirection,
        float near,
        float far,
        int resolution,
        out Matrix4x4 lightViewProjection,
        out float worldTexelSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        lightViewProjection = Matrix4x4.Identity;
        worldTexelSize = 0f;

        if (lightDirection.LengthSquared() < 1e-12f) return false;
        if (far <= near) return false;
        Vector3 forward = Vector3.Normalize(lightDirection);

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

        // Rounded up a little: the snap moves the box by up to a texel, and a
        // radius that exactly touches the corners would clip them afterwards.
        radius = MathF.Sqrt(furthest) * 1.02f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.DestroyRenderTarget(_target);
    }
}
