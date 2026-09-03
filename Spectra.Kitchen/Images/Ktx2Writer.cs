using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Graphics;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Spectra.Kitchen.Images;

/// <summary>
/// Writes a <c>.simage</c>: spec-conformant KTX2 restricted to the profile
/// <see cref="SimageFormat"/> describes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bytes are a pure function of the arguments</b>, which is the whole
/// reason a cook can be content-addressed at all: no clock, no path, no
/// dictionary iteration, no host detail. Every reserved and unused field is
/// written as an explicit zero rather than left to whatever a rented buffer held,
/// for the reason <c>PackWriter</c> already records - an unzeroed field picks up
/// stack garbage and turns a byte-identity oracle red in a way that is very hard
/// to bisect.
/// </para>
/// <para>
/// <b>Level data is stored SMALLEST FIRST while the level index is written base
/// first.</b> Both are the KTX2 spec's, and they exist for streaming: a reader
/// that wants a low-resolution image first reads the front of the file. Getting
/// the pairing backwards produces a file that parses perfectly and uploads the
/// 1x1 level as the base, which renders as a flat colour up close.
/// </para>
/// <para>
/// <b>The DFD is written and never read back.</b> KTX2 requires one and external
/// tools use it; <see cref="SimageReader"/> deliberately parses none, because a
/// conforming DFD parser is most of the reader cost the restricted profile exists
/// to avoid. It is therefore written from a table keyed on the format rather than
/// derived, and the table is small enough to read in one sitting.
/// </para>
/// </remarks>
public static class Ktx2Writer
{
    // KHR_DF_MODEL_*. The BC family starts at 128; RGBSDA is the uncompressed
    // one. Values from the Khronos Data Format specification 1.3.
    private const byte ModelRgbsda = 1;
    private const byte ModelBc1A = 128;
    private const byte ModelBc3 = 130;
    private const byte ModelBc4 = 131;
    private const byte ModelBc5 = 132;
    private const byte ModelBc6H = 133;
    private const byte ModelBc7 = 134;

    private const byte PrimariesBt709 = 1;
    private const byte TransferLinear = 1;
    private const byte TransferSrgb = 2;

    // Sample qualifier bits, packed into the high nibble of the channel byte.
    private const byte ChannelFloat = 0x80;

    // KHR_DF_CHANNEL_RGBSDA_*.
    private const byte ChannelRed = 0;
    private const byte ChannelGreen = 1;
    private const byte ChannelBlue = 2;
    private const byte ChannelAlpha = 15;

    private const int BasicBlockHeaderBytes = 24;
    private const int SampleBytes = 16;

    /// <summary>
    /// Assembles the file.
    /// </summary>
    /// <param name="format">The block or pixel format every level is stored in.</param>
    /// <param name="width">Base level width in texels.</param>
    /// <param name="height">Base level height in texels.</param>
    /// <param name="levels">
    /// Every level's tightly packed bytes, MOST DETAILED FIRST. Each must be
    /// exactly the size its own dimensions imply, which is checked.
    /// </param>
    /// <param name="rowOrder">Which end of the picture level row 0 is.</param>
    /// <param name="profileVersion">Value of the <c>SpectraProfile</c> key.</param>
    /// <exception cref="ArgumentException">
    /// A level is the wrong size for its dimensions, or there are no levels.
    /// </exception>
    public static byte[] Write(
        TextureFormat format,
        int width,
        int height,
        IReadOnlyList<byte[]> levels,
        SimageRowOrder rowOrder,
        int profileVersion)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (levels.Count == 0)
            throw new ArgumentException("A .simage carries at least one level.", nameof(levels));

        // Checked here rather than trusted, because a level that is one block
        // short still writes a valid-looking index and the reader's own length
        // check would then reject the cooker's own output at LOAD time - which is
        // a failure in the wrong build, hours later.
        for (int level = 0; level < levels.Count; level++)
        {
            int levelWidth = Math.Max(1, width >> level);
            int levelHeight = Math.Max(1, height >> level);
            int expected = TextureFormatInfo.RowCount(format, levelHeight)
                * TextureFormatInfo.TightRowPitch(format, levelWidth);

            if (levels[level].Length != expected)
            {
                throw new ArgumentException(
                    $"Level {level} of a {width}x{height} {format} image is {levelWidth}x{levelHeight}, which " +
                    $"occupies {expected} bytes; {levels[level].Length} were supplied.",
                    nameof(levels));
            }
        }

        byte[] dfd = BuildDataFormatDescriptor(format);
        byte[] kvd = BuildKeyValueData(rowOrder, profileVersion);

