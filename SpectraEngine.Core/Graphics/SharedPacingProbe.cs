using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Hosting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Drives the producer's half of the shared-target handshake against a REAL
/// consumer on a second device whose turn arrives at a cadence this probe sets,
/// and reports what the engine's frame rate does at each one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A composited viewport paces the engine on the keyed
/// mutex: the producer cannot start a frame until the consumer has released key
/// 0, and the consumer releases it from work scheduled on somebody else's UI
/// thread. So the engine's frame rate is a function of another process's
/// scheduler, and every instrument the engine already has is blind to that -
/// frame time INCLUDES the wait, so a stalled producer and a slow one report
/// the same number, and no debug layer has anything to say about either.
/// <see cref="Renderer.DrainSharedAcquireWait"/> separates the two; this drives
/// the thing that makes the wait happen, so the coupling can be measured
/// instead of argued about.
/// </para>
/// <para>
/// <b>Everything here is real except the cadence.</b> The frame, the resolve,
/// the keyed-mutex bracket, the acquire and its timeout are the shipping path
/// on a real device; the consumer is a real second device that opens the
/// producer's NT handle, acquires key 1, copies the whole texture and hands key
/// 0 back, which is what a compositor does. What is synthetic is only WHEN it
/// takes its turn - which is the one thing a compositor's own scheduler decides
/// and the one thing a headless run cannot otherwise vary.
/// </para>
/// <para>
/// <b>A second DEVICE, not a second thread, and that was measured.</b> A keyed
/// mutex is owned per device: a turn taken on the renderer's own device while
/// the producer holds the key is refused with <c>DXGI_ERROR_INVALID_CALL</c>
/// rather than made to wait, so the overlap being measured is the one case that
/// cannot happen there. The first version of this probe did exactly that and
/// reported that error several times a second.
/// </para>
/// <para>
/// <b>The consumer thread SPINS to its deadline.</b> Windows' default timer
/// granularity is 15.6 ms, so a <c>Sleep</c>-based 16.7 ms cadence is not a
/// cadence at all; a probe that owns the machine for a few seconds can afford a
/// busy core and cannot afford a clock that quantises every phase to the same
/// number.
/// </para>
/// <para>
/// Render thread only for <see cref="Update"/>, in the slot
/// <see cref="ViewportCompareProbe"/> occupies.
/// </para>
/// </remarks>
public sealed class SharedPacingProbe
{
    /// <summary>How long each row is measured for.</summary>
    private const double PhaseSeconds = 2.0;

    /// <summary>
    /// Frames rendered before the first phase starts, so a cold shader compile
    /// and the first static-world build are not counted as pacing.
    /// </summary>
    private const int WarmupFrames = 30;

    private enum ConsumerMode
    {
        /// <summary>Nobody takes a turn at all: a pane nobody can see.</summary>
        Stopped,

        /// <summary>A turn every <see cref="Phase.PeriodMs"/>, which is a compositor.</summary>
        Paced,

        /// <summary>Turns taken as fast as the mutex allows: the producer's own ceiling.</summary>
        Free,
    }

    /// <summary>
    /// One configuration, measured for <see cref="PhaseSeconds"/> of wall time.
    /// </summary>
    /// <param name="Name">What the row is called in the report.</param>
    /// <param name="Mode">How the consumer behaves.</param>
    /// <param name="PeriodMs">Milliseconds between turns while <see cref="ConsumerMode.Paced"/>.</param>
    /// <param name="Meaning">One line for the report, so a row explains itself.</param>
    private sealed record Phase(string Name, ConsumerMode Mode, double PeriodMs, string Meaning);

