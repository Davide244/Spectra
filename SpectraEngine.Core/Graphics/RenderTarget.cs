using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// How a render target's colour attachment is made available to something
/// outside this renderer, and how the two sides take turns on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>An enum rather than a bool, because a second mode is already visible from
/// here.</b> A keyed mutex is what the shared-surface import on this machine
/// actually accepts today, measured rather than assumed; a shared fence is the
/// other way the same handshake is expressed, and it is not offered until a
/// backend can honour it. A bool would have to be widened the moment that lands,
/// and every call site written against it would have to be revisited.
/// </para>
/// </remarks>
public enum RenderTargetSharing
{
    /// <summary>Not shared. The attachment belongs to this renderer alone.</summary>
    None,

    /// <summary>
    /// Shared with a keyed mutex: the producer acquires key 0 and releases key
    /// 1, the consumer acquires key 1 and releases key 0. See
    /// <see cref="Renderer.BeginSharedWrite"/>, which is where that protocol is
    /// written down.
    /// </summary>
    KeyedMutex,
}

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
/// <param name="Color">
/// Whether to attach colour at all. False makes a depth-only target, which is
/// what a shadow map is; see <see cref="DepthOnly"/>.
/// </param>
/// <param name="Sharing">
/// Whether the colour attachment is created so something outside this renderer
/// can import it, and under which handshake. See <see cref="RenderTargetSharing"/>.
/// </param>
public readonly record struct RenderTargetDesc(
    int Width,
    int Height,
    TextureFormat ColorFormat = TextureFormat.Rgba8,
    TextureColorSpace ColorSpace = TextureColorSpace.Linear,
    bool Depth = true,
    TextureFilter Filter = TextureFilter.Linear,
    TextureWrap Wrap = TextureWrap.Clamp,
    bool Color = true,
    RenderTargetSharing Sharing = RenderTargetSharing.None)
{
    /// <summary>
    /// A target with depth and nothing else: a shadow map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not an optimisation, a correctness choice, and a large one.</b> A
    /// 2048-square colour attachment nobody reads is 16 MB per cascade, and
    /// binding no render target at all is what lets the hardware take its
    /// double-speed depth-only path, which is the difference between a shadow
    /// pass costing half a geometry pass and costing a whole one.
    /// </para>
    /// <para>
    /// The depth attachment samples <b>nearest</b>, which every backend already
    /// forces, and that is correct rather than a limitation: linear filtering of
    /// DEPTH averages two distances and returns one that lies on neither
    /// surface. A PCF kernel filters the comparison RESULTS instead, by taking
    /// several point taps and averaging the booleans, which is what the light
    /// pass does.
    /// </para>
    /// </remarks>
    public static RenderTargetDesc DepthOnly(int size) => DepthOnly(size, size);

    /// <inheritdoc cref="DepthOnly(int)"/>
    public static RenderTargetDesc DepthOnly(int width, int height) => new(
        width, height, TextureFormat.Rgba8, TextureColorSpace.Linear,
        Depth: true, TextureFilter.Linear, TextureWrap.Clamp, Color: false);

    /// <summary>Throws if this description cannot be built. Called by every backend before allocating.</summary>
    public void Validate()
    {
        if (Width <= 0 || Height <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(Width), $"A render target needs a positive size; got {Width}x{Height}.");

        // A target with neither attachment is a pass that can write nothing,
        // which is a caller mistake rather than a configuration.
        if (!Color && !Depth)
            throw new ArgumentException(
                "A render target needs at least one attachment; both Color and Depth are false.",
                nameof(Color));

        // The attachment is created as a render target, and no backend can make
        // one out of a three-channel format: D3D has no 24-bit format at all.
        // R8 is excluded for a different reason: nothing needs a one-channel
        // colour target, and the shadow maps that might have wanted one carry no
        // colour at all. Rejecting here beats a backend-specific failure three
        // layers down.
        if (Color && ColorFormat is not (TextureFormat.Rgba8 or TextureFormat.Rgba16Float))
            throw new ArgumentOutOfRangeException(
                nameof(ColorFormat),
                $"Render targets support {nameof(TextureFormat.Rgba8)} and " +
                $"{nameof(TextureFormat.Rgba16Float)}; got {ColorFormat}.");

        if (Sharing == RenderTargetSharing.None)
            return;

        // A depth-only target has no colour attachment, so there is nothing to
        // hand out: asking to share one is a caller mistake and not a
        // configuration a backend could honour.
        if (!Color)
            throw new ArgumentException(
                "A shared render target needs a colour attachment; a depth-only target has nothing to share.",
                nameof(Sharing));

        // The external-image import a shared handle exists to feed has no
        // half-float path AT ALL, so an Rgba16Float shared target is a request
        // nothing can satisfy. Refused here, where the caller wrote it, rather
        // than three layers down as a driver HRESULT that names neither the
        // format nor the target.
        if (ColorFormat != TextureFormat.Rgba8)
            throw new ArgumentOutOfRangeException(
                nameof(ColorFormat),
                $"A shared render target must be {nameof(TextureFormat.Rgba8)}; got {ColorFormat}. " +
                "The import that consumes a shared handle has no half-float format.");
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
/// <b>That guarantee does NOT extend to a shared handle
/// (<see cref="RenderTargetSharing"/>).</b> A shared target is destroyed and
/// recreated under a new <see cref="Renderer.SharedTargetHandle.Generation"/>,
/// never resized in place. The wrapper's identity surviving is exactly what
/// would let a caller assume the handle survived with it, and the consumer on
/// the other side of that handle would go on sampling a resource that has been
/// destroyed - which produces no error on either side, only a picture that has
/// stopped changing or a device removal some frames later.
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
    /// The colour attachment, bindable into any material, or null on a
    /// depth-only target. Its identity is stable across <see cref="Resize"/>.
    /// </summary>
    public abstract Texture? ColorTexture { get; }

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
