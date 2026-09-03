using System;

namespace SpectraEngine.Core.Audio;

/// <summary>
/// A decoded sound the engine owns: PCM16 the manager uploaded, plus the loop
/// points that decide how it is played back.
/// </summary>
/// <remarks>
/// <para><b>Whether the CPU copy is kept depends on the loop, and that is not
/// an optimisation detail.</b> A clip with no loop is uploaded once into one AL
/// buffer and never read again, so its samples are dropped; a clip WITH a loop
/// is fed through a buffer queue frame by frame (see
/// <see cref="AudioLoopCursor"/>), so its samples are the source that queue
/// reads from and have to stay. Keeping both for everything would double the
/// memory of every gunshot in the game for a re-read that never happens, which
/// is the same call <c>MeshCpuAccess</c> makes on the graphics side.</para>
/// <para>Clips are created and destroyed through <see cref="AudioManager"/>,
/// which owns the AL buffer. A caller never frees one itself, exactly as it
/// never disposes a texture it got from <c>AssetManager</c>.</para>
/// </remarks>
public sealed class AudioClip
{
    private short[]? _samples;

    internal AudioClip(AudioFormat format, LoopRegion loop, long frameCount, uint buffer, short[]? samples)
    {
        Format = format;
        Loop = loop;
        FrameCount = frameCount;
        Buffer = buffer;
        _samples = samples;
    }

    /// <summary>Rate and channel count.</summary>
    public AudioFormat Format { get; }

    /// <summary>The region this clip repeats, or <see cref="LoopRegion.None"/>.</summary>
    public LoopRegion Loop { get; }

    /// <summary>Decoded length in sample frames.</summary>
    public long FrameCount { get; }

    /// <summary>Seconds of audio.</summary>
    public double Duration => Format.FramesToSeconds(FrameCount);

    /// <summary>
    /// The AL buffer holding the whole clip, or 0 for a looping clip, which is
    /// played from its samples through a queue instead.
    /// </summary>
    internal uint Buffer { get; private set; }

    /// <summary>True once the manager has destroyed it; playing it is a no-op afterwards.</summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>Interleaved PCM, kept only for a looping clip.</summary>
    internal ReadOnlySpan<short> Samples => _samples is { } samples ? new ReadOnlySpan<short>(samples) : default;

    /// <summary>True when the clip can feed a buffer queue, i.e. it kept its samples.</summary>
    internal bool HasSamples => _samples is not null;

    internal void MarkDestroyed()
    {
        IsDestroyed = true;
        Buffer = 0;
        _samples = null;
    }
}

/// <summary>
/// Reads a resident <see cref="AudioClip"/>'s samples for a
/// <see cref="StreamingVoice"/>, which is how a clip with loop points is played
/// without ever touching <c>AL_LOOPING</c>.
/// </summary>
internal sealed class ClipSampleProvider : IAudioSampleProvider
{
    private readonly AudioClip _clip;

    public ClipSampleProvider(AudioClip clip) => _clip = clip;

    public AudioFormat Format => _clip.Format;

    public long FrameCount => _clip.FrameCount;

    public LoopRegion Loop => _clip.Loop;

    public int ReadFrames(long offsetFrames, Span<short> destination, int frameCount)
    {
        ReadOnlySpan<short> samples = _clip.Samples;
        if (samples.IsEmpty || frameCount <= 0) return 0;

        int channels = _clip.Format.Channels;
        long available = _clip.FrameCount - offsetFrames;
        if (available <= 0) return 0;

        int frames = (int)Math.Min(frameCount, available);
        frames = Math.Min(frames, destination.Length / channels);
        if (frames <= 0) return 0;

        samples.Slice((int)(offsetFrames * channels), frames * channels).CopyTo(destination);
        return frames;
    }
}
