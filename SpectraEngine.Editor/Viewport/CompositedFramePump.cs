using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Editor.Viewport.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// One imported generation of the engine's shared colour target.
/// </summary>
/// <remarks>
/// <b>The compositor half of the composited viewport, narrowed to what the pump
/// actually does with it.</b> A real one wraps an
/// <c>ICompositionImportedGpuImage</c> and a <c>CompositionDrawingSurface</c>;
/// the interface exists so the pump's generation handling, its retirement and
/// its acknowledgement can be proved with no GPU, no compositor and no window -
/// which is the same reason <see cref="IViewportCursor"/> exists one layer
/// across.
/// </remarks>
internal interface ICompositedImage
{
    /// <summary>
    /// Completes when the compositor has actually opened the shared resource.
    /// </summary>
    /// <remarks>
    /// <b>Nothing may be drawn from this image before it completes</b> - the
    /// compositor throws rather than waiting - and the handle it was imported
    /// from may not be closed before it either, because the open runs on the
    /// render thread and closing underneath it is a race with no diagnostic.
    /// </remarks>
    Task ImportCompleted { get; }

    /// <summary>
    /// Takes the consumer's turn on the keyed mutex and snapshots the texture
    /// into the surface: acquire <paramref name="acquireKey"/>, copy, release
    /// <paramref name="releaseKey"/>.
    /// </summary>
    Task UpdateAsync(uint acquireKey, uint releaseKey);

    /// <summary>Releases the import. The surface keeps the last frame it took.</summary>
    ValueTask DisposeAsync();
}

/// <summary>
/// Creates imports. The compositor half of <see cref="CompositedFramePump"/>'s
/// world.
/// </summary>
internal interface ICompositedImageSource : IAsyncDisposable
{
    /// <summary>Imports the shared texture named by an NT handle this process owns.</summary>
    ICompositedImage Import(nint ntHandle, int width, int height);
}

/// <summary>
/// One window of consumer-side pacing: how many turns this side took, and where
/// the time between them went.
/// </summary>
/// <remarks>
/// <b>The split is the whole value.</b> A hand-over rate below the display's
/// refresh is either the compositor being late with its turn or this side being
/// late re-issuing, and those have opposite fixes: the first is the producer
/// holding the key across work it does not need the key for, the second is
/// where the resume is scheduled. One number for the pair says only that the
/// picture is slow, which is what the frame rate already said.
/// </remarks>
/// <param name="HandOvers">Turns completed in the window.</param>
/// <param name="Seconds">The window's wall time.</param>
/// <param name="CompositorAverageMs">Issue to the update completing, averaged.</param>
/// <param name="CompositorPeakMs">The worst of those.</param>
/// <param name="ResumeAverageMs">The update completing to the loop running again, averaged.</param>
/// <param name="ResumePeakMs">The worst of those.</param>
internal readonly record struct HandOverPacing(
    int HandOvers,
    double Seconds,
    float CompositorAverageMs,
    float CompositorPeakMs,
    float ResumeAverageMs,
    float ResumePeakMs)
{
    /// <summary>Turns per second, which the producer's frame rate equals.</summary>
    internal double PerSecond => Seconds > 0.0 ? HandOvers / Seconds : 0.0;
}

