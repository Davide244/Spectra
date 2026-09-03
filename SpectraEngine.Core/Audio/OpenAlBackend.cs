using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Silk.NET.OpenAL;

namespace SpectraEngine.Core.Audio;

/// <summary>
/// <see cref="IAudioBackend"/> over Silk.NET's OpenAL bindings: opens the
/// default device, creates one context, and forwards every call the engine
/// makes.
/// </summary>
/// <remarks>
/// <para><b>Opening is allowed to fail, and failing is not an exception.</b>
/// A machine with no sound card, a headless CI agent and a remote-desktop
/// session with audio redirection off are all ordinary, and none of them is a
/// reason a game engine may not start. <see cref="TryCreate"/> therefore
/// returns false with a sentence rather than throwing, and
/// <see cref="AudioManager"/> turns that into disabled mode. This is the same
/// division the rest of the engine already makes: content and hardware
/// failures degrade, build steps refuse.</para>
/// <para><b>Errors are checked at creation and upload sites, not per property
/// write.</b> <c>alGetError</c> is a single latch per context and reading it
/// costs a round trip through the driver; checking it after every gain write in
/// a per-frame loop would spend more time asking than playing. The sites that
/// are checked are the ones whose failure is otherwise invisible: a buffer that
/// did not upload plays silence, and a source that was not created is handle
/// zero, which AL accepts and ignores.</para>
/// <para><b>The context is process-wide current, deliberately.</b> Core
/// <c>alcMakeContextCurrent</c> sets the current context for the PROCESS, not
/// for the calling thread. That is what lets the main thread open the device
/// in <c>Engine.InitializeSubsystems</c> and the render thread own every call
/// after it. OpenAL Soft's <c>ALC_EXT_thread_local_context</c> would change
/// that, so it is deliberately not used: a thread-local current context would
/// make every AL call the render thread makes a silent no-op against a null
/// context.</para>
/// </remarks>
public sealed unsafe class OpenAlBackend : IAudioBackend
{
    private readonly ILogger _logger;
    private readonly AL _al;
    private readonly ALContext _alc;

    private Device* _device;
    private Context* _context;
    private bool _disposed;

    private OpenAlBackend(ILogger logger, AL al, ALContext alc, Device* device, Context* context, string deviceName)
    {
        _logger = logger;
        _al = al;
        _alc = alc;
        _device = device;
        _context = context;
        DeviceName = deviceName;
    }

    /// <inheritdoc />
    public string DeviceName { get; }

    /// <summary>
    /// Opens the default device and makes a context current, or reports why it
    /// could not. Never throws: every failure the OpenAL loader can produce
    /// (no library, no device, no context) arrives here as false plus a
    /// sentence.
    /// </summary>
    public static bool TryCreate(
        ILogger logger,
        [NotNullWhen(true)] out IAudioBackend? backend,
        out string failureReason)
    {
        backend = null;
        failureReason = string.Empty;

        AL al;
        ALContext alc;
        try
        {
            // GetApi resolves the native OpenAL library. On a machine with no
            // OpenAL runtime at all this is where it fails, and it fails by
            // throwing rather than returning null.
            alc = ALContext.GetApi();
            al = AL.GetApi();
        }
        catch (Exception ex)
        {
            failureReason = $"the OpenAL runtime could not be loaded ({ex.GetType().Name}: {ex.Message})";
            return false;
        }

        Device* device = null;
        Context* context = null;
        try
        {
            device = alc.OpenDevice(string.Empty);
            if (device is null)
            {
                failureReason = "no audio output device is available";
                alc.Dispose();
                al.Dispose();
                return false;
            }

            context = alc.CreateContext(device, null);
            if (context is null)
            {
                failureReason = $"the audio device refused a context ({alc.GetError(device)})";
                alc.CloseDevice(device);
                alc.Dispose();
                al.Dispose();
                return false;
            }

            if (!alc.MakeContextCurrent(context))
            {
                failureReason = $"the audio context could not be made current ({alc.GetError(device)})";
                alc.DestroyContext(context);
                alc.CloseDevice(device);
                alc.Dispose();
                al.Dispose();
                return false;
            }
        }
        catch (Exception ex)
        {
            failureReason = $"opening the audio device threw ({ex.GetType().Name}: {ex.Message})";
            if (context is not null) alc.DestroyContext(context);
            if (device is not null) alc.CloseDevice(device);
            alc.Dispose();
            al.Dispose();
            return false;
        }

        string name = alc.GetContextProperty(device, GetContextString.DeviceSpecifier) ?? "unnamed device";

        // Inverse-distance-clamped is OpenAL's own default and what every
        // ReferenceDistance/MaxDistance number a designer authors assumes. It
        // is set explicitly because the default is per-context state a previous
        // context in the same process could have changed.
        al.DistanceModel(DistanceModel.InverseDistanceClamped);

        backend = new OpenAlBackend(logger, al, alc, device, context, name);
        return true;
    }

    /// <inheritdoc />
    public uint CreateBuffer()
    {
        uint buffer = _al.GenBuffer();
        CheckError("creating a buffer");
        return buffer;
    }

    /// <inheritdoc />
    public void DestroyBuffer(uint buffer) => _al.DeleteBuffer(buffer);

