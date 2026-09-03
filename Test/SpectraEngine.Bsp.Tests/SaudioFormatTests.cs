using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Audio;
using SpectraEngine.Core.Audio;
using System;
using System.Buffers.Binary;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The D18 oracle for the cooked audio container: what
/// <see cref="SaudioReader"/> accepts, what it refuses, and that a refusal names
/// the thing that was wrong.
/// </summary>
/// <remarks>
/// <para>Every file here is built by <see cref="HandBuiltSaudio"/>, which writes
/// the bytes from the format specification and touches none of the engine's own
/// types, and each refusal test then damages exactly one field of a valid
/// file.</para>
/// <para><b>The refusals matter more than the happy path, and for two different
/// reasons.</b> Two of these fields bound a read - the frame count and the data
/// offset - and the bytes normally arrive as a span into a memory-mapped view,
/// where an unchecked length is an access violation with no managed stack and
/// nothing in a log naming the file. Every other field fails SILENTLY instead: a
/// rate that disagrees with the payload plays the whole asset at the wrong
/// pitch, a channel count that disagrees swaps the ears, a loop point past the
/// end asks the fill loop for frames that are not there. Neither class raises
/// anything on its own, so each gets its own case and each asserts on the
/// MESSAGE, because a refusal that does not say which field was wrong is only
/// marginally better than the silence it replaced.</para>
/// </remarks>
public sealed class SaudioFormatTests
{
    private const string Source = "Sounds/probe.saudio";

    // ------------------------------------------------------------------
    // (a) The layout is a file format, so the constants are pinned against a
    // second spelling of them rather than assumed.
    // ------------------------------------------------------------------

    [Fact]
    public void The_header_geometry_is_exactly_what_the_format_declares()
    {
        SaudioFormat.HeaderSize.ShouldBe(HandBuiltSaudio.HeaderSize);
        SaudioFormat.Magic.ShouldBe(HandBuiltSaudio.Magic);
        SaudioFormat.SeekTableHeaderSize.ShouldBe(HandBuiltSaudio.SeekTableHeaderSize);
        SaudioFormat.SeekTableEntrySize.ShouldBe(HandBuiltSaudio.SeekTableEntrySize);

        SaudioFormat.MagicOffset.ShouldBe(HandBuiltSaudio.MagicOffset);
        SaudioFormat.VersionOffset.ShouldBe(HandBuiltSaudio.VersionOffset);
        SaudioFormat.CodecOffset.ShouldBe(HandBuiltSaudio.CodecOffset);
        SaudioFormat.FlagsOffset.ShouldBe(HandBuiltSaudio.FlagsOffset);
        SaudioFormat.SampleRateOffset.ShouldBe(HandBuiltSaudio.SampleRateOffset);
        SaudioFormat.ChannelsOffset.ShouldBe(HandBuiltSaudio.ChannelsOffset);
        SaudioFormat.ChannelLayoutOffset.ShouldBe(HandBuiltSaudio.ChannelLayoutOffset);
        SaudioFormat.FrameCountOffset.ShouldBe(HandBuiltSaudio.FrameCountOffset);
        SaudioFormat.LoopStartOffset.ShouldBe(HandBuiltSaudio.LoopStartOffset);
        SaudioFormat.LoopEndOffset.ShouldBe(HandBuiltSaudio.LoopEndOffset);
        SaudioFormat.SeekTableOffsetOffset.ShouldBe(HandBuiltSaudio.SeekTableOffsetOffset);
        SaudioFormat.DataOffsetOffset.ShouldBe(HandBuiltSaudio.DataOffsetOffset);

        // The magic reads S A U D in a hex dump, which is the whole reason it is
        // stored little-endian rather than as a number somebody chose.
        Span<byte> dump = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(dump, SaudioFormat.Magic);
        dump[0].ShouldBe((byte)'S');
        dump[1].ShouldBe((byte)'A');
        dump[2].ShouldBe((byte)'U');
        dump[3].ShouldBe((byte)'D');
    }

    // ------------------------------------------------------------------
    // (b) The happy path, value for value.
    // ------------------------------------------------------------------