/// <summary>
/// Drives the composited viewport's picture: imports each generation of the
/// engine's shared target, keeps taking the consumer's turn on the keyed mutex,
/// and lets go of a retired generation once nothing is still reading it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mutex is the clock, and that is the whole pacing design.</b> The
/// producer acquires key 0, writes the frame and releases key 1; this side
/// acquires key 1, snapshots and releases key 0. Neither can run twice in a
/// row, so a self-rescheduling update loop settles at whichever side is slower
/// - the compositor's vsync, in practice - without either side polling, timing
/// or throttling. The engine's own acquire carries a short timeout and SKIPS
/// its shared write rather than blocking, which is what makes a stopped pump
/// safe on the producer's side.
/// </para>
/// <para>
/// <b>What that clock COSTS is that this loop sets the engine's frame rate, so
/// where its resume is scheduled is a rendering decision.</b> Measured with
/// <c>--pacing-probe</c> against a real second device: the engine's frame rate
/// equals this side's turn rate exactly, at every cadence - 61 fps at a turn
/// every 16.7 ms, 40 at every 25 ms, 30 at every 33 ms - while the producer's
/// own ceiling is about 2,600. The whole difference is the producer sitting in
/// its acquire, which is why one late turn per frame is one dropped engine
/// frame and not a fraction of one.
/// </para>
/// <para>
/// <b>The re-issue is posted at <see cref="DispatcherPriority.Send"/> and the
/// rest of the loop runs INSIDE that post</b>, never after awaiting it: awaiting
/// an already-completed task continues synchronously on the awaiting thread, so
/// a resume that has already run hands the loop back to the compositor's render
/// thread and the next update throws from <c>Dispatcher.VerifyAccess</c>. That
/// shipped once and faulted every composited session. See
/// <see cref="IssueHandOver"/>.
/// <b>Formerly: the hand-over was re-issued at <see cref="DispatcherPriority.Send"/>,
/// and an ordinary <c>await</c> is what this fixes.</b> Avalonia completes the
/// update's task from the compositor's own render thread with
/// <c>RunContinuationsAsynchronously</c>, so a bare <c>await</c> hands the
/// resume to <c>AvaloniaSynchronizationContext</c> - which posts at
/// <c>DispatcherPriority.Default</c>, documented as "the lowest foreground
/// dispatcher priority". The job that hands the engine its next turn was
/// therefore the last thing in the whole application's queue: behind input,
/// behind layout and render, behind everything the shell posts at Normal. With
/// an idle queue that is invisible, which is exactly why this design measured
/// 58 to 60 fps AT REST and dropped to about 40 the moment the shell had work
/// to do every frame.
/// </para>
/// <para>
/// <b><c>ConfigureAwait(false)</c> alone cannot do it, and that is a fact about
/// Avalonia rather than a preference.</b> <c>UpdateWithKeyedMutexAsync</c>
/// reaches <c>Compositor.PostServerJob</c>, which calls
/// <c>Dispatcher.VerifyAccess()</c>, so the next update MUST be issued from the
/// UI thread. The await leaves that thread and the resume brings it back at the
/// top of the queue, which is the smallest change that removes the queue
/// position from the loop's period. It does NOT make the pump preemptive: a UI
/// job already running still has to finish.
/// </para>
/// <para>
/// <b>It stops while hidden, deliberately.</b> A minimised window, a closed
/// session or a viewport nobody can see has nothing to show, and an update loop
/// left running would go on copying a full-screen texture per vsync for it. The
/// producer answers a stopped consumer by timing out and carrying on, which it
/// logs exactly once.
/// </para>
/// <para>
/// <b>Generations, never handles.</b> A shared target is destroyed and rebuilt
/// on every resize; the handle it is named by is a value the OS recycles, so
/// the only thing that can be compared is the generation counter the renderer
/// hands out. A new one means re-import; the same one means the picture on
/// screen is already the right texture.
/// </para>
/// <para>
/// <b>Retirement is what keeps a resize from being a crash.</b> The renderer
/// holds a retired generation's resource until the consumer says it is done
/// with it, because the consumer may be sampling it this instant and freeing it
/// underneath produces no exception on either side. So a superseded import is
/// disposed once its last update has finished - never while one is in flight -
/// and only then is the generation acknowledged back to the renderer.
/// </para>
/// <para>
/// <b>The pump owns the SOURCE, and that ownership is what makes a re-parent
/// survivable.</b> The compositor half is one drawing surface every import
/// snapshots into, and a viewport that is dragged into another dock tears its
/// compositor objects down and builds fresh ones on the other side. Disposing
/// that surface from the outside, at the moment of detach, means disposing it
/// while a hand-over may still be inside the keyed-mutex bracket: the pending
/// update faults, the pump reports a fault, and every re-dock becomes a session
/// that says the composited viewport failed. So <see cref="Stop"/> takes the
/// source with it and disposes it after the last import has settled, in the
/// same order and for the same reason the imports themselves are held back.
/// </para>
/// <para>
/// <b>Threading:</b> UI thread only, every member and every field. The
/// compositor's import and update calls verify that themselves; the one place
/// this loop leaves that thread is the await on a hand-over, and it is back on
/// it before any field below is touched.
/// </para>
/// </remarks>
internal sealed class CompositedFramePump
{
    /// <summary>
    /// How long one hand-over may take before the pump gives up scheduling
    /// more.
    /// </summary>
    /// <remarks>
    /// <b>Not a timeout on the acquire - there is no such thing here.</b> The
    /// compositor waits on the keyed mutex ON ITS RENDER THREAD with an
    /// effectively infinite deadline, so a producer that stops releasing the key
    /// (a faulted render thread, a session torn down out of order) freezes the
    /// whole UI and nothing anywhere says why. This cannot unblock that, and
    /// does not pretend to: it stops the pump adding to it and puts one line in
    /// the log naming the cause, which is the difference between a bug report
    /// that can be acted on and "the editor hung".
    /// </remarks>
    internal static readonly TimeSpan UpdateWatchdog = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many hand-overs this side keeps outstanding at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two, because one is not enough to fill the compositor's queue and the
    /// gap is a whole refresh.</b> An update is a server job the compositor
    /// picks up on its own tick. With a single hand-over in flight the next one
    /// is not issued until the previous has completed and the loop is back on
    /// the UI thread, which lands microseconds after the tick that completed it
    /// - and about half the time that is microseconds too late for the tick
    /// after, so that hand-over waits a whole refresh. Measured in a real
    /// composited session, on d3d11 AND d3d12 alike: 40.5 hand-overs a second
    /// at 24.7 ms each against a 16.7 ms refresh, with the resume hop itself
    /// costing 0.1 ms. With a second update already queued the compositor never
    /// idles: 60.5 a second at 16.5 ms, same machine, same scene, one constant
    /// apart.
    /// </para>
    /// <para>
    /// <b>The engine's frame rate IS this number</b>, because the producer
    /// cannot start a frame until this side hands the key back, which
    /// <c>--pacing-probe</c> measured as an exact equality at every cadence. So
    /// this constant is a rendering decision wearing a scheduling costume.
    /// </para>
    /// <para>
    /// <b>What the second one costs, stated rather than discovered.</b> Both
    /// jobs snapshot the SAME shared texture, so the queued one acquires a
    /// consumer key the producer has not released yet and the compositor's
    /// render thread blocks for one producer frame - about 0.4 ms of render on
    /// this machine, and however long a hitch lasts when the producer hitches.
    /// The only thing that removes that coupling is a second shared texture, so
    /// the queued job always targets the one the producer has already finished.
    /// Not built: measured, the block is a fraction of a millisecond against a
    /// refresh, and a ring costs a second full-size shared target plus per-slot
    /// generations through the renderer, the snapshot and this pump.
    /// </para>
    /// <para>
    /// <b>Never more than two, because depth is bought WITH latency.</b>
    /// Throughput is depth over latency, so the same measurement that shows 60
    /// hand-overs a second shows each one taking 33.2 ms rather than 24.7: the
    /// picture is a refresh further behind the scene than it was. End to end
    /// that is about 4 ms worse (a frame's own age plus half the gap to the
    /// next one: 36.9 ms at one deep, 41.3 at two) and it buys an even 16.7 ms
    /// cadence in place of one that alternated 16.7 and 33.3, which is the
    /// judder that reads as lag. A third would buy no throughput at all - the
    /// queue is already full at two - and would cost another whole refresh of
    /// it.
    /// </para>
    /// </remarks>
    internal const int HandOverDepth = 2;

