using SpectraEngine.Core.Graphics;
using System;

namespace SpectraEngine.Core.Assets.Images;

/// <summary>
/// What <see cref="SimageReader"/> found in a <c>.simage</c>: the format, the
/// shape, and the per-mip layout over the file's own bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Mips"/> offsets are into the WHOLE FILE, not into a payload
/// slice</b>, so a caller builds its upload straight over the mapped span with
/// no copy and no arithmetic of its own:
/// <c>new TextureUploadDesc(info.Format, requested, fileSpan, info.Mips, ...)</c>.
/// Slicing off a payload region would put the same offset arithmetic in every
/// caller, and each one would be free to get it slightly wrong.
/// </para>
/// <para>
/// <b>A class rather than a ref struct, holding an array rather than a span.</b>
/// It carries no bytes - only numbers - so it may outlive the span it describes,
/// which is what lets a background read hand it to the render thread beside the
/// <c>ContentBlob</c> whose reference keeps the mapping alive.
/// </para>
/// </remarks>
public sealed class SimageInfo
{
    internal SimageInfo(
        TextureFormat format,
        TextureColorSpace declaredColorSpace,
        SimageRowOrder rowOrder,
        int profileVersion,
        TextureMipDesc[] mips,
        int payloadBytes)
    {
        Format = format;
        DeclaredColorSpace = declaredColorSpace;
        RowOrder = rowOrder;
        ProfileVersion = profileVersion;
        Mips = mips;
        PayloadBytes = payloadBytes;
    }

    /// <summary>The block or pixel format every level is stored in.</summary>
    public TextureFormat Format { get; }

    /// <summary>
    /// The colour space the file's <c>vkFormat</c> DECLARES, which is not what a
    /// texture built from it gets. See
    /// <see cref="SimageFormat.TryResolveVkFormat"/>.
    /// </summary>
    public TextureColorSpace DeclaredColorSpace { get; }

    /// <summary>Which end of the picture the first stored row is.</summary>
    public SimageRowOrder RowOrder { get; }

    /// <summary>The <c>SpectraProfile</c> version this file was cooked under.</summary>
    public int ProfileVersion { get; }

    /// <summary>
    /// Every level, most detailed first, with offsets into the whole file.
    /// </summary>
    public TextureMipDesc[] Mips { get; }

    /// <summary>How many bytes of the file are level data.</summary>
    public int PayloadBytes { get; }

    /// <summary>Width of the base level in texels.</summary>
    public int Width => Mips[0].Width;

    /// <summary>Height of the base level in texels.</summary>
    public int Height => Mips[0].Height;

    /// <summary>How many levels the file supplies.</summary>
    public int MipCount => Mips.Length;
}
