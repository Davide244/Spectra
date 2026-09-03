using Spectra.Kitchen.Audio;
using SpectraEngine.Core.Assets.Audio;
using SpectraEngine.Core.Audio;
using System;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The writer against the reader: what <see cref="SaudioWriter"/> produces, the
/// engine's own <see cref="SaudioReader"/> takes back, value for value.
/// </summary>
/// <remarks>
/// <para><b>This is the pair test, and <c>SaudioFormatTests</c> in the engine's
/// own suite is the second opinion.</b> A writer checked only against its own
/// reader proves the two agree rather than that either is right, so the refusals
/// and the byte geometry are pinned over there against hand-written bytes; what
/// is pinned HERE is that the cooker's output is inside what the engine
/// accepts.</para>
/// <para><b>Byte identity, not equivalence.</b> The cook cache is
/// content-addressed, so a writer that produced two different-but-equivalent
/// files for one input would make every cache entry a lie while producing sounds
/// nobody could tell apart.</para>
/// </remarks>
public class SaudioCodecTests
{
    [Fact]
    public void A_written_sound_reads_back_field_for_field()
    {
        short[] pcm = Ramp(frames: 32, channels: 2);
        var format = new AudioFormat(48_000, 2);

        byte[] file = SaudioWriter.Write(format, pcm, LoopRegion.None, positional: false);
        SaudioInfo info = SaudioReader.Read(file, "probe");

        info.Codec.ShouldBe(SaudioCodec.PcmS16);
        info.Format.ShouldBe(format);
        info.ChannelLayout.ShouldBe(SaudioChannelLayout.Stereo);
        info.FrameCount.ShouldBe(32);
        info.Loop.IsLooping.ShouldBeFalse();
        info.IsStreaming.ShouldBeFalse();
        info.IsPositional.ShouldBeFalse();
        info.SeekTable.ShouldBeEmpty();

        // Sample for sample, not length for length: a payload written at the
        // wrong offset has exactly the right length and none of the right values.
        ReadOnlySpan<short> back = info.Pcm(file);
        back.Length.ShouldBe(pcm.Length);
        for (int i = 0; i < pcm.Length; i++) back[i].ShouldBe(pcm[i]);
    }

    [Fact]
    public void Loop_points_survive_the_container_as_sample_frames()
    {
        short[] pcm = Ramp(frames: 100, channels: 2);
        var loop = new LoopRegion(17, 83);

        byte[] file = SaudioWriter.Write(new AudioFormat(48_000, 2), pcm, loop, positional: false);

        // 17 and 83 exactly. As bytes they would be frames 4 and 20 of a stereo
        // file, and as seconds they would be nothing at all - both of which are
        // legal, playable loops in the wrong place.
        SaudioReader.Read(file, "probe").Loop.ShouldBe(loop);
    }

    [Fact]
    public void A_streamed_sound_carries_a_seek_table_the_reader_walks()
    {
        short[] pcm = Ramp(frames: 100, channels: 1);

        byte[] file = SaudioWriter.Write(
            new AudioFormat(48_000, 1), pcm, LoopRegion.None, positional: true, framesPerSeekEntry: 32);

        SaudioInfo info = SaudioReader.Read(file, "probe");

        info.IsStreaming.ShouldBeTrue();
        info.FramesPerSeekEntry.ShouldBe(32);

        // Four entries for a hundred frames at thirty-two: the last one covers a
        // partial block, which is the case a table sized by division rather than
        // by rounding up gets wrong.
        info.SeekTable.Length.ShouldBe(4);
        info.SeekTable[0].ShouldBe(info.DataOffset);
        info.SeekTable[3].ShouldBe(info.DataOffset + 96 * 2);
    }

    [Fact]
    public void A_stereo_sound_is_never_written_as_positional()
    {
        // OpenAL plays a stereo buffer unpositioned whatever any flag says, so
        // recording the intent would put a promise in the file no driver keeps.
        short[] pcm = Ramp(frames: 8, channels: 2);

        byte[] file = SaudioWriter.Write(new AudioFormat(48_000, 2), pcm, LoopRegion.None, positional: true);

        SaudioReader.Read(file, "probe").IsPositional.ShouldBeFalse();
    }

    [Fact]
    public void A_mono_sound_asked_for_positionally_says_so()
    {
        short[] pcm = Ramp(frames: 8, channels: 1);

        byte[] file = SaudioWriter.Write(new AudioFormat(48_000, 1), pcm, LoopRegion.None, positional: true);

        SaudioReader.Read(file, "probe").IsPositional.ShouldBeTrue();
    }

    [Fact]
    public void Writing_the_same_sound_twice_produces_the_same_bytes()
    {
        short[] pcm = Ramp(frames: 64, channels: 2);
        var format = new AudioFormat(44_100, 2);
        var loop = new LoopRegion(8, 40);

        byte[] first = SaudioWriter.Write(format, pcm, loop, positional: false, framesPerSeekEntry: 16);
        byte[] second = SaudioWriter.Write(format, pcm, loop, positional: false, framesPerSeekEntry: 16);

        first.ShouldBe(second);
    }

    [Fact]
    public void Every_reserved_byte_is_written_zero()
    {
        // Deliberate rather than incidental: an unzeroed field picks up whatever
        // was in the buffer and turns the byte-identity oracle red in a field
        // nothing reads, which is very hard to bisect.
        byte[] file = SaudioWriter.Write(
            new AudioFormat(48_000, 1), Ramp(frames: 4, channels: 1), LoopRegion.None, positional: true);

        file[SaudioFormat.ReservedOffset].ShouldBe((byte)0);
        file[SaudioFormat.ReservedOffset + 1].ShouldBe((byte)0);
    }

    [Fact]
    public void A_loop_past_the_end_of_the_sound_is_refused_at_write_rather_than_shipped()
    {
        // The reader refuses it, so writing one produces a file nothing can open.
        // Thrown rather than clamped, because a clamped loop is a sound that is
        // merely wrong and a cook log that says it was fine.
        short[] pcm = Ramp(frames: 8, channels: 1);

        Should.Throw<ArgumentException>(() => SaudioWriter.Write(
            new AudioFormat(48_000, 1), pcm, new LoopRegion(2, 900), positional: true));
    }

    [Fact]
    public void Interleaved_samples_that_are_not_a_whole_number_of_frames_are_refused()
    {
        Should.Throw<ArgumentException>(() => SaudioWriter.Write(
            new AudioFormat(48_000, 2), new short[7], LoopRegion.None, positional: false));
    }

    // A ramp with a per-channel offset, so a channel swap or an off-by-one
    // offset is a value mismatch rather than two arrays that happen to match.
    private static short[] Ramp(int frames, int channels)
    {
        var pcm = new short[frames * channels];
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
                pcm[frame * channels + channel] = (short)(frame * 53 + channel * 7 - 3000);
        }

        return pcm;
    }
}