        int levelIndexBytes = levels.Count * SimageFormat.LevelIndexEntrySize;
        int dfdOffset = SimageFormat.LevelIndexOffset + levelIndexBytes;
        int kvdOffset = dfdOffset + dfd.Length;

        // Every level offset is a multiple of the format's mip padding, and the
        // levels are laid out smallest first. The alignment is what lets a
        // mapped payload reach the GPU with no copy.
        int alignment = SimageFormat.LevelAlignment(format);
        var offsets = new long[levels.Count];
        long at = Align(kvdOffset + kvd.Length, alignment);
        for (int level = levels.Count - 1; level >= 0; level--)
        {
            offsets[level] = at;
            at = Align(at + levels[level].Length, alignment);
        }

        // The trailing pad of the LAST-written (i.e. base) level is not emitted:
        // padding exists to align what follows, and nothing follows.
        long totalBytes = offsets[0] + levels[0].Length;
        var file = new byte[checked((int)totalBytes)];

        SimageFormat.Identifier.CopyTo(file);
        WriteU32(file, 12, SimageFormat.ToVkFormat(format));
        WriteU32(file, 16, 1);                       // typeSize: one byte per component in every profile format
        WriteU32(file, 20, (uint)width);
        WriteU32(file, 24, (uint)height);
        WriteU32(file, 28, 0);                       // pixelDepth: 2D only
        WriteU32(file, 32, 0);                       // layerCount: not an array
        WriteU32(file, 36, 1);                       // faceCount: not a cube map
        WriteU32(file, 40, (uint)levels.Count);
        WriteU32(file, 44, SimageFormat.SupercompressionNone);
        WriteU32(file, 48, (uint)dfdOffset);
        WriteU32(file, 52, (uint)dfd.Length);
        WriteU32(file, 56, (uint)kvdOffset);
        WriteU32(file, 60, (uint)kvd.Length);
        WriteU64(file, 64, 0);                       // sgdByteOffset: no supercompression global data
        WriteU64(file, 72, 0);                       // sgdByteLength

        for (int level = 0; level < levels.Count; level++)
        {
            int entry = SimageFormat.LevelIndexOffset + level * SimageFormat.LevelIndexEntrySize;
            WriteU64(file, entry, (ulong)offsets[level]);
            WriteU64(file, entry + 8, (ulong)levels[level].Length);

            // With no supercompression the two lengths are equal by spec. Written
            // explicitly rather than left zero, because a zero here is a legal
            // encoding of "unknown" that some readers act on.
            WriteU64(file, entry + 16, (ulong)levels[level].Length);
        }

        dfd.CopyTo(file, dfdOffset);
        kvd.CopyTo(file, kvdOffset);
        for (int level = 0; level < levels.Count; level++)
            levels[level].CopyTo(file, (int)offsets[level]);

