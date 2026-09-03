using SpectraEngine.Core.Graphics;
using System;
using Xunit;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The comparison arithmetic behind <c>--viewport-compare</c>, with no device
/// anywhere near it.
/// </summary>
/// <remarks>
/// <b>Separate from the driver-backed half deliberately</b>, the same split
/// <c>TextureOrientationProbe</c> already uses: the numbers are what decide
/// whether a run is reported as PASS or FAIL, and they must be provable without
/// a GPU, without a composited surface and without a keyed mutex to take turns
/// on. What the driver tests then add is that the two pictures being compared
/// are really the two pictures they are supposed to be.
/// </remarks>
public sealed class ViewportCompareTests
{
    /// <summary>Linear 0.5 stored through an sRGB view, which is 188 of 255.</summary>
    private const byte EncodedOnce = 188;

    /// <summary>The same value encoded a SECOND time, which is what this gate exists to catch.</summary>
    private const byte EncodedTwice = 223;

    [Fact]
    public void Two_identical_pictures_report_no_difference_at_all()
    {
        byte[] picture = [10, 20, 30, 255, 40, 50, 60, 255];

        ViewportCompare.Reading reading = ViewportCompare.Compare(picture, picture);

        reading.MaxDelta.ShouldBe(0);
        reading.PixelCount.ShouldBe(2);
        reading.Passes.ShouldBeTrue();
    }

    [Fact]
    public void A_difference_within_the_threshold_still_passes()
    {
        // The slack is for a driver that rounds a format conversion by a level,
        // not for anything the engine is expected to do: a correct pair is
        // bit-identical.
        byte[] reference = [100, 100, 100, 255];
        byte[] shared = [102, 99, 100, 255];

        ViewportCompare.Reading reading = ViewportCompare.Compare(reference, shared);

        reading.MaxDelta.ShouldBe(ViewportCompare.Threshold);
        reading.Passes.ShouldBeTrue();
    }

    [Fact]
    public void One_level_past_the_threshold_fails()
    {
        byte[] reference = [100, 100, 100, 255];
        byte[] shared = [100, 100, 103, 255];

        ViewportCompare.Reading reading = ViewportCompare.Compare(reference, shared);

        reading.MaxDelta.ShouldBe(ViewportCompare.Threshold + 1);
        reading.Passes.ShouldBeFalse();
        reading.WorstChannel.ShouldBe(ViewportCompare.Channel.Blue);
    }

    [Fact]
    public void A_double_srgb_encode_is_reported_as_a_large_delta_and_a_failure()
    {
        // The failure the whole probe exists for, in numbers: an sRGB view over
        // a value that was already encoded stores 223 where 188 was meant. The
        // point of asserting the magnitude is that the threshold has a factor of
        // seventeen of headroom over it, so no plausible tightening or loosening
        // of the tolerance can make this pass or a correct pair fail.
        byte[] reference = [EncodedOnce, EncodedOnce, EncodedOnce, 255];
        byte[] shared = [EncodedTwice, EncodedTwice, EncodedTwice, 255];

        ViewportCompare.Reading reading = ViewportCompare.Compare(reference, shared);

        reading.MaxDelta.ShouldBe(EncodedTwice - EncodedOnce);
        reading.MaxDelta.ShouldBeGreaterThan(ViewportCompare.Threshold * 10);
        reading.Passes.ShouldBeFalse();
        reading.Verdict.ShouldStartWith("FAIL");
    }

    [Fact]
    public void The_worst_channel_and_texel_are_the_FIRST_ones_that_reach_the_maximum()
    {
        // Stable coordinates matter more than they look: two runs of one defect
        // must name the same pixel, or a report cannot be compared with the one
        // before it.
        byte[] reference = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] shared = [0, 0, 0, 0, 0, 40, 0, 0, 0, 0, 40, 0];

        ViewportCompare.Reading reading = ViewportCompare.Compare(reference, shared);

        reading.MaxDelta.ShouldBe(40);
        reading.WorstPixel.ShouldBe(1);
        reading.WorstChannel.ShouldBe(ViewportCompare.Channel.Green);
        reading.Reference.ShouldBe((byte)0);
        reading.Shared.ShouldBe((byte)40);
    }

    [Fact]
    public void Comparing_pictures_of_different_sizes_is_refused_rather_than_truncated()
    {
        // Silently comparing the overlap would report a PASS for two pictures
        // that are not of the same thing, which is the one answer this must
        // never give.
        byte[] four = [1, 2, 3, 4];
        byte[] eight = [1, 2, 3, 4, 5, 6, 7, 8];

        Should.Throw<ArgumentException>(() => ViewportCompare.Compare(four, eight));
    }

    [Fact]
    public void A_span_that_is_not_whole_texels_is_refused()
    {
        byte[] ragged = [1, 2, 3];

        Should.Throw<ArgumentException>(() => ViewportCompare.Compare(ragged, ragged));
    }

    [Fact]
    public void A_flat_picture_has_no_variation_and_a_drawn_one_does()
    {
        // The net under the comparison: two blank pictures agree perfectly, so
        // a frame that drew nothing would report the strongest possible PASS.
        byte[] flat = [7, 8, 9, 255, 7, 8, 9, 255, 7, 8, 9, 255];
        byte[] drawn = [7, 8, 9, 255, 7, 8, 10, 255, 7, 8, 9, 255];

        ViewportCompare.HasVariation(flat).ShouldBeFalse();
        ViewportCompare.HasVariation(drawn).ShouldBeTrue();

        // One texel cannot vary from anything, so it is not evidence either.
        ViewportCompare.HasVariation([7, 8, 9, 255]).ShouldBeFalse();
    }
}
