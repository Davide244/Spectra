using SpectraEngine.Core.Audio;

namespace Spectra.Kitchen.Audio;

/// <summary>
/// What a source audio file decoded to: interleaved PCM16 at the file's own
/// rate, plus whatever loop the file declared.
/// </summary>
/// <remarks>
/// <para><b>PCM16 at the SOURCE rate, deliberately in two steps.</b> Widening
/// every bit depth to one representation first and resampling second keeps the
/// resampler from having to know about 24-bit packing or float clamping, and
/// keeps the decoder from having to know about rates at all. The alternative -
/// one pass that converts and resamples together - is where a 24-bit file ends
/// up resampled as if it were 32-bit, which produces a sound rather than a
/// failure.</para>
/// <para><b>The loop is the file's, in SAMPLE FRAMES, and it is not validated
/// here.</b> A WAV's <c>smpl</c> chunk can name a loop past the end of its own
/// data; the rule reports that and drops the loop, because a decoder that
/// silently repaired it would leave a cook log saying the sound was fine.</para>
/// </remarks>
/// <param name="SampleRate">Frames a second, as the file states it.</param>
/// <param name="Channels">1 or 2.</param>
/// <param name="Samples">Interleaved PCM16, <c>FrameCount * Channels</c> long.</param>
/// <param name="Loop">The declared loop region, or <see cref="LoopRegion.None"/>.</param>
/// <param name="LoopWasRefused">
/// True when the file declared a loop this engine cannot carry and the decoder
/// dropped it, so the rule can say so rather than the sound quietly playing once.
/// </param>
public readonly record struct DecodedAudio(
    int SampleRate,
    int Channels,
    short[] Samples,
    LoopRegion Loop,
    bool LoopWasRefused)
{
    /// <summary>Decoded length in sample frames.</summary>
    public long FrameCount => Samples.Length / Channels;

    /// <summary>Seconds of audio.</summary>
    public double Duration => (double)FrameCount / SampleRate;
}
