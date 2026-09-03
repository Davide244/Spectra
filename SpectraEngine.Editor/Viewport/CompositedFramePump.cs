using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Editor.Viewport.Windows;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
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
/// <b>So the hand-over is re-issued at <see cref="DispatcherPriority.Send"/>,
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

    // When the outstanding hand-over started, or null when there is none. See
    // CheckForStall.
    private long? _updateStartedAt;

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
        _ = RunAsync(live);
    }

    private async Task RunAsync(Import import)
    {
        try
        {
            while (!_stopped && !_stalled && _visible && ReferenceEquals(_live, import))
            {
                import.UpdatesInFlight++;
                _updateStartedAt = Environment.TickCount64;

                // Captured rather than rethrown from a finally, because the
                // resume below has to happen either way and has to happen
                // FIRST: the bookkeeping under it and the catch that reports a
                // fault are both UI-thread work.
                ExceptionDispatchInfo? failure = null;
                try
                {
                    // ConfigureAwait(false), so the resume is not handed to
                    // Avalonia's synchronization context - which posts it at the
                    // lowest foreground priority there is. See the class remarks.
                    await import.Image.UpdateAsync(
                            (uint)Renderer.SharedConsumerKey, (uint)Renderer.SharedProducerKey)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }

                await ResumeOnUiThreadAsync().ConfigureAwait(false);

                _updateStartedAt = null;
                import.UpdatesInFlight--;
                SettleIfRetired(import);

                failure?.Throw();
            }
        }
        catch (Exception ex)
        {
            _stalled = true;
            _logger.LogError(ex, "The composited viewport's frame pump stopped.");
            _onFault?.Invoke();
        }
        finally
        {
            _looping = false;

            // A generation that replaced this one while its last hand-over was
            // still in flight has been sitting with no loop behind it: the loop
            // is per import, and the one that adopted the new import found this
            // one still running and stood down.
            StartLoop();
        }
    }

    /// <summary>
    /// Completes on the UI thread, ahead of whatever the shell has queued.
    /// </summary>
    /// <remarks>
    /// <b>No <c>RunContinuationsAsynchronously</c>, and <c>ConfigureAwait(false)</c>
    /// at the call site</b>: together those make the loop resume INLINE on
    /// whichever thread the posted action lands on, which is the UI thread.
    /// Either one alone would hand the rest of the loop - and the next
    /// <c>UpdateAsync</c>, which verifies UI-thread access - back to a thread
    /// pool.
    /// </remarks>
    private Task ResumeOnUiThreadAsync()
    {
        var resumed = new TaskCompletionSource();
        _resumeOnUiThread(() => resumed.TrySetResult());
        return resumed.Task;
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
        if (_stalled || _updateStartedAt is not { } started)
            return;

        if (Environment.TickCount64 - started < (long)UpdateWatchdog.TotalMilliseconds)
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
