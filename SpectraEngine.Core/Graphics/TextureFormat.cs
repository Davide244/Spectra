namespace SpectraEngine.Core.Graphics;

/// <summary>Pixel format for <see cref="Texture"/> data uploads.</summary>
public enum TextureFormat
{
    /// <summary>8 bits per channel, four channels (RGBA).</summary>
    Rgba8,

    /// <summary>8 bits per channel, three channels (RGB).</summary>
    Rgb8,

    /// <summary>8 bits, single channel (red / luminance / mask).</summary>
    R8,

    /// <summary>
    /// 16-bit float per channel, four channels. A render-target format, not an
    /// upload format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes an intermediate target worth having.</b> A post
    /// chain that renders the scene into an 8-bit buffer and tone-maps out of
    /// it is worse than no chain at all: the intermediate is linear light, and
    /// 8 bits of linear spends almost all its codes on highlights nobody can
    /// distinguish while leaving visible banding in the shadows, which is
    /// precisely the problem the sRGB curve exists to solve. Half-float keeps
    /// the dynamic range that exposure and a tone curve then have something to
    /// work with.
    /// </para>
    /// <para>
    /// <b>No sRGB variant, and that is not a limitation.</b> The transfer
    /// function is a way of spending a small number of integer codes well; a
    /// float format has no such problem and stores linear values directly. See
    /// <see cref="TextureFormatInfo.SupportsSrgb"/>.
    /// </para>
    /// <para>
    /// <b>Nothing decodes an image file to this.</b> <c>ImageDecoder</c> only
    /// ever produces the three byte formats, so the CPU-upload paths refuse it
    /// rather than pretending a byte array could be half-floats.
    /// </para>
    /// </remarks>
    Rgba16Float,
}

/// <summary>
/// How the bytes in a texture should be interpreted: as sRGB-encoded colour, or
/// as raw linear values.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a property of the content, not of the sampler.</b> A photograph of
/// a brick wall and a normal map may be byte-identical RGBA8 files; one is a
/// picture of light and the other is a table of vectors, and only the author
/// knows which. Getting it wrong is not subtle in either direction: a normal map
/// decoded as sRGB bends every surface, and an albedo left linear renders dark
/// and muddy.
/// </para>
/// <para>
/// <b>The conversion is done by the hardware sampler, never in a shader.</b>
/// That is the whole reason this is a format flag rather than a
/// <c>pow()</c> in <c>Lit.spectrashade</c>: the sampler decodes each texel
/// <i>before</i> filtering, so bilinear taps and mip levels average light rather
/// than display codes. On the tiled brush surfaces that cover most of this
/// engine's screen, that difference is the difference between a wall that stays
/// the same brightness into the distance and one that darkens.
/// </para>
/// <para>
/// <b>Not every format can be sRGB.</b> There is no single-channel sRGB format
/// on any of the three backends, so <see cref="TextureFormat.R8"/> is always
/// linear; see <see cref="TextureFormatInfo.SupportsSrgb"/>, which is the one
/// place that rule lives so the backends cannot disagree about it.
/// </para>
/// </remarks>
public enum TextureColorSpace
{
    /// <summary>Raw values, uploaded and sampled untouched. Normal, roughness, metallic, AO, masks.</summary>
    Linear,

    /// <summary>sRGB-encoded colour, decoded to linear by the sampler. Albedo, emissive.</summary>
    Srgb,
}

/// <summary>Facts about a <see cref="TextureFormat"/> that all three backends must agree on.</summary>
public static class TextureFormatInfo
{
    /// <summary>
    /// Whether an sRGB variant of <paramref name="format"/> exists in hardware.
    /// False for <see cref="TextureFormat.R8"/> (neither DXGI nor GL defines a
    /// one-channel sRGB format) and for <see cref="TextureFormat.Rgba16Float"/>
    /// (a float format stores linear values; the curve exists to ration integer
    /// codes, and there are none to ration).
    /// </summary>
    public static bool SupportsSrgb(TextureFormat format) =>
        format is TextureFormat.Rgba8 or TextureFormat.Rgb8;

    /// <summary>
    /// Whether <paramref name="format"/> stores floating-point channels, and so
    /// cannot be filled from a byte array.
    /// </summary>
    public static bool IsFloat(TextureFormat format) => format is TextureFormat.Rgba16Float;

    /// <summary>
    /// The colour space a texture will <i>actually</i> get, which is the
    /// requested one unless the format cannot carry it.
    /// </summary>
    /// <remarks>
    /// Backends call this instead of testing the format themselves, so a request
    /// that cannot be honoured degrades to linear identically everywhere rather
    /// than throwing on one backend and silently working on another. The
    /// downgrade is reported once, with a file path, by
    /// <c>AssetManager</c>, which is the layer that knows which file it was.
    /// </remarks>
    public static TextureColorSpace Resolve(TextureFormat format, TextureColorSpace requested) =>
        requested == TextureColorSpace.Srgb && SupportsSrgb(format)
            ? TextureColorSpace.Srgb
            : TextureColorSpace.Linear;
}

/// <summary>Magnification and minification filtering applied when sampling.</summary>
public enum TextureFilter
{
    /// <summary>Point sampling — sharp pixels, no blending. Good for pixel art.</summary>
    Nearest,

    /// <summary>Bilinear interpolation between the four nearest texels.</summary>
    Linear,

    /// <summary>Linear with trilinear mipmap interpolation; requires mipmaps.</summary>
    LinearMipmap,
}

/// <summary>Wrap behaviour when UV coordinates fall outside [0,1].</summary>
public enum TextureWrap
{
    /// <summary>Tile the texture infinitely.</summary>
    Repeat,

    /// <summary>Clamp to the nearest edge pixel.</summary>
    Clamp,
}
