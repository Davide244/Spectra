using Spectra.Kitchen.Images;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Graphics;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The <c>.simage</c> container: what the writer produces, and every rule the
/// reader refuses.
/// </summary>
/// <remarks>
/// <para><b>Every refusal gets its own test, because every one of them has a
/// failure that renders a picture rather than raising anything.</b> A wrong block
/// size shears the image, a level read at the wrong offset uploads somebody else's
/// bytes, and an unrecognised <c>vkFormat</c> guessed at computes a row pitch for
/// a layout the file does not have - which is a read past the end of a mapped view
/// and, on Windows, an access violation with no managed stack. A reader that
/// merely "handles" these is a reader nobody can debug.</para>
/// <para><b>The fixtures are made by CORRUPTING a real cooked file rather than by
/// hand-writing bytes.</b> A hand-written one proves the reader rejects a thing no
/// writer produces; patching one field of a file the writer just made proves the
/// reader rejects exactly the file a stale tool would hand it, and that everything
/// else about that file was fine.</para>
/// </remarks>
public class SimageCodecTests
{
    [Fact]
    public void A_written_image_reads_back_as_the_levels_that_went_in()
    {
        List<byte[]> levels = Chain(TextureFormat.Bc7, 16, 16);
        byte[] file = Ktx2Writer.Write(
            TextureFormat.Bc7, 16, 16, levels, SimageRowOrder.BottomUp, EngineInfo.TextureFormatVersion);

        SimageInfo info = SimageReader.Read(file, "round-trip.simage");

        info.Format.ShouldBe(TextureFormat.Bc7);
        info.Width.ShouldBe(16);
        info.Height.ShouldBe(16);
        info.MipCount.ShouldBe(levels.Count);
        info.RowOrder.ShouldBe(SimageRowOrder.BottomUp);
        info.ProfileVersion.ShouldBe(EngineInfo.TextureFormatVersion);

        // Level index 0 is the BASE while the level DATA is stored smallest-first,
        // and both are the KTX2 spec's. Pairing them backwards produces a file that
        // parses perfectly and uploads the 1x1 level as the base, which renders as
        // a flat colour up close - so the check is that each level's OWN bytes come
        // back, not merely that the offsets are in range.
        for (int level = 0; level < levels.Count; level++)
        {
            TextureMipDesc mip = info.Mips[level];
            mip.Width.ShouldBe(Math.Max(1, 16 >> level));
            mip.Height.ShouldBe(Math.Max(1, 16 >> level));
            file.AsSpan(mip.Offset, levels[level].Length).ToArray().ShouldBe(levels[level]);
        }

        // And the base level really is the LAST thing in the file, which is what
        // makes a streaming reader able to take the small levels first.
        info.Mips[0].Offset.ShouldBeGreaterThan(info.Mips[^1].Offset);
    }

    [Fact]
    public void Every_level_starts_on_the_alignment_its_format_requires()
    {
        // The alignment is what lets a mapped payload reach the GPU with no copy;
        // a file that ignored it would need one, silently, on some future path
        // that assumed otherwise.
        foreach ((TextureFormat format, int alignment) in
                 new[] { (TextureFormat.Bc7, 16), (TextureFormat.Bc4, 8), (TextureFormat.R8, 4) })
        {
            byte[] file = Ktx2Writer.Write(
                format, 8, 8, Chain(format, 8, 8), SimageRowOrder.BottomUp, EngineInfo.TextureFormatVersion);

            SimageInfo info = SimageReader.Read(file, $"{format}.simage");
            SimageFormat.LevelAlignment(format).ShouldBe(alignment);

            foreach (TextureMipDesc mip in info.Mips)
                (mip.Offset % alignment).ShouldBe(0, $"{format} level at {mip.Offset}");
        }
    }

    [Fact]
    public void A_written_image_is_still_a_KTX2_file_any_other_tool_can_open()
    {
        byte[] file = Valid();

        // There is no Spectra magic here on purpose: the extension is the user's
        // vocabulary and the bytes are KTX2, which is the whole reason toktx,
        // RenderDoc and any GPU debugger read the same file.
        file.AsSpan(0, 12).ToArray().ShouldBe(SimageFormat.Identifier.ToArray());

        // A DFD is written although nothing here parses one, because the format
        // requires it and external tools use it. Its total size includes itself,
        // which is the field a reader of it would start from.
        uint dfdOffset = ReadU32(file, 48);
        uint dfdLength = ReadU32(file, 52);
        dfdLength.ShouldBeGreaterThan(0u);
        ReadU32(file, (int)dfdOffset).ShouldBe(dfdLength);
    }

