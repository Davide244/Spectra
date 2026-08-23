using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The surfaces a deferred geometry pass writes, and a light pass reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Five colour attachments plus depth, and the layout is a contract.</b>
/// Every material shader writes it and the light pass reads it, so changing a
/// channel means touching both ends at once. It is written down here rather than
/// only in the shaders because those are two files that can drift.
/// </para>
/// <code>
/// RT0  RGBA8 sRGB   albedo.rgb        ambient occlusion.a
/// RT1  RGBA16F      normal.rgb        roughness.a
/// RT2  RGBA8        metallic.r        shadingModel.g       (spare .ba)
/// RT3  RGBA16F      emissive.rgb      (spare .a)
/// RT4  RGBA16F      custom.rgba       per shading model
/// depth R32 typeless, sampled         world position, by reconstruction
/// </code>
/// <para>
/// <b>Position is not stored.</b> It reconstructs exactly from depth and the
/// pixel's screen coordinate, and storing it instead would cost an entire
/// RGBA16F for information the depth buffer already holds.
/// </para>
/// <para>
/// <b>The custom channel is what keeps this from growing.</b> Subsurface
/// scattering, clearcoat and anisotropy are different shading models, not
/// different BRDF parameters; giving each its own attachment is how a G-buffer
/// reaches eight surfaces and stays there. Instead <c>shadingModel</c> says how
/// to read <c>custom</c>, so a new model costs an enum value and a branch in the
/// light pass rather than a migration of every material shader.
/// </para>
/// <para>
/// <b>Albedo is the only sRGB attachment.</b> It is a colour a person picked and
/// benefits from the transfer curve's precision near black; everything else is
/// either a direction, a linear coefficient or a value with range above one, and
/// encoding those would be actively wrong.
/// </para>
/// <para>
/// Honest cost: about 36 bytes per pixel, so roughly 75 MB written per frame at
/// 1080p. That is the deferred bargain, and it is why deferred loses to forward
/// on low-end hardware with few lights.
/// </para>
/// </remarks>
public sealed class GBuffer : IDisposable
{
    /// <summary>How many colour attachments the layout uses.</summary>
    public const int AttachmentCount = 5;

    private readonly Renderer _renderer;
    private readonly RenderTarget[] _targets = new RenderTarget[AttachmentCount];
    private bool _disposed;

    /// <summary>Creates the whole set at one size. Render thread.</summary>
    public GBuffer(Renderer renderer, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;

        // Only the first carries depth: it is shared by the whole pass, and one
        // depth buffer per attachment would be four full-screen surfaces
        // allocated and never read.
        _targets[0] = renderer.CreateRenderTarget(new RenderTargetDesc(
            width, height, TextureFormat.Rgba8, TextureColorSpace.Srgb, Depth: true));
        _targets[1] = renderer.CreateRenderTarget(new RenderTargetDesc(
            width, height, TextureFormat.Rgba16Float, TextureColorSpace.Linear, Depth: false));
        _targets[2] = renderer.CreateRenderTarget(new RenderTargetDesc(
            width, height, TextureFormat.Rgba8, TextureColorSpace.Linear, Depth: false));
        _targets[3] = renderer.CreateRenderTarget(new RenderTargetDesc(
            width, height, TextureFormat.Rgba16Float, TextureColorSpace.Linear, Depth: false));
        _targets[4] = renderer.CreateRenderTarget(new RenderTargetDesc(
            width, height, TextureFormat.Rgba16Float, TextureColorSpace.Linear, Depth: false));

        Width = width;
        Height = height;
    }

    /// <summary>Current width, shared by every attachment.</summary>
    public int Width { get; private set; }

    /// <summary>Current height, shared by every attachment.</summary>
    public int Height { get; private set; }

    // Every attachment is created with colour above, so the null-forgiving
    // operator on each accessor below is a statement about this constructor
    // rather than a hope: a depth-only G-buffer attachment would be a surface
    // the geometry shader writes and nothing can read.

    /// <summary>The attachments, in binding order. Pass this to <c>BeginPass</c>.</summary>
    public ReadOnlySpan<RenderTarget> Targets => _targets;

    /// <summary>Albedo in rgb, ambient occlusion in a.</summary>
    public Texture Albedo => _targets[0].ColorTexture!;

    /// <summary>World normal in rgb, roughness in a.</summary>
    public Texture NormalRoughness => _targets[1].ColorTexture!;

    /// <summary>Metallic in r, shading-model id in g.</summary>
    public Texture MaterialData => _targets[2].ColorTexture!;

    /// <summary>Emissive radiance in rgb.</summary>
    public Texture Emissive => _targets[3].ColorTexture!;

    /// <summary>Whatever the shading model in <see cref="MaterialData"/> says this means.</summary>
    public Texture Custom => _targets[4].ColorTexture!;

    /// <summary>Depth, for reconstructing world position. Never null: attachment 0 always has it.</summary>
    public Texture Depth => _targets[0].DepthTexture!;

    /// <summary>
    /// Resizes every attachment together. Free when the size is unchanged, which
    /// is what lets a caller say "match the window" every frame.
    /// </summary>
    /// <remarks>
    /// They must stay the same size: one rasterisation writes all of them, and
    /// <c>BeginPass</c> refuses a mismatched set.
    /// </remarks>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == Width && height == Height) return;

        foreach (RenderTarget target in _targets)
            target.Resize(width, height);

        Width = width;
        Height = height;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (RenderTarget target in _targets)
            _renderer.DestroyRenderTarget(target);
    }
}