    /// <summary>
    /// The script. <c>free</c> is the producer's own ceiling and therefore the
    /// most any amount of buffering on this side could ever buy; the three
    /// paced rows walk the consumer from a display's rate down through the rate
    /// a composited editor was actually reported at; <c>hidden</c> is the
    /// timeout path, the one case where the producer is SUPPOSED to be slow.
    /// </summary>
    private static readonly Phase[] Script =
    [
        new("free", ConsumerMode.Free, 0.0,
            "turns taken as fast as the mutex allows: the producer's ceiling"),
        new("vsync-60", ConsumerMode.Paced, 1000.0 / 60.0,
            "a turn every display refresh, which is a compositor keeping up"),
        new("late-40", ConsumerMode.Paced, 25.0,
            "a turn every 25 ms, which is a compositor missing every other refresh"),
        new("slow-30", ConsumerMode.Paced, 1000.0 / 30.0,
            "a turn every other refresh"),
        new("hidden", ConsumerMode.Stopped, 0.0,
            "no turns at all: the acquire timeout, which is meant to cost this"),
    ];

    private readonly ILogger _logger;
    private readonly List<Reading> _readings = [];

    private PacedConsumer? _consumer;
    private Stopwatch? _phaseClock;
    private int _phaseIndex;
    private int _warmupFrames;
    private int _phaseFrames;
    private long _phaseHandOversAtStart;

    // The producer's wait, taken from the published snapshot rather than from
    // the renderer's own drain. See ObserveSnapshot.
    private double _waitSum;
    private int _waitWindows;
    private float _waitPeak;

    /// <summary>True until the probe has run its whole script and reported.</summary>
    public bool Running { get; private set; } = true;

    /// <summary>
    /// Whether the run produced a measurement. False means it never had a
    /// shared target or a consumer to measure with, or it threw.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a threshold on the numbers. What a machine manages at a
    /// given cadence is the thing being measured, and a probe that failed a run
    /// for being slow would be asserting the answer it was written to find out.
    /// </remarks>
    public bool Passed { get; private set; }

    public SharedPacingProbe(ILogger logger) => _logger = logger;

    /// <summary>
    /// One phase's measurement.
    /// </summary>
    /// <param name="Name">The phase's name.</param>
    /// <param name="Fps">Engine frames per second over the phase.</param>
    /// <param name="HandOversPerSecond">Turns the consumer completed.</param>
    /// <param name="AcquireAverageMs">Mean producer wait for the key.</param>
    /// <param name="AcquirePeakMs">Worst producer wait for the key.</param>
    /// <param name="Meaning">The phase's own description.</param>
    public readonly record struct Reading(
        string Name,
        double Fps,
        double HandOversPerSecond,
        float AcquireAverageMs,
        float AcquirePeakMs,
        string Meaning);

    /// <summary>What each phase measured, once the run has finished.</summary>
    public IReadOnlyList<Reading> Readings => _readings;

    /// <summary>
    /// Takes the producer's acquire wait from a published frame, which is the
    /// only place it can be read from.
    /// </summary>
    /// <remarks>
    /// <b>Reading the renderer's own drain here would report zero, and it did.</b>
    /// <see cref="Renderer.DrainSharedAcquireWait"/> RESETS what it returns, and
    /// the snapshot publisher already calls it once per published frame - so a
    /// second reader on a slower clock gets whatever happened to arrive in the
    /// gap, which for a two-second phase at thirty publishes a second is one
    /// sample or none at all. The first version of this probe reported 0.00 ms
    /// of wait beside an engine running at exactly the consumer's rate, which
    /// is the arithmetic saying it was measuring nothing.
    /// <para>
    /// So the peak is the MAX of what was published (exact - a maximum of
    /// maxima is a maximum) and the average is the mean of the published
    /// per-window averages, which weights every publish window equally rather
    /// than by its sample count. Stated rather than hidden: it is an average of
    /// averages, and it is within a per-window rounding of the true mean
    /// because every window here contains frames.
    /// </para>
    /// Render thread, like <see cref="Update"/>: <c>FrameCompleted</c> is raised
    /// there, so the two never race.
    /// </remarks>
    public void ObserveSnapshot(FrameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Running || _consumer is null) return;

