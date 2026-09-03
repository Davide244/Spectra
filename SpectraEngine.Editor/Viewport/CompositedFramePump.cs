using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Editor.Viewport.Windows;
using System;
using System.Collections.Generic;
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
internal interface ICompositedImageSource
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
/// <b>Threading:</b> UI thread only, every member. The compositor's import and
/// update calls verify that themselves, and every continuation here resumes
/// through the UI thread's synchronization context.
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
    private readonly Func<nint, nint> _duplicateHandle;
    private readonly Action<nint> _closeHandle;
    private readonly ILogger _logger;

    private readonly List<Import> _retired = [];
    private Import? _live;

    private bool _visible = true;
    private bool _stopped;
    private bool _looping;
    private bool _stalled;

    // When the outstanding hand-over started, or null when there is none. See
    // CheckForStall.
    private long? _updateStartedAt;

    internal CompositedFramePump(
        ICompositedImageSource source,
        Action<int> acknowledgeRelease,
        ILogger logger,
        Func<nint, nint>? duplicateHandle = null,
        Action<nint>? closeHandle = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(acknowledgeRelease);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _acknowledgeRelease = acknowledgeRelease;
        _logger = logger;

        // The handle this side holds is its own duplicate, because Avalonia's
        // importer does not take ownership and the renderer may retire its
        // original at any resize. Injected so the pump's own logic is provable
        // without a kernel object anywhere in sight.
        _duplicateHandle = duplicateHandle ?? Win32Interop.DuplicateForCaller;
        _closeHandle = closeHandle ?? (handle => Win32Interop.CloseHandle(handle));
    }

    /// <summary>The generation currently on screen, or zero before the first import.</summary>
    internal int LiveGeneration => _live?.Generation ?? 0;

    /// <summary>How many superseded imports are still waiting to be let go of.</summary>
    internal int RetiredCount => _retired.Count;

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
    /// Stops scheduling for good and lets go of every import.
    /// </summary>
    /// <remarks>
    /// <b>Called while the engine is still running, never after it stops.</b>
    /// An update already in flight is waiting on a key only the producer can
    /// release; stopping the producer first leaves the compositor's render
    /// thread waiting for a frame that will never come. The shell's teardown
    /// clears the viewport's host - which lands here - before it stops the
    /// session, and that order is the whole safety argument.
    /// </remarks>
    internal void Stop()
    {
        _stopped = true;

        if (_live is { } live)
        {
            _live = null;
            Retire(live);
        }
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
                try
                {
                    await import.Image.UpdateAsync(
                        (uint)Renderer.SharedConsumerKey, (uint)Renderer.SharedProducerKey);
                }
                finally
                {
                    _updateStartedAt = null;
                    import.UpdatesInFlight--;
                    SettleIfRetired(import);
                }
            }
        }
        catch (Exception ex)
        {
            _stalled = true;
            _logger.LogError(ex, "The composited viewport's frame pump stopped.");
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
