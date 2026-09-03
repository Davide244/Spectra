using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// One mip level of a <see cref="TextureUploadDesc"/>: its size, where it starts
/// in the payload, and how far apart its rows are.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="RowPitch"/> comes from the file and is never recomputed here.</b>
/// A block-compressed pitch depends on the block size and on whatever alignment
/// the tool that wrote the image chose, so a runtime that derives it is a second
/// implementation of the same rule with nothing to make the two agree. The
/// failure is not an exception either: a pitch four bytes out reads each row
/// from slightly the wrong place and produces a picture that is merely wrong.
/// So the pitch is carried, trusted, and checked against the payload's length.
/// </para>
/// <para>
/// <b><see cref="Offset"/> exists so the levels need not be contiguous.</b> A
/// pack aligns payloads, and a cooked image is free to leave padding between
/// mips; an implied "each level follows the last" layout would have to be
/// respected by every future writer to stay true.
/// </para>
/// </remarks>
/// <param name="Width">Width of this level in TEXELS, not blocks.</param>
/// <param name="Height">Height of this level in TEXELS, not blocks.</param>
/// <param name="Offset">Byte offset of this level's first row within the payload.</param>
/// <param name="RowPitch">
/// Bytes from the start of one row to the start of the next. A row is a row of
/// BLOCKS for a compressed format and a row of texels otherwise.
/// </param>
public readonly record struct TextureMipDesc(int Width, int Height, int Offset, int RowPitch);