    /// <summary>How much wall time one <see cref="HandOverPacing"/> window covers.</summary>
    internal static readonly TimeSpan PacingWindow = TimeSpan.FromSeconds(2);

    private static readonly long PacingWindowTicks =
        (long)(PacingWindow.TotalSeconds * Stopwatch.Frequency);

    private readonly ICompositedImageSource _source;
    private readonly Action<int> _acknowledgeRelease;
    private readonly Action? _onFault;
    private readonly Func<nint, nint> _duplicateHandle;
    private readonly Action<nint> _closeHandle;
    private readonly Action<Action> _resumeOnUiThread;
    private readonly ILogger _logger;

    private readonly List<Import> _retired = [];
    private Import? _live;

    private bool _visible = true;
    private bool _stopped;
    private bool _looping;
    private bool _stalled;

    // Imports that exist and have not finished being released. The source
    // outlives every one of them, because each snapshots into its surface.
    private int _outstanding;
    private bool _sourceReleased;

    // When each outstanding hand-over was issued, oldest first. A QUEUE rather
    // than one slot because HandOverDepth is greater than one: with a single
    // field the second issue overwrites the first, so the watchdog measures the
    // wrong hand-over and the pacing split charges one hand-over's wait to
    // another. Server jobs run in order on the compositor's own thread, so
    // these complete in the order they were issued.
    private readonly Queue<long> _outstandingIssues = new();

