using System;

namespace SpectraEngine.Core.Audio;

/// <summary>
/// The half-open region <c>[StartFrame, EndFrame)</c> a sound repeats, in
/// SAMPLE FRAMES. <see cref="None"/> is the default and means "play once".
/// </summary>
/// <remarks>
/// <para><b>Why this type exists at all, rather than <c>AL_LOOPING</c>.</b>
/// OpenAL's looping flag repeats the WHOLE buffer, so it can express exactly
/// one loop region: the entire sound. Music with an intro, ambience with a
/// pickup bar, an engine sample with a spin-up, every one of them needs a
/// region strictly inside the asset, and the flag cannot say it. The engine
/// therefore never sets <c>AL_LOOPING</c>; a loop is buffer-queue arithmetic
/// (see <see cref="AudioLoopCursor"/>) and this is the region that arithmetic
/// reads. Getting that wrong is invisible until somebody authors a sound with
/// an intro, which is precisely why it is decided once, here, and not per call
/// site.</para>
/// <para><b>Frames, not bytes and not seconds.</b> Bytes change the moment the
/// channel count or the sample width does, and a loop point expressed in
/// seconds cannot be sample-accurate at all: the one-sample gap it leaves in a
/// sustained ambience loop is a click, once a second, forever.</para>
/// <para>An empty region is refused rather than tolerated. A loop with
/// <c>End == Start</c> reads zero frames and asks for zero frames again, which
/// is a hang inside the fill loop rather than a silent sound, and there is no
/// sane reading of it to fall back to.</para>
/// </remarks>
public readonly struct LoopRegion : IEquatable<LoopRegion>
{
    /// <summary>No loop: the sound plays once and finishes.</summary>
    public static LoopRegion None => default;

    /// <param name="startFrame">First frame of the region. Zero is legal and ordinary.</param>
    /// <param name="endFrame">One past the last frame of the region.</param>
    public LoopRegion(long startFrame, long endFrame)
    {
        if (startFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(startFrame), startFrame, "A loop cannot start before the sound.");
        if (endFrame <= startFrame)
            throw new ArgumentOutOfRangeException(nameof(endFrame), endFrame, "A loop region must contain at least one frame.");

        StartFrame = startFrame;
        EndFrame = endFrame;
    }

    /// <summary>First frame of the repeated region.</summary>
    public long StartFrame { get; }

    /// <summary>One past the last frame of the repeated region; 0 means no loop.</summary>
    public long EndFrame { get; }

    /// <summary>False for <see cref="None"/>, true for every constructed region.</summary>
    public bool IsLooping => EndFrame > StartFrame;

    /// <summary>Frames the region repeats; 0 when there is no loop.</summary>
    public long LengthFrames => IsLooping ? EndFrame - StartFrame : 0;

    public bool Equals(LoopRegion other) => StartFrame == other.StartFrame && EndFrame == other.EndFrame;

    public override bool Equals(object? obj) => obj is LoopRegion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(StartFrame, EndFrame);

    public override string ToString() => IsLooping ? $"[{StartFrame}, {EndFrame}) frames" : "no loop";

    public static bool operator ==(LoopRegion left, LoopRegion right) => left.Equals(right);

    public static bool operator !=(LoopRegion left, LoopRegion right) => !left.Equals(right);
}
