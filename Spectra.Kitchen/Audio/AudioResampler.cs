using System;

namespace Spectra.Kitchen.Audio;

/// <summary>
/// Converts interleaved PCM16 from one sample rate to another, at cook time and
/// nowhere else.
/// </summary>
/// <remarks>
/// <para><b>The cooker resamples and the runtime never does.</b> A mixer handed
/// sounds at three rates either resamples per source in the frame loop or hands
/// the driver the job, and OpenAL will do it at a quality and a cost nobody
/// chose. That is a cost paid forever for a mistake made once, so it is paid
/// once, here, where there is time to do it properly and where the answer is
/// cached by content. A file arriving at the wrong rate at runtime is logged and
/// played, not fixed.</para>
/// <para><b>Frame counts convert with INTEGER arithmetic, never through
/// seconds.</b> <see cref="ConvertFrames"/> is the one conversion, and both the
/// resampled length and the loop points go through it - which is the whole
/// reason it is a named function rather than an expression at two call sites. A
/// loop point that went via a double count of seconds and back lands a frame or
/// two off, and a frame or two off at a loop boundary is a click once a bar,
/// forever, in an asset that measures correct in every other way.</para>
/// <para><b>A windowed sinc rather than linear interpolation.</b> Linear is two
/// multiplies and sounds like a low-pass filter that changes with pitch: it
/// images badly on the way up and aliases on the way down, and 44.1 to 48 is
/// exactly the conversion a project's library will be full of. The kernel is
/// evaluated per output frame and shared across channels, so a stereo file costs
/// one kernel rather than two.</para>
/// <para><b>Outside the signal is ZERO, and the weights are still summed over
/// the whole kernel.</b> Clamping to the edge sample would extend a DC level
/// past the end of the sound, which is a step the next stage renders as a click;
/// dividing by only the in-range weights would hold the edges at full amplitude,
/// which is the same click by a different route. Zero outside plus a full-weight
/// normalisation is the band-limited answer, and it fades the first and last
/// half-kernel exactly as reconstruction says it should.</para>
/// </remarks>
public static class AudioResampler
{
    /// <summary>
    /// Taps either side of the centre. Thirty-two taps in total is well past
    /// audible transparency for a rate conversion and cheap enough that a cook
    /// of a whole sound library is seconds rather than minutes.
    /// </summary>
    public const int HalfTaps = 16;