    /// <inheritdoc />
    public void UploadBuffer(uint buffer, AudioBufferFormat format, ReadOnlySpan<short> pcm, int sampleRate)
    {
        if (pcm.IsEmpty) return;

        BufferFormat alFormat = format == AudioBufferFormat.Mono16 ? BufferFormat.Mono16 : BufferFormat.Stereo16;
        fixed (short* data = pcm)
            _al.BufferData(buffer, alFormat, data, pcm.Length * AudioFormat.BytesPerSample, sampleRate);

        // Silent failure here is the expensive one: an upload that did not take
        // leaves the previous contents queued, so a stream repeats a chunk of
        // itself forever with nothing anywhere reporting a problem.
        CheckError("uploading PCM");
    }

    /// <inheritdoc />
    public bool TryCreateSource(out uint source)
    {
        source = _al.GenSource();
        AudioError error = _al.GetError();
        if (error == AudioError.NoError && source != 0)
            return true;

        // A driver with a hard source limit reports OutOfMemory here rather than
        // failing later, which is exactly what the pool wants: it sizes itself
        // to what the device actually granted instead of assuming 32.
        if (source != 0) _al.DeleteSource(source);
        source = 0;
        return false;
    }

    /// <inheritdoc />
    public void DestroySource(uint source) => _al.DeleteSource(source);

    /// <inheritdoc />
    public void ConfigureSource(uint source, in AudioSourceSettings settings)
    {
        _al.SetSourceProperty(source, SourceFloat.Gain, settings.Gain);
        _al.SetSourceProperty(source, SourceFloat.Pitch, settings.Pitch);
        _al.SetSourceProperty(source, SourceVector3.Position, settings.Position.X, settings.Position.Y, settings.Position.Z);
        _al.SetSourceProperty(source, SourceVector3.Velocity, settings.Velocity.X, settings.Velocity.Y, settings.Velocity.Z);
        _al.SetSourceProperty(source, SourceBoolean.SourceRelative, settings.Relative);

        // AL_LOOPING is cleared, never set, and this is the one line that
        // enforces the engine's loop policy at the driver. The flag repeats the
        // whole buffer and cannot express a region inside one, so loops are
        // buffer-queue arithmetic (see AudioLoopCursor) instead. Clearing it
        // explicitly matters because a pooled source is reused: a previous
        // voice that had somehow set it would leave the next sound looping with
        // nothing in this engine's own code to blame.
        _al.SetSourceProperty(source, SourceBoolean.Looping, false);
    }

    /// <inheritdoc />
    public AudioSourceState GetSourceState(uint source)
    {
        _al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
        return (SourceState)state switch
        {
            SourceState.Playing => AudioSourceState.Playing,
            SourceState.Paused => AudioSourceState.Paused,
            SourceState.Stopped => AudioSourceState.Stopped,
            _ => AudioSourceState.Initial,
        };
    }

    /// <inheritdoc />
    public int GetBuffersProcessed(uint source)
    {
        _al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out int processed);
        return processed;
    }

    /// <inheritdoc />
    public int GetBuffersQueued(uint source)
    {
        _al.GetSourceProperty(source, GetSourceInteger.BuffersQueued, out int queued);
        return queued;
    }

    /// <inheritdoc />
    public void SetSourceBuffer(uint source, uint buffer) =>
        _al.SetSourceProperty(source, SourceInteger.Buffer, buffer);

    /// <inheritdoc />
    public void QueueBuffer(uint source, uint buffer)
    {
        uint handle = buffer;
        _al.SourceQueueBuffers(source, 1, &handle);
    }

    /// <inheritdoc />
    public uint UnqueueBuffer(uint source)
    {
        uint handle = 0;
        _al.SourceUnqueueBuffers(source, 1, &handle);
        return handle;
    }

    /// <inheritdoc />
    public void Play(uint source) => _al.SourcePlay(source);

    /// <inheritdoc />
    public void Stop(uint source) => _al.SourceStop(source);

    /// <inheritdoc />
    public void Pause(uint source) => _al.SourcePause(source);

    /// <inheritdoc />
    public void SetListener(Vector3 position, Vector3 velocity, Vector3 forward, Vector3 up)
    {
        _al.SetListenerProperty(ListenerVector3.Position, position.X, position.Y, position.Z);
        _al.SetListenerProperty(ListenerVector3.Velocity, velocity.X, velocity.Y, velocity.Z);

        // AL_ORIENTATION is one six-float array, at (forward, up). Writing it as
        // two three-float calls is not the same thing and AL rejects it.
        float* orientation = stackalloc float[6]
        {
            forward.X, forward.Y, forward.Z,
            up.X, up.Y, up.Z,
        };
        _al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
    }

    /// <inheritdoc />
    public void SetListenerGain(float gain) => _al.SetListenerProperty(ListenerFloat.Gain, gain);

    /// <summary>
    /// Drops the context and closes the device. Idempotent, because a faulted
    /// render loop can reach shutdown twice.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unbind before destroying: destroying the current context is undefined
        // in the ALC spec and crashes rather than erroring on some drivers.
        _alc.MakeContextCurrent(null);

        if (_context is not null)
        {
            _alc.DestroyContext(_context);
            _context = null;
        }

        if (_device is not null)
        {
            _alc.CloseDevice(_device);
            _device = null;
        }

        _al.Dispose();
        _alc.Dispose();
    }

    private void CheckError(string what)
    {
        AudioError error = _al.GetError();
        if (error != AudioError.NoError)
            _logger.LogWarning("OpenAL reported {Error} while {What}", error, what);
    }
}