        return file;
    }

    // Keys are sorted by codepoint, which KTX2 requires. The two this profile
    // writes are already in that order ('K' before 'S'), and they are written in
    // that order literally rather than sorted at runtime, because a sort is a
    // place a comparer's culture could get in and change the bytes.
    private static byte[] BuildKeyValueData(SimageRowOrder rowOrder, int profileVersion)
    {
        string orientation = rowOrder == SimageRowOrder.BottomUp
            ? SimageFormat.OrientationBottomUp
            : SimageFormat.OrientationTopDown;

        var bytes = new List<byte>(64);
        AppendPair(bytes, SimageFormat.OrientationKey, orientation);

        // Invariant formatting, for the reason the console's number parsing gives:
        // a value that renders differently on a machine with another culture is a
        // cooked byte that depends on who ran the cook.
        AppendPair(bytes, SimageFormat.ProfileKey, profileVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return [.. bytes];
    }

    private static void AppendPair(List<byte> bytes, string key, string value)
    {
        // Both halves are NUL-terminated: the key because KTX2 says so, the value
        // because these two are string values and a reader that takes the whole
        // remainder would otherwise hand back the padding as part of it.
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);
        int pairLength = keyBytes.Length + 1 + valueBytes.Length + 1;

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)pairLength);
        bytes.AddRange(length);
        bytes.AddRange(keyBytes);
        bytes.Add(0);
        bytes.AddRange(valueBytes);
        bytes.Add(0);

        while (bytes.Count % 4 != 0) bytes.Add(0);
    }

    // One basic descriptor block, which is all a single-plane format needs. The
    // sample table is what varies: a format's channels, where each sits in the
    // block, and how wide it is.
    private static byte[] BuildDataFormatDescriptor(TextureFormat format)
    {
        (byte model, Sample[] samples) = DescribeFormat(format);

        int blockSize = BasicBlockHeaderBytes + samples.Length * SampleBytes;
        var dfd = new byte[4 + blockSize];

        WriteU32(dfd, 0, (uint)dfd.Length);          // dfdTotalSize, including itself
        WriteU32(dfd, 4, 0);                         // vendorId 0 (Khronos), descriptorType 0 (basic)
        WriteU32(dfd, 8, 2u | ((uint)blockSize << 16));  // versionNumber 2 (KDF 1.3), descriptorBlockSize

        dfd[12] = model;
        dfd[13] = PrimariesBt709;

        // Always LINEAR, because the cooker always writes the UNORM vkFormat: the
        // colour space is a property of the material SLOT rather than of the
        // image, so the file must not claim one. The two would otherwise be free
        // to disagree, and KTX2 carries sRGB-ness twice precisely so that they
        // cannot.
        dfd[14] = TransferLinear;
        dfd[15] = 0;                                 // flags: straight (unpremultiplied) alpha

        dfd[16] = (byte)(TextureFormatInfo.BlockWidth(format) - 1);
        dfd[17] = (byte)(TextureFormatInfo.BlockHeight(format) - 1);
        dfd[18] = 0;                                 // depth: one texel
        dfd[19] = 0;                                 // no fourth dimension
        dfd[20] = (byte)TextureFormatInfo.BytesPerBlock(format);   // bytesPlane0
        // bytesPlane1..7 stay zero: every format here is single-plane.

        for (int i = 0; i < samples.Length; i++)
        {
            int at = 4 + BasicBlockHeaderBytes + i * SampleBytes;
            Sample sample = samples[i];

            // bitLength is stored one less than the real width, which is what
            // lets a 128-bit BC7 block fit in eight bits.
            WriteU32(dfd, at, (uint)sample.BitOffset
                | ((uint)(sample.BitLength - 1) << 16)
                | ((uint)sample.Channel << 24));

            // samplePosition[0..3] stay zero: no format here is subsampled.
            WriteU32(dfd, at + 8, sample.Lower);
            WriteU32(dfd, at + 12, sample.Upper);
        }

        return dfd;
    }

    private static (byte Model, Sample[] Samples) DescribeFormat(TextureFormat format) => format switch
    {
        // One whole 64-bit block is one sample for the BC families whose colour
        // is not separable into channels; the sample bounds are the conventional
        // full-range pair the KDF spec gives for them.
        TextureFormat.Bc1 => (ModelBc1A, [new Sample(0, 64, ChannelRed, 0, uint.MaxValue)]),

        // BC3 stores alpha in the first 64 bits and colour in the second, which
        // is why it has two samples and BC7 has one.
        TextureFormat.Bc3 => (ModelBc3,
        [
            new Sample(0, 64, ChannelAlpha, 0, uint.MaxValue),
            new Sample(64, 64, ChannelRed, 0, uint.MaxValue),
        ]),

        TextureFormat.Bc4 => (ModelBc4, [new Sample(0, 64, ChannelRed, 0, uint.MaxValue)]),

        TextureFormat.Bc5 => (ModelBc5,
        [
            new Sample(0, 64, ChannelRed, 0, uint.MaxValue),
            new Sample(64, 64, ChannelGreen, 0, uint.MaxValue),
        ]),

        // The FLOAT qualifier and the float-bit-pattern bounds: BC6H decodes to
        // half-floats, so a 0..255 range would describe a different format.
        TextureFormat.Bc6H => (ModelBc6H,
            [new Sample(0, 128, (byte)(ChannelRed | ChannelFloat), 0, 0x7F7FFFFF)]),

        TextureFormat.Bc7 => (ModelBc7, [new Sample(0, 128, ChannelRed, 0, uint.MaxValue)]),

        TextureFormat.Rgba8 => (ModelRgbsda,
        [
            new Sample(0, 8, ChannelRed, 0, 255),
            new Sample(8, 8, ChannelGreen, 0, 255),
            new Sample(16, 8, ChannelBlue, 0, 255),
            new Sample(24, 8, ChannelAlpha, 0, 255),
        ]),

        TextureFormat.R8 => (ModelRgbsda, [new Sample(0, 8, ChannelRed, 0, 255)]),

        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, $"{format} has no data format descriptor in the .simage profile."),
    };

    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static void WriteU32(byte[] bytes, int at, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);

    private static void WriteU64(byte[] bytes, int at, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);

    private readonly record struct Sample(int BitOffset, int BitLength, byte Channel, uint Lower, uint Upper);
}
