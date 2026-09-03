using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Audio;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The manager over a fake device: clip ownership, the two playback paths, the
/// per-frame pump, and the disabled mode a machine with no sound card gets.
/// </summary>
/// <remarks>
/// Real playback is a manual gate, like <c>--offscreen-probe</c>: CI has no
/// sound card, and a test that needs one is a test that gets disabled. What is
/// testable without a device is every decision the engine makes ABOUT the
/// device, and that is what is here.
/// </remarks>
public sealed class AudioManagerTests
{
    private const int Rate = 48000;

    [Fact]
    public void No_device_is_disabled_mode_and_every_call_is_a_safe_no_op()
    {
        var logger = new CapturingLogger();
        var audio = new AudioManager(logger, FailingBackend("no audio output device is available"));

        audio.Initialize();

        audio.IsEnabled.ShouldBeFalse();
        audio.DisabledReason.ShouldContain("no audio output device");
        audio.SourceCount.ShouldBe(0);

        // One line, and at Warning rather than Error: a machine with no sound
        // card still runs the engine, and an ERR here would fail every smoke
        // gate that greps for one.
        logger.MessagesAt(LogLevel.Warning).Count.ShouldBe(1);
        logger.MessagesAt(LogLevel.Error).ShouldBeEmpty();

        // Everything below must be reachable without a device, because a game
        // that guarded each call would be a game that forgot one.
        AudioClip? clip = audio.CreateClip(new AudioFormat(Rate, 1), Tone(1000));
        clip.ShouldBeNull();

        audio.Play(clip).ShouldBeNull();
        audio.Play(clip, AudioSourceSettings.At(Vector3.One)).ShouldBeNull();
        audio.PlayStream(new RampSampleProvider(new AudioFormat(Rate, 1), 1000, LoopRegion.None), AudioSourceSettings.Default)
            .ShouldBeNull();
        audio.DestroyClip(clip);
        audio.SetListener(Vector3.One, -Vector3.UnitZ, Vector3.UnitY);
        audio.SetListener(Vector3.One, -Vector3.UnitZ, Vector3.UnitY, Vector3.Zero);
        audio.MasterGain = 0.5f;
        audio.StopAll();
        audio.Update().ShouldBe(0);
        audio.Shutdown();
        audio.Dispose();

        // The listener still reports what it was told, so a caller reading it
        // back does not see the device's absence leak into its own state.
        audio.ListenerPosition.ShouldBe(Vector3.One);
        audio.MasterGain.ShouldBe(0.5f);
        logger.MessagesAt(LogLevel.Error).ShouldBeEmpty(logger.Describe());
    }

    [Fact]
    public void A_disabled_manager_initializes_and_shuts_down_repeatedly_without_throwing()
    {
        var logger = new CapturingLogger();
        var audio = new AudioManager(logger, FailingBackend("the OpenAL runtime could not be loaded"));

        audio.Initialize();
        audio.Initialize();
        audio.Shutdown();
        audio.Shutdown();
        audio.Dispose();

        logger.MessagesAt(LogLevel.Error).ShouldBeEmpty(logger.Describe());
    }

    [Fact]
    public void A_clip_with_no_loop_uploads_once_and_plays_from_a_single_buffer()
    {
        var backend = new FakeAudioBackend();
        var audio = NewManager(backend);

        AudioClip clip = audio.CreateClip(new AudioFormat(Rate, 1), Tone(600))!;
        clip.ShouldNotBeNull();
        backend.Uploads.Count.ShouldBe(1);
        backend.Uploads[0].Length.ShouldBe(600);

        AudioVoice voice = audio.Play(clip)!;
        voice.ShouldBeOfType<StaticVoice>();
        audio.ActiveVoiceCount.ShouldBe(1);

        // Still going: the pump must not reclaim a source that is playing.
        audio.Update().ShouldBe(1);

        backend.Finish(voice.Source);
        audio.Update().ShouldBe(0);
        voice.IsFinished.ShouldBeTrue();

        audio.Shutdown();
    }

    [Fact]
    public void A_clip_with_loop_points_is_played_through_a_queue_rather_than_a_single_buffer()
    {
        // The load-bearing behaviour of the whole stage: AL_LOOPING repeats the
        // WHOLE buffer, so a clip with a region inside it cannot use a single
        // static buffer at all. It has to arrive as a queue.
        var backend = new FakeAudioBackend();
        var audio = NewManager(backend);

        AudioClip clip = audio.CreateClip(new AudioFormat(Rate, 1), Tone(4000), new LoopRegion(1000, 3000))!;

        // Nothing uploaded at create time: a looping clip keeps its CPU samples
        // and feeds them to a queue instead.
        backend.Uploads.ShouldBeEmpty();

        AudioVoice voice = audio.Play(clip)!;
        voice.ShouldBeOfType<StreamingVoice>();
        backend.QueueDepth(voice.Source).ShouldBe(StreamingVoice.DefaultBufferCount);
        backend.StateOf(voice.Source).ShouldBe(AudioSourceState.Playing);

        audio.Shutdown();
    }

