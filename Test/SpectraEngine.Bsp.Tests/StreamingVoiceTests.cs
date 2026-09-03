using SpectraEngine.Core.Audio;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The buffer queue: what actually lands in a buffer, that a loop wraps inside
/// one, that a starved queue is restarted rather than mistaken for a finished
/// sound, and that a seek discards what was already queued.
/// </summary>
/// <remarks>
/// These are the assertions that make the loop points real rather than
/// arithmetic in isolation. <see cref="AudioLoopCursorTests"/> proves the plan;
/// this proves the plan reaches the driver as the frames it named, which is
/// where a wrong buffer format, a wrong scratch offset or a missing wrap would
/// show up.
/// </remarks>
public sealed class StreamingVoiceTests
{
    private const int Rate = 48000;
    private const int BufferFrames = 100;
    private const int BufferCount = 4;

    [Fact]
    public void A_priming_fill_queues_every_buffer_and_starts_the_source()
    {
        var backend = new FakeAudioBackend();
        StreamingVoice voice = Start(backend, Ramp(10_000, LoopRegion.None));

        backend.QueueDepth(voice.Source).ShouldBe(BufferCount);
        backend.StateOf(voice.Source).ShouldBe(AudioSourceState.Playing);
        voice.PositionFrames.ShouldBe(BufferFrames * BufferCount);

        // The ramp says which frames went in: buffer k holds frames k*100 up.
        for (int k = 0; k < BufferCount; k++)
        {
            backend.Uploads[k].Length.ShouldBe(BufferFrames);
            backend.Uploads[k][0].ShouldBe((short)(k * BufferFrames));
        }
    }

    [Fact]
    public void A_loop_wraps_inside_a_single_uploaded_buffer()
    {
        // The case AL_LOOPING cannot express at all: a 250-frame region strictly
        // inside a 1000-frame sound, against 100-frame buffers, so a buffer has
        // to carry the end of the region followed immediately by its start. If
        // that buffer came out padded, or repeating its tail, or running past
        // the loop end into the outro, the loop would click or drift and no
        // driver anywhere would report it.
        var backend = new FakeAudioBackend();
        StreamingVoice voice = Start(backend, Ramp(1000, new LoopRegion(200, 450)));

        // Priming filled [0..100) [100..200) [200..300) [300..400).
        backend.Uploads.Count.ShouldBe(BufferCount);
        backend.Uploads[3][0].ShouldBe((short)300);

        // One buffer consumed refills exactly one, and that is the straddling
        // one: 50 frames to reach 450, then back to 200 for the other 50.
        backend.Consume(voice.Source, 1);
        voice.Update().ShouldBeTrue();

        short[] straddling = backend.Uploads[^1];
        straddling.Length.ShouldBe(BufferFrames);
        for (int i = 0; i < 50; i++) straddling[i].ShouldBe((short)(400 + i));
        for (int i = 50; i < 100; i++) straddling[i].ShouldBe((short)(200 + (i - 50)));
    }

    [Fact]
    public void A_loop_shorter_than_one_buffer_repeats_inside_it()
    {
        // Thirty frames of loop against a 100-frame buffer. A buffer is not a
        // loop iteration, and this is where treating it as one would silently
        // stretch a short ambience loop to three times its length.
        var backend = new FakeAudioBackend();
        Start(backend, Ramp(200, new LoopRegion(100, 130)));

        // First buffer reaches the loop end from 0, so it is 130 frames of
        // source in a 100-frame budget: [0..100).
        backend.Uploads[0][99].ShouldBe((short)99);

        // The second crosses the wrap twice: 30 to reach 130, then 100..130
        // again, then again, then part of a fourth pass.
        short[] second = backend.Uploads[1];
        second.Length.ShouldBe(BufferFrames);
        for (int i = 0; i < 30; i++) second[i].ShouldBe((short)(100 + i));
        for (int i = 30; i < 60; i++) second[i].ShouldBe((short)(100 + (i - 30)));
        for (int i = 60; i < 90; i++) second[i].ShouldBe((short)(100 + (i - 60)));
        for (int i = 90; i < 100; i++) second[i].ShouldBe((short)(100 + (i - 90)));
    }

    [Fact]
    public void A_starved_queue_is_restarted_rather_than_mistaken_for_a_finished_sound()
    {
        // A source that ran dry reports Stopped exactly as one that played its
        // last buffer does. Reading the state alone ends the music the first
        // time a frame hitches, permanently, with nothing logged. The queue
        // depth is the discriminator.
        var backend = new FakeAudioBackend();
        StreamingVoice voice = Start(backend, Ramp(1_000_000, LoopRegion.None));

        backend.Starve(voice.Source);
        backend.StateOf(voice.Source).ShouldBe(AudioSourceState.Stopped);

        voice.Update().ShouldBeTrue();

        voice.IsFinished.ShouldBeFalse();
        voice.UnderrunCount.ShouldBe(1);
        backend.StateOf(voice.Source).ShouldBe(AudioSourceState.Playing);
        backend.QueueDepth(voice.Source).ShouldBe(BufferCount);
    }

