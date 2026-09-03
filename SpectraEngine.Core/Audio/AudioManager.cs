using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.Logging;

namespace SpectraEngine.Core.Audio;

/// <summary>
/// The engine's audio device: one OpenAL context, one listener, a fixed pool of
/// sources, and the voices currently feeding them.
/// </summary>
/// <remarks>
/// <para><b>Threading, and the failure it prevents.</b> The render thread owns
/// this class, exactly as it owns the asset manager's GPU half, and every
/// member except <see cref="Initialize"/> and <see cref="Shutdown"/> is render
/// thread only. The reason is not thread affinity: core OpenAL's
/// <c>alcMakeContextCurrent</c> makes a context current for the PROCESS, so any
/// thread may legally call AL. What cannot be shared is the calling, for two
/// reasons that both fail silently. <c>alGetError</c> is a single latch per
/// context, so two threads interleaving turns every error check into a coin
/// flip: one thread clears the error the other was about to read, a failed
/// upload reports success, and the sound simply never plays. And every
/// interesting operation here is read-then-act on driver state that the other
/// thread is also writing: the pool reads <c>AL_SOURCE_STATE</c> and hands the
/// source out, the streaming pump reads <c>AL_BUFFERS_PROCESSED</c> and
/// unqueues exactly that many, so two callers either hand the same source to
/// two sounds or unqueue a buffer that is not theirs.</para>
/// <para><b>No new channel was invented for it.</b> <see cref="Initialize"/> and
/// <see cref="Shutdown"/> run on the main thread from
/// <c>Engine.InitializeSubsystems</c>/<c>ShutdownSubsystems</c>, which is safe
/// for the single reason it is safe for <c>AssetManager</c>: the render thread
/// provably does not exist yet, or has already been joined. Anything else that
/// wants to start a sound posts through <c>EngineHost.EnqueueCommand</c>, which
/// already drains on the render thread once a frame.</para>
/// <para><b>No device is DISABLED MODE, never a crash.</b> A machine with no
/// sound card, a CI agent and a remote session with audio redirection off are
/// all ordinary. Opening the device is allowed to fail; it logs one line naming
/// the reason and every call afterwards is a safe no-op returning null or zero.
/// This is the engine's standing division: hardware and content failures
/// degrade, build steps refuse.</para>
/// <para><b>Loops are never <c>AL_LOOPING</c>.</b> That flag repeats a whole
/// buffer and cannot express a region inside one, which is what music with an
/// intro and ambience with a pickup bar actually need. A clip carrying loop
/// points is played through <see cref="StreamingVoice"/>, whose
/// <see cref="AudioLoopCursor"/> does the wrap in sample frames; the flag is
/// explicitly cleared on every source the pool hands out.</para>
/// </remarks>
public sealed class AudioManager : IDisposable
{
    /// <summary>
    /// Sources asked of the driver. Thirty-two is the number every OpenAL
    /// implementation grants and roughly the number of simultaneous sounds a
    /// listener can distinguish; a driver granting fewer is honoured rather
    /// than argued with (see <see cref="AudioSourcePool.Capacity"/>).
    /// </summary>
    public const int DefaultSourceCount = 32;

    private readonly ILogger _logger;
    private readonly AudioBackendFactory _factory;
    private readonly int _requestedSourceCount;
    private readonly List<AudioVoice> _voices = new();
    private readonly List<AudioClip> _clips = new();

    private IAudioBackend? _backend;
    private AudioSourcePool? _pool;
    private float _masterGain = 1f;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates a manager that opens the default OpenAL device on <see cref="Initialize"/>.</summary>
    public AudioManager(ILogger logger)
        : this(logger, OpenAlBackend.TryCreate, DefaultSourceCount)
    {
    }