    [Fact]
    public void Anything_that_is_not_KTX2_is_refused_by_the_identifier()
    {
        byte[] file = Valid();
        file[3] ^= 0xFF;

        Refuse(file).Message.ShouldContain("KTX2 identifier");
    }

    [Fact]
    public void Zstandard_supercompression_is_refused_BY_NAME()
    {
        // The important half is the NAME. Zstandard is in the .simage profile and
        // .NET ships no decoder for it, so the honest answer is "not implemented
        // yet" - and a reader that instead took the level bytes for raw blocks
        // would upload noise and report nothing.
        byte[] file = Valid();
        WriteU32(file, 44, SimageFormat.SupercompressionZstd);

        InvalidDataException refused = Refuse(file);
        refused.Message.ShouldContain("Zstandard");
        refused.Message.ShouldContain("not implemented yet");
    }

    [Fact]
    public void BasisLZ_supercompression_is_refused_permanently_rather_than_as_a_gap()
    {
        // A different sentence from Zstandard's on purpose: this one needs a whole
        // transcoder, which is precisely the reader cost the restricted profile
        // exists to refuse, so the answer is to recook rather than to wait.
        byte[] file = Valid();
        WriteU32(file, 44, SimageFormat.SupercompressionBasisLz);

        Refuse(file).Message.ShouldContain("BasisLZ");
    }

    [Fact]
    public void A_vkFormat_off_the_allowlist_is_refused_with_its_own_number()
    {
        // An ALLOWLIST, so the refusal is what happens to anything unknown. The
        // number travels because a person can look it up in vulkan_core.h and
        // "an unsupported format" cannot be acted on at all.
        byte[] file = Valid();
        WriteU32(file, 12, 109);  // VK_FORMAT_R16G16B16A16_SFLOAT

        InvalidDataException refused = Refuse(file);
        refused.Message.ShouldContain("109");
        refused.Message.ShouldContain("allowlist");
    }

    [Fact]
    public void A_container_shape_outside_the_profile_is_refused_and_says_which()
    {
        byte[] depth = Valid();
        WriteU32(depth, 28, 1);
        Refuse(depth).Message.ShouldContain("3D texture");

        byte[] layers = Valid();
        WriteU32(layers, 32, 4);
        Refuse(layers).Message.ShouldContain("texture array");

        byte[] faces = Valid();
        WriteU32(faces, 36, 2);
        Refuse(faces).Message.ShouldContain("1 face or 6");

        // Six faces is a LEGAL KTX2 shape and is in the profile; what is missing is
        // an uploader, since Renderer.CreateTexture has no cube path. Refused
        // rather than uploaded as its first face, which would light a scene from a
        // sixth of a sky and report nothing.
        byte[] cube = Valid();
        WriteU32(cube, 36, 6);
        Refuse(cube).Message.ShouldContain("cube map");

        byte[] generated = Valid();
        WriteU32(generated, 40, 0);
        Refuse(generated).Message.ShouldContain("generate the mip chain");
    }

    [Fact]
    public void A_level_index_the_file_is_too_short_to_hold_is_refused_rather_than_read()
    {
        // The refusal that is not about content at all: an index running past the
        // end of a mapped view is an access violation with no managed stack, so
        // the bound is checked before any entry is touched.
        byte[] file = Valid();
        WriteU32(file, 40, 64);  // 64 levels, whose index alone is larger than the file

        Refuse(file).Message.ShouldContain("64 levels");
    }

    [Fact]
    public void A_level_whose_length_disagrees_with_its_own_size_is_refused()
    {
        // KTX2 states that a level's rows are tightly packed, which is what lets
        // the reader DERIVE the row pitch rather than trusting one. This is the
        // check that makes that a reading rather than an assumption.
        byte[] file = Valid();
        int baseEntry = SimageFormat.LevelIndexOffset;
        WriteU64(file, baseEntry + 8, 12);

        Refuse(file).Message.ShouldContain("level 0");
    }