    // ---- Consumer-side pacing ----------------------------------------------
    //
    // The producer times its own acquire (Renderer.RecordSharedAcquireWait) and
    // that number says only THAT it waited, never why. This is the other half:
    // one hand-over splits into the compositor's own latency (issue to the
    // update completing, which carries its tick, its copy and its own blocked
    // acquire) and this side's resume hop (the completion to the loop running
    // again on the UI thread). A hand-over rate below the display's is one or
    // the other, and no instrument in the engine can tell them apart.
    private long _pacingWindowStart;
    private int _pacingSamples;
    private long _compositorTicks;
    private long _compositorPeakTicks;
    private long _resumeTicks;
    private long _resumePeakTicks;

    /// <param name="onFault">
    /// Raised once, on the UI thread, when the picture stops arriving and is not
    /// going to start again - a hand-over that threw, or one the watchdog gave
    /// up on. <b>What the caller may do with it is say so</b>: a viewport that
    /// answered by swapping its hosting model mid-session would tear down a live
    /// engine under the user's hands and leave two viewports in one log.
    /// </param>
    internal CompositedFramePump(
        ICompositedImageSource source,
        Action<int> acknowledgeRelease,
        ILogger logger,
        Func<nint, nint>? duplicateHandle = null,
        Action<nint>? closeHandle = null,
        Action? onFault = null,
        Action<Action>? resumeOnUiThread = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(acknowledgeRelease);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _acknowledgeRelease = acknowledgeRelease;
        _onFault = onFault;
        _logger = logger;

        // The handle this side holds is its own duplicate, because Avalonia's
        // importer does not take ownership and the renderer may retire its
        // original at any resize. Injected so the pump's own logic is provable
        // without a kernel object anywhere in sight.
        _duplicateHandle = duplicateHandle ?? Win32Interop.DuplicateForCaller;
        _closeHandle = closeHandle ?? (handle => Win32Interop.CloseHandle(handle));

        // Send, which is the top of the dispatcher's ordering: the class
        // remarks say why the default an await would use is the bottom of it.
        // Injected for the same reason the two handle callbacks are - the whole
        // loop is then provable with no dispatcher, no compositor and no window
        // anywhere in sight.
        _resumeOnUiThread = resumeOnUiThread
            ?? (action => Dispatcher.UIThread.Post(action, DispatcherPriority.Send));
    }

    /// <summary>The generation currently on screen, or zero before the first import.</summary>
    internal int LiveGeneration => _live?.Generation ?? 0;

