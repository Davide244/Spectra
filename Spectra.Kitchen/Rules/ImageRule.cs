using BCnEncoder.Encoder;
using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Images;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;
using System.IO;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// Compresses an authored image into a <c>.simage</c>: a mip chain of BC blocks
/// in a restricted-profile KTX2 container.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this buys at runtime is no decode and no conversion, on all three
/// backends.</b> A loose PNG costs a full CPU decode per load, four bytes of VRAM
/// per texel and a driver-side mip build; the cooked form arrives as blocks the
/// GPU samples directly, at one byte per texel for BC7 and half of that for BC4,
/// with its levels already made. What it does NOT buy is one memcpy per mip -
/// D3D12's upload path copies per row into a 256-byte-aligned staging heap
/// whatever the file's layout is - and the promise is stated that way in
/// <c>docs/formats-and-pipeline.md</c> so nobody optimises for a claim nothing
/// made.
/// </para>
/// <para>
/// <b>The rule DECODES through <see cref="ImageDecoder"/> rather than reading
/// pixels its own way</b>, which is what makes a cooked image sample the same way
/// up as the loose file it came from: that decoder performs the vertical flip
/// that establishes the engine's v = 0 convention, and sharing it means the two
/// paths cannot drift about orientation. The flip is then FROZEN into the blocks,
/// because block compression cannot be reversed at load, and the file says so
/// through KTX2's own <c>KTXorientation</c> key rather than leaving it implied.
/// </para>
/// <para>
/// <b>The emitted path is the source path with a <c>.simage</c> extension</b>
/// (<see cref="ImageContentPath.CookedPathFor"/>), and the authored file is NOT
/// also copied into the pack: shipping both would double every texture in the
/// build for content nothing reads. A material still names the <c>.png</c>, and
/// <see cref="ImageContentPath.Resolve"/> is the single place that redirection
/// happens for the engine and for <c>scook verify</c> alike.
/// </para>
/// <para>
/// <b>A file the decoder refuses is reported and emits nothing.</b> Falling back
/// to a raw copy would be worse than failing: the pack would carry a broken PNG
/// under a path the engine resolves, the runtime would degrade it to the magenta
/// placeholder, and the build log would say a texture cooked.
/// </para>
/// </remarks>
public sealed class ImageRule : IRule
{
    // What ImageDecoder (StbImageSharp) actually reads. Listed rather than
    // derived, because the set the decoder supports and the set this rule claims
    // must be the same set: an extension added here that stb cannot open turns
    // every file of that kind into an SC2001 rather than the raw copy it was
    // getting perfectly well before.
    private static readonly string[] SourceExtensions = [".png", ".jpg", ".jpeg", ".tga", ".bmp"];

    /// <inheritdoc/>
    public RuleKind Kind => RuleKind.Image;

    /// <inheritdoc/>
    /// <remarks>
    /// Raise this whenever the bytes this rule emits for one source can change:
    /// a different block format for the same input, a change to the container
    /// layout, a different mip policy. The ENCODER's own output moving is not
    /// covered by it - that rides
    /// <see cref="InstructionSetBaseline"/> and the encoder package version in
    /// the cache key - and neither is
    /// <c>EngineInfo.TextureFormatVersion</c>, which a reader enforces instead.
    /// </remarks>
    public int Version => 1;

    /// <inheritdoc/>
    /// <remarks>
    /// The profile, and only that: it selects the encoder's search quality, so a
    /// preview cook and a ship cook genuinely produce different bytes for one
    /// source. The target list is not read - a block format is the same on every
    /// backend - so a cook that changes <c>-t</c> must not re-encode a project's
    /// textures.
    /// </remarks>
    public CookSettingKeys SettingsRead => CookSettingKeys.Profile;

    /// <summary>Whether <paramref name="contentPath"/> is an image this rule cooks.</summary>
    public static bool Handles(string contentPath)
    {
        ArgumentNullException.ThrowIfNull(contentPath);

        foreach (string extension in SourceExtensions)
        {
            if (contentPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public void Cook(IRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        byte[] source = context.Read(context.SourcePath);

        DecodedImage image;
        try
        {
            image = ImageDecoder.Decode(source, context.SourcePath);
        }
        catch (InvalidDataException ex)
        {
            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.ImageUndecodable,
                $"'{context.SourcePath}' could not be decoded: {ex.Message}",
                context.SourcePath));

            return;
        }

        TextureFormat format = ImageBlockEncoder.ChooseFormat(image);
        CompressionQuality quality = ImageBlockEncoder.QualityFor(context.Profile);

        byte[][] encoded = ImageBlockEncoder.Encode(image, format, quality);
        IReadOnlyList<byte[]> levels = TrimToHalvingChain(encoded, image.Width, image.Height, format);

        byte[] cooked;
        try
        {
            cooked = Ktx2Writer.Write(
                format,
                image.Width,
                image.Height,
                levels,
                // Bottom-up, because that is what ImageDecoder produced and what
                // the engine samples at v = 0. Recorded in the file rather than
                // assumed: KTX2's default reading is top-down, so an external
                // tool handed this file would otherwise show it upside down and
                // be right to.
                SimageRowOrder.BottomUp,
                EngineInfo.TextureFormatVersion);
        }
        catch (ArgumentException ex)
        {
            // The writer measures every level against its own dimensions, so this
            // is the encoder handing back a chain shaped differently from the one
            // the halving rule implies. Reported rather than thrown, because a
            // cook must name the asset that broke rather than stopping at
            // SC1004.
            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.ImageEncodeFailed,
                $"'{context.SourcePath}' produced a mip chain the container cannot hold: {ex.Message}",
                context.SourcePath));

            return;
        }

        context.Emit(ImageContentPath.CookedPathFor(context.SourcePath), cooked, PackEntryKind.Image);
    }

    // The uploader's contract is that every level is the previous one halved,
    // rounded up, which is how a GPU decides a texture's level sizes from its
    // base. An encoder returning a chain that stops early is fine - the levels
    // are a prefix and the texture simply carries fewer - but one whose level
    // sizes disagree would be uploaded into levels of the wrong dimensions: no
    // error, and a corrupt picture only at distance and only when minified. So
    // the chain is cut at the first level that does not measure what it should,
    // which the writer's own length check then confirms.
    private static IReadOnlyList<byte[]> TrimToHalvingChain(
        byte[][] encoded, int width, int height, TextureFormat format)
    {
        var kept = new List<byte[]>(encoded.Length);
        for (int level = 0; level < encoded.Length; level++)
        {
            int levelWidth = Math.Max(1, width >> level);
            int levelHeight = Math.Max(1, height >> level);
            int expected = TextureFormatInfo.RowCount(format, levelHeight)
                * TextureFormatInfo.TightRowPitch(format, levelWidth);

            if (encoded[level].Length != expected) break;

            kept.Add(encoded[level]);
        }

        return kept;
    }
}
