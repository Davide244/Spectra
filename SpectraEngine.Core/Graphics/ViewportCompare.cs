using System;
using System.Globalization;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The arithmetic and the vocabulary for the one question a composited frame
/// cannot answer about itself: does the picture that leaves through the shared
/// handle carry the same bytes as the picture that would have gone to a window.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this exists for is a DOUBLE sRGB ENCODE, and it produces no
/// error of any kind.</b> A shared present target is a UNORM resource wearing an
/// <c>_SRGB</c> render-target view, so the write encodes exactly once and an
/// importer that decodes on sample gets the picture back. If anything on that
/// route encodes a second time the frame washes out - no exception, no HRESULT,
/// nothing on the debug layer, and <c>--offscreen-probe</c> sails through
/// because it counts debug-layer errors and a wrong picture raises none. The
/// only witness is a byte.
/// </para>
/// <para>
/// <b>Pure, and deliberately separate from the probe that drives it</b>, the
/// same split <see cref="TextureOrientationProbe"/> and
/// <see cref="OffscreenProbe"/> already use: the arithmetic is testable with no
/// device, and the driving is testable only with one.
/// </para>
/// </remarks>
public static class ViewportCompare
{
    /// <summary>
    /// The largest per-channel difference that still passes, in 8-bit levels.
    /// </summary>
    /// <remarks>
    /// <b>Two, and the number is a tolerance rather than a threshold anything
    /// is expected to reach.</b> Both pictures come out of one shader reading
    /// one source into two targets of the same format in one frame, so a
    /// correct pair is bit-identical and the honest expectation is zero. What
    /// the slack buys is not being red on a driver that rounds a blend or a
    /// format conversion differently by a level. A double encode is nowhere
    /// near it: linear 0.5 stores as 188, and encoding that again reaches 223,
    /// so the failure this guards arrives at 35 levels rather than at 3.
    /// </remarks>
    public const int Threshold = 2;

    /// <summary>Which channel of a texel a difference was found in.</summary>
    public enum Channel
    {
        Red,
        Green,
        Blue,
        Alpha,
    }

    /// <summary>
    /// What comparing two readbacks found: the largest per-channel difference,
    /// and where it was.
    /// </summary>
    /// <param name="MaxDelta">Largest absolute per-channel difference, in 8-bit levels.</param>
    /// <param name="WorstChannel">The channel <paramref name="MaxDelta"/> was measured in.</param>
    /// <param name="WorstPixel">Index of the texel it was measured at, in the compared order.</param>
    /// <param name="Reference">The reference value at that texel and channel.</param>
    /// <param name="Shared">The shared value at that texel and channel.</param>
    /// <param name="PixelCount">How many texels were compared.</param>
    public readonly record struct Reading(
        int MaxDelta,
        Channel WorstChannel,
        int WorstPixel,
        byte Reference,
        byte Shared,
        int PixelCount)
    {
        /// <summary>True when the two pictures agree to within <see cref="Threshold"/>.</summary>
        public bool Passes => MaxDelta <= Threshold;

        /// <summary>The verdict in words, including what a large delta most likely means.</summary>
        public string Verdict => Passes
            ? "PASS"
            : "FAIL (the shared picture is not the picture a window would have shown)";

        /// <summary>One line naming the measurement, for a log or a failure message.</summary>
        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "max delta {0} on {1} at texel {2} (window {3}, shared {4}), over {5} texels",
            MaxDelta, WorstChannel, WorstPixel, Reference, Shared, PixelCount);
    }

    /// <summary>
    /// Whether a readback holds more than one colour.
    /// </summary>
    /// <remarks>
    /// <b>The net under the whole comparison.</b> Two blank pictures agree
    /// perfectly, so a frame that drew nothing at all - a pipeline that failed
    /// to select, a scene that never loaded, a pass bound to the wrong target -
    /// would report the strongest possible PASS while proving nothing whatever
    /// about the colour route. Cheap to ask and the difference between a gate
    /// and a gate-shaped thing.
    /// </remarks>
    public static bool HasVariation(ReadOnlySpan<byte> picture)
    {
        if (picture.Length < PixelReadback.BytesPerPixel * 2) return false;

        for (int i = PixelReadback.BytesPerPixel; i < picture.Length; i++)
        {
            if (picture[i] != picture[i % PixelReadback.BytesPerPixel]) return true;
        }

        return false;
    }

    /// <summary>
    /// Compares two 8-bit RGBA readbacks of the same picture and reports the
    /// largest per-channel difference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per CHANNEL rather than per texel</b>, because the failure being
    /// looked for is a transfer function applied twice, which moves each
    /// channel independently and by different amounts: a texel-level distance
    /// would blur a 35-level shift on one channel into an average with two
    /// channels that were already at their extremes and could not move.
    /// </para>
    /// <para>
    /// <b>The FIRST worst texel is kept, not the last</b>, so the reported
    /// coordinate is stable across runs and two runs of the same defect name
    /// the same pixel.
    /// </para>
    /// </remarks>
    /// <param name="reference">The picture an ordinary sRGB target received.</param>
    /// <param name="shared">The picture read back through the shared handle.</param>
    /// <exception cref="ArgumentException">The two spans are not the same length, or not whole texels.</exception>
    public static Reading Compare(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> shared)
    {
        if (reference.Length != shared.Length)
        {
            throw new ArgumentException(
                $"The two readbacks must describe the same picture; got {reference.Length} and {shared.Length} bytes.",
                nameof(shared));
        }

        if (reference.Length == 0 || reference.Length % PixelReadback.BytesPerPixel != 0)
        {
            throw new ArgumentException(
                $"A readback is whole 8-bit RGBA texels; {reference.Length} bytes is not.", nameof(reference));
        }

        int pixels = reference.Length / PixelReadback.BytesPerPixel;
        int maxDelta = 0;
        var worstChannel = Channel.Red;
        int worstPixel = 0;
        byte worstReference = 0;
        byte worstShared = 0;

        for (int i = 0; i < reference.Length; i++)
        {
            int delta = Math.Abs(reference[i] - shared[i]);
            if (delta <= maxDelta) continue;

            maxDelta = delta;
            worstChannel = (Channel)(i % PixelReadback.BytesPerPixel);
            worstPixel = i / PixelReadback.BytesPerPixel;
            worstReference = reference[i];
            worstShared = shared[i];
        }

        return new Reading(maxDelta, worstChannel, worstPixel, worstReference, worstShared, pixels);
    }
}