    /// <summary>How many superseded imports are still waiting to be let go of.</summary>
    internal int RetiredCount => _retired.Count;

    /// <summary>
    /// Whether the compositor half has been let go of. See
    /// <see cref="ReleaseSourceIfSettled"/>.
    /// </summary>
    internal bool SourceReleased => _sourceReleased;

    /// <summary>Whether an update loop is running.</summary>
    internal bool IsPumping => _looping;

    /// <summary>
    /// The last completed pacing window, or default before one has closed.
    /// </summary>
    internal HandOverPacing LastPacing { get; private set; }

    /// <summary>
    /// Whether the pump gave up on a hand-over that never completed. See
    /// <see cref="UpdateWatchdog"/>.
    /// </summary>
    internal bool IsStalled => _stalled;

    /// <summary>
    /// Takes whatever the engine's latest frame said about its shared target.
    /// Cheap and idempotent for a generation already on screen, which is every
    /// call but the handful that follow a resize.
    /// </summary>
    internal void Observe(Renderer.SharedTargetHandle handle)
    {
        if (_stopped)
            return;

        if (handle.NtHandle == 0 || handle.Width <= 0 || handle.Height <= 0)
            return;

        if (_live is { } live && live.Generation == handle.Generation)
        {
            // The same texture as last time. The loop is already running against
            // it; restarting one here is how a pump ends up with two.
            StartLoop();
            return;
        }

        if (_live is { } superseded)
        {
            _live = null;
            Retire(superseded);
        }

        nint owned = _duplicateHandle(handle.NtHandle);
        if (owned == 0)
        {
            // The producer's handle went away between the publish and here,
            // which is a resize that outran the shell. Nothing to import and
            // nothing to fix: the next generation is already on its way.
            _logger.LogWarning(
                "Shared target generation {Generation} could not be duplicated; waiting for the next one.",
                handle.Generation);
            return;
        }

        var import = new Import(
            handle.Generation, owned, _source.Import(owned, handle.Width, handle.Height));
        _outstanding++;
        _live = import;

        _logger.LogInformation(
            "Composited viewport importing shared target generation {Generation} at {Width}x{Height}.",
            handle.Generation, handle.Width, handle.Height);

        _ = AdoptAsync(import);
    }

    /// <summary>
    /// Whether the viewport can be seen. False stops the loop; true starts it
    /// again if there is anything to show.
    /// </summary>
    internal void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;

