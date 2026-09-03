using SpectraEngine.Core.Graphics;
using System;

namespace SpectraEngine.Core.Assets.Images;

/// <summary>
/// Which end of the picture the first stored row is.
/// </summary>
/// <remarks>
/// <b>KTX2 has a key for this and it is the reason a cooked image can be honest
/// rather than merely convenient.</b> The engine's sampling convention is that
/// v = 0 is the BOTTOM of the picture (see
/// <see cref="Graphics.Renderer.CreateTexture(in TextureUploadDesc)"/>), and a
/// block-compressed payload cannot be flipped at load: BC6H and BC7 blocks
/// cannot be reversed without a full decode and re-encode. So the flip happens
/// once, at cook time, on the decoded texels before they are ever compressed,
/// and the file SAYS so through <c>KTXorientation</c> instead of leaving the
/// next tool to guess. A file that does not say is refused, because an
/// upside-down world raises nothing anywhere.
/// </remarks>
public enum SimageRowOrder
{
    /// <summary>
    /// <c>KTXorientation = "ru"</c>: the first stored row is the bottom of the
    /// picture, which is the row the engine samples at v = 0.
    /// </summary>
    BottomUp,

    /// <summary>
    /// <c>KTXorientation = "rd"</c>: the first stored row is the top of the
    /// picture, which is what every other image format and every external KTX2
    /// tool means.
    /// </summary>
    /// <remarks>
    /// Read and named rather than silently accepted. Nothing in the engine can
    /// consume one today - honouring it needs the v = 0 convention to move to
    /// the top of the picture and the compensating flip to land once in UV
    /// generation, which is a content-visible change to brush faces and imported
    /// models rather than anything an uploader can do.
    /// </remarks>
    TopDown,
}

/// <summary>
/// The KTX2 container constants a <c>.simage</c> is written from and read back
/// against, in one place so the writer and the reader cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>.simage</c> is a RESTRICTED PROFILE of spec-conformant KTX2, not a
/// format of this engine's own.</b> There is no Spectra magic number here: the
/// file opens with KTX2's own 12-byte identifier, so <c>toktx</c>, KTX-Software,
/// RenderDoc and any GPU debugger read the same bytes. The extension is the
/// user's vocabulary and is cosmetic.
/// </para>
/// <para>
/// <b>What the profile refuses is the whole point of having one.</b> A
/// conforming KTX2 reader must parse a variable-length Data Format Descriptor
/// and, for BasisLZ supercompression, ship an entire transcoder. This one
/// accepts two supercompression schemes, four container shapes and a small
/// <see cref="TryResolveVkFormat"/> allowlist, and refuses everything else BY
/// NAME. See <c>docs/formats-and-pipeline.md</c> section 2.2.
/// </para>
/// <para>
/// <b>Every number here is little-endian, by KTX2 spec</b>, which is why the
/// reader uses <c>BinaryPrimitives</c> throughout rather than reinterpreting
/// structs: a big-endian host reading a struct cast would produce enormous
/// plausible-looking dimensions rather than failing.
/// </para>
/// </remarks>
public static class SimageFormat
{
    /// <summary>The cooked extension, dot included.</summary>
    public const string FileExtension = ".simage";