        _waitSum += snapshot.SharedAcquireWaitMs;
        _waitWindows++;
        if (snapshot.SharedAcquirePeakMs > _waitPeak) _waitPeak = snapshot.SharedAcquirePeakMs;
    }

    /// <summary>
    /// Called once per frame on the render thread, before
    /// <see cref="Renderer.Render"/>.
    /// </summary>
    public void Update(Renderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (!Running) return;

        try
        {
            if (_consumer is null)
            {
                Begin(renderer);
                return;
            }

            _phaseFrames++;

            if (_phaseClock!.Elapsed.TotalSeconds < PhaseSeconds) return;

            RecordPhase();

            _phaseIndex++;
            if (_phaseIndex >= Script.Length)
            {
                Report();
                Finish(passed: true);
                return;
            }

            StartPhase();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shared pacing probe: FAIL - the measurement threw");
            Finish(passed: false);
        }
    }

    private void Begin(Renderer renderer)
    {
        // Warmed up first: the first frames of a session carry a shader compile
        // and the static-world build, and a phase that averaged those in would
        // report the load time as pacing.
        if (++_warmupFrames < WarmupFrames) return;

        if (!renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle))
        {
            _logger.LogError(
                "Shared pacing probe: FAIL - {Backend} has no shared present target, so there is no " +
                "hand-over to pace. This probe needs a composited surface.",
                renderer.Backend);
            Finish(passed: false);
            return;
        }

        // A D3D11 consumer whatever the backend, and that is not a
        // simplification: the shared texture is a D3D11 one on both, because
        // D3D12 creates it through the D3D11On12 bridge precisely so the two
        // cannot disagree about its colour space. It asks the renderer for
        // nothing beyond the handle it already publishes, which is what keeps
        // both backends' files out of this entirely.
        if (D3D11.D3D11SharedTargetConsumer.TryOpen(
                handle.NtHandle, handle.Width, handle.Height, _logger) is not { } consumer)
        {
            _logger.LogError(
                "Shared pacing probe: FAIL - the shared target would not open on a second device, so " +
                "nothing can take the consumer's turn while a frame is in flight.");
            Finish(passed: false);
            return;
        }

        _logger.LogInformation(
            "Shared pacing probe on {Backend} at {Width}x{Height} (shared generation {Generation}, " +
            "handle 0x{Handle:X}). The producer is real and so is the consumer - a second device that " +
            "opens the same handle - and only the cadence of its turn is set here. Each phase runs " +
            "{Seconds:0.0} s.",
            renderer.Backend, handle.Width, handle.Height, handle.Generation, handle.NtHandle,
            PhaseSeconds);

        _consumer = new PacedConsumer(consumer);
        StartPhase();
    }

    private void StartPhase()
    {
        Phase phase = Script[_phaseIndex];
        _consumer!.Configure(phase.Mode, phase.PeriodMs);

        // Cleared rather than carried, so the row that follows reports its own
        // waits and none of the previous row's.
        _waitSum = 0;
        _waitWindows = 0;
        _waitPeak = 0f;

        _phaseFrames = 0;
        _phaseHandOversAtStart = _consumer.HandOvers;
        _phaseClock = Stopwatch.StartNew();
    }

    private void RecordPhase()
    {
        Phase phase = Script[_phaseIndex];
        double seconds = _phaseClock!.Elapsed.TotalSeconds;
        long handOvers = _consumer!.HandOvers - _phaseHandOversAtStart;

        _readings.Add(new Reading(
            phase.Name,
            _phaseFrames / seconds,
            handOvers / seconds,
            _waitWindows > 0 ? (float)(_waitSum / _waitWindows) : 0f,
            _waitPeak,
            phase.Meaning));
    }

    private void Report()
    {
        _logger.LogInformation(
            "Shared pacing:  {Header,-10} {Fps,9} {Handovers,9} {Average,11} {Peak,11}",
            "phase", "eng fps", "hand/s", "wait avg", "wait peak");

        foreach (Reading reading in _readings)
        {
            _logger.LogInformation(
                "Shared pacing:  {Name,-10} {Fps,9:0.0} {Handovers,9:0.0} {Average,8:0.00} ms " +
                "{Peak,8:0.00} ms   {Meaning}",
                reading.Name, reading.Fps, reading.HandOversPerSecond,
                reading.AcquireAverageMs, reading.AcquirePeakMs, reading.Meaning);
        }

        // Said out loud rather than left to be read off the table, because the
        // shape of the answer is the finding: the engine's frame rate and the
        // consumer's turn rate are ONE number in every paced row, and the free
        // row says what the producer would do if nothing paced it. Buffering on
        // the producer's side moves the first column toward the free row and
        // leaves the second exactly where it is, so what a viewport shows is
        // set by the consumer's schedule and by nothing this side can do.
        _logger.LogInformation(
            "Shared pacing: the engine's frame rate equals the consumer's turn rate in every paced row - " +
            "the producer cannot start a frame until key 0 comes back, so the wait is the difference " +
            "between the two rates. The 'free' row is the producer's own speed with nothing pacing it.");
    }

    private void Finish(bool passed)
    {
        _consumer?.Dispose();
        _consumer = null;
        Running = false;
        Passed = passed;
    }

    /// <summary>
    /// Takes the consumer's turn on a thread of its own, at a cadence the probe
    /// sets.
    /// </summary>
    /// <remarks>
    /// The deadline advances from the SCHEDULED time rather than from when the
    /// turn finished, so a phase is a rate rather than a floor; it is reset when
    /// it falls more than one period behind, since a consumer that can never
    /// catch up should report the rate it managed rather than sprint to make up
    /// turns it missed.
    /// </remarks>
    private sealed class PacedConsumer : IDisposable
    {
        private readonly ISharedTargetConsumer _consumer;
        private readonly Thread _thread;

        private long _handOvers;
        private volatile bool _running = true;
        private volatile int _mode = (int)ConsumerMode.Stopped;

        // Read on the consumer thread, written between phases from the render
        // thread. A torn read costs one turn of one phase.
        private volatile int _periodTicks;

        internal PacedConsumer(ISharedTargetConsumer consumer)
        {
            _consumer = consumer;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Shared pacing consumer",
            };
            _thread.Start();
        }

        /// <summary>Turns completed since the consumer started.</summary>
        internal long HandOvers => Interlocked.Read(ref _handOvers);

        internal void Configure(ConsumerMode mode, double periodMs)
        {
            _periodTicks = (int)Math.Max(1, periodMs * Stopwatch.Frequency / 1000.0);
            _mode = (int)mode;
        }

        public void Dispose()
        {
            _running = false;

            // Joined BEFORE the consumer is released: the device and its
            // immediate context belong to that thread while it is running, and
            // freeing a keyed-mutex resource under a live acquire is a driver
            // crash with no managed stack.
            _thread.Join(TimeSpan.FromSeconds(2));
            _consumer.Dispose();
        }

        private void Run()
        {
            long next = Stopwatch.GetTimestamp();

            while (_running)
            {
                var mode = (ConsumerMode)_mode;

                if (mode == ConsumerMode.Stopped)
                {
                    next = Stopwatch.GetTimestamp();
                    Thread.Sleep(1);
                    continue;
                }

                if (mode == ConsumerMode.Free)
                {
                    next = Stopwatch.GetTimestamp();
                    TakeTurn();
                    continue;
                }

                long period = _periodTicks;
                long now = Stopwatch.GetTimestamp();
                if (now < next)
                {
                    Thread.SpinWait(200);
                    continue;
                }

                next = now - next > period ? now + period : next + period;
                TakeTurn();
            }
        }

        private void TakeTurn()
        {
            if (_consumer.TakeTurn(100))
                Interlocked.Increment(ref _handOvers);
        }
    }
}