    [Fact]
    public void A_hand_built_sound_round_trips_through_the_reader()
    {
        byte[] file = HandBuiltSaudio.Resident(frames: 16, channels: 2, sampleRate: 44_100);

        SaudioInfo info = SaudioReader.Read(file, Source);

        info.FormatVersion.ShouldBe(EngineInfo.AudioFormatVersion);
        info.Codec.ShouldBe(SaudioCodec.PcmS16);
        info.Flags.ShouldBe(SaudioFlags.None);
        info.Format.SampleRate.ShouldBe(44_100);
        info.Format.Channels.ShouldBe(2);
        info.ChannelLayout.ShouldBe(SaudioChannelLayout.Stereo);
        info.FrameCount.ShouldBe(16);
        info.Loop.IsLooping.ShouldBeFalse();
        info.DataOffset.ShouldBe(SaudioFormat.HeaderSize);
        info.DataLength.ShouldBe(16 * 2 * 2);
        info.SeekTable.ShouldBeEmpty();
        info.IsStreaming.ShouldBeFalse();

        // The payload is read straight out of the file with no copy, so the last
        // sample is the assertion that the offset and the length agree: a length
        // one frame short reads the right first sample and the wrong last one.
        ReadOnlySpan<short> pcm = info.Pcm(file);
        pcm.Length.ShouldBe(32);
        pcm[0].ShouldBe((short)(-4000));
        pcm[31].ShouldBe((short)(31 * 37 - 4000));
    }

    [Fact]
    public void Loop_points_are_read_as_sample_frames_rather_than_bytes_or_seconds()
    {
        // The one field the format exists for. 4 and 12 are frames; as bytes they
        // would be frames 1 and 3 of this stereo file, and as seconds they would
        // be nothing at all - so a reader that muddled the units produces a loop
        // that is legal, playable and in the wrong place.
        byte[] file = HandBuiltSaudio.Resident(frames: 16, channels: 2, loopStart: 4, loopEnd: 12);

        LoopRegion loop = SaudioReader.Read(file, Source).Loop;

        loop.IsLooping.ShouldBeTrue();
        loop.StartFrame.ShouldBe(4);
        loop.EndFrame.ShouldBe(12);
        loop.LengthFrames.ShouldBe(8);
    }

    [Fact]
    public void A_streaming_sound_carries_a_seek_table_that_walks_its_own_payload()
    {
        byte[] file = HandBuiltSaudio.Streaming(frames: 64, channels: 2, framesPerEntry: 16);

        SaudioInfo info = SaudioReader.Read(file, Source);

        info.IsStreaming.ShouldBeTrue();
        info.FramesPerSeekEntry.ShouldBe(16);
        info.SeekTable.Length.ShouldBe(4);

        // Every entry lands on a frame boundary inside the payload, ascending.
        // That is the whole claim a seek table makes: starting a track part-way
        // through is arithmetic rather than a linear decode.
        for (int i = 0; i < info.SeekTable.Length; i++)
            info.SeekTable[i].ShouldBe(info.DataOffset + i * 16 * 2 * 2);
    }

    [Fact]
    public void The_payload_may_be_shorter_than_the_file_but_never_longer()
    {
        // Trailing bytes are tolerated: a container that pads, or a source that
        // hands over a larger buffer, is not a broken sound. What is refused is
        // the other direction, which is the one that reads off the end.
        byte[] file = HandBuiltSaudio.Resident(frames: 8);
        Array.Resize(ref file, file.Length + 32);

        SaudioReader.Read(file, Source).DataLength.ShouldBe(8 * 2);
    }

    // ------------------------------------------------------------------
    // (c) The refusals, one field damaged per case.
    // ------------------------------------------------------------------

    [Fact]
    public void A_file_that_is_not_a_saudio_at_all_is_told_so_rather_than_told_it_is_short()
    {
        byte[] file = HandBuiltSaudio.Resident();
        file[0] = (byte)'X';

        Refusal(file).ShouldContain("'SAUD' magic");
    }

    [Fact]
    public void A_file_shorter_than_the_header_is_refused_by_length()
    {
        byte[] file = HandBuiltSaudio.Resident();
        Array.Resize(ref file, 20);

        Refusal(file).ShouldContain("shorter than the 48-byte header");
    }

    [Fact]
    public void A_file_cooked_for_another_format_version_is_refused_and_told_to_recook()
    {
        byte[] file = HandBuiltSaudio.Resident();
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(HandBuiltSaudio.VersionOffset), 99);