    [Fact]
    public void A_non_looping_stream_finishes_only_once_the_queue_has_drained()
    {
        // 250 frames against 100-frame buffers: three fills, the last of them
        // short. A short fill is the right answer rather than a padded one,
        // because padding is a click at the end of every sound.
        var backend = new FakeAudioBackend();
        StreamingVoice voice = Start(backend, Ramp(250, LoopRegion.None));

        backend.QueueDepth(voice.Source).ShouldBe(3);
        backend.Uploads[2].Length.ShouldBe(50);

        // Two buffers played, nothing left to plan, so nothing is requeued. One
        // is still in flight and the sound is NOT over.
        backend.Consume(voice.Source, 2);
        voice.Update().ShouldBeTrue();
        voice.IsFinished.ShouldBeFalse();
        backend.QueueDepth(voice.Source).ShouldBe(1);

        backend.Consume(voice.Source, 1);
        voice.Update().ShouldBeFalse();
        voice.IsFinished.ShouldBeTrue();
    }

    [Fact]
    public void A_looping_stream_never_finishes_on_its_own()
    {
        var backend = new FakeAudioBackend();
        StreamingVoice voice = Start(backend, Ramp(300, new LoopRegion(0, 300)));

        for (int frame = 0; frame < 50; frame++)
        {
            backend.Consume(voice.Source, 2);
            voice.Update().ShouldBeTrue();
            backend.QueueDepth(voice.Source).ShouldBe(BufferCount);
        }

        voice.IsFinished.ShouldBeFalse();

        // Fifty pumps of two buffers each out of a 300-frame asset: it has run
        // round the loop more than thirty times and never read past the region.
        voice.PositionFrames.ShouldBeLessThanOrEqualTo(300);
    }

    [Fact]
    public void A_seek_discards_what_was_already_queued_and_refills_from_the_new_position()
    {
        // Without stopping first, the buffers the seek means to discard are not
        // processed, AL refuses to unqueue them, and the listener hears the old
        // position for another four buffers.
        var backend = new FakeAudioBackend();
        StreamingVoice voice = Start(backend, Ramp(10_000, LoopRegion.None));

        int uploadsBefore = backend.Uploads.Count;
        voice.Seek(5000);

        backend.QueueDepth(voice.Source).ShouldBe(BufferCount);
        backend.StateOf(voice.Source).ShouldBe(AudioSourceState.Playing);
        voice.PositionFrames.ShouldBe(5000 + (BufferFrames * BufferCount));

        for (int k = 0; k < BufferCount; k++)
            backend.Uploads[uploadsBefore + k][0].ShouldBe((short)(5000 + (k * BufferFrames)));
    }

    [Fact]
    public void A_seek_into_the_middle_of_a_loop_keeps_looping_from_there()
    {
        var backend = new FakeAudioBackend();
        StreamingVoice voice = Start(backend, Ramp(1000, new LoopRegion(200, 450)));

        voice.Seek(400);

        // First buffer after the seek: 50 frames to the loop end, then the wrap,
        // never a restart from the intro.
        short[] first = backend.Uploads[^BufferCount];
        for (int i = 0; i < 50; i++) first[i].ShouldBe((short)(400 + i));
        for (int i = 50; i < 100; i++) first[i].ShouldBe((short)(200 + (i - 50)));

        voice.IsFinished.ShouldBeFalse();
        backend.QueueDepth(voice.Source).ShouldBe(BufferCount);
    }

    [Fact]
    public void A_stereo_stream_interleaves_both_channels_into_the_buffer()
    {
        // A frame is one sample PER CHANNEL, so a 100-frame stereo buffer is 200
        // samples. Confusing the two halves the length of every buffer and
        // doubles the apparent rate, which is the drift that measuring in frames
        // exists to prevent.
        var backend = new FakeAudioBackend();
        Start(backend, new RampSampleProvider(new AudioFormat(Rate, 2), 1000, LoopRegion.None));

        backend.Uploads[0].Length.ShouldBe(BufferFrames * 2);
        backend.Uploads[1][0].ShouldBe((short)BufferFrames);
    }

    [Fact]
    public void Detaching_destroys_every_buffer_the_voice_owned()
    {
        // A source is scarce and shared; buffers belong to whatever is streaming
        // through them, and handing a source back with a stranger's buffers
        // still queued is how a footstep inherits the tail of a music track.
        var backend = new FakeAudioBackend();
        StreamingVoice voice = Start(backend, Ramp(10_000, LoopRegion.None));
        backend.LiveBufferCount.ShouldBe(BufferCount);

        voice.Stop();
        voice.Detach();

        backend.LiveBufferCount.ShouldBe(0);
        voice.IsFinished.ShouldBeTrue();
    }

    // --- helpers -------------------------------------------------------------

    private static RampSampleProvider Ramp(long frames, LoopRegion loop) =>
        new(new AudioFormat(Rate, 1), frames, loop);

    private static StreamingVoice Start(FakeAudioBackend backend, IAudioSampleProvider provider)
    {
        backend.TryCreateSource(out uint source).ShouldBeTrue();
        return new StreamingVoice(backend, source, provider, AudioSourceSettings.Default, BufferCount, BufferFrames);
    }
}
