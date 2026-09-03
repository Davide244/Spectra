using SpectraEngine.Core.Graphics;
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace SpectraEngine.Core.Assets.Images;

/// <summary>
/// Reads a <c>.simage</c>: the strict KTX2 subset described by
/// <see cref="SimageFormat"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It validates and REFUSES, naming what was wrong.</b> Every field this
/// reader checks has a failure that renders a picture rather than raising
/// anything - a wrong block size shears the image, a wrong level offset uploads
/// somebody else's bytes, an unrecognised format computes a row pitch for a
/// layout the file does not have - and the last of those is a read past the end
/// of a memory-mapped view, which on Windows is an access violation with no
/// managed stack. So the answer to every uncertainty here is a message, not a
/// guess.
/// </para>
/// <para>
/// <b>It WRITES a DFD and never parses one.</b> The Data Format Descriptor is in
/// the file because KTX2 requires it and because external tools read it; nothing
/// in this reader looks at it. The level index and the <c>vkFormat</c> carry
/// everything an uploader needs, and a conforming DFD parser is most of the
/// reader-cost this restricted profile exists to avoid. The one thing that would
/// change that is a format whose block size the DFD alone states, which the
/// allowlist cannot contain by construction.
/// </para>
/// <para>
/// <b>A span in, no streams.</b> A mounted pack hands out a span into a mapped
/// view, and wrapping one in a <c>MemoryStream</c> copies the whole file to read
/// eighty bytes of header - the copy the container exists to avoid. Every field
/// is read through <see cref="BinaryPrimitives"/> rather than by reinterpreting a
/// struct, because KTX2 fixes little-endian and a struct cast on a big-endian
/// host produces enormous plausible dimensions instead of a failure.
/// </para>
/// </remarks>
public static class SimageReader
{
    /// <summary>
    /// Whether <paramref name="file"/> opens with KTX2's identifier. Cheap, and
    /// says nothing about whether the rest of the file is in the profile.
    /// </summary>
    public static bool LooksLikeSimage(ReadOnlySpan<byte> file) =>
        file.Length >= SimageFormat.Identifier.Length &&
        file[..SimageFormat.Identifier.Length].SequenceEqual(SimageFormat.Identifier);

