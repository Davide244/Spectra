using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Hosting;

/// <summary>
/// Accumulates the scene's structural events between snapshots, so a shell's
/// tree view can be updated incrementally instead of rebuilt.
/// </summary>
/// <remarks>
/// <b>A tree view that rebuilds itself every frame is not an option at this
/// engine's scale.</b> The measured ceiling is around 25,000 nodes; re-walking
/// that to refresh a panel would cost more than rendering the frame, and would
/// throw away every expansion and scroll position the user had. So the engine
/// reports what changed, and the shell applies it.
/// <para>
/// <b>Three events, not two.</b> Adds and removes come from the membership
/// events, but a reparent within one scene raises neither, because nothing
/// entered or left the graph. <c>Scene.NodeReparented</c> exists for exactly
/// that hole, and a log without it desynchronises silently the first time
/// somebody drags a node onto another in the very tree this feeds.
/// </para>
/// <para>
/// <b>Transform changes are deliberately NOT logged.</b> They fire for every
/// moved node every frame an animation runs, which at the engine's own demo
/// rate is hundreds a second, and a tree view does not show positions. An
/// inspector wants them, and wants the current value rather than the history,
/// so it reads the selection's values out of the snapshot instead.
/// </para>
/// <para>
/// <b>Threading:</b> the engine calls every member on the render thread, inside
/// the scene's own event dispatch. <see cref="Drain"/> hands ownership of the
/// batch across to whoever consumes the snapshot, and the log starts a fresh
/// one, so nothing is shared after the hand-off.
/// </para>
/// </remarks>
public sealed class SceneChangeLog
{
    /// <summary>
    /// How many changes may pile up before the log stops recording individual
    /// ones and reports a reset instead. A shell that has fallen this far
    /// behind is better served rebuilding than replaying.
    /// </summary>
    public const int DefaultCapacity = 4096;

    private readonly int _capacity;
    private List<SceneChange> _changes = [];
    private Scene.Scene? _scene;

    /// <summary>Creates a log with the default capacity.</summary>
    public SceneChangeLog()
        : this(DefaultCapacity)
    {
    }

    /// <summary>Creates a log that overflows after <paramref name="capacity"/> changes.</summary>
    public SceneChangeLog(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>
    /// True when more changes arrived than the log could hold, so the batch is
    /// incomplete and a consumer must rebuild rather than replay. Cleared by
    /// <see cref="Drain"/> along with the batch.
    /// </summary>
    /// <remarks>
    /// <b>Reported rather than silently truncated.</b> A tree view fed a partial
    /// log looks right and is wrong, which is far worse than one told to start
    /// over: the engine's standing rule is that nothing degrades silently.
    /// </remarks>
    public bool Overflowed { get; private set; }

    /// <summary>How many changes are waiting in the current batch.</summary>
    public int Count => _changes.Count;

    /// <summary>
    /// Starts recording <paramref name="scene"/>'s structural events, detaching
    /// from whichever scene was previously observed. Passing null detaches.
    /// </summary>
    /// <remarks>
    /// A scene swap is reported as an overflow rather than as a list of removes
    /// and adds: the shell's view of the old graph is worthless either way, and
    /// enumerating a whole scene into a change log to say so would be the
    /// rebuild it is trying to avoid, done twice.
    /// </remarks>
    public void Observe(Scene.Scene? scene)
    {
        if (ReferenceEquals(_scene, scene))
            return;

        if (_scene is { } previous)
        {
            previous.NodeAdded -= OnNodeAdded;
            previous.NodeRemoved -= OnNodeRemoved;
            previous.NodeReparented -= OnNodeReparented;
        }

        _scene = scene;

        if (scene is not null)
        {
            scene.NodeAdded += OnNodeAdded;
            scene.NodeRemoved += OnNodeRemoved;
            scene.NodeReparented += OnNodeReparented;
        }

        // Whatever was queued described a graph that is no longer on screen.
        _changes.Clear();
        Overflowed = true;
    }

    /// <summary>
    /// Hands the accumulated batch to the caller and starts a fresh one,
    /// reporting whether the batch is complete. Returns an empty list and false
    /// when nothing happened, which is the common case.
    /// </summary>
    public (IReadOnlyList<SceneChange> Changes, bool Overflowed) Drain()
    {
        bool overflowed = Overflowed;
        Overflowed = false;

        if (_changes.Count == 0)
            return (Array.Empty<SceneChange>(), overflowed);

        // Hand the list over rather than copying it: the consumer holds it as
        // an immutable batch, and the log allocates the next one. Reusing a
        // single list would mean the snapshot's contents changed underneath a
        // UI thread that had not read it yet.
        List<SceneChange> batch = _changes;
        _changes = new List<SceneChange>(Math.Min(batch.Count, _capacity));
        return (batch, overflowed);
    }

    private void OnNodeAdded(SceneNode node) => Record(SceneChangeKind.Added, node);

    private void OnNodeRemoved(SceneNode node) => Record(SceneChangeKind.Removed, node);

    private void OnNodeReparented(SceneNode node) => Record(SceneChangeKind.Reparented, node);

    private void Record(SceneChangeKind kind, SceneNode node)
    {
        if (_changes.Count >= _capacity)
        {
            // Stop recording rather than grow without bound: a shell this far
            // behind is going to rebuild anyway, and an unbounded list on the
            // render thread is how a stalled UI thread becomes an engine leak.
            Overflowed = true;
            return;
        }

        _changes.Add(new SceneChange(
            kind,
            node.Id,
            node.Parent?.Id ?? Guid.Empty,
            node.Name,
            node.IndexInParent));
    }
}