/// <summary>
/// Everything one texture upload needs: a format, a colour space, a payload, and
/// a per-mip layout over that payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the single upload path.</b> The older
/// <see cref="Renderer.CreateTexture(ReadOnlySpan{byte}, int, int, TextureFormat, TextureColorSpace, TextureFilter, TextureWrap)"/>
/// is expressed as <see cref="SingleLevel"/> plus this, rather than living
/// beside it, because two upload paths is how a fix for the sRGB-ness of a mip
/// chain, or for a padded row pitch, lands in one of them and not the other.
/// </para>
/// <para>
/// <b>A ref struct, so the payload is a span rather than a copy.</b> A cooked
/// image arrives as a span into a memory-mapped pack view, and taking a
/// <c>byte[]</c> here would copy every texture in a shipped game once on the way
/// to the GPU, which is exactly what the container's alignment exists to avoid.
/// The struct provably cannot outlive the mapping, which is the other half of
/// the pack's ownership contract.
/// </para>
/// </remarks>
public readonly ref struct TextureUploadDesc
{
    /// <summary>Builds a descriptor. Nothing is validated until <see cref="Validate"/> runs.</summary>
    public TextureUploadDesc(
        TextureFormat format,
        TextureColorSpace colorSpace,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<TextureMipDesc> mips,
        TextureFilter filter = TextureFilter.Linear,
        TextureWrap wrap = TextureWrap.Repeat)
    {
        Format = format;
        ColorSpace = colorSpace;
        Payload = payload;
        Mips = mips;
        Filter = filter;
        Wrap = wrap;
    }

    /// <summary>The pixel or block format of every level.</summary>
    public TextureFormat Format { get; }

    /// <summary>
    /// How the bytes should be interpreted. The <i>requested</i> space: a format
    /// with no sRGB variant is resolved down to linear by
    /// <see cref="TextureFormatInfo.Resolve"/>, identically on all three
    /// backends.
    /// </summary>
    public TextureColorSpace ColorSpace { get; }

    /// <summary>Every level's bytes, addressed through <see cref="Mips"/>.</summary>
    public ReadOnlySpan<byte> Payload { get; }

    /// <summary>
    /// The levels, most detailed first. Never empty in a valid descriptor, and
    /// each level is half the previous one rounded up.
    /// </summary>
    public ReadOnlySpan<TextureMipDesc> Mips { get; }

    /// <summary>Magnification and minification filtering.</summary>
    public TextureFilter Filter { get; }

    /// <summary>Wrap behaviour outside [0,1].</summary>
    public TextureWrap Wrap { get; }

    /// <summary>Width of the most detailed level, which is the texture's width.</summary>
    public int Width => Mips[0].Width;

    /// <summary>Height of the most detailed level, which is the texture's height.</summary>
    public int Height => Mips[0].Height;

    /// <summary>How many levels are supplied.</summary>
    public int MipCount => Mips.Length;

    /// <summary>
    /// True when the descriptor supplies more than one level, which is what tells
    /// a backend NOT to build a chain of its own.
    /// </summary>
    public bool HasSuppliedMipChain => Mips.Length > 1;

    /// <summary>
    /// A descriptor over one tightly packed level: how the single-span
    /// <c>CreateTexture</c> overload reaches this path.
    /// </summary>
    /// <remarks>
    /// The pitch is computed here, and that is not a contradiction of the rule
    /// stated on <see cref="TextureMipDesc"/>. That rule is about not
    /// second-guessing a FILE; this overload's own documented contract is that
    /// its rows are tightly packed, so the tight pitch is the caller's stated
    /// pitch rather than a guess at somebody else's.
    /// </remarks>
    public static TextureUploadDesc SingleLevel(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter = TextureFilter.Linear,
        TextureWrap wrap = TextureWrap.Repeat)
    {
        // A one-element array rather than a stackalloc: a span over stack memory
        // cannot escape this method, and the levels have to outlive it. One
        // 16-byte allocation per texture, at load time, is the cost of not
        // needing a second inline representation.
        var mips = new TextureMipDesc[]
        {
            new(width, height, 0, TextureFormatInfo.IsFloat(format)
                // A float format is refused by Validate a moment later, and
                // TightRowPitch throws for one. Reporting the real refusal beats
                // reporting the arithmetic that could not be done.
                ? 0
                : TextureFormatInfo.TightRowPitch(format, width)),
        };
        return new TextureUploadDesc(format, colorSpace, pixels, mips, filter, wrap);
    }

    /// <summary>
    /// Refuses a descriptor that cannot be uploaded, naming the mip that is
    /// wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Naming the mip is the point.</b> A cooked chain is a dozen levels of
    /// numbers nobody typed, and "the payload is too short" says nothing about
    /// which of them is off; the alternative to a named refusal is not a
    /// friendlier message but a read past the end of a mapped view, which on
    /// Windows is an access violation with no managed stack.
    /// </para>
    /// <para>
    /// <b>The last row is measured tight, not padded.</b> A writer that packs
    /// levels back to back legitimately stops after the final row's real bytes,
    /// so requiring a whole trailing pitch would refuse a correct file. This is
    /// the same bound <c>GetCopyableFootprints</c> reports as its total size.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The format cannot be filled from bytes at all.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A level is degenerate, is not the previous level halved, declares a pitch
    /// too small for its width, or runs past the end of the payload.
    /// </exception>
    public void Validate()
    {
        // ArgumentOutOfRangeException rather than ArgumentException, because
        // this is the refusal the three backends already made for a float format
        // and callers test for it.
        if (TextureFormatInfo.IsFloat(Format))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Format),
                $"{Format} cannot be uploaded from bytes; it is a render-target format.");
        }

        if (Mips.Length == 0)
            throw new ArgumentException("A texture upload needs at least one mip level.", nameof(Mips));

        for (int level = 0; level < Mips.Length; level++)
        {
            TextureMipDesc mip = Mips[level];

            if (mip.Width <= 0 || mip.Height <= 0)
            {
                throw new ArgumentException(
                    $"Mip {level} is {mip.Width}x{mip.Height}; every level must have a positive size.",
                    nameof(Mips));
            }

            if (level > 0)
            {
                // A GPU texture's level sizes are decided by the API from the
                // base size, so a chain that halves differently is uploaded into
                // levels of the wrong dimensions: no error, a corrupt picture
                // only at distance, and only when minified.
                int expectedWidth = Math.Max(1, Mips[level - 1].Width / 2);
                int expectedHeight = Math.Max(1, Mips[level - 1].Height / 2);
                if (mip.Width != expectedWidth || mip.Height != expectedHeight)
                {
                    throw new ArgumentException(
                        $"Mip {level} is {mip.Width}x{mip.Height}, but halving mip {level - 1} " +
                        $"({Mips[level - 1].Width}x{Mips[level - 1].Height}) gives " +
                        $"{expectedWidth}x{expectedHeight}.",
                        nameof(Mips));
                }
            }

            int tightPitch = TextureFormatInfo.TightRowPitch(Format, mip.Width);
            if (mip.RowPitch < tightPitch)
            {
                throw new ArgumentException(
                    $"Mip {level} ({mip.Width}x{mip.Height}) declares a row pitch of {mip.RowPitch} bytes, " +
                    $"which is less than the {tightPitch} bytes one row of {Format} occupies.",
                    nameof(Mips));
            }

            if (mip.Offset < 0)
            {
                throw new ArgumentException(
                    $"Mip {level} declares a negative payload offset of {mip.Offset}.", nameof(Mips));
            }

            int rows = TextureFormatInfo.RowCount(Format, mip.Height);
            long required = (long)mip.Offset + (long)(rows - 1) * mip.RowPitch + tightPitch;
            if (required > Payload.Length)
            {
                throw new ArgumentException(
                    $"Mip {level} ({mip.Width}x{mip.Height}) needs {required} bytes at offset {mip.Offset} " +
                    $"over {rows} rows of pitch {mip.RowPitch}, but the payload is " +
                    $"{Payload.Length} bytes.",
                    nameof(Mips));
            }
        }
    }
}