    /// <summary>
    /// <paramref name="frames"/> at <paramref name="fromRate"/>, expressed at
    /// <paramref name="toRate"/>, rounded to nearest.
    /// </summary>
    /// <remarks>
    /// <b>Integer arithmetic on purpose.</b> The obvious spelling is
    /// <c>(long)(frames * (double)toRate / fromRate)</c>, and it is wrong in a
    /// way nothing catches: it truncates, so a loop point drifts one frame
    /// earlier at every rate that does not divide evenly, and the drift depends
    /// on the value rather than being a constant somebody could notice. This
    /// rounds, exactly, with no floating point anywhere in it.
    /// </remarks>
    public static long ConvertFrames(long frames, int fromRate, int toRate)
    {
        if (fromRate <= 0) throw new ArgumentOutOfRangeException(nameof(fromRate), fromRate, "A rate is positive.");
        if (toRate <= 0) throw new ArgumentOutOfRangeException(nameof(toRate), toRate, "A rate is positive.");
        if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames), frames, "A frame count is not negative.");

        return (frames * toRate + fromRate / 2) / fromRate;
    }

    /// <summary>
    /// <paramref name="samples"/> converted from <paramref name="fromRate"/> to
    /// <paramref name="toRate"/>, interleaved the same way.
    /// </summary>
    /// <remarks>
    /// Returns the input array itself when the rates already match, which is the
    /// common case in a project whose library was exported at the project rate:
    /// there is nothing to do and nothing is copied.
    /// </remarks>
    public static short[] Resample(short[] samples, int channels, int fromRate, int toRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels), channels, "A frame has channels.");

        if (fromRate == toRate) return samples;

        long inFrames = samples.Length / channels;
        long outFrames = ConvertFrames(inFrames, fromRate, toRate);
        if (outFrames <= 0) return [];

        // The cutoff is relative to the SOURCE Nyquist: downsampling has to
        // band-limit to the destination's Nyquist first or everything above it
        // folds back as aliasing, while upsampling must not filter at all or the
        // sound is dulled for nothing.
        double cutoff = Math.Min(1.0, (double)toRate / fromRate);

        var output = new short[checked((int)(outFrames * channels))];
        Span<double> kernel = stackalloc double[HalfTaps * 2];

        for (long frame = 0; frame < outFrames; frame++)
        {
            double centre = (double)frame * fromRate / toRate;
            long first = (long)Math.Floor(centre) - HalfTaps + 1;

            double weightSum = 0;
            for (int tap = 0; tap < kernel.Length; tap++)
            {
                double offset = (first + tap) - centre;
                double weight = Sinc(offset * cutoff) * Window(offset);
                kernel[tap] = weight;
                weightSum += weight;
            }

            // A kernel whose weights cancel is not something the window above can
            // produce, and dividing by it would be a NaN written into every
            // channel of the frame - silence at best, a driver-level pop at worst.
            if (weightSum == 0) weightSum = 1;

            for (int channel = 0; channel < channels; channel++)
            {
                double accumulated = 0;
                for (int tap = 0; tap < kernel.Length; tap++)
                {
                    long source = first + tap;
                    if (source < 0 || source >= inFrames) continue;

                    accumulated += kernel[tap] * samples[source * channels + channel];
                }

                output[frame * channels + channel] = Saturate(accumulated / weightSum);
            }
        }

        return output;
    }

    // KNOWN DETERMINISM EXPOSURE, and the one place in the cook that has one.
    //
    // Math.Sin and Math.Cos are not guaranteed bit-identical across platforms or
    // runtime versions: .NET defers to the platform's own libm, so the CRT on
    // Windows and glibc on Linux may disagree in the last ulp. Everything else
    // the cook does is integer work, IEEE basic operations, or a hash, all of
    // which are exactly reproducible; this kernel is not.
    //
    // The three determinism oracles cannot see it. They compare two cooks in two
    // processes on ONE machine, where libm agrees with itself, and that is also
    // true of the cache-versus-clean and the -j1-versus-jN pair. So the property
    // that actually holds today is per-host reproducibility, and cross-host byte
    // identity of cooked AUDIO is unproven rather than established. Every other
    // cooked artifact keeps the stronger claim.
    //
    // Left as is deliberately: the audible difference is nil (a fraction of one
    // LSB of a 16-bit sample), InstructionSetBaseline cannot help because the
    // divergence is in the library rather than in the instruction set, and the
    // fix is a deterministic sine of our own, which is real work to write and to
    // prove. Written down here because the alternative is somebody discovering a
    // one-byte pack difference between two CI hosts and looking everywhere else
    // first.
    //
    // Blackman over the whole kernel span. It is not the sharpest window
    // available, and that is the point: a sharper one rings, and ringing on a
    // transient is a pre-echo that a listener hears as the sound arriving twice.
    private static double Window(double offset)
    {
        double position = (offset + HalfTaps) / (HalfTaps * 2);
        if (position is < 0 or > 1) return 0;

        return 0.42
            - 0.5 * Math.Cos(2 * Math.PI * position)
            + 0.08 * Math.Cos(4 * Math.PI * position);
    }

    private static double Sinc(double x)
    {
        // Exactly zero rather than near it: sin(0)/0 is a NaN, and the limit is 1.
        if (x == 0) return 1;

        double scaled = Math.PI * x;
        return Math.Sin(scaled) / scaled;
    }

    // Clamped rather than cast. A resampled peak legitimately overshoots the
    // source's own maximum - that is what reconstruction between two samples
    // does - and an unclamped cast wraps full scale to the opposite polarity,
    // which is a bang rather than the clip nobody would have heard.
    private static short Saturate(double value)
    {
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded >= short.MaxValue) return short.MaxValue;
        if (rounded <= short.MinValue) return short.MinValue;
        return (short)rounded;
    }
}