        string message = Refusal(file);
        message.ShouldContain("version 99");
        message.ShouldContain("recook");
    }

    [Fact]
    public void A_codec_the_format_reserves_is_named_rather_than_lumped_in_with_a_bad_byte()
    {
        // The two have different answers: this one is "this build has no decoder",
        // and an undefined byte is "this is not a codec at all".
        byte[] file = HandBuiltSaudio.Resident();
        file[HandBuiltSaudio.CodecOffset] = (byte)SaudioCodec.Opus;

        string message = Refusal(file);
        message.ShouldContain("Opus");
        message.ShouldContain("PcmS16");
    }

    [Fact]
    public void A_codec_byte_the_format_does_not_define_is_refused()
    {
        byte[] file = HandBuiltSaudio.Resident();
        file[HandBuiltSaudio.CodecOffset] = 200;

        Refusal(file).ShouldContain("codec byte is 200");
    }

    [Fact]
    public void A_flag_bit_this_build_does_not_define_is_refused_rather_than_masked_off()
    {
        // Masking would play a file whose writer meant something by that bit, and
        // whatever it meant is exactly the thing this build would be ignoring.
        byte[] file = HandBuiltSaudio.Resident();
        file[HandBuiltSaudio.FlagsOffset] = 0x80;

        Refusal(file).ShouldContain("flag byte is 0x80");
    }

    [Fact]
    public void A_sample_rate_of_zero_is_refused()
    {
        byte[] file = HandBuiltSaudio.Resident();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(HandBuiltSaudio.SampleRateOffset), 0);

        Refusal(file).ShouldContain("sample rate is 0");
    }

    [Fact]
    public void A_sample_rate_nothing_could_have_written_is_refused()
    {
        byte[] file = HandBuiltSaudio.Resident();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(HandBuiltSaudio.SampleRateOffset), 5_000_000);

        Refusal(file).ShouldContain("sample rate is 5000000");
    }

    [Fact]
    public void A_channel_count_OpenAL_has_no_PCM16_buffer_for_is_refused()
    {
        byte[] file = HandBuiltSaudio.Resident();
        file[HandBuiltSaudio.ChannelsOffset] = 3;

        Refusal(file).ShouldContain("declares 3 channels");
    }

    [Fact]
    public void A_channel_layout_that_disagrees_with_the_channel_count_is_refused()
    {
        // Two fields describing one thing. Honouring either would silently pick a
        // side, and the side that loses is the one that decides how a frame is
        // laid out.
        byte[] file = HandBuiltSaudio.Resident(channels: 2);
        file[HandBuiltSaudio.ChannelLayoutOffset] = 0;

        Refusal(file).ShouldContain("do not describe the same frame");
    }

    [Fact]
    public void A_sound_with_no_frames_in_it_is_refused()
    {
        byte[] file = HandBuiltSaudio.Resident();
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(HandBuiltSaudio.FrameCountOffset), 0);

        Refusal(file).ShouldContain("declares 0 sample frames");
    }

    [Fact]
    public void A_frame_count_whose_byte_length_would_not_fit_an_int_is_refused()
    {
        // The bound that keeps every derived size positive. Wrapped, this is a
        // small plausible length rather than a failure, and the read that follows
        // it walks off the end of a mapped view.
        byte[] file = HandBuiltSaudio.Resident();
        BinaryPrimitives.WriteUInt64LittleEndian(
            file.AsSpan(HandBuiltSaudio.FrameCountOffset), (ulong)SaudioFormat.MaxFrameCount + 1);

        Refusal(file).ShouldContain("sample frames");
    }

    [Fact]
    public void A_u64_field_above_what_a_long_holds_is_refused_rather_than_cast_negative()
    {
        // Cast blindly, this is a negative frame count, and a negative one passes
        // every upper bound below it.
        byte[] file = HandBuiltSaudio.Resident();
        BinaryPrimitives.WriteUInt64LittleEndian(
            file.AsSpan(HandBuiltSaudio.FrameCountOffset), ulong.MaxValue);

        Refusal(file).ShouldContain("FrameCount");
    }

    [Fact]
    public void A_payload_that_runs_off_the_end_of_the_file_is_refused()
    {
        byte[] file = HandBuiltSaudio.Resident(frames: 16);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(HandBuiltSaudio.FrameCountOffset), 4096);

        Refusal(file).ShouldContain("and the file is");
    }

    [Fact]
    public void A_payload_starting_before_the_header_ends_is_refused()
    {
        byte[] file = HandBuiltSaudio.Resident();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(HandBuiltSaudio.DataOffsetOffset), 8);

        Refusal(file).ShouldContain("at offset 8");
    }

    [Fact]
    public void A_payload_at_an_odd_offset_is_refused_rather_than_read_across_sample_boundaries()
    {
        byte[] file = HandBuiltSaudio.Resident(frames: 8);
        Array.Resize(ref file, file.Length + 2);
        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(HandBuiltSaudio.DataOffsetOffset), SaudioFormat.HeaderSize + 1);

        Refusal(file).ShouldContain("not a multiple of the 2-byte PCM16 sample");
    }

    [Fact]
    public void A_loop_start_with_no_loop_end_is_refused()
    {
        // A loop nothing can play. Reading it as "no loop" would silently discard
        // the one thing the author was asking for.
        byte[] file = HandBuiltSaudio.Resident(frames: 16);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(HandBuiltSaudio.LoopStartOffset), 4);

        Refusal(file).ShouldContain("no loop end");
    }

    [Fact]
    public void An_empty_loop_region_is_refused_rather_than_hung_on()
    {
        // End equal to start reads zero frames and asks for zero again, which is
        // a hang inside the fill loop rather than a silent sound.
        byte[] file = HandBuiltSaudio.Resident(frames: 16, loopStart: 4, loopEnd: 8);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(HandBuiltSaudio.LoopEndOffset), 4);

        Refusal(file).ShouldContain("contains no frames");
    }

    [Fact]
    public void A_loop_that_ends_past_the_sound_is_refused()
    {
        byte[] file = HandBuiltSaudio.Resident(frames: 16, loopStart: 4, loopEnd: 12);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(HandBuiltSaudio.LoopEndOffset), 64);

        Refusal(file).ShouldContain("loop ends at frame 64");
    }

    [Fact]
    public void A_streaming_flag_with_no_seek_table_is_refused()
    {
        byte[] file = HandBuiltSaudio.Resident(flags: 1);

        Refusal(file).ShouldContain("flagged streaming and carries no seek table");
    }

    [Fact]
    public void A_seek_table_on_a_file_that_is_not_flagged_streaming_is_refused()
    {
        byte[] file = HandBuiltSaudio.Streaming();
        file[HandBuiltSaudio.FlagsOffset] = 0;

        Refusal(file).ShouldContain("is not flagged streaming");
    }

    [Fact]
    public void A_seek_stride_of_zero_frames_is_refused()
    {
        byte[] file = HandBuiltSaudio.Streaming();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(SaudioFormat.HeaderSize + 4), 0);

        Refusal(file).ShouldContain("0 frames per entry");
    }

    [Fact]
    public void A_seek_table_that_does_not_cover_the_whole_sound_is_refused()
    {
        // A table belonging to a different sound. It presents as a seek landing
        // at the wrong moment rather than as anything that fails.
        byte[] file = HandBuiltSaudio.Streaming(frames: 64, framesPerEntry: 16);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(SaudioFormat.HeaderSize), 3);

        Refusal(file).ShouldContain("needs 4");
    }

    [Fact]
    public void A_seek_table_and_a_payload_that_overlap_are_refused()
    {
        // The payload pulled back into the middle of the table. Every length in
        // the file is still self-consistent and every entry still lands inside
        // the payload region; what is wrong is that two regions claim the same
        // bytes, which reads as a sound whose first half-second is its own seek
        // table.
        byte[] file = HandBuiltSaudio.Streaming(frames: 64, channels: 1, framesPerEntry: 16);
        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(HandBuiltSaudio.DataOffsetOffset), SaudioFormat.HeaderSize + 8);

        Refusal(file).ShouldContain("the two overlap");
    }

    [Fact]
    public void A_seek_entry_outside_the_payload_is_refused()
    {
        byte[] file = HandBuiltSaudio.Streaming(frames: 64, framesPerEntry: 16);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(HandBuiltSaudio.SeekEntryOffset(2)), 4);

        Refusal(file).ShouldContain("outside the payload");
    }

    [Fact]
    public void A_seek_table_that_does_not_ascend_is_refused()
    {
        byte[] file = HandBuiltSaudio.Streaming(frames: 64, channels: 1, framesPerEntry: 16);

        // Entry 2 pulled back to entry 1's own offset: still inside the payload,
        // still frame-aligned, and a seek through it walks backwards.
        BinaryPrimitives.WriteUInt64LittleEndian(
            file.AsSpan(HandBuiltSaudio.SeekEntryOffset(2)),
            BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(HandBuiltSaudio.SeekEntryOffset(1))));

        Refusal(file).ShouldContain("must ascend");
    }

    [Fact]
    public void A_seek_entry_landing_mid_frame_is_refused()
    {
        // Landing between the channels of a frame swaps the ears for the whole
        // rest of the stream, and no length anywhere is wrong.
        byte[] file = HandBuiltSaudio.Streaming(frames: 64, channels: 2, framesPerEntry: 16);
        BinaryPrimitives.WriteUInt64LittleEndian(
            file.AsSpan(HandBuiltSaudio.SeekEntryOffset(1)),
            BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(HandBuiltSaudio.SeekEntryOffset(1))) + 2);

        Refusal(file).ShouldContain("whole number of 2-channel frames");
    }

    // ------------------------------------------------------------------
    // (d) The path rule, which is where the engine and the cooker have to
    // produce the same string or the lookup simply misses.
    // ------------------------------------------------------------------

    [Fact]
    public void An_authored_sound_maps_to_the_cooked_path_beside_it()
    {
        AudioContentPath.CookedPathFor("Sounds/door_open.wav").ShouldBe("Sounds/door_open.saudio");
        AudioContentPath.IsCooked("Sounds/door_open.saudio").ShouldBeTrue();
        AudioContentPath.IsCooked("Sounds/door_open.wav").ShouldBeFalse();

        // Already cooked stays put rather than growing a second extension.
        AudioContentPath.CookedPathFor("Sounds/door_open.saudio").ShouldBe("Sounds/door_open.saudio");
    }

    private static string Refusal(byte[] file) =>
        Should.Throw<SaudioFormatException>(() => SaudioReader.Read(file, Source)).Message;
}
