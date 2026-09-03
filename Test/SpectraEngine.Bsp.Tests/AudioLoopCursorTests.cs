using SpectraEngine.Core.Audio;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The loop-point arithmetic, which is the whole reason the engine feeds a
/// buffer queue instead of setting <c>AL_LOOPING</c>.
/// </summary>
/// <remarks>
/// <para>OpenAL's looping flag repeats an entire buffer, so it can express
/// exactly one loop region: the whole sound. Music with an intro, ambience with
/// a pickup bar and an engine sample with a spin-up all need a region strictly
/// inside the asset, and the failure mode of getting this wrong is invisible
/// until somebody authors the first such sound, at which point every looping
/// asset in the project is wrong at once. So the arithmetic gets the most
/// tests, and they are all pure: no device, no driver, no sound card.</para>
/// <para>Everything below is in SAMPLE FRAMES. Bytes change with the channel
/// count and the sample width, seconds cannot be sample-accurate, and both are
/// exactly the conversions that drift.</para>
/// </remarks>
public sealed class AudioLoopCursorTests
{
    private const int Runs = 16;

    [Fact]
    public void A_sound_with_no_loop_reads_straight_through_and_then_is_exhausted()
    {
        var cursor = new AudioLoopCursor(1000, LoopRegion.None);
        Span<AudioSegment> runs = stackalloc AudioSegment[Runs];

        int count = cursor.Plan(runs, 400, out long planned);
        count.ShouldBe(1);
        runs[0].ShouldBe(new AudioSegment(0, 400));
        planned.ShouldBe(400);
        cursor.IsExhausted.ShouldBeFalse();

        // The tail is short, and a short fill is the correct answer rather than
        // a padded one: padding is a click at the end of every sound.
        cursor.Plan(runs, 400, out _);
        count = cursor.Plan(runs, 400, out planned);
        count.ShouldBe(1);
        runs[0].ShouldBe(new AudioSegment(800, 200));
        planned.ShouldBe(200);
        cursor.IsExhausted.ShouldBeTrue();

        cursor.Plan(runs, 400, out planned).ShouldBe(0);
        planned.ShouldBe(0);
    }

    [Fact]
    public void An_intro_and_the_first_pass_through_the_loop_are_one_run()
    {
        // The case AL_LOOPING cannot express at all: a region strictly inside
        // the sound, with audio before it and after it.
        var cursor = new AudioLoopCursor(1000, new LoopRegion(200, 600));
        Span<AudioSegment> runs = stackalloc AudioSegment[Runs];

        // Frames 0..600 are contiguous in the source, so treating the intro as
        // its own phase would split this into two runs and put a buffer
        // boundary, and therefore a possible click, at frame 200 for nothing.
        int count = cursor.Plan(runs, 1000, out long planned);
        count.ShouldBe(2);
        runs[0].ShouldBe(new AudioSegment(0, 600));
        runs[1].ShouldBe(new AudioSegment(200, 400));
        planned.ShouldBe(1000);

        // And the intro is never heard again.
        cursor.Position.ShouldBe(200);
        cursor.IsExhausted.ShouldBeFalse();
    }

    [Fact]
    public void A_loop_starting_at_zero_wraps_to_zero_and_never_exhausts()
    {
        var cursor = new AudioLoopCursor(500, new LoopRegion(0, 500));
        Span<AudioSegment> runs = stackalloc AudioSegment[Runs];

        int count = cursor.Plan(runs, 1200, out long planned);
        count.ShouldBe(3);
        runs[0].ShouldBe(new AudioSegment(0, 500));
        runs[1].ShouldBe(new AudioSegment(0, 500));
        runs[2].ShouldBe(new AudioSegment(0, 200));
        planned.ShouldBe(1200);

        // A loop covering the whole sound is the one case AL_LOOPING would also
        // have handled, and it must not be the case that stops looping.
        cursor.IsExhausted.ShouldBeFalse();
        cursor.Position.ShouldBe(200);
    }

