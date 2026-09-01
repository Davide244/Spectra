using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SpectraEngine.Core.Hosting;

/// <summary>
/// The entire surface a UI thread gets onto a running engine: enqueue work,
/// ask it to stop, and hear about finished frames.
/// </summary>
/// <remarks>
/// <b>Embedded mode is engine-driven, and this is the shape of that decision.</b>
/// The render thread already owns the graphics context, every scene mutation and
/// all GPU resource creation, including the chunk-mesh swaps inside the compile
/// pump. Keeping that ownership means the async CSG pipeline, the spatial index,
/// the selection set and the undo stack all keep their existing single-threaded
/// proofs verbatim, and it decouples the viewport from a UI framework's layout
/// stalls. So a shell does not call into the engine; it posts work and reads
/// results.
/// <para>
/// <b><see cref="SubmitInput"/> is the fourth member, and it arrived a
/// milestone late on purpose.</b> Routing input needed a backend-neutral event
/// vocabulary, and designing one with no real host to shape it against is how a
/// seam ends up fitting nothing. With a shell in hand the shape settled at
/// <see cref="InputEvent"/>: engine-named keys, a flag set of pointer buttons,
/// and an absolute pointer position kept distinct from a raw delta because a
/// captured cursor only has the second. The standalone window's own device
/// callbacks now go through the same submission path, so the two hosts cannot
/// drift in how a press or a drag behaves.
/// </para>
/// <para>
/// <b>Everything here is thread-safe by construction, not by convention.</b>
/// <see cref="EnqueueCommand"/> and <see cref="RequestShutdown"/> are called
/// from a UI thread and consumed on the render thread through a concurrent queue
/// and a volatile flag; <see cref="FrameCompleted"/> is raised ON the render
/// thread and its payload is immutable, so a handler may hold it but must
/// marshal to its own thread before touching UI.
/// </para>
/// </remarks>
public sealed class EngineHost
{
    private readonly ConcurrentQueue<Action<Scene.Scene>> _commands = new();
    private readonly SceneChangeLog _changeLog = new();
    private readonly ILogger _logger;

    private volatile bool _shutdownRequested;
    private long _frameNumber;