        _visible = visible;
        if (visible)
            StartLoop();
    }

    /// <summary>
    /// Stops scheduling for good and lets go of every import, then of the
    /// source they were snapshotting into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called while the engine is still running, never after it stops.</b>
    /// An update already in flight is waiting on a key only the producer can
    /// release; stopping the producer first leaves the compositor's render
    /// thread waiting for a frame that will never come. The shell's teardown
    /// clears the viewport's host - which lands here - before it stops the
    /// session, and that order is the whole safety argument.
    /// </para>
    /// <para>
    /// <b>A pump is stopped for a re-parent as well as for a teardown</b>, and
    /// the two are the same call because they are the same requirement: this
    /// pump is finished with, the producer is still there to answer whatever is
    /// outstanding, and a fresh pump built on the other side of the move starts
    /// from no import at all and re-imports the generation it is next told
    /// about.
    /// </para>
    /// </remarks>
    internal void Stop()
    {
        _stopped = true;

        if (_live is { } live)
        {
            _live = null;
            Retire(live);
        }

        ReleaseSourceIfSettled();
    }

    // --- The loop ------------------------------------------------------------

    private async Task AdoptAsync(Import import)
    {
        try
        {
            // Before anything is drawn from it and before its handle is closed:
            // the compositor opens the resource on its render thread, throws if
            // asked to draw sooner, and racing the open with a CloseHandle has
            // no diagnostic at all.
            await import.Image.ImportCompleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "The compositor refused shared target generation {Generation}.", import.Generation);

            if (ReferenceEquals(_live, import))
                _live = null;

            Retire(import);
            return;
        }
        finally
        {
            // The duplicate has done its job either way: the compositor holds
            // the resource through its own COM reference from here on.
            import.CloseHandle(_closeHandle);
        }

        StartLoop();
    }

    private void StartLoop()
    {
        if (_looping || _stopped || _stalled || !_visible)
            return;

        if (_live is not { } live || !live.Image.ImportCompleted.IsCompletedSuccessfully)
            return;

        _looping = true;
        TopUp(live);
    }

    /// <summary>
    /// Issues hand-overs until <see cref="HandOverDepth"/> are outstanding, and
    /// ends the loop when none are and none should be. <b>UI thread only.</b>
    /// </summary>
    /// <remarks>
    /// <b>The stand-down check is here rather than inside the issue</b>, so one
    /// place decides whether the loop goes on. It ends the loop only when
    /// nothing is still in flight: a generation superseded while its second
    /// hand-over was outstanding still owes that hand-over a completion, and
    /// tearing the loop down around it would leave the retirement waiting for a
    /// settle that never comes.
    /// </remarks>
    private void TopUp(Import import)
    {
        while (import.UpdatesInFlight < HandOverDepth)
        {
            if (_stopped || _stalled || !_visible || !ReferenceEquals(_live, import))
            {
                if (import.UpdatesInFlight == 0) EndLoop();
                return;
            }

            IssueHandOver(import);
        }
    }

    /// <summary>
    /// Issues one hand-over. <b>UI thread only.</b>
    /// </summary>
    /// <remarks>
    /// <b>Not an async loop, and that is the whole point.</b> The obvious shape
    /// is <c>while (...) await UpdateAsync(); await ResumeOnUiThread();</c>, and
    /// it is wrong in a way that reads as correct: awaiting a task that is
    /// ALREADY COMPLETE continues synchronously on the awaiting thread, so a
    /// resume posted at <see cref="DispatcherPriority.Send"/> - which usually
    /// runs before the caller reaches its await - hands the rest of the loop
    /// back to the compositor's render thread rather than to the UI thread. The
    /// next <c>UpdateAsync</c> then calls <c>Dispatcher.VerifyAccess</c> from
    /// the wrong thread and the pump reports a fault on a viewport that is
    /// working perfectly. Measured, in a real composited session.
    /// <para>
    /// So the continuation is not awaited at all: everything after the
    /// hand-over runs INSIDE the posted action, where the thread is not in
    /// question. The chain does not grow a stack, because each pass ends by
    /// posting rather than by returning into its caller.
    /// </para>
    /// </remarks>
    private void IssueHandOver(Import import)
    {
        import.UpdatesInFlight++;
        _outstandingIssues.Enqueue(Stopwatch.GetTimestamp());

        Task handOver;
        try
        {
            handOver = import.Image.UpdateAsync(
                (uint)Renderer.SharedConsumerKey, (uint)Renderer.SharedProducerKey);
        }
        catch (Exception ex)
        {
            // Threw before a task existed, so there is nothing to continue from
            // and this is already the UI thread. No completion instant either,
            // which is what the null says.
            CompleteHandOver(import, ExceptionDispatchInfo.Capture(ex), completedAt: null);
            return;
        }

        // Stamped HERE, on whichever thread completed the update, because the
        // whole point is to separate the wait before this instant from the hop
        // after it. Taken inside the continuation and passed by value, so the
        // post's own delay cannot be charged to the compositor.
        handOver.ContinueWith(
            finished =>
            {
                long completedAt = Stopwatch.GetTimestamp();
                _resumeOnUiThread(() => CompleteHandOver(import, Failure(finished), completedAt));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Finishes one hand-over and issues the next. <b>UI thread only</b>, which
    /// is structural: every path here arrives through
    /// <see cref="_resumeOnUiThread"/> or from <see cref="IssueHandOver"/>
    /// itself, which is UI-thread only in turn.
    /// </summary>
    private void CompleteHandOver(Import import, ExceptionDispatchInfo? failure, long? completedAt)
    {
        long issuedAt = _outstandingIssues.Dequeue();
        import.UpdatesInFlight--;
        if (completedAt is { } finishedAt) RecordPacing(issuedAt, finishedAt);
        SettleIfRetired(import);

        if (failure is not null)
        {
            _stalled = true;
            _logger.LogError(
                failure.SourceException, "The composited viewport's frame pump stopped.");
            _onFault?.Invoke();
            EndLoop();
            return;
        }

        TopUp(import);
    }

    private static ExceptionDispatchInfo? Failure(Task finished) =>
        finished.Exception is { } aggregate
            ? ExceptionDispatchInfo.Capture(aggregate.InnerException ?? aggregate)
            : null;

    /// <summary>
    /// Accounts for one completed hand-over and reports the window when it is
    /// full. <b>UI thread only</b>, like everything else the loop touches.
    /// </summary>
    /// <remarks>
    /// At Debug rather than Information: this is one line every
    /// <see cref="PacingWindow"/> for as long as a composited session is open,
    /// which at Information would be a standing wall in a log people read for
    /// one-off events. The editor's minimum level is Debug, so it is in the
    /// file either way.
    /// </remarks>
    private void RecordPacing(long issuedAt, long completedAt)
    {
        long now = Stopwatch.GetTimestamp();
        if (_pacingWindowStart == 0L) _pacingWindowStart = issuedAt;

        long compositor = Math.Max(0L, completedAt - issuedAt);
        long resume = Math.Max(0L, now - completedAt);

        _pacingSamples++;
        _compositorTicks += compositor;
        _resumeTicks += resume;
        if (compositor > _compositorPeakTicks) _compositorPeakTicks = compositor;
        if (resume > _resumePeakTicks) _resumePeakTicks = resume;

        long windowTicks = now - _pacingWindowStart;
        if (windowTicks < PacingWindowTicks) return;

        double toMs = 1000.0 / Stopwatch.Frequency;
        var pacing = new HandOverPacing(
            _pacingSamples,
            windowTicks / (double)Stopwatch.Frequency,
            (float)(_compositorTicks * toMs / _pacingSamples),
            (float)(_compositorPeakTicks * toMs),
            (float)(_resumeTicks * toMs / _pacingSamples),
            (float)(_resumePeakTicks * toMs));

        LastPacing = pacing;

        _logger.LogDebug(
            "Composited pacing: {Rate:0.0} hand-overs/s; compositor {CompositorAvg:0.0}/{CompositorPeak:0.0} ms, " +
            "resume {ResumeAvg:0.0}/{ResumePeak:0.0} ms (avg/peak).",
            pacing.PerSecond, pacing.CompositorAverageMs, pacing.CompositorPeakMs,
            pacing.ResumeAverageMs, pacing.ResumePeakMs);

        _pacingWindowStart = now;
        _pacingSamples = 0;
        _compositorTicks = 0L;
        _compositorPeakTicks = 0L;
        _resumeTicks = 0L;
        _resumePeakTicks = 0L;
    }

    private void EndLoop()
    {
        _looping = false;

        // A generation that replaced this one while its last hand-over was
        // still in flight has been sitting with no loop behind it: the loop
        // is per import, and the one that adopted the new import found this
        // one still running and stood down.
        StartLoop();
    }

    /// <summary>
    /// Notices a hand-over that is never going to complete. Called once per
    /// pass of the shell's pump, which keeps running when the render thread
    /// does not.
    /// </summary>
    /// <remarks>
    /// <b>This cannot unblock anything and does not pretend to.</b> See
    /// <see cref="UpdateWatchdog"/>: the wait is on the compositor's own render
    /// thread with no deadline worth the name, so all that is available is to
    /// stop adding to it and to put the cause in the log. Polled from the UI
    /// thread rather than raced against a timer per update, because a timer per
    /// update is sixty allocations a second forever to catch something that
    /// happens once.
    /// <para>
    /// <b>It now covers the resume as well as the hand-over</b>, because
    /// <c>_updateStartedAt</c> is cleared after the loop is back on the UI
    /// thread. That is deliberate and it is what the watchdog was always for:
    /// what it reports is that the picture has stopped arriving, and a
    /// dispatcher that never runs the resume stops it just as completely as a
    /// producer that never releases the key.
    /// </para>
    /// </remarks>
    internal void CheckForStall()
    {
        if (_stalled || !_outstandingIssues.TryPeek(out long oldest))
            return;

        double waitedSeconds = (Stopwatch.GetTimestamp() - oldest) / (double)Stopwatch.Frequency;
        if (waitedSeconds < UpdateWatchdog.TotalSeconds)
            return;

        _stalled = true;
        _logger.LogError(
            "The compositor has been waiting {Seconds:0.#} s for the engine to release the shared target's " +
            "key. The producer has stopped rendering; the viewport is frozen and the pump has stopped " +
            "scheduling. This is the one ordering the composited path cannot recover from: the consumer's " +
            "acquire has no usable deadline, so the engine must outlive the pump.",
            UpdateWatchdog.TotalSeconds);

        _onFault?.Invoke();
    }

    // --- Retirement ----------------------------------------------------------

    private void Retire(Import import)
    {
        import.Retired = true;
        _retired.Add(import);
        SettleIfRetired(import);
    }

    private void SettleIfRetired(Import import)
    {
        // NEVER while a hand-over is still in flight. The compositor is inside
        // the keyed-mutex bracket at that moment, and disposing the import from
        // under it is the crash the whole retirement handshake exists to avoid.
        if (!import.Retired || import.UpdatesInFlight > 0)
            return;

        if (!_retired.Remove(import))
            return;

        _ = ReleaseAsync(import);
    }

    private async Task ReleaseAsync(Import import)
    {
        try
        {
            await import.Image.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Releasing shared target generation {Generation} failed.", import.Generation);
        }
        finally
        {
            // In case the import never completed: the duplicate is this side's
            // and closing it twice is refused rather than repeated.
            import.CloseHandle(_closeHandle);
        }

        // AFTER the import is gone, never before. The acknowledgement is what
        // frees the renderer's resource, and it frees every generation at or
        // below this one - which is exactly right, because a generation the
        // shell never saw is a generation it never imported.
        _acknowledgeRelease(import.Generation);

        _outstanding--;
        ReleaseSourceIfSettled();
    }

    /// <summary>
    /// Lets go of the compositor half, once nothing is still reading through
    /// it.
    /// </summary>
    /// <remarks>
    /// <b>Only after <see cref="Stop"/>, and only with no import left.</b> The
    /// drawing surface is what every hand-over snapshots into, so disposing it
    /// with one outstanding is the same crash the per-import retirement exists
    /// to avoid, one level up. A pump that never settles simply never releases
    /// it, which is a leak the window's own teardown ends and is strictly better
    /// than a free under a live bracket.
    /// </remarks>
    private void ReleaseSourceIfSettled()
    {
        if (!_stopped || _sourceReleased || _outstanding > 0)
            return;

        _sourceReleased = true;
        _ = DisposeSourceAsync();
    }

    private async Task DisposeSourceAsync()
    {
        try
        {
            await _source.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Releasing the composited viewport's drawing surface failed.");
        }
    }

    private sealed class Import(int generation, nint handle, ICompositedImage image)
    {
        private nint _handle = handle;

        internal int Generation { get; } = generation;

        internal ICompositedImage Image { get; } = image;

        internal int UpdatesInFlight { get; set; }

        internal bool Retired { get; set; }

        internal void CloseHandle(Action<nint> close)
        {
            if (_handle == 0)
                return;

            nint handle = _handle;
            _handle = 0;
            close(handle);
        }
    }
}
