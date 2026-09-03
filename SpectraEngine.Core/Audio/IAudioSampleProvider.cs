using System;

namespace SpectraEngine.Core.Audio;

/// <summary>
/// Where a <see cref="StreamingVoice"/> gets its frames. Random access by frame
/// offset, because a loop and a seek are both jumps and neither is expressible
/// against a forward-only reader.
/// </summary>
/// <remarks>
/// <para>Random access is the requirement that shapes this interface. A
/// decoder that can only go forwards can play a sound, but it cannot loop a
/// region inside one without decoding from the start every time round, and it
/// cannot honour a seek at all: those are the two things the buffer-queue loop
/// arithmetic exists to do. A codec whose decoder is forward-only pays for that
/// with a seek table, which is exactly what <c>.saudio</c> reserves a field
/// for.</para>
/// <para>Called from the render thread, inside the per-frame pump, so a
/// provider that reads from disk owes its caller a buffer rather than a blocking
/// read. Nothing in this stage does that; the memory provider below is the only
/// implementation.</para>
/// </remarks>
public interface IAudioSampleProvider
{
    /// <summary>Rate and channel count of the frames this provider returns.</summary>
    AudioFormat Format { get; }

    /// <summary>Total decoded sample frames.</summary>
    long FrameCount { get; }

    /// <summary>The region to repeat, or <see cref="LoopRegion.None"/>.</summary>
    LoopRegion Loop { get; }

    /// <summary>
    /// Copies <paramref name="frameCount"/> sample frames starting at
    /// <paramref name="offsetFrames"/> into <paramref name="destination"/>, as
    /// interleaved samples.
    /// </summary>
    /// <returns>
    /// Frames actually written, which may be fewer at the end of the sound. A
    /// short read is not an error; the voice shortens the buffer it uploads.
    /// </returns>
    int ReadFrames(long offsetFrames, Span<short> destination, int frameCount);
}