    [Fact]
    public void A_loop_region_shorter_than_one_buffer_repeats_inside_it()
    {
        // 150 frames into a 1024-frame fill: six whole repetitions and part of
        // a seventh. A buffer is not a loop iteration, and this is the case
        // that proves the two lengths are independent.
        var cursor = new AudioLoopCursor(400, new LoopRegion(100, 250));
        Span<AudioSegment> runs = stackalloc AudioSegment[Runs];

        int count = cursor.Plan(runs, 1024, out long planned);

        // First run reaches the loop end from 0; the rest are whole or partial
        // repetitions of [100, 250).
        runs[0].ShouldBe(new AudioSegment(0, 250));
        for (int i = 1; i < count; i++)
            runs[i].Offset.ShouldBe(100);

        long total = 0;
        for (int i = 0; i < count; i++) total += runs[i].Count;
        total.ShouldBe(planned);
        planned.ShouldBe(1024);

        // Every frame after the first run comes from inside the region.
        for (int i = 1; i < count; i++)
            (runs[i].Offset + runs[i].Count).ShouldBeLessThanOrEqualTo(250);
    }

    [Fact]
    public void A_loop_shorter_than_the_run_budget_shortens_the_fill_instead_of_running_away()
    {
        // Ten frames per repetition against a 4096-frame request is 410 runs.
        // The scratch is what bounds it: planning stops when the span is full
        // and reports the shorter fill, which is the answer that keeps a
        // pathological loop from asking for unbounded scratch.
        var cursor = new AudioLoopCursor(100, new LoopRegion(0, 10));
        Span<AudioSegment> runs = stackalloc AudioSegment[4];

        int count = cursor.Plan(runs, 4096, out long planned);

        count.ShouldBe(4);
        planned.ShouldBe(40);
        cursor.IsExhausted.ShouldBeFalse();
    }

    [Fact]
    public void A_loop_length_that_is_not_a_multiple_of_the_buffer_crosses_the_wrap_mid_fill()
    {
        // 700-frame loop against 512-frame buffers: the wrap lands inside a
        // fill on every pass but the first, and at a different offset each
        // time. Assembling the sequence and checking it is continuous in
        // playback order is what catches an off-by-one at the wrap, which is
        // audible as a click once per loop and nowhere else.
        const int BufferFrames = 512;
        var loop = new LoopRegion(300, 1000);
        var cursor = new AudioLoopCursor(1500, loop);
        Span<AudioSegment> runs = stackalloc AudioSegment[Runs];

        long expected = 0;
        for (int fill = 0; fill < 12; fill++)
        {
            int count = cursor.Plan(runs, BufferFrames, out long planned);
            planned.ShouldBe(BufferFrames, $"fill {fill} came up short");

            for (int i = 0; i < count; i++)
            {
                // Each run must begin exactly where the previous one left off,
                // in playback order: either the next frame, or the loop start
                // immediately after the loop end.
                if (expected == loop.EndFrame) expected = loop.StartFrame;
                runs[i].Offset.ShouldBe(expected, $"fill {fill}, run {i} is discontinuous");

                expected = runs[i].Offset + runs[i].Count;
                expected.ShouldBeLessThanOrEqualTo(loop.EndFrame);
            }
        }

        // Twelve 512-frame fills is 6144 frames from a 700-frame loop: the
        // sound played its 300-frame intro and then wrapped eight times, and
        // never once ran off the end of a 1500-frame asset.
        cursor.IsExhausted.ShouldBeFalse();
    }

    [Fact]
    public void A_seek_into_the_middle_of_a_loop_keeps_looping_from_where_it_landed()
    {
        var cursor = new AudioLoopCursor(1000, new LoopRegion(200, 600));
        Span<AudioSegment> runs = stackalloc AudioSegment[Runs];

        cursor.Seek(450);
        cursor.Position.ShouldBe(450);

        int count = cursor.Plan(runs, 500, out long planned);
        count.ShouldBe(2);

        // The remainder of this pass, then back to the loop start, never to the
        // intro: a seek into a loop is a position inside the region and not a
        // restart of the sound.
        runs[0].ShouldBe(new AudioSegment(450, 150));
        runs[1].ShouldBe(new AudioSegment(200, 350));
        planned.ShouldBe(500);
        cursor.IsExhausted.ShouldBeFalse();
    }