    /// <summary>Creates a host that logs the things a shell cannot see.</summary>
    public EngineHost(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    // The engine's input state machine, attached at construction. Nullable only
    // so a host can be built and exercised without one.
    private InputManager? _input;

    /// <summary>Points this host's input surface at the engine's input state.</summary>
    internal void AttachInput(InputManager input) => _input = input;

    /// <summary>
    /// Feeds one piece of input to the engine, in its own vocabulary. Safe to
    /// call from the thread that owns the window.
    /// </summary>
    /// <remarks>
    /// <b>Applied immediately rather than queued</b>, because the engine's input
    /// state is already a lock-guarded state machine written from the OS-event
    /// thread and read from the render thread — a host's UI thread is exactly
    /// the position the standalone window's own device callbacks are in.
    /// Queueing would add a frame of latency to a mouse move for no safety that
    /// is not already there.
    /// <para>
    /// <b>An embedded engine gets input no other way.</b> A host-supplied
    /// surface has no devices the engine could enumerate, so with nothing
    /// submitting, the engine runs with every key up: a correct resting state,
    /// and the reason a viewport that renders but does not respond is a wiring
    /// bug rather than a crash.
    /// </para>
    /// </remarks>
    public void SubmitInput(in InputEvent input) => _input?.Submit(in input);

    /// <summary>
    /// The cursor mode the engine is asking for, whether or not it has been
    /// applied. See <see cref="ApplyPendingCursorMode"/>.
    /// </summary>
    public CursorMode RequestedCursorMode => _input?.RequestedCursorMode ?? CursorMode.Normal;

    /// <summary>
    /// Acknowledges the requested cursor mode, after the caller has performed
    /// whatever platform capture it implies.
    /// </summary>
    /// <remarks>
    /// <b>The cursor belongs to whoever owns the window, and that is the whole
    /// shape of this pair.</b> The engine's editor camera asks for a locked
    /// cursor from the render thread; the standalone path applies that to a Silk
    /// mouse in its own event pump. An embedded host has no such device — it
    /// owns the real one — so it polls <see cref="RequestedCursorMode"/>, hides
    /// and captures the pointer itself, then calls this to close the engine's
    /// state machine. Calling it when nothing changed is free.
    /// </remarks>
    public void ApplyPendingCursorMode() => _input?.ApplyPendingCursorMode();

    /// <summary>
    /// Raised on the RENDER thread once per published frame, carrying an
    /// immutable description of it.
    /// </summary>
    /// <remarks>
    /// <b>A handler runs inside the engine's frame and must not block.</b>
    /// Anything a shell does here delays the next frame directly. The intended
    /// shape is to stash the snapshot and post to the UI thread; the snapshot is
    /// immutable precisely so that stashing it is safe.
    /// <para>
    /// Not raised every frame. See <see cref="SnapshotInterval"/>.
    /// </para>
    /// </remarks>
    public event Action<FrameSnapshot>? FrameCompleted;

    /// <summary>
    /// How often a snapshot is published. Defaults to about thirty a second.
    /// </summary>
    /// <remarks>
    /// <b>Publishing per frame would be the wrong trade in both directions.</b>
    /// The engine runs at several hundred frames a second in an empty scene, and
    /// no panel refreshes that fast; each snapshot allocates, so per-frame
    /// publishing would put real garbage on the render thread to feed a UI that
    /// throws most of it away. Structural CHANGES are never dropped by this: they
    /// accumulate continuously in the change log and every one of them rides the
    /// next snapshot out, which is the entire reason the log exists.
    /// </remarks>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMilliseconds(33);

    /// <summary>
    /// The interval used instead while the editor is mid-gesture. Defaults to
    /// about a hundred and twenty a second.
    /// </summary>
    /// <remarks>
    /// <b>A gizmo drag is the one case where the resting interval is visibly
    /// wrong.</b> The engine renders the drag at several hundred frames a second
    /// and the inspector beside it steps at thirty, so the numbers a user is
    /// watching move in visible jerks while the object under them is smooth -
    /// which reads as the panel being broken rather than as it being throttled.
    /// <para>
    /// The cost is bounded by the gesture: a drag lasts about a second, so this
    /// is on the order of ninety extra snapshots for the whole gesture, and the
    /// argument against per-frame publishing (hundreds a second, forever, for a
    /// UI that discards most of them) does not apply to it.
    /// </para>
    /// <para>
    /// A shell that drains snapshots rather than sampling the newest MUST size
    /// its queue in time rather than in count, or a bound that meant eight
    /// seconds at the resting rate means two here.
    /// </para>
    /// </remarks>
    public TimeSpan InteractiveSnapshotInterval { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>The most recently published snapshot, or <see cref="FrameSnapshot.Empty"/>.</summary>
    /// <remarks>
    /// For a shell that wants to poll rather than subscribe, and for one that
    /// starts up mid-run and needs something to bind to immediately. Reads are
    /// atomic because the reference is replaced, never mutated.
    /// </remarks>
    public FrameSnapshot LastSnapshot { get; private set; } = FrameSnapshot.Empty;

    /// <summary>True once <see cref="RequestShutdown"/> has been called.</summary>
    public bool ShutdownRequested => _shutdownRequested;

    /// <summary>How many commands are waiting to run.</summary>
    public int PendingCommandCount => _commands.Count;

    /// <summary>
    /// Queues work to run on the render thread, at a defined point in the next
    /// frame. Safe to call from any thread.
    /// </summary>
    /// <remarks>
    /// <b>This is how a shell edits anything.</b> A menu item, a toolbar button,
    /// a property field and a tree drag all end up here, because the scene may
    /// only be touched by the thread that owns it.
    /// <para>
    /// <b>The active scene is passed in rather than captured</b>, so a command
    /// queued before a scene swap runs against the scene that is actually live
    /// when it executes. A command queued while NO scene is active is held, not
    /// dropped: work a user asked for must not evaporate because it arrived
    /// during a load.
    /// </para>
    /// <para>
    /// Exceptions are caught and logged rather than allowed to escape: one bad
    /// command from a shell must not take down the render thread and with it the
    /// whole editor.
    /// </para>
    /// </remarks>
    public void EnqueueCommand(Action<Scene.Scene> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Enqueue(command);

        // A command the user just issued is news, exactly as a structural change
        // is. Without this the echo waits for the clock, so a toolbar toggle
        // sits dark for up to a whole interval after the click and the wait is
        // VARIABLE, which the eye reads as unreliability rather than as
        // latency. The same argument the change log already makes; this is the
        // other half of it.
        _stateDirty = true;
    }

    /// <summary>
    /// Asks the engine to stop after the current frame. Safe to call from any
    /// thread, and idempotent.
    /// </summary>
    public void RequestShutdown() => _shutdownRequested = true;

    // --- Engine-level requests ----------------------------------------------
    //
    // Play mode, debug visualisations and the pipeline are ENGINE state, not
    // scene state, so EnqueueCommand's Action<Scene> cannot reach them — and
    // handing a shell the Engine itself would hand it every render-thread-only
    // member on it. Each is therefore a request latch in the exact shape of the
    // title, cursor and window-mode latches: any thread writes the request,
    // the render loop takes it at the same site the matching key press is read,
    // so a button and a key cannot disagree about what the verb does.
    //
    // Latches rather than a queue, deliberately: every one of these is
    // last-write-wins state ("be playing", "wireframe on", "use Deferred"),
    // and a queue would replay a stale intermediate click for no benefit.

    // Every request latch marks the state dirty for the same reason
    // EnqueueCommand does: a play button, a debug toggle and a pipeline change
    // are all things a user just clicked, and all of them are read back from
    // the next snapshot. MarkDirty is the one place that says so, so a latch
    // added later cannot quietly opt out of it.
    private volatile bool _stateDirty;

    private void MarkDirty() => _stateDirty = true;

    private int _playModeRequest = -1;
    private int _debugFlagsToSet;
    private int _debugFlagsToClear;
    private string? _pipelineRequest;

    /// <summary>
    /// Asks the engine to enter (<c>true</c>) or leave (<c>false</c>) play
    /// mode. Idempotent — requesting the state it is already in does nothing —
    /// and safe from any thread.
    /// </summary>
    /// <remarks>
    /// A request, not a switch: entering play is a transfer of the camera and
    /// the cursor away from an editor that may be holding both mid-gesture, and
    /// only the render loop can run that hand-off. Whether it happened is
    /// reported by <see cref="FrameSnapshot.IsPlaying"/>; a scene with no
    /// character (<see cref="FrameSnapshot.CanPlay"/> false) ignores the
    /// request entirely.
    /// </remarks>
    public void RequestPlayMode(bool active)
    {
        Interlocked.Exchange(ref _playModeRequest, active ? 1 : 0);
        MarkDirty();
    }

    /// <summary>
    /// Asks the engine to turn the given debug visualisations on or off.
    /// Idempotent, and safe from any thread; flags accumulate until the render
    /// loop applies them, with the newest request winning where two conflict.
    /// </summary>
    /// <remarks>
    /// Set semantics rather than toggle, because the caller is a checkbox
    /// reading <see cref="FrameSnapshot.DebugFlags"/>: a toggle verb sent
    /// against a snapshot one publish stale flips the wrong way exactly when
    /// the user clicks fastest.
    /// </remarks>
    public void RequestDebugVisualization(DebugVisualization flags, bool enabled)
    {
        int bits = (int)flags;
        if (enabled)
        {
            Interlocked.Or(ref _debugFlagsToSet, bits);
            Interlocked.And(ref _debugFlagsToClear, ~bits);
        }
        else
        {
            Interlocked.Or(ref _debugFlagsToClear, bits);
            Interlocked.And(ref _debugFlagsToSet, ~bits);
        }

        MarkDirty();
    }

    /// <summary>
    /// Asks the engine to switch to the named rendering pipeline. A name the
    /// backend does not offer is a logged warning and no change, exactly as
    /// the startup switch behaves. Safe from any thread.
    /// </summary>
    public void RequestPipeline(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Interlocked.Exchange(ref _pipelineRequest, name);
        MarkDirty();
    }

    /// <summary>
    /// Takes the pending play-mode request, if any. Render thread only, called
    /// once per frame at the site the play-mode key is read.
    /// </summary>
    internal bool TryTakePlayModeRequest(out bool enter)
    {
        int request = Interlocked.Exchange(ref _playModeRequest, -1);
        enter = request == 1;
        return request >= 0;
    }

    /// <summary>
    /// Takes the accumulated debug-visualisation requests. Render thread only.
    /// The caller applies set before clear; the two are kept disjoint by
    /// <see cref="RequestDebugVisualization"/>, so the order only matters for
    /// a write that lands between the two exchanges, where clearing is the
    /// safe direction — a visualisation that fails to appear is one more
    /// click, a stuck one reads as a broken menu.
    /// </summary>
    internal void TakeDebugVisualizationRequests(out DebugVisualization set, out DebugVisualization clear)
    {
        set = (DebugVisualization)Interlocked.Exchange(ref _debugFlagsToSet, 0);
        clear = (DebugVisualization)Interlocked.Exchange(ref _debugFlagsToClear, 0);
    }

    /// <summary>Takes the pending pipeline request, or null. Render thread only.</summary>
    internal string? TakeRequestedPipeline() =>
        Interlocked.Exchange(ref _pipelineRequest, null);

    // --- Render-thread side --------------------------------------------------

    /// <summary>
    /// Runs every queued command against the active scene. Called on the render
    /// thread once per frame, before the static-world compile pump, so an edit
    /// and the recompile it causes land in the same frame.
    /// </summary>
    /// <remarks>
    /// Drains a bounded number per call so a flood of commands cannot starve
    /// rendering outright; the remainder run next frame.
    /// </remarks>
    public void DrainCommands(Scene.Scene? scene, int maxPerFrame = 256)
    {
        // Held, not dropped: see EnqueueCommand.
        if (scene is null)
            return;

        for (int i = 0; i < maxPerFrame && _commands.TryDequeue(out Action<Scene.Scene>? command); i++)
        {
            try
            {
                command(scene);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A host command threw; the frame continues");
            }
        }
    }

    /// <summary>
    /// Points the change log at the scene whose structure should be reported.
    /// Called on the render thread whenever the active scene changes.
    /// </summary>
    public void ObserveScene(Scene.Scene? scene) => _changeLog.Observe(scene);

    /// <summary>
    /// Publishes a snapshot if enough time has passed, or if there is structural
    /// news that should not wait. Called on the render thread at the end of a
    /// frame.
    /// </summary>
    /// <param name="elapsed">The engine's total elapsed time, for interval timing.</param>
    /// <param name="build">
    /// Produces the frame's values. Invoked only when a snapshot is actually
    /// going out, so a host that nobody is listening to pays almost nothing.
    /// </param>
    /// <param name="interactive">
    /// True while a gesture is in flight, which selects
    /// <see cref="InteractiveSnapshotInterval"/> instead of
    /// <see cref="SnapshotInterval"/>.
    /// </param>
    /// <returns>The snapshot that was published, or null when none was due.</returns>
    public FrameSnapshot? PublishFrame(
        TimeSpan elapsed,
        Func<FrameSnapshotBuilder, FrameSnapshot> build,
        bool interactive = false)
    {
        ArgumentNullException.ThrowIfNull(build);

        _frameNumber++;

        // Nobody listening and nobody polling anything but the last value: the
        // interval still governs, so LastSnapshot stays fresh for a shell that
        // attaches later without the engine paying per frame for one that never
        // does.
        // Nullable rather than a sentinel: a TimeSpan.MinValue sentinel makes
        // the very first subtraction overflow, which is a throw on frame one
        // rather than the "publish immediately" it looks like.
        TimeSpan interval = interactive ? InteractiveSnapshotInterval : SnapshotInterval;
        bool due = _lastPublished is not { } last || elapsed - last >= interval;

        // Structural news goes out on the next frame regardless of the clock: a
        // tree view lagging a third of a second behind a delete is exactly the
        // kind of thing that reads as a broken editor. So does state the user
        // just asked for - see EnqueueCommand.
        if (!due && !_stateDirty && _changeLog.Count == 0 && !_changeLog.Overflowed)
            return null;

        _lastPublished = elapsed;
        _stateDirty = false;

        (IReadOnlyList<SceneChange> changes, bool overflowed) = _changeLog.Drain();
        FrameSnapshot snapshot = build(new FrameSnapshotBuilder(_frameNumber, changes, overflowed));

        LastSnapshot = snapshot;
        FrameCompleted?.Invoke(snapshot);
        return snapshot;
    }

    private TimeSpan? _lastPublished;
}

/// <summary>
/// The parts of a <see cref="FrameSnapshot"/> the host already knows, handed to
/// the engine so it only has to add what it alone can read.
/// </summary>
/// <param name="FrameNumber">How many frames have completed.</param>
/// <param name="Changes">The structural changes since the previous snapshot.</param>
/// <param name="ChangesOverflowed">Whether that list is complete.</param>
public readonly record struct FrameSnapshotBuilder(
    long FrameNumber,
    IReadOnlyList<SceneChange> Changes,
    bool ChangesOverflowed);