    /// <summary>
    /// Creates a manager over a supplied backend factory. The seam exists so
    /// the pool, the reclaim policy, the loop arithmetic and this class's
    /// disabled path have an oracle on a machine with no sound card, which is
    /// every CI machine.
    /// </summary>
    public AudioManager(ILogger logger, AudioBackendFactory factory, int sourceCount = DefaultSourceCount)
    {
        if (sourceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceCount), sourceCount, "A source pool needs at least one source.");

        _logger = logger;
        _factory = factory;
        _requestedSourceCount = sourceCount;
    }

    /// <summary>False when no device could be opened. Every call is a no-op in that state.</summary>
    public bool IsEnabled => _backend is not null;

    /// <summary>Why audio is off, or empty when it is on. Logged once at startup.</summary>
    public string DisabledReason { get; private set; } = string.Empty;

    /// <summary>The opened device's name, or empty when disabled.</summary>
    public string DeviceName => _backend?.DeviceName ?? string.Empty;

    /// <summary>Sources the driver granted; 0 when disabled.</summary>
    public int SourceCount => _pool?.Capacity ?? 0;

    /// <summary>Voices currently playing.</summary>
    public int ActiveVoiceCount => _voices.Count;

    /// <summary>Sounds cut off because every source was busy. See <see cref="AudioSourcePool.StolenCount"/>.</summary>
    public int StolenVoiceCount => _pool?.StolenCount ?? 0;

    /// <summary>Sounds dropped because every source was carrying a stream. See <see cref="AudioSourcePool.StarvedCount"/>.</summary>
    public int DroppedVoiceCount => _pool?.StarvedCount ?? 0;

    /// <summary>
    /// Master gain, applied by the driver after every source's own. Clamped at
    /// zero rather than throwing, because a fader running slightly negative is
    /// an ordinary rounding result and silence is the obvious answer to it.
    /// </summary>
    public float MasterGain
    {
        get => _masterGain;
        set
        {
            _masterGain = Math.Max(0f, value);
            _backend?.SetListenerGain(_masterGain);
        }
    }

    /// <summary>Where the listener is, in world units.</summary>
    public Vector3 ListenerPosition { get; private set; }

    /// <summary>Which way the listener faces.</summary>
    public Vector3 ListenerForward { get; private set; } = -Vector3.UnitZ;

    /// <summary>The listener's up axis. Paired with forward, which is how AL takes orientation.</summary>
    public Vector3 ListenerUp { get; private set; } = Vector3.UnitY;

    /// <summary>The listener's velocity, for Doppler. Zero unless a caller supplies one.</summary>
    public Vector3 ListenerVelocity { get; private set; }

    /// <summary>
    /// Opens the audio device, or turns disabled mode on with one line saying
    /// why. Main thread, before the render thread starts. Idempotent.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        if (!_factory(_logger, out IAudioBackend? backend, out string failureReason))
        {
            DisabledReason = string.IsNullOrEmpty(failureReason) ? "the audio device could not be opened" : failureReason;

            // One line, at Warning rather than Error: a machine with no sound
            // card is a machine the engine still runs on, and an ERR here would
            // fail every smoke gate that greps for one.
            _logger.LogWarning("Audio disabled: {Reason}. Every audio call is a no-op for this session", DisabledReason);
            return;
        }

        _backend = backend;
        _pool = new AudioSourcePool(backend, _requestedSourceCount);
        backend.SetListenerGain(_masterGain);
        backend.SetListener(ListenerPosition, ListenerVelocity, ListenerForward, ListenerUp);

        _logger.LogInformation(
            "Audio manager initialized on {Device} with {Sources} sources",
            backend.DeviceName,
            _pool.Capacity);

        if (_pool.Capacity < _requestedSourceCount)
        {
            _logger.LogWarning(
                "The audio driver granted {Granted} of {Requested} sources; sounds past that will reclaim or be dropped",
                _pool.Capacity,
                _requestedSourceCount);
        }
    }

    /// <summary>
    /// Stops everything, frees every clip and source, and closes the device.
    /// Main thread, after the render thread has been joined. Idempotent.
    /// </summary>
    public void Shutdown()
    {
        if (!_initialized) return;
        _initialized = false;

        if (_backend is null)
        {
            _logger.LogInformation("Audio manager shut down (was disabled: {Reason})", DisabledReason);
            return;
        }

        for (int i = 0; i < _voices.Count; i++)
        {
            _voices[i].Stop();
            _voices[i].Detach();
        }

        _voices.Clear();

        for (int i = 0; i < _clips.Count; i++)
        {
            if (_clips[i].Buffer != 0) _backend.DestroyBuffer(_clips[i].Buffer);
            _clips[i].MarkDestroyed();
        }

        _clips.Clear();

        _pool?.ReleaseAll();
        _pool?.Dispose();
        _pool = null;

        _backend.Dispose();
        _backend = null;

        _logger.LogInformation("Audio manager shut down");
    }

    /// <summary>
    /// Places the listener. Render thread, once a frame: the engine feeds it the
    /// active camera, without which every positional sound plays at the world
    /// origin and the whole feature reads as broken.
    /// </summary>
    public void SetListener(Vector3 position, Vector3 forward, Vector3 up) =>
        SetListener(position, forward, up, Vector3.Zero);

    /// <inheritdoc cref="SetListener(Vector3, Vector3, Vector3)" />
    public void SetListener(Vector3 position, Vector3 forward, Vector3 up, Vector3 velocity)
    {
        ListenerPosition = position;
        ListenerForward = forward;
        ListenerUp = up;
        ListenerVelocity = velocity;
        _backend?.SetListener(position, velocity, forward, up);
    }

    /// <summary>
    /// Uploads decoded PCM16 and returns the clip that owns it, or null when
    /// audio is disabled. Render thread, because the AL buffer is created here.
    /// </summary>
    /// <param name="format">Rate and channel count of <paramref name="pcm"/>.</param>
    /// <param name="pcm">Interleaved samples. Length must be a whole number of frames.</param>
    /// <param name="loop">
    /// The region to repeat, in sample frames. A clip carrying one keeps its CPU
    /// samples, because the loop is played by feeding a buffer queue from them.
    /// </param>
    public AudioClip? CreateClip(AudioFormat format, ReadOnlySpan<short> pcm, LoopRegion loop = default)
    {
        if (_backend is null) return null;

        if (pcm.Length % format.Channels != 0)
            throw new ArgumentException("PCM length is not a whole number of sample frames.", nameof(pcm));

        long frames = format.SamplesToFrames(pcm.Length);
        if (loop.IsLooping && loop.EndFrame > frames)
            throw new ArgumentOutOfRangeException(nameof(loop), loop, "A loop cannot end past the clip.");

        AudioClip clip;
        if (loop.IsLooping)
        {
            // Kept, not uploaded: a looping clip is played through a queue this
            // array feeds, and one AL buffer holding the whole sound could only
            // be looped with AL_LOOPING, which is the thing this engine refuses
            // to use.
            clip = new AudioClip(format, loop, frames, buffer: 0, samples: pcm.ToArray());
        }
        else
        {
            uint buffer = _backend.CreateBuffer();
            _backend.UploadBuffer(buffer, ToBufferFormat(format), pcm, format.SampleRate);
            clip = new AudioClip(format, loop, frames, buffer, samples: null);
        }

        _clips.Add(clip);
        return clip;
    }

    /// <summary>
    /// Stops every voice playing the clip and frees it. Render thread.
    /// Idempotent, and safe on a clip from a different manager (it is simply not
    /// found).
    /// </summary>
    public void DestroyClip(AudioClip? clip)
    {
        if (_backend is null || clip is null || clip.IsDestroyed) return;
        if (!_clips.Remove(clip)) return;

        if (clip.Buffer != 0)
        {
            // The voices holding it go first, and go all the way back to the
            // pool rather than merely stopping: deleting a buffer a source
            // still has BOUND is an AL_INVALID_OPERATION, and AL answers it by
            // leaving the buffer alive, so the leak is silent. Releasing the
            // source is what detaches it.
            RetireVoicesPlaying(clip);
            _backend.DestroyBuffer(clip.Buffer);
        }

        clip.MarkDestroyed();
    }

    /// <summary>
    /// Plays a clip once, or on its loop when it has one. Returns null when
    /// audio is disabled, the clip is gone, or every source is carrying a
    /// stream. Render thread.
    /// </summary>
    public AudioVoice? Play(AudioClip? clip) => Play(clip, AudioSourceSettings.Default);

    /// <inheritdoc cref="Play(AudioClip)" />
    public AudioVoice? Play(AudioClip? clip, in AudioSourceSettings settings)
    {
        if (_backend is null || _pool is null || clip is null || clip.IsDestroyed) return null;

        bool streaming = clip.Loop.IsLooping;
        if (!_pool.TryAcquire(streaming, out uint source)) return null;

        AudioVoice voice = streaming
            ? new StreamingVoice(_backend, source, new ClipSampleProvider(clip), settings)
            : new StaticVoice(_backend, source, clip, settings);

        return Track(voice, source);
    }

    /// <summary>
    /// Plays a long sound through a buffer queue fed by
    /// <paramref name="provider"/>. Returns null when audio is disabled or no
    /// source could be had. Render thread.
    /// </summary>
    public StreamingVoice? PlayStream(IAudioSampleProvider provider, in AudioSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_backend is null || _pool is null) return null;
        if (!_pool.TryAcquire(streaming: true, out uint source)) return null;

        var voice = new StreamingVoice(_backend, source, provider, settings);
        return (StreamingVoice?)Track(voice, source);
    }

    /// <summary>Stops every voice without touching the clips they were playing. Render thread.</summary>
    public void StopAll()
    {
        for (int i = 0; i < _voices.Count; i++)
            _voices[i].Stop();
    }

    /// <summary>
    /// Refills every streaming queue and hands finished sources back to the
    /// pool. Render thread, once a frame, in the same slot the asset manager's
    /// upload pump runs. Returns the number of voices still playing.
    /// </summary>
    /// <remarks>
    /// A frame that skips this does not merely stop reclaiming: a streaming
    /// voice is only ever refilled here, so its queue drains and the sound
    /// stutters. That is why it sits beside the asset pump rather than behind a
    /// condition.
    /// </remarks>
    public int Update()
    {
        if (_backend is null || _pool is null) return 0;

        // Iterated backwards so a voice that finishes can be removed without
        // moving an index the loop has not reached yet.
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            AudioVoice voice = _voices[i];
            if (voice.Update()) continue;

            uint source = voice.Source;
            voice.Detach();
            _pool.Release(source);
            _voices.RemoveAt(i);
        }

        return _voices.Count;
    }

    /// <summary>Shuts down if it has not already. Present so a host can use a <c>using</c>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }

    private AudioVoice Track(AudioVoice voice, uint source)
    {
        // A voice can finish inside its own constructor: a zero-length stream
        // queues nothing and is over before it is tracked. Handing the source
        // straight back is what keeps the pool from leaking one per such call.
        if (voice.IsFinished)
        {
            voice.Detach();
            _pool!.Release(source);
            return voice;
        }

        _voices.Add(voice);
        return voice;
    }

    // A static voice is the only kind that binds a clip's buffer directly; a
    // streaming voice owns its own buffers and merely READS the clip's samples,
    // so it survives the clip's AL buffer going away.
    private void RetireVoicesPlaying(AudioClip clip)
    {
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            if (_voices[i] is not StaticVoice voice || voice.Clip != clip) continue;

            uint source = voice.Source;
            voice.Stop();
            voice.Detach();
            _pool!.Release(source);
            _voices.RemoveAt(i);
        }
    }

    private static AudioBufferFormat ToBufferFormat(AudioFormat format) =>
        format.Channels == 1 ? AudioBufferFormat.Mono16 : AudioBufferFormat.Stereo16;
}
