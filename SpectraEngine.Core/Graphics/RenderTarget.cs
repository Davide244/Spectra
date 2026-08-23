using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// What an offscreen render target is made of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Depth is a bool, not a format, and it is always sampleable.</b> A deferred
/// light pass reconstructs world position from depth rather than storing it,
/// which saves an entire RGB channel of G-buffer, so depth being readable is not
/// an extra: it is the point. Every backend therefore allocates depth as a
/// texture in a typeless family, with a depth-stencil view for writing and a
/// float view for reading.
/// </para>
/// <para>
/// <b>Reading depth needs no comparison sampler.</b> A plain <c>sampler2D</c>
/// returns the depth value in <c>.r</c> on all three backends. The comparison
/// sampling that shadow-map filtering wants is a separate and much larger piece
/// of work, and deliberately not required here.
/// </para>
/// <para>
/// <b>The colour space is the same choice a texture makes</b>, because the
/// attachment is a texture. A target that a later pass will read as an image
/// and encode wants <see cref="TextureColorSpace.Srgb"/>; an intermediate that
/// carries light values through a chain of passes wants
/// <see cref="TextureColorSpace.Linear"/>, and that is what an HDR pipeline will
/// use once `R4` gives it somewhere to tone-map.
/// </para>
/// </remarks>
/// <param name="Width">Pixel width. Must be positive.</param>
/// <param name="Height">Pixel height. Must be positive.</param>
/// <param name="ColorFormat">
/// Format of the single colour attachment: <see cref="TextureFormat.Rgba8"/> for
/// a display-ready image, <see cref="TextureFormat.Rgba16Float"/> for an
/// intermediate carrying linear light between passes.
/// </param>
/// <param name="ColorSpace">Whether the colour attachment encodes on write and decodes on read.</param>
/// <param name="Depth">Whether to attach a depth buffer. Anything drawing 3D geometry wants one.</param>
/// <param name="Filter">How the colour attachment samples. Mipmaps are not generated for targets.</param>
/// <param name="Wrap">Wrap mode of the colour attachment.</param>
public readonly record struct RenderTargetDesc(
    int Width,
    int Height,
    TextureFormat ColorFormat = TextureFormat.Rgba8,
    TextureColorSpace ColorSpace = TextureColorSpace.Linear,
    bool Depth = true,
    TextureFilter Filter = TextureFilter.Linear,
    TextureWrap Wrap = TextureWrap.Clamp)
{
    /// <summary>Throws if this description cannot be built. Called by every backend before allocating.</summary>
    public void Validate()
    {
        if (Width <= 0 || Height <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(Width), $"A render target needs a positive size; got {Width}x{Height}.");

        // The attachment is created as a render target, and no backend can make
        // one out of a three-channel format: D3D has no 24-bit format at all.
        // R8 is excluded for a different reason: nothing needs a one-channel
        // target until R6 wants a shadow map, and that milestone brings the
        // typeless-depth work with it. Rejecting here beats a backend-specific
        // failure three layers down.
        if (ColorFormat is not (TextureFormat.Rgba8 or TextureFormat.Rgba16Float))
            throw new ArgumentOutOfRangeException(
                nameof(ColorFormat),
                $"Render targets support {nameof(TextureFormat.Rgba8)} and " +
                $"{nameof(TextureFormat.Rgba16Float)}; got {ColorFormat}.");
    }
}

/// <summary>
/// An offscreen surface a pass can draw into, whose colour attachment is a plain
/// <see cref="Texture"/> that anything else can sample.
/// </summary>
/// <remarks>
/// <para>
/// <b>The attachment is a plain <see cref="Texture"/> on purpose.</b> That one
/// decision is why shadows, post-processing and material previews need no new
/// binding concept: a material binds <see cref="ColorTexture"/> exactly as it
/// binds a PNG, through the sampler path that already exists.
/// </para>
/// <para>
/// <b><see cref="ColorTexture"/>'s object identity survives a resize.</b> The
/// GPU handle inside is swapped; the wrapper is not replaced. Without that,
/// every editor-viewport resize would leave every material that sampled the
/// viewport pointing at a destroyed texture, which is the same trick
/// <c>ShaderProgram.TryReload</c> and <c>TextureAsset</c> already use and for
/// the same reason.
/// </para>
/// <para>
/// Owned by the renderer that created it, like meshes and textures: callers
/// call <see cref="Renderer.DestroyRenderTarget"/> or let shutdown clean up.
/// Render thread only.
/// </para>
/// </remarks>
public abstract class RenderTarget : IDisposable
{
    /// <summary>Current pixel width.</summary>
    public int Width { get; protected set; }

    /// <summary>Current pixel height.</summary>
    public int Height { get; protected set; }

    /// <summary>What this target was created from. Size fields are the original ones, not the current.</summary>
    public RenderTargetDesc Desc { get; protected set; }

    /// <summary>
    /// The colour attachment, bindable into any material. Its identity is
    /// stable across <see cref="Resize"/>.
    /// </summary>
    public abstract Texture ColorTexture { get; }

    /// <summary>
    /// The depth attachment as a sampleable texture, or null when this target
    /// was created without depth. Its identity is stable across
    /// <see cref="Resize"/>, exactly like <see cref="ColorTexture"/>.
    /// </summary>
    public abstract Texture? DepthTexture { get; }

    /// <summary>
    /// Resizes in place, keeping <see cref="ColorTexture"/>'s identity. A call
    /// that does not change the size is free.
    /// </summary>
    public abstract void Resize(int width, int height);

    /// <summary>
    /// Removes this target from the creating renderer's tracking list. Same
    /// contract as <see cref="Texture.Unregister"/>.
    /// </summary>
    internal Action? Unregister { get; set; }

    public abstract void Dispose();
}
