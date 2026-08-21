using SpectraEngine.Core.Physics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The accumulator that lets <see cref="IScenePhysics.Step"/> promise a fixed
/// step and mean it. Handing a backend the frame delta would make the
/// simulation a function of frame rate — the same input producing a different
/// world on a faster machine — which is exactly what determinism, replay and
/// server reconciliation all rest on not being true.
/// </summary>
public sealed class FixedTickAccumulatorTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void A_frame_shorter_than_a_tick_runs_nothing_and_banks_the_time()
    {
        var acc = new FixedTickAccumulator(Dt, 5);

        acc.Advance(Dt * 0.4).ShouldBe(0);
        acc.Advance(Dt * 0.4).ShouldBe(0);
        acc.Advance(Dt * 0.4).ShouldBe(1, "the banked fractions add up to a whole tick");
        acc.TotalTicks.ShouldBe(1);
    }

    [Fact]
    public void A_long_frame_runs_the_ticks_it_owes()
    {
        var acc = new FixedTickAccumulator(Dt, 5);

        acc.Advance(Dt * 3.0).ShouldBe(3);
        acc.TotalTicks.ShouldBe(3);
        acc.DroppedTicks.ShouldBe(0);
    }

    [Fact]
    public void Simulated_time_tracks_wall_clock_over_many_uneven_frames()
    {
        // The property that actually matters: however the frames are chopped
        // up, the number of ticks run matches the elapsed time. A step that
        // varied with frame time would fail this by construction.
        var acc = new FixedTickAccumulator(Dt, 1000);
        double[] frames = [0.004, 0.021, 0.016, 0.0009, 0.033, 0.0161, 0.0159, 0.008, 0.05];

        double total = 0;
        foreach (double frame in frames)
        {
            acc.Advance(frame);
            total += frame;
        }

        long expected = (long)(total / Dt);
        acc.TotalTicks.ShouldBe(expected);
    }

    [Fact]
    public void The_cap_stops_the_spiral_and_says_it_dropped_time()
    {
        // A frame that owes more ticks than the cap must not bank the debt:
        // carrying it makes the next frame longer still, which is the
        // unrecoverable slide the cap exists to prevent. Losing simulated time
        // is the lesser evil — but it must be countable, not silent.
        var acc = new FixedTickAccumulator(Dt, 5);

        acc.Advance(Dt * 20.0).ShouldBe(5);

        acc.DroppedTicks.ShouldBe(15);
        acc.Advance(Dt * 1.0).ShouldBe(1, "the debt was discarded, not carried");
    }

    [Fact]
    public void Alpha_reports_the_fraction_of_a_tick_left_over()
    {
        var acc = new FixedTickAccumulator(Dt, 5);

        acc.Advance(Dt * 1.5);

        acc.Alpha.ShouldBe(0.5f, 1e-4f);
    }

    [Fact]
    public void Alpha_stays_inside_the_unit_interval()
    {
        var acc = new FixedTickAccumulator(Dt, 5);

        for (int i = 0; i < 200; i++)
        {
            acc.Advance(Dt * 0.37);
            acc.Alpha.ShouldBeGreaterThanOrEqualTo(0f);
            acc.Alpha.ShouldBeLessThan(1f);
        }
    }

    [Fact]
    public void A_bad_timer_reading_contributes_nothing_instead_of_throwing()
    {
        // This runs every frame on the render thread. A debugger pause, a clock
        // step or a first-frame zero must not be able to take the process down
        // or bank an enormous debt.
        var acc = new FixedTickAccumulator(Dt, 5);

        acc.Advance(0d).ShouldBe(0);
        acc.Advance(-1d).ShouldBe(0);
        acc.Advance(double.NaN).ShouldBe(0);
        acc.Advance(double.PositiveInfinity).ShouldBe(0);

        acc.TotalTicks.ShouldBe(0);
        acc.Advance(Dt).ShouldBe(1, "the accumulator is still usable afterwards");
    }

    [Fact]
    public void Reset_discards_the_partial_tick()
    {
        var acc = new FixedTickAccumulator(Dt, 5);
        acc.Advance(Dt * 0.9);

        acc.Reset();

        acc.Advance(Dt * 0.5).ShouldBe(0, "the banked 0.9 of a tick is gone");
    }

    [Fact]
    public void The_phase_does_not_drift_over_a_long_session()
    {
        // The residual is carried for the process's life, which is why it is
        // accumulated in double: a float would let rounding walk the phase.
        var acc = new FixedTickAccumulator(Dt, 1000);
        const int frames = 200_000;

        for (int i = 0; i < frames; i++)
            acc.Advance(Dt);

        acc.TotalTicks.ShouldBe(frames);
        acc.DroppedTicks.ShouldBe(0);
    }
}
