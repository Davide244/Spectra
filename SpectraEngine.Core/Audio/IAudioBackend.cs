using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.Extensions.Logging;

namespace SpectraEngine.Core.Audio;

/// <summary>What OpenAL reports a source is doing right now.</summary>
public enum AudioSourceState
{
    /// <summary>Created and never played.</summary>
    Initial,

    /// <summary>Consuming its buffer or its queue.</summary>
    Playing,

    /// <summary>Held mid-sound; resuming continues from the same offset.</summary>
    Paused,

    /// <summary>
    /// Finished, stopped, or STARVED. The third is the trap: a streaming source
    /// that ran out of queued data reports Stopped exactly as a finished one
    /// does, which is why <see cref="AudioSourcePool"/> never classifies a
    /// streaming voice from this value alone.
    /// </summary>
    Stopped,
}

/// <summary>PCM16 buffer layouts, the only ones core OpenAL takes.</summary>
public enum AudioBufferFormat
{
    /// <summary>One channel. Required for a positional source: a stereo buffer plays unpositioned.</summary>
    Mono16,

    /// <summary>Two interleaved channels. Music and ambience; never positional.</summary>
    Stereo16,
}

/// <summary>Everything a voice tells its source about itself when it starts.</summary>
/// <param name="Gain">Linear amplitude multiplier; 1 is unattenuated.</param>
/// <param name="Pitch">Playback rate multiplier; 1 is the authored rate.</param>
/// <param name="Position">World position. Ignored by the driver for a stereo buffer.</param>
/// <param name="Velocity">World velocity, for Doppler.</param>
/// <param name="Relative">
/// True pins the source to the listener, which is how a UI click or a
/// first-person foley sound stays put while the head turns.
/// </param>
public readonly record struct AudioSourceSettings(
    float Gain,
    float Pitch,
    Vector3 Position,
    Vector3 Velocity,
    bool Relative)
{
    /// <summary>Unattenuated, unpitched, at the listener.</summary>
    public static AudioSourceSettings Default => new(1f, 1f, Vector3.Zero, Vector3.Zero, Relative: true);

    /// <summary>Unattenuated and unpitched at a world point.</summary>
    public static AudioSourceSettings At(Vector3 position) => new(1f, 1f, position, Vector3.Zero, Relative: false);
}

/// <summary>
/// The engine's whole OpenAL surface, and nothing more.
/// </summary>
/// <remarks>
/// <para><b>It exists for two reasons and no third one.</b> A CI machine has no
/// sound card, so the source pool, the reclaim policy and the buffer-queue loop
/// arithmetic would otherwise have no oracle at all; and the engine must run on
/// a machine with no audio device, which is the same code path with no
/// implementation behind it. It is deliberately NOT a general audio
/// abstraction: it names OpenAL's own vocabulary (buffer handles, source
/// handles, a processed count) because a second implementation would be a fake,
/// never a second driver, and pretending otherwise would cost indirection for a
/// portability nobody asked for.</para>
/// <para><b>Threading.</b> One thread at a time, and in this engine that thread
/// is the render thread. See <see cref="AudioManager"/> for the whole rule and
/// the failure it prevents.</para>
/// </remarks>
public interface IAudioBackend : IDisposable
{
    /// <summary>Human-readable device name, for the one line startup logs.</summary>
    string DeviceName { get; }

    /// <summary>Allocates an AL buffer handle.</summary>
    uint CreateBuffer();

    /// <summary>Frees an AL buffer handle. Must not be queued on a live source.</summary>
    void DestroyBuffer(uint buffer);

    /// <summary>Uploads interleaved PCM16 into a buffer, replacing whatever it held.</summary>
    void UploadBuffer(uint buffer, AudioBufferFormat format, ReadOnlySpan<short> pcm, int sampleRate);

    /// <summary>Allocates an AL source handle. Returns false when the driver refuses, which is how a pool learns its real size.</summary>
    bool TryCreateSource(out uint source);

    /// <summary>Frees an AL source handle.</summary>
    void DestroySource(uint source);

    /// <summary>Applies gain, pitch, position, velocity and listener-relative in one call.</summary>
    void ConfigureSource(uint source, in AudioSourceSettings settings);

    /// <summary>Reads the source's play state.</summary>
    AudioSourceState GetSourceState(uint source);

    /// <summary>Buffers the source has finished with and is waiting to hand back.</summary>
    int GetBuffersProcessed(uint source);

    /// <summary>Buffers queued on the source, processed ones included.</summary>
    int GetBuffersQueued(uint source);

    /// <summary>
    /// Binds a single buffer to a static source, or detaches with 0. Detaching
    /// is what makes a source that was static reusable as a streaming one; AL
    /// refuses a queue operation on a source still holding a static buffer.
    /// </summary>
    void SetSourceBuffer(uint source, uint buffer);

    /// <summary>Appends a buffer to the source's play queue.</summary>
    void QueueBuffer(uint source, uint buffer);

    /// <summary>Takes one processed buffer back off the head of the queue.</summary>
    uint UnqueueBuffer(uint source);

    /// <summary>Starts or resumes the source.</summary>
    void Play(uint source);

    /// <summary>Stops the source and rewinds it. Processed buffers stay queued until unqueued.</summary>
    void Stop(uint source);

    /// <summary>Holds the source at its current offset.</summary>
    void Pause(uint source);

    /// <summary>Places the listener. Forward and up are the orientation pair AL takes together.</summary>
    void SetListener(Vector3 position, Vector3 velocity, Vector3 forward, Vector3 up);

    /// <summary>Master gain, applied by the driver after every source's own.</summary>
    void SetListenerGain(float gain);
}

/// <summary>
/// Opens the audio device, or says in one sentence why it could not.
/// </summary>
/// <remarks>
/// A delegate rather than an interface because there is exactly one production
/// implementation (<see cref="OpenAlBackend.TryCreate"/>) and exactly one test
/// one, and an interface for two implementations that never vary is ceremony.
/// </remarks>
/// <param name="logger">Where the backend reports driver-level problems.</param>
/// <param name="backend">The opened backend, or null.</param>
/// <param name="failureReason">
/// One sentence naming what was missing. It is logged verbatim, so it has to
/// read as an explanation to somebody who is not holding this file: "no audio
/// device", not "alcOpenDevice returned NULL".
/// </param>
public delegate bool AudioBackendFactory(
    ILogger logger,
    [NotNullWhen(true)] out IAudioBackend? backend,
    out string failureReason);
