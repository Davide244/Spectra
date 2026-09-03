using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Spectra.Kitchen.Cooking;
using SpectraEngine.Core.Assets;
using System;
using EnginePixelFormat = SpectraEngine.Core.Graphics.TextureFormat;

namespace Spectra.Kitchen.Images;

/// <summary>
/// Turns a decoded image into block-compressed levels: the one place the cooker
/// touches an encoder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism is the property this whole class exists to hold still, and it
/// was measured before it was relied on.</b>
/// <c>docs/spikes/2026-09-cook-dependency-spikes.md</c> encoded one PNG with one
/// encoder and one set of settings and got two different BC7 payloads: 310 of
/// 1,024 blocks differed between an AVX2 baseline and a non-AVX2 one, visually
/// equivalent and byte-different, which is the worst shape the failure could take
/// because nothing in the artifact signals it. That is why
/// <see cref="Cache.InstructionSetBaseline"/> is already in every cache key, and
/// nothing here may undo it.
/// </para>
/// <para>
/// <b>The encoder runs SINGLE-THREADED, and that is a scheduling decision rather
/// than a determinism one.</b> The spike measured parallel and serial encodes as
/// byte-equal, so the parallel path is not a correctness risk - but the cook
/// already runs one rule per worker across the whole asset list, so a second
/// layer of parallelism inside one image competes with the first for the same
/// cores and buys nothing. Turning it off also removes an axis the byte-identity
/// oracles would otherwise have to cover, which is why <see cref="Encode"/>
/// takes the flag rather than hiding it: <c>ImageRuleTests</c> drives the same
/// code both ways and asserts the spike's finding still holds on this machine.
/// </para>
/// <para>
/// <b>The row order is the DECODER's, and it is deliberately not adjusted
/// here.</b> <see cref="ImageDecoder"/> flips image rows on the way in, so what
/// arrives is already bottom-up - the order the engine samples at v = 0 - and
/// compressing it as it stands is what makes a cooked texture sample the same way
/// up as the loose file it came from. A block-compressed payload cannot be
/// flipped afterwards at all (BC6H and BC7 need a full decode and re-encode), so
/// this is the only moment the choice can be made.
/// </para>
/// </remarks>
public static class ImageBlockEncoder
{
    /// <summary>
    /// The block format a decoded image cooks to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Chosen from the CHANNEL COUNT, which is the only thing the cooker
    /// knows.</b> A one-channel file is a mask, a height field, an AO or a
    /// roughness map, and BC4 stores exactly one interpolated channel at a
    /// quarter of the size. Everything else is colour and goes to BC7, which is
    /// the highest quality the BC family reaches and handles an alpha channel
    /// without a second format decision.
    /// </para>
    /// <para>
    /// <b>BC1 is deliberately not chosen automatically.</b> It is four times
    /// smaller than RGBA8 against BC7's two, and it is visibly worse on the
    /// gradients an albedo is mostly made of; picking it for "opaque" images
    /// would make the quality of a texture depend on whether its author happened
    /// to save an alpha channel. It becomes reachable when a per-asset cook
    /// setting exists to ask for it.
    /// </para>
    /// </remarks>
    public static EnginePixelFormat ChooseFormat(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.Channels == 1 ? EnginePixelFormat.Bc4 : EnginePixelFormat.Bc7;
    }

    /// <summary>
    /// How hard the encoder searches, per cook profile.
    /// </summary>
    /// <remarks>
    /// This is the one setting <see cref="ImageRule"/> reads, and it is why that
    /// rule declares <c>CookSettingKeys.Profile</c>: a preview cook and a ship
    /// cook produce different bytes for the same source, so a cache that did not
    /// know would hand a ship build the preview artifact.
    /// </remarks>
    public static CompressionQuality QualityFor(CookProfile profile) => profile switch
    {
        CookProfile.Ship => CompressionQuality.Balanced,
        _ => CompressionQuality.Fast,
    };

    /// <summary>
    /// Encodes <paramref name="image"/> and its mip chain, most detailed first.
    /// </summary>
    /// <param name="image">The decoded source. Its rows are already bottom-up.</param>
    /// <param name="format">The block format, from <see cref="ChooseFormat"/>.</param>
    /// <param name="quality">How hard to search, from <see cref="QualityFor"/>.</param>
    /// <param name="parallel">
    /// Whether the encoder may split one image across tasks. False in the cook;
    /// true only from the oracle that asserts the two agree.
    /// </param>
    public static byte[][] Encode(
        DecodedImage image, EnginePixelFormat format, CompressionQuality quality, bool parallel = false)
    {
        ArgumentNullException.ThrowIfNull(image);

        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                GenerateMipMaps = true,
                Quality = quality,
                Format = ToCompressionFormat(format),
            },
            Options = { IsParallel = parallel },
            // BC4 keeps one channel and has to be told which. Red, matching the
            // channel a single-channel PNG decodes into and the channel R8
            // samples in a shader, so a mask cooked to BC4 reads back where its
            // author put it.
            InputOptions = { Bc4Component = ColorComponent.R },
        };

        // The encoder has no single-channel input format, so a grey image is
        // widened to RGBA here rather than being decoded twice. r = g = b keeps
        // it a legitimate greyscale picture for any encoder path that averages
        // channels, and the alpha is opaque because a mask with a zero alpha
        // would be discarded by anything that reads one.
        if (image.Channels == 1)
        {
            byte[] widened = WidenGreyToRgba(image);
            return encoder.EncodeToRawBytes(widened, image.Width, image.Height, PixelFormat.Rgba32);
        }

        PixelFormat input = image.Channels switch
        {
            3 => PixelFormat.Rgb24,
            4 => PixelFormat.Rgba32,
            _ => throw new ArgumentOutOfRangeException(
                nameof(image),
                image.Channels,
                "A decoded image has 1, 3 or 4 channels; ImageDecoder produces no others."),
        };

        return encoder.EncodeToRawBytes(image.Pixels, image.Width, image.Height, input);
    }

    private static byte[] WidenGreyToRgba(DecodedImage image)
    {
        ReadOnlySpan<byte> grey = image.Pixels;
        var rgba = new byte[image.Width * image.Height * 4];
        for (int i = 0, o = 0; i < image.Width * image.Height; i++, o += 4)
        {
            rgba[o] = grey[i];
            rgba[o + 1] = grey[i];
            rgba[o + 2] = grey[i];
            rgba[o + 3] = 255;
        }

        return rgba;
    }

    private static CompressionFormat ToCompressionFormat(EnginePixelFormat format) => format switch
    {
        EnginePixelFormat.Bc1 => CompressionFormat.Bc1,
        EnginePixelFormat.Bc3 => CompressionFormat.Bc3,
        EnginePixelFormat.Bc4 => CompressionFormat.Bc4,
        EnginePixelFormat.Bc5 => CompressionFormat.Bc5,
        EnginePixelFormat.Bc7 => CompressionFormat.Bc7,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"{format} is not a format this cooker encodes to."),
    };
}