    [Fact]
    public void Destroying_a_clip_retires_the_voices_holding_its_buffer_first()
    {
        // Deleting a buffer a source still has BOUND is an AL_INVALID_OPERATION
        // that AL answers by leaving the buffer alive, so the leak is silent.
        var backend = new FakeAudioBackend();
        var audio = NewManager(backend);

        AudioClip clip = audio.CreateClip(new AudioFormat(Rate, 1), Tone(600))!;
        AudioVoice voice = audio.Play(clip)!;
        backend.LiveBufferCount.ShouldBe(1);

        audio.DestroyClip(clip);

        voice.IsFinished.ShouldBeTrue();
        audio.ActiveVoiceCount.ShouldBe(0);
        backend.LiveBufferCount.ShouldBe(0);
        clip.IsDestroyed.ShouldBeTrue();

        // A stale handle stops nothing and plays nothing.
        voice.Stop();
        audio.Play(clip).ShouldBeNull();

        audio.Shutdown();
    }

    [Fact]
    public void The_listener_reaches_the_driver_as_a_position_and_an_orientation_pair()
    {
        var backend = new FakeAudioBackend();
        var audio = NewManager(backend);

        audio.SetListener(new Vector3(1, 2, 3), -Vector3.UnitX, Vector3.UnitY, new Vector3(0, 0, 4));

        backend.ListenerPosition.ShouldBe(new Vector3(1, 2, 3));
        backend.ListenerForward.ShouldBe(-Vector3.UnitX);
        backend.ListenerUp.ShouldBe(Vector3.UnitY);
        backend.ListenerVelocity.ShouldBe(new Vector3(0, 0, 4));

        // A fader running slightly negative is an ordinary rounding result;
        // silence is the obvious answer to it, and throwing would take out the
        // frame that produced it.
        audio.MasterGain = -0.25f;
        audio.MasterGain.ShouldBe(0f);
        backend.ListenerGain.ShouldBe(0f);

        audio.Shutdown();
    }

    [Fact]
    public void Shutdown_frees_every_source_and_buffer_and_closes_the_device()
    {
        var backend = new FakeAudioBackend(maxSources: 4);
        var audio = NewManager(backend, sources: 4);

        AudioClip shot = audio.CreateClip(new AudioFormat(Rate, 1), Tone(600))!;
        AudioClip music = audio.CreateClip(new AudioFormat(Rate, 1), Tone(4000), new LoopRegion(0, 4000))!;
        audio.Play(shot);
        audio.Play(music);

        backend.LiveBufferCount.ShouldBe(1 + StreamingVoice.DefaultBufferCount);

        audio.Shutdown();

        backend.LiveBufferCount.ShouldBe(0);
        backend.LiveSourceCount.ShouldBe(0);
        backend.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public void An_exhausted_pool_drops_a_one_shot_rather_than_cutting_the_music()
    {
        var backend = new FakeAudioBackend(maxSources: 1);
        var audio = NewManager(backend, sources: 1);

        AudioClip music = audio.CreateClip(new AudioFormat(Rate, 1), Tone(4000), new LoopRegion(0, 4000))!;
        AudioClip shot = audio.CreateClip(new AudioFormat(Rate, 1), Tone(600))!;

        audio.Play(music).ShouldNotBeNull();
        audio.Play(shot).ShouldBeNull();
        audio.DroppedVoiceCount.ShouldBe(1);

        audio.Shutdown();
    }

    // --- helpers -------------------------------------------------------------

    private static AudioManager NewManager(FakeAudioBackend backend, int sources = AudioManager.DefaultSourceCount)
    {
        var audio = new AudioManager(new CapturingLogger(), Supply(backend), sources);
        audio.Initialize();
        audio.IsEnabled.ShouldBeTrue();
        return audio;
    }

    private static AudioBackendFactory Supply(IAudioBackend backend) =>
        (ILogger _, [NotNullWhen(true)] out IAudioBackend? created, out string reason) =>
        {
            created = backend;
            reason = string.Empty;
            return true;
        };

    private static AudioBackendFactory FailingBackend(string reason) =>
        (ILogger _, [NotNullWhen(true)] out IAudioBackend? created, out string failure) =>
        {
            created = null;
            failure = reason;
            return false;
        };

    /// <summary>A ramp rather than silence, so a test can tell one frame from another.</summary>
    private static short[] Tone(int frames)
    {
        var pcm = new short[frames];
        for (int i = 0; i < frames; i++) pcm[i] = (short)(i % short.MaxValue);
        return pcm;
    }
}