    [Fact]
    public void A_file_cooked_for_another_profile_version_names_BOTH_numbers_and_says_recook()
    {
        // A cooked artifact versions the strict way: exact match or refuse. It is a
        // build output that can always be regenerated, and the bytes past the
        // header only mean anything under the version that wrote them.
        int stale = EngineInfo.TextureFormatVersion + 1;
        byte[] file = Ktx2Writer.Write(
            TextureFormat.Bc7, 8, 8, Chain(TextureFormat.Bc7, 8, 8), SimageRowOrder.BottomUp, stale);

        InvalidDataException refused = Refuse(file);
        refused.Message.ShouldContain(stale.ToString(CultureInfo.InvariantCulture));
        refused.Message.ShouldContain(EngineInfo.TextureFormatVersion.ToString(CultureInfo.InvariantCulture));
        refused.Message.ShouldContain("recook");
    }

    [Fact]
    public void A_top_down_file_is_refused_because_nothing_here_can_flip_a_block()
    {
        // The engine samples v = 0 at the BOTTOM of the picture, and a
        // block-compressed payload cannot be flipped at load - BC6H and BC7 need a
        // full decode and re-encode. So the one thing worse than refusing a
        // top-down file is uploading it: the world renders upside down, raises
        // nothing, and looks like an art problem.
        byte[] file = Ktx2Writer.Write(
            TextureFormat.Bc7,
            8,
            8,
            Chain(TextureFormat.Bc7, 8, 8),
            SimageRowOrder.TopDown,
            EngineInfo.TextureFormatVersion);

        Refuse(file).Message.ShouldContain("top-down");
    }

    [Fact]
    public void A_KTX2_file_that_is_not_ours_is_refused_for_saying_neither_thing()
    {
        // An arbitrary conforming KTX2 file carries neither key, and it is exactly
        // the file this engine cannot upload correctly: it does not know which way
        // up the rows are, and it does not know whether the tool that wrote it
        // agreed with this build about what a .simage is.
        byte[] file = Valid();
        WriteU32(file, 60, 0);  // kvdByteLength

        Refuse(file).Message.ShouldContain(SimageFormat.ProfileKey);
    }

    [Fact]
    public void The_orientation_key_is_read_rather_than_assumed()
    {
        byte[] file = Valid();
        int at = Encoding.UTF8.GetString(file).IndexOf(SimageFormat.OrientationKey, StringComparison.Ordinal);
        at.ShouldBeGreaterThan(0);

        // "rl" is a legal KTXorientation value and is not one of the two this
        // profile understands, so it is named rather than defaulted: a default
        // here would be a guess about which way up somebody's texture is.
        file[at + SimageFormat.OrientationKey.Length + 1] = (byte)'r';
        file[at + SimageFormat.OrientationKey.Length + 2] = (byte)'l';

        Refuse(file).Message.ShouldContain("'rl'");
    }

    // --- helpers -------------------------------------------------------------

    private static byte[] Valid() => Ktx2Writer.Write(
        TextureFormat.Bc7,
        8,
        8,
        Chain(TextureFormat.Bc7, 8, 8),
        SimageRowOrder.BottomUp,
        EngineInfo.TextureFormatVersion);

    // Levels of the right SIZE with recognisable contents. The bytes are not real
    // BC blocks and do not need to be: nothing in the container reads inside a
    // level, and a fixture that had to be encoded would make every refusal test
    // depend on the encoder.
    private static List<byte[]> Chain(TextureFormat format, int width, int height)
    {
        var levels = new List<byte[]>();
        for (int level = 0; ; level++)
        {
            int levelWidth = Math.Max(1, width >> level);
            int levelHeight = Math.Max(1, height >> level);
            int size = TextureFormatInfo.RowCount(format, levelHeight)
                * TextureFormatInfo.TightRowPitch(format, levelWidth);

            var bytes = new byte[size];
            for (int i = 0; i < size; i++) bytes[i] = (byte)(level * 41 + i);
            levels.Add(bytes);

            if (levelWidth == 1 && levelHeight == 1) break;
        }

        return levels;
    }

    private static InvalidDataException Refuse(byte[] file) =>
        Should.Throw<InvalidDataException>(() => SimageReader.Read(file, "fixture.simage"));

    private static uint ReadU32(byte[] file, int at) =>
        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));

    private static void WriteU32(byte[] file, int at, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), value);

    private static void WriteU64(byte[] file, int at, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at), value);
}