    [Fact]
    public void A_seek_past_the_loop_plays_the_tail_and_finishes()
    {
        // Somebody who scrubbed past the loop asked to hear the outro. Wrapping
        // them back into the body would make the end of a looping track
        // unreachable, and a wrap condition written against the position alone
        // rather than against what bounded the run does exactly that.
        var cursor = new AudioLoopCursor(1000, new LoopRegion(200, 600));
        Span<AudioSegment> runs = stackalloc AudioSegment[Runs];

        cursor.Seek(800);
        int count = cursor.Plan(runs, 500, out long planned);

        count.ShouldBe(1);
        runs[0].ShouldBe(new AudioSegment(800, 200));
        planned.ShouldBe(200);
        cursor.IsExhausted.ShouldBeTrue();
    }

    [Fact]
    public void A_seek_before_the_loop_replays_the_intro()
    {
        var cursor = new AudioLoopCursor(1000, new LoopRegion(200, 600));
        Span<AudioSegment> runs = stackalloc AudioSegment[Runs];

        cursor.Plan(runs, 700, out _);
        cursor.Seek(50);

        int count = cursor.Plan(runs, 300, out long planned);
        count.ShouldBe(1);
        runs[0].ShouldBe(new AudioSegment(50, 300));
        planned.ShouldBe(300);
    }

    [Fact]
    public void A_seek_to_the_end_finishes_and_a_rewind_undoes_it()
    {
        var cursor = new AudioLoopCursor(1000, LoopRegion.None);
        Span<AudioSegment> runs = stackalloc AudioSegment[Runs];

        // Clamped rather than refused: scrubbing to "the end" is a real gesture.
        cursor.Seek(5000);
        cursor.Position.ShouldBe(1000);
        cursor.IsExhausted.ShouldBeTrue();

        cursor.Rewind();
        cursor.IsExhausted.ShouldBeFalse();
        cursor.Plan(runs, 10, out long planned).ShouldBe(1);
        planned.ShouldBe(10);
    }

    [Fact]
    public void A_negative_seek_throws_rather_than_clamping_to_zero()
    {
        var cursor = new AudioLoopCursor(1000, LoopRegion.None);

        // Clamping would hide a sign error in whatever computed the frame, and
        // the symptom would be a sound that restarts instead of seeking.
        Should.Throw<ArgumentOutOfRangeException>(() => cursor.Seek(-1));
    }

    [Fact]
    public void An_empty_loop_region_is_refused_because_it_is_a_hang()
    {
        // Reading zero frames and asking for zero frames again is an infinite
        // fill loop, not a silent sound, and there is no sane reading to fall
        // back to.
        Should.Throw<ArgumentOutOfRangeException>(() => new LoopRegion(400, 400));
        Should.Throw<ArgumentOutOfRangeException>(() => new LoopRegion(400, 100));
    }

    [Fact]
    public void A_loop_ending_past_the_sound_is_refused_at_the_cursor()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new AudioLoopCursor(500, new LoopRegion(100, 900)));
    }

    [Fact]
    public void Frame_conversions_live_in_one_place()
    {
        // The whole reason positions are frames: the two conversions that drift
        // are the two that need the format, and only the format knows them.
        var stereo = new AudioFormat(48000, 2);
        stereo.FramesToSamples(100).ShouldBe(200);
        stereo.SamplesToFrames(200).ShouldBe(100);
        stereo.FramesToSeconds(48000).ShouldBe(1.0);
        stereo.SecondsToFrames(0.5).ShouldBe(24000);

        var mono = new AudioFormat(48000, 1);
        mono.FramesToSamples(100).ShouldBe(100);

        // Same frame count, same seconds, different byte counts: which is why
        // a loop point stored in bytes breaks the moment the channel count does.
        mono.FramesToSeconds(48000).ShouldBe(stereo.FramesToSeconds(48000));
        mono.FramesToSamples(48000).ShouldNotBe(stereo.FramesToSamples(48000));

        Should.Throw<ArgumentOutOfRangeException>(() => new AudioFormat(0, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => new AudioFormat(48000, 6));
    }
}