    /// <summary>
    /// Parses <paramref name="file"/>, or refuses it saying which rule it broke.
    /// </summary>
    /// <param name="file">The whole file. Mip offsets in the result are relative to its start.</param>
    /// <param name="originForErrors">Path or label naming the file in messages.</param>
    /// <exception cref="InvalidDataException">
    /// The bytes are not a <c>.simage</c> this engine can upload.
    /// </exception>
    public static SimageInfo Read(ReadOnlySpan<byte> file, string originForErrors = "<memory>")
    {
        if (!LooksLikeSimage(file))
        {
            throw Refuse(
                originForErrors,
                "it does not start with the KTX2 identifier, so it is not a KTX2 file at all.");
        }

        if (file.Length < SimageFormat.LevelIndexOffset)
        {
            throw Refuse(
                originForErrors,
                $"it is {file.Length} bytes, which is shorter than the {SimageFormat.LevelIndexOffset}-byte " +
                "KTX2 header and index.");
        }

        uint vkFormat = ReadU32(file, 12);
        uint typeSize = ReadU32(file, 16);
        uint pixelWidth = ReadU32(file, 20);
        uint pixelHeight = ReadU32(file, 24);
        uint pixelDepth = ReadU32(file, 28);
        uint layerCount = ReadU32(file, 32);
        uint faceCount = ReadU32(file, 36);
        uint levelCount = ReadU32(file, 40);
        uint supercompression = ReadU32(file, 44);
        uint kvdOffset = ReadU32(file, 56);
        uint kvdLength = ReadU32(file, 60);

        // Supercompression first, because it decides whether the level bytes are
        // the payload at all: everything measured below is meaningless under a
        // scheme this reader cannot undo.
        if (supercompression != SimageFormat.SupercompressionNone)
        {
            throw Refuse(originForErrors, supercompression switch
            {
                // Named, and permanently: BasisLZ needs a full transcoder, which
                // is precisely the reader cost the restricted profile exists to
                // refuse. Recook without it.
                SimageFormat.SupercompressionBasisLz =>
                    "it uses BasisLZ supercompression (scheme 1), which this engine will never support; " +
                    "cook it to an uncompressed BC format instead.",

                // Named separately because it is a "not yet" rather than a
                // "never": the profile admits Zstandard and .NET ships no
                // Zstandard decoder, so saying so beats reading the levels as if
                // they were raw blocks and rendering noise.
                SimageFormat.SupercompressionZstd =>
                    "it uses Zstandard supercompression (scheme 2), which is in the .simage profile and is " +
                    "not implemented yet; cook it with supercompression off.",

                SimageFormat.SupercompressionZlib =>
                    "it uses ZLIB supercompression (scheme 3), which the .simage profile does not admit.",

                _ => $"it declares supercompression scheme {supercompression}, which is not a scheme KTX2 defines.",
            });
        }

        if (!SimageFormat.TryResolveVkFormat(vkFormat, out TextureFormat format, out TextureColorSpace declared))
        {
            // The number is in the message on purpose: a person can look it up
            // in vulkan_core.h, and a build log saying "an unsupported format"
            // cannot be acted on at all.
            throw Refuse(
                originForErrors,
                $"its vkFormat is {vkFormat}, which is not on the .simage allowlist " +
                "(R8, RGBA8, BC1, BC3, BC4, BC5, BC6H and BC7).");
        }

        // 1 for every format on the allowlist, which is what the KTX2 spec
        // requires for a block-compressed format and for an 8-bit one. A file
        // saying otherwise disagrees with its own vkFormat about how wide a
        // component is.
        if (typeSize != 1)
            throw Refuse(originForErrors, $"its typeSize is {typeSize}; every format in this profile is 1.");

        if (pixelDepth != 0)
        {
            throw Refuse(
                originForErrors,
                $"its pixelDepth is {pixelDepth}: it is a 3D texture, and the .simage profile is 2D and cube only.");
        }

        if (layerCount > 1)
        {
            throw Refuse(
                originForErrors,
                $"its layerCount is {layerCount}: it is a texture array, which the .simage profile reserves " +
                "and does not carry yet.");
        }

        if (faceCount is not (1 or 6))
        {
            throw Refuse(
                originForErrors,
                $"its faceCount is {faceCount}; a KTX2 file has 1 face or 6.");
        }

        // Cube maps are IN the profile and have no uploader: Renderer.CreateTexture
        // has no cube path at all, which is also why point-light shadows are
        // unbuilt. Refused here rather than uploaded as its first face, which
        // would light a scene from a sixth of a sky and report nothing.
        if (faceCount == 6)
        {
            throw Refuse(
                originForErrors,
                "it is a cube map, which the .simage profile carries and this engine has no upload path for yet.");
        }

        if (levelCount == 0)
        {
            // Zero legally means "generate the chain at load", and this engine's
            // uploaders do generate one for a single supplied level - but a
            // block-compressed level cannot be downsampled on the GPU, so the
            // request is one no cooked image can honour and silence would be a
            // texture with no mips and no report.
            throw Refuse(
                originForErrors,
                "its levelCount is 0, which asks the loader to generate the mip chain; a cooked image must " +
                "carry its own levels.");
        }

        // The upper bound is not a policy about how big a texture may be: it is
        // what keeps the width a positive int through the shift below, since a
        // value past int.MaxValue casts negative and every derived size then
        // computes off a number nobody wrote.
        const uint maxDimension = 65536;
        if (pixelWidth is 0 or > maxDimension || pixelHeight is 0 or > maxDimension)
        {
            throw Refuse(
                originForErrors,
                $"its base level is {pixelWidth}x{pixelHeight}; a .simage is between 1 and {maxDimension} texels " +
                "on each axis.");
        }

        long indexEnd = (long)SimageFormat.LevelIndexOffset + (long)levelCount * SimageFormat.LevelIndexEntrySize;
        if (indexEnd > file.Length)
        {
            throw Refuse(
                originForErrors,
                $"it declares {levelCount} levels, whose index needs {indexEnd} bytes, and the file is " +
                $"{file.Length} bytes.");
        }

        var mips = new TextureMipDesc[levelCount];
        int alignment = SimageFormat.LevelAlignment(format);
        int payloadBytes = 0;

        for (int level = 0; level < mips.Length; level++)
        {
            // Index entry 0 is the BASE level, while the level DATA is stored
            // smallest-first in the file. Both facts are the spec's; conflating
            // them uploads the 1x1 level as the base and the base as the 1x1,
            // which renders as a flat colour up close and is the single easiest
            // KTX2 mistake to make.
            int entry = SimageFormat.LevelIndexOffset + level * SimageFormat.LevelIndexEntrySize;
            ulong byteOffset = ReadU64(file, entry);
            ulong byteLength = ReadU64(file, entry + 8);
            ulong uncompressedLength = ReadU64(file, entry + 16);

            int width = Math.Max(1, (int)pixelWidth >> level);
            int height = Math.Max(1, (int)pixelHeight >> level);

            // The pitch is DERIVED here and that is not a contradiction of the
            // rule stated on TextureMipDesc. That rule is about not
            // second-guessing a file; KTX2's own contract is that a level's rows
            // are tightly packed, so the tight pitch IS the file's stated pitch -
            // and the check below is what makes it a reading rather than an
            // assumption.
            int rowPitch = TextureFormatInfo.TightRowPitch(format, width);
            long expected = (long)TextureFormatInfo.RowCount(format, height) * rowPitch;

            if ((long)byteLength != expected)
            {
                throw Refuse(
                    originForErrors,
                    $"level {level} is {width}x{height}, which occupies {expected} bytes of {format}, and the " +
                    $"level index says {byteLength}.");
            }

            if (supercompression == SimageFormat.SupercompressionNone && uncompressedLength != byteLength)
            {
                throw Refuse(
                    originForErrors,
                    $"level {level} is not supercompressed, so its uncompressedByteLength must equal its " +
                    $"byteLength; the index says {uncompressedLength} and {byteLength}.");
            }

            if (byteOffset % (ulong)alignment != 0)
            {
                throw Refuse(
                    originForErrors,
                    $"level {level} starts at byte {byteOffset}, which is not a multiple of the {alignment}-byte " +
                    $"mip padding {format} requires.");
            }

            if (byteOffset + byteLength > (ulong)file.Length)
            {
                throw Refuse(
                    originForErrors,
                    $"level {level} claims {byteLength} bytes at offset {byteOffset}, and the file is " +
                    $"{file.Length} bytes.");
            }

            mips[level] = new TextureMipDesc(width, height, (int)byteOffset, rowPitch);
            payloadBytes += (int)byteLength;
        }

        ReadKeyValues(file, kvdOffset, kvdLength, originForErrors, out SimageRowOrder rowOrder, out int profile);

        return new SimageInfo(format, declared, rowOrder, profile, mips, payloadBytes);
    }

