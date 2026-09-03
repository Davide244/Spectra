using SpectraEngine.Core.Audio;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// An <see cref="IAudioSampleProvider"/> whose sample at frame <c>n</c> IS
/// <c>n</c>, so a test can read a buffer the streaming voice uploaded and say
/// exactly which source frames went into it.
/// </summary>
/// <remarks>
/// Silence would prove the plumbing and nothing about the loop arithmetic: the
/// whole question is which frames came out and in what order, and against a
/// constant signal every wrap looks identical to no wrap at all.
/// </remarks>
internal sealed class RampSampleProvider : IAudioSampleProvider
{
    public RampSampleProvider(AudioFormat format, long frameCount, LoopRegion loop)
    {
        Format = format;
        FrameCount = frameCount;
        Loop = loop;
    }

    public AudioFormat Format { get; }

    public long FrameCount { get; }

    public LoopRegion Loop { get; }

    /// <summary>Frames handed out since construction, for asserting a fill is not doing extra work.</summary>
    public long FramesRead { get; private set; }

    public int ReadFrames(long offsetFrames, Span<short> destination, int frameCount)
    {
        int channels = Format.Channels;
        long available = FrameCount - offsetFrames;
        if (available <= 0 || frameCount <= 0) return 0;

        int frames = (int)Math.Min(frameCount, available);
        frames = Math.Min(frames, destination.Length / channels);

        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                destination[(i * channels) + c] = (short)(offsetFrames + i);

        FramesRead += frames;
        return frames;
    }
}