    /// <summary>KTX2's 12-byte file identifier.</summary>
    /// <remarks>
    /// The <c>0xAB</c>/<c>0xBB</c> pair and the CR-LF-SUB-LF tail are a
    /// deliberate transfer check in the spec: a file mangled by a text-mode copy
    /// stops matching here rather than three fields later.
    /// </remarks>
    public static ReadOnlySpan<byte> Identifier =>
        [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Bytes from the start of the file to <c>vkFormat</c>.</summary>
    public const int HeaderOffset = 12;

    /// <summary>Bytes in the fixed header and index, i.e. where the level index starts.</summary>
    /// <remarks>
    /// 12 identifier + 36 header + 32 index. Stated as a constant rather than
    /// summed at each site because it is the offset every read is relative to.
    /// </remarks>
    public const int LevelIndexOffset = 80;

    /// <summary>Bytes in one level-index entry: three <c>uint64</c>.</summary>
    public const int LevelIndexEntrySize = 24;

    /// <summary>No supercompression: level bytes are the payload.</summary>
    public const uint SupercompressionNone = 0;

    /// <summary>BasisLZ. Refused by name - it needs a transcoder this engine will never carry.</summary>
    public const uint SupercompressionBasisLz = 1;

    /// <summary>Zstandard, per level.</summary>
    public const uint SupercompressionZstd = 2;

    /// <summary>ZLIB, per level.</summary>
    public const uint SupercompressionZlib = 3;

    /// <summary>
    /// The KTX2 standard key naming which way up the rows are stored.
    /// </summary>
    public const string OrientationKey = "KTXorientation";

    /// <summary>Value of <see cref="OrientationKey"/> for <see cref="SimageRowOrder.BottomUp"/>.</summary>
    public const string OrientationBottomUp = "ru";

    /// <summary>Value of <see cref="OrientationKey"/> for <see cref="SimageRowOrder.TopDown"/>.</summary>
    public const string OrientationTopDown = "rd";

    /// <summary>
    /// The key carrying <c>EngineInfo.TextureFormatVersion</c>, as a
    /// NUL-terminated ASCII decimal.
    /// </summary>
    /// <remarks>
    /// <b>The engine's PROFILE is versioned, never the container.</b> KTX2 has
    /// its own versioning and this file is valid KTX2 whatever this says; what
    /// moves is what the cooker chose to put in one - which formats, which row
    /// order, which mip policy. A decimal string rather than four raw bytes so
    /// that <c>ktx info</c> and any other KTX2 tool prints something a person can
    /// read, which is most of the reason for using a standard container at all.
    /// </remarks>
    public const string ProfileKey = "SpectraProfile";

    // --- the vkFormat allowlist ---------------------------------------------
    //
    // An ALLOWLIST rather than a blocklist, and that asymmetry is the whole
    // safety property: an unknown vkFormat refused by name costs a recook, while
    // an unknown vkFormat guessed at is a block size and a row pitch computed
    // for the wrong layout, which reads past the end of a mapped view.

    /// <summary>VK_FORMAT_R8_UNORM.</summary>
    public const uint VkFormatR8Unorm = 9;

    /// <summary>VK_FORMAT_R8G8B8A8_UNORM.</summary>
    public const uint VkFormatR8G8B8A8Unorm = 37;

    /// <summary>VK_FORMAT_R8G8B8A8_SRGB.</summary>
    public const uint VkFormatR8G8B8A8Srgb = 43;

    /// <summary>VK_FORMAT_BC1_RGB_UNORM_BLOCK.</summary>
    public const uint VkFormatBc1RgbUnormBlock = 131;

    /// <summary>VK_FORMAT_BC1_RGB_SRGB_BLOCK.</summary>
    public const uint VkFormatBc1RgbSrgbBlock = 132;

    /// <summary>VK_FORMAT_BC1_RGBA_UNORM_BLOCK.</summary>
    public const uint VkFormatBc1RgbaUnormBlock = 133;

    /// <summary>VK_FORMAT_BC1_RGBA_SRGB_BLOCK.</summary>
    public const uint VkFormatBc1RgbaSrgbBlock = 134;

    /// <summary>VK_FORMAT_BC3_UNORM_BLOCK.</summary>
    public const uint VkFormatBc3UnormBlock = 137;

    /// <summary>VK_FORMAT_BC3_SRGB_BLOCK.</summary>
    public const uint VkFormatBc3SrgbBlock = 138;

    /// <summary>VK_FORMAT_BC4_UNORM_BLOCK.</summary>
    public const uint VkFormatBc4UnormBlock = 139;

    /// <summary>VK_FORMAT_BC5_UNORM_BLOCK.</summary>
    public const uint VkFormatBc5UnormBlock = 141;

    /// <summary>VK_FORMAT_BC6H_UFLOAT_BLOCK.</summary>
    public const uint VkFormatBc6HUfloatBlock = 143;

    /// <summary>VK_FORMAT_BC7_UNORM_BLOCK.</summary>
    public const uint VkFormatBc7UnormBlock = 145;

    /// <summary>VK_FORMAT_BC7_SRGB_BLOCK.</summary>
    public const uint VkFormatBc7SrgbBlock = 146;

    /// <summary>
    /// Turns a <c>vkFormat</c> into the engine's own format and the colour space
    /// the file DECLARES, or false for anything not on the allowlist.
    /// </summary>
    /// <remarks>
    /// <b>The declared colour space is what the file says, never what a texture
    /// gets.</b> Whether a block of bytes is colour or data is a property of the
    /// material SLOT rather than of the image - the same file is legitimately an
    /// albedo in one material and a mask in another, which is exactly why
    /// <c>AssetManager</c>'s cache key carries the colour space - so the loader
    /// passes the CALLER's request to the upload and this answer travels only
    /// for diagnostics and for external tools. The cooker writes the UNORM forms
    /// for that reason; the sRGB ones are here so a file some other tool wrote is
    /// read rather than refused.
    /// </remarks>
    public static bool TryResolveVkFormat(uint vkFormat, out TextureFormat format, out TextureColorSpace declared)
    {
        switch (vkFormat)
        {
            case VkFormatR8Unorm: format = TextureFormat.R8; declared = TextureColorSpace.Linear; return true;
            case VkFormatR8G8B8A8Unorm: format = TextureFormat.Rgba8; declared = TextureColorSpace.Linear; return true;
            case VkFormatR8G8B8A8Srgb: format = TextureFormat.Rgba8; declared = TextureColorSpace.Srgb; return true;
            case VkFormatBc1RgbUnormBlock: format = TextureFormat.Bc1; declared = TextureColorSpace.Linear; return true;
            case VkFormatBc1RgbSrgbBlock: format = TextureFormat.Bc1; declared = TextureColorSpace.Srgb; return true;
            case VkFormatBc1RgbaUnormBlock: format = TextureFormat.Bc1; declared = TextureColorSpace.Linear; return true;
            case VkFormatBc1RgbaSrgbBlock: format = TextureFormat.Bc1; declared = TextureColorSpace.Srgb; return true;
            case VkFormatBc3UnormBlock: format = TextureFormat.Bc3; declared = TextureColorSpace.Linear; return true;
            case VkFormatBc3SrgbBlock: format = TextureFormat.Bc3; declared = TextureColorSpace.Srgb; return true;
            case VkFormatBc4UnormBlock: format = TextureFormat.Bc4; declared = TextureColorSpace.Linear; return true;
            case VkFormatBc5UnormBlock: format = TextureFormat.Bc5; declared = TextureColorSpace.Linear; return true;
            case VkFormatBc6HUfloatBlock: format = TextureFormat.Bc6H; declared = TextureColorSpace.Linear; return true;
            case VkFormatBc7UnormBlock: format = TextureFormat.Bc7; declared = TextureColorSpace.Linear; return true;
            case VkFormatBc7SrgbBlock: format = TextureFormat.Bc7; declared = TextureColorSpace.Srgb; return true;
            default:
                format = default;
                declared = default;
                return false;
        }
    }

    /// <summary>
    /// The <c>vkFormat</c> the cooker writes for <paramref name="format"/>,
    /// always the UNORM form. See <see cref="TryResolveVkFormat"/> for why.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The format has no place in this profile at all - the two render-target
    /// formats, and <see cref="TextureFormat.Rgb8"/>, which no API here can
    /// sample and which the upload path expands anyway.
    /// </exception>
    public static uint ToVkFormat(TextureFormat format) => format switch
    {
        TextureFormat.R8 => VkFormatR8Unorm,
        TextureFormat.Rgba8 => VkFormatR8G8B8A8Unorm,
        TextureFormat.Bc1 => VkFormatBc1RgbaUnormBlock,
        TextureFormat.Bc3 => VkFormatBc3UnormBlock,
        TextureFormat.Bc4 => VkFormatBc4UnormBlock,
        TextureFormat.Bc5 => VkFormatBc5UnormBlock,
        TextureFormat.Bc6H => VkFormatBc6HUfloatBlock,
        TextureFormat.Bc7 => VkFormatBc7UnormBlock,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"{format} is not a format the .simage profile carries."),
    };

    /// <summary>
    /// Every level's byte offset is a multiple of this, per the KTX2 mip-padding
    /// rule: the least common multiple of the texel block size and 4.
    /// </summary>
    /// <remarks>
    /// Checked on the way in as well as applied on the way out. The alignment is
    /// what lets a mapped payload reach the GPU without a copy, so a file that
    /// ignores it is a file whose levels would need one, silently, on some
    /// future path that assumed otherwise.
    /// </remarks>
    public static int LevelAlignment(TextureFormat format)
    {
        int blockBytes = TextureFormatInfo.BytesPerBlock(format);
        return blockBytes % 4 == 0 ? blockBytes : blockBytes * 4;
    }
}