    // The two keys this profile requires, read in one pass. Everything else in
    // the KV block is skipped rather than refused: KTX2's key space is open and
    // a writer that adds its own metadata has not broken anything.
    private static void ReadKeyValues(
        ReadOnlySpan<byte> file,
        uint kvdOffset,
        uint kvdLength,
        string origin,
        out SimageRowOrder rowOrder,
        out int profileVersion)
    {
        string? orientation = null;
        string? profile = null;

        if (kvdLength != 0)
        {
            if ((long)kvdOffset + kvdLength > file.Length)
            {
                throw Refuse(
                    origin,
                    $"its key/value block claims {kvdLength} bytes at offset {kvdOffset}, and the file is " +
                    $"{file.Length} bytes.");
            }

            ReadOnlySpan<byte> kvd = file.Slice((int)kvdOffset, (int)kvdLength);
            int at = 0;
            while (at + 4 <= kvd.Length)
            {
                uint pairLength = ReadU32(kvd, at);
                at += 4;
                if (pairLength == 0 || at + pairLength > kvd.Length)
                {
                    throw Refuse(
                        origin,
                        $"its key/value block declares a {pairLength}-byte entry with {kvd.Length - at} bytes left.");
                }

                ReadOnlySpan<byte> pair = kvd.Slice(at, (int)pairLength);
                int nul = pair.IndexOf((byte)0);
                if (nul >= 0)
                {
                    string key = Encoding.UTF8.GetString(pair[..nul]);
                    string value = ReadNulTerminated(pair[(nul + 1)..]);
                    if (key == SimageFormat.OrientationKey) orientation = value;
                    else if (key == SimageFormat.ProfileKey) profile = value;
                }

                // Every entry is padded up to a four-byte boundary, and skipping
                // the padding is what keeps the walk aligned: without it the
                // next length is read out of the middle of this entry's tail and
                // the block parses as garbage of a plausible size.
                at += (int)pairLength;
                at = (at + 3) & ~3;
            }
        }

        // Both keys are REQUIRED, and requiring them is the point of a profile.
        // An arbitrary KTX2 file has neither, which is exactly the file this
        // engine cannot upload correctly: it does not know which way up the rows
        // are, and it does not know whether the cooker that wrote it agreed with
        // this build about what a .simage is.
        if (profile is null)
        {
            throw Refuse(
                origin,
                $"it carries no '{SimageFormat.ProfileKey}' key, so it was not cooked by scook; recook it.");
        }

        if (!int.TryParse(profile, NumberStyles.None, CultureInfo.InvariantCulture, out profileVersion))
        {
            throw Refuse(
                origin,
                $"its '{SimageFormat.ProfileKey}' key reads '{profile}', which is not a version number.");
        }

        // A cooked artifact versions the STRICT way: exact match or refuse, and
        // the message names both numbers and says recook, because a cooked
        // artifact is a build output that can always be regenerated and the bytes
        // past this header only mean anything under the version that wrote them.
        if (profileVersion != EngineInfo.TextureFormatVersion)
        {
            throw Refuse(
                origin,
                $"it was cooked for texture format version {profileVersion} and this engine reads version " +
                $"{EngineInfo.TextureFormatVersion}; recook it.");
        }

        if (orientation is null)
        {
            throw Refuse(
                origin,
                $"it carries no '{SimageFormat.OrientationKey}' key, so which way up its rows are stored is " +
                "undeclared; recook it.");
        }

        rowOrder = orientation switch
        {
            SimageFormat.OrientationBottomUp => SimageRowOrder.BottomUp,

            // Named rather than accepted, because this is the one refusal whose
            // alternative is a picture: uploading a top-down payload renders the
            // whole world upside down, raises nothing, and looks like an art
            // problem.
            SimageFormat.OrientationTopDown => throw Refuse(
                origin,
                $"its rows are stored top-down ('{SimageFormat.OrientationKey}' = " +
                $"'{SimageFormat.OrientationTopDown}') and this engine samples v = 0 at the bottom of the " +
                "picture; a block-compressed payload cannot be flipped at load, so recook it."),

            _ => throw Refuse(
                origin,
                $"its '{SimageFormat.OrientationKey}' key reads '{orientation}', which is neither " +
                $"'{SimageFormat.OrientationBottomUp}' nor '{SimageFormat.OrientationTopDown}'."),
        };
    }

    private static string ReadNulTerminated(ReadOnlySpan<byte> value)
    {
        int nul = value.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? value[..nul] : value);
    }

    private static uint ReadU32(ReadOnlySpan<byte> bytes, int at) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);

    private static ulong ReadU64(ReadOnlySpan<byte> bytes, int at) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);

    // InvalidDataException rather than a type of this format's own, because it
    // is what ImageDecoder throws for a file it cannot read and what
    // AssetManager's texture path already catches and degrades on: a cooked
    // image that will not parse must land on the magenta placeholder exactly as
    // an unreadable PNG does, not take a frame down.
    private static InvalidDataException Refuse(string origin, string because) =>
        new($"'{origin}' is not a .simage this engine can read: {because}");
}
