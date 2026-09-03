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

    /// <summary>
    /// 32-bit float depth. A render-target attachment only, and the one format
    /// whose GPU resource is created in a <i>typeless</i> family so it can carry
    /// two views: a depth-stencil view to write through and a red-channel float
    /// view to read through.
    /// </summary>
    /// <remarks>
    /// D3D refuses to create a shader-resource view over a resource declared
    /// <c>D32_FLOAT</c>; the resource has to be <c>R32_TYPELESS</c> and the
    /// depth-ness has to live on the view. Getting that wrong fails loudly at
    /// creation rather than silently, which is the one mercy of this corner of
    /// the API.
    /// </remarks>
    Depth32Float,

    /// <summary>
    /// BC1 (DXT1): 4x4 blocks, 8 bytes each, RGB plus one bit of alpha. Four
    /// times smaller than <see cref="Rgba8"/>, and the cheapest thing a cook can
    /// do to an opaque albedo.
    /// </summary>
    /// <remarks>
    /// <b>Every block-compressed format here is a COOKED format and nothing
    /// else.</b> No decoder in the engine produces one, no editor path writes
    /// one, and the runtime never compresses: the blocks arrive already
    /// assembled, with their per-mip row pitches stated by the file that carries
    /// them. See <see cref="TextureUploadDesc"/> for why those pitches are
    /// carried rather than recomputed.
    /// </remarks>
    Bc1,

    /// <summary>
    /// BC3 (DXT5): 4x4 blocks, 16 bytes each, RGB plus a separately
    /// interpolated 8-bit alpha. What an albedo with real transparency cooks to.
    /// </summary>
    Bc3,

    /// <summary>
    /// BC4 (RGTC1): 4x4 blocks, 8 bytes each, one interpolated channel. A mask,
    /// a height field, an AO or a roughness map on its own.
    /// </summary>
    /// <remarks>
    /// <b>A data format: no API here defines an sRGB BC4</b>, for exactly the
    /// reason <see cref="R8"/> has none, and
    /// <see cref="TextureFormatInfo.SupportsSrgb"/> is the one place that is
    /// written down.
    /// </remarks>
    Bc4,

    /// <summary>
    /// BC5 (RGTC2): 4x4 blocks, 16 bytes each, two interpolated channels. The
    /// normal-map format - x and y stored, z reconstructed - and a data format
    /// with no sRGB variant.
    /// </summary>
    Bc5,

    /// <summary>
    /// BC6H: 4x4 blocks, 16 bytes each, three HALF-FLOAT channels. What an HDR
    /// source - a sky, a reflection probe, a light cookie - cooks to.
    /// </summary>
    /// <remarks>
    /// <b>No sRGB variant, for the same reason <see cref="Rgba16Float"/> has
    /// none</b>: the transfer curve exists to ration a small number of integer
    /// codes, and a float format has none to ration. But BC6H is deliberately
    /// NOT one of <see cref="TextureFormatInfo.IsFloat"/>'s answers: that
    /// predicate asks whether a byte array can fill the texture, and what fills
    /// this one is compressed BLOCKS, which are bytes however they decode.
    /// </remarks>
    Bc6H,

    /// <summary>
    /// BC7: 4x4 blocks, 16 bytes each, RGBA at the highest quality the BC family
    /// reaches. The default for a cooked colour texture that can afford 8 bits
    /// per pixel.
    /// </summary>
    Bc7,
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
        format is TextureFormat.Rgba8 or TextureFormat.Rgb8
            or TextureFormat.Bc1 or TextureFormat.Bc3 or TextureFormat.Bc7;

    /// <summary>
    /// Whether <paramref name="format"/> stores floating-point channels, and so
    /// cannot be filled from a byte array.
    /// </summary>
    /// <remarks>
    /// <b><see cref="TextureFormat.Bc6H"/> is deliberately not here.</b> Its
    /// channels really are half-floats, and this predicate is not about the
    /// channels: it is the guard the CPU-upload paths use to refuse a format a
    /// byte array cannot fill, and what fills a BC6H texture is compressed
    /// blocks, which are bytes. Answering "float" here would refuse the one
    /// HDR format the cooker can actually produce.
    /// </remarks>
    public static bool IsFloat(TextureFormat format) =>
        format is TextureFormat.Rgba16Float or TextureFormat.Depth32Float;

    /// <summary>
    /// Whether <paramref name="format"/> stores 4x4 blocks rather than
    /// individual texels.
    /// </summary>
    public static bool IsBlockCompressed(TextureFormat format) => format
        is TextureFormat.Bc1 or TextureFormat.Bc3 or TextureFormat.Bc4
        or TextureFormat.Bc5 or TextureFormat.Bc6H or TextureFormat.Bc7;

    /// <summary>
    /// The width in texels of one addressable unit: 4 for a block-compressed
    /// format, 1 otherwise.
    /// </summary>
    public static int BlockWidth(TextureFormat format) => IsBlockCompressed(format) ? 4 : 1;

    /// <summary>The height in texels of one addressable unit. See <see cref="BlockWidth"/>.</summary>
    public static int BlockHeight(TextureFormat format) => IsBlockCompressed(format) ? 4 : 1;

    /// <summary>
    /// Bytes in one addressable unit: one block for a compressed format, one
    /// texel otherwise.
    /// </summary>
    /// <remarks>
    /// Throws for the two render-target-only formats rather than returning a
    /// size for them. Nothing uploads either from the CPU, so a number here
    /// would only ever be used to size an upload that
    /// <see cref="TextureUploadDesc.Validate"/> is about to refuse anyway, and a
    /// plausible-looking answer is how such a call slips through.
    /// </remarks>
    public static int BytesPerBlock(TextureFormat format) => format switch
    {
        TextureFormat.Rgba8 => 4,
        TextureFormat.Rgb8 => 3,
        TextureFormat.R8 => 1,
        TextureFormat.Bc1 or TextureFormat.Bc4 => 8,
        TextureFormat.Bc3 or TextureFormat.Bc5 or TextureFormat.Bc6H or TextureFormat.Bc7 => 16,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), $"{format} has no CPU-side block size; it is a render-target format."),
    };

    /// <summary>
    /// How many rows a mip of <paramref name="height"/> texels occupies: texel
    /// rows for an uncompressed format, BLOCK rows for a compressed one.
    /// </summary>
    /// <remarks>
    /// <b>This is derived and the row PITCH is not, and the difference is not an
    /// inconsistency.</b> A pitch depends on whatever alignment the tool that
    /// wrote the file chose, which is why <see cref="TextureUploadDesc"/> carries
    /// it; a row count is exactly the number of block rows the height covers,
    /// which no writer is free to disagree about. D3D's own
    /// <c>GetCopyableFootprints</c> reports the same value.
    /// </remarks>
    public static int RowCount(TextureFormat format, int height)
    {
        int blockHeight = BlockHeight(format);
        return (height + blockHeight - 1) / blockHeight;
    }

    /// <summary>
    /// The smallest row pitch that can describe a mip of <paramref name="width"/>
    /// texels: the tight, unpadded one.
    /// </summary>
    /// <remarks>
    /// A validation FLOOR, never a substitute for the declared pitch. Padding is
    /// always upward, so a declared pitch below this cannot describe the row at
    /// all and is a malformed file rather than an unusual alignment.
    /// </remarks>
    public static int TightRowPitch(TextureFormat format, int width)
    {
        int blockWidth = BlockWidth(format);
        return (width + blockWidth - 1) / blockWidth * BytesPerBlock(format);
    }

    /// <summary>Whether <paramref name="format"/> is a depth format rather than a colour one.</summary>
    public static bool IsDepth(TextureFormat format) => format is TextureFormat.Depth32Float;

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
