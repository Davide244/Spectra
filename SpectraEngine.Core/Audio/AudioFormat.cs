using System;

namespace SpectraEngine.Core.Audio;

/// <summary>
/// The shape of a block of PCM: how many samples a second, and how many
/// channels are interleaved into each sample frame.
/// </summary>
/// <remarks>
/// <para>A <i>sample frame</i> is one sample per channel. Everything in this
/// namespace that measures a position or a length measures it in frames, never
/// in bytes and never in seconds: bytes depend on the channel count and the
/// sample width, seconds depend on the rate, and both are exactly the
/// conversions that drift when a codec or a rate changes. The one place the
/// conversion is allowed is here, where the numbers that decide it are in
/// hand.</para>
/// <para>Only mono and stereo exist, because that is all OpenAL's PCM16 buffer
/// formats can carry. A stereo buffer also plays <i>unpositioned</i> in
/// OpenAL, which is the classic "why is my 3D sound not 3D" report; the
/// positional path therefore wants mono, and the cook step that will produce
/// these (see <c>docs/formats-and-pipeline.md</c> section 2.4) warns about it
/// rather than silently flattening a picture that still plays.</para>
/// </remarks>
public readonly struct AudioFormat : IEquatable<AudioFormat>
{
    /// <summary>Bytes one sample of one channel occupies. PCM16 only, today.</summary>
    public const int BytesPerSample = sizeof(short);

    /// <param name="sampleRate">Frames per second. Must be positive.</param>
    /// <param name="channels">1 (mono) or 2 (stereo).</param>
    public AudioFormat(int sampleRate, int channels)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Only mono and stereo PCM16 exist.");

        SampleRate = sampleRate;
        Channels = channels;
    }

    /// <summary>Sample frames per second.</summary>
    public int SampleRate { get; }

    /// <summary>Interleaved channels per sample frame: 1 or 2.</summary>
    public int Channels { get; }

    /// <summary>True for a format this struct could not have been built without.</summary>
    public bool IsValid => SampleRate > 0 && Channels > 0;

    /// <summary>Interleaved samples in <paramref name="frames"/> sample frames.</summary>
    public long FramesToSamples(long frames) => frames * Channels;

    /// <summary>Whole sample frames in <paramref name="samples"/> interleaved samples.</summary>
    public long SamplesToFrames(long samples) => samples / Channels;

    /// <summary>Seconds of audio in <paramref name="frames"/> sample frames.</summary>
    public double FramesToSeconds(long frames) => (double)frames / SampleRate;

    /// <summary>Whole sample frames in <paramref name="seconds"/> of audio.</summary>
    public long SecondsToFrames(double seconds) => (long)(seconds * SampleRate);

    public bool Equals(AudioFormat other) => SampleRate == other.SampleRate && Channels == other.Channels;

    public override bool Equals(object? obj) => obj is AudioFormat other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(SampleRate, Channels);

    public override string ToString() => $"{SampleRate} Hz, {(Channels == 1 ? "mono" : "stereo")}";

    public static bool operator ==(AudioFormat left, AudioFormat right) => left.Equals(right);

    public static bool operator !=(AudioFormat left, AudioFormat right) => !left.Equals(right);
}
