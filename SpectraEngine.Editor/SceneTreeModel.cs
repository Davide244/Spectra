using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SpectraEngine.Editor;

/// <summary>One node in the shell's tree view.</summary>
/// <remarks>
/// A value copied out of the graph, never a live <see cref="SceneNode"/>: the
/// real node belongs to the render thread and is mutated again the instant the
/// frame ends.
/// </remarks>
public sealed class SceneTreeNode(Guid id, string name)
{
    /// <summary>The node's stable identity, and how the engine is addressed about it.</summary>
    public Guid Id { get; } = id;

    /// <summary>The node's name at the last change that mentioned it.</summary>
    public string Name { get; set; } = name;

    /// <summary>The node's children, in the graph's own sibling order.</summary>
    public ObservableCollection<SceneTreeNode> Children { get; } = [];

    /// <summary>Whether the engine reports this node as selected.</summary>
    public bool IsSelected { get; set; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// The shell's mirror of the scene graph, maintained from
/// <see cref="FrameSnapshot"/>s.
/// </summary>
/// <remarks>
/// <b>This is the whole reason <c>SceneChangeLog</c> exists, exercised.</b> The
/// engine reports what changed; the tree applies it. Rebuilding the panel every
/// frame would cost more than rendering the frame at the engine's own measured
/// ceiling of about 25,000 nodes, and would throw away every expansion and
/// scroll position the user had.
/// <para>
/// <b>An overflow is a rebuild, and the rebuild goes back through the
/// engine.</b> The log says "you have fallen behind, or the scene was swapped";
/// the only correct response is to ask the render thread for the whole graph,
/// which is a queued command like every other read of live state. The reply
/// arrives as a pre-order list of <see cref="SceneChangeKind.Added"/> changes,
/// deliberately the same shape the log itself emits, so there is one apply path
/// rather than two.
/// </para>
/// <para>
/// <b>Threading:</b> UI thread only. The rebuild's body runs on the render
/// thread and posts its result back, which is the one crossing here and it
/// carries nothing but ids, names and indices.
/// </para>
/// </remarks>
public sealed class SceneTreeModel
{
    private readonly EngineHost _host;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, SceneTreeNode> _index = [];

    // Set while a rebuild command is out, so a run of overflowed snapshots
    // queues one walk of the graph rather than one per frame.
    private bool _rebuildPending;

    // The last frame whose changes were applied, so a snapshot seen twice is
    // replayed zero times.
    private long _appliedFrame = -1;

    /// <summary>Creates a tree fed by <paramref name="host"/>.</summary>
    public SceneTreeModel(EngineHost host, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        _host = host;
        _logger = logger;
    }

    /// <summary>The top-level nodes, which for a live scene is its root.</summary>
    public ObservableCollection<SceneTreeNode> Roots { get; } = [];

    /// <summary>How many nodes the tree is showing.</summary>
    public int Count => _index.Count;

    /// <summary>
    /// Applies one snapshot's structural news. UI thread, and every published
    /// snapshot must be passed here exactly once.
    /// </summary>
    /// <remarks>
    /// <b>Skipping a snapshot loses its changes permanently.</b> The engine's
    /// guarantee is that every structural change rides the NEXT snapshot out,
    /// which makes each one a batch nobody else will ever repeat; a shell that
    /// keeps only the most recent one silently throws graph edits away and its
    /// tree drifts from the scene with nothing reporting it. That is what
    /// <c>MainWindow</c>'s snapshot queue exists for.
    /// </remarks>
    public void ApplyChanges(FrameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Belt and braces against the same snapshot arriving twice: replaying
        // a batch re-inserts each added node at the index it was reported at,
        // which is the wrong index once its siblings have moved.
        if (snapshot.FrameNumber == _appliedFrame)
            return;

        _appliedFrame = snapshot.FrameNumber;

        if (snapshot.ChangesOverflowed)
        {
            MarkStale();
            return;
        }

        for (int i = 0; i < snapshot.Changes.Count; i++)
            ApplyChange(snapshot.Changes[i]);
    }

    /// <summary>
    /// Marks the tree as no longer trustworthy and asks the engine for the whole
    /// graph. UI thread.
    /// </summary>
    /// <remarks>
    /// Called when the engine reports its own log overflowed, and when the shell
    /// itself could not keep up. Both mean the same thing to a view, and both
    /// have to say so rather than carry on looking plausible.
    /// </remarks>
    public void MarkStale() => RequestRebuild();

    /// <summary>Marks which nodes the engine reports as selected. UI thread.</summary>
    public void ApplySelection(IReadOnlyList<Guid> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ApplySelectionCore(selected);
    }

    private void ApplyChange(in SceneChange change)
    {
        switch (change.Kind)
        {
            case SceneChangeKind.Added:
                Attach(change);
                break;

            case SceneChangeKind.Removed:
                Detach(change.NodeId);
                break;

            case SceneChangeKind.Reparented:
                // A move, not a membership change: detach from wherever the
                // tree currently believes it is, then attach at the reported
                // parent and index. Same node object, so an expanded subtree
                // stays expanded through a drag.
                if (_index.TryGetValue(change.NodeId, out SceneTreeNode? moved))
                {
                    RemoveFromParent(moved);
                    Insert(moved, change.ParentId, change.SiblingIndex);
                    moved.Name = change.Name;
                }
                else
                {
                    Attach(change);
                }
                break;
        }
    }

    private void Attach(in SceneChange change)
    {
        if (_index.TryGetValue(change.NodeId, out SceneTreeNode? existing))
        {
            // A re-add of a node the tree already has: an undone delete puts
            // the node back under the same id, which is the point of commands
            // addressing nodes by id rather than by reference.
            existing.Name = change.Name;
            RemoveFromParent(existing);
            Insert(existing, change.ParentId, change.SiblingIndex);
            return;
        }

        var node = new SceneTreeNode(change.NodeId, change.Name);
        _index[change.NodeId] = node;
        Insert(node, change.ParentId, change.SiblingIndex);
    }

    private void Insert(SceneTreeNode node, Guid parentId, int siblingIndex)
    {
        ObservableCollection<SceneTreeNode> siblings =
            parentId != Guid.Empty && _index.TryGetValue(parentId, out SceneTreeNode? parent)
                ? parent.Children
                : Roots;

        // Clamped rather than trusted: a parent's add can legitimately arrive
        // in the same batch as a child's, and a child reported at index 3 of a
        // list the tree has only two of is a batch mid-replay, not a bug.
        int index = siblingIndex < 0 || siblingIndex > siblings.Count ? siblings.Count : siblingIndex;
        siblings.Insert(index, node);
    }

    private void Detach(Guid nodeId)
    {
        if (!_index.TryGetValue(nodeId, out SceneTreeNode? node))
            return;

        RemoveFromParent(node);
        Forget(node);
    }

    private void RemoveFromParent(SceneTreeNode node)
    {
        if (Roots.Remove(node))
            return;

        foreach (SceneTreeNode candidate in _index.Values)
        {
            if (candidate.Children.Remove(node))
                return;
        }
    }

    private void Forget(SceneTreeNode node)
    {
        _index.Remove(node.Id);
        for (int i = 0; i < node.Children.Count; i++)
            Forget(node.Children[i]);
        node.Children.Clear();
    }

    private void ApplySelectionCore(IReadOnlyList<Guid> selected)
    {
        foreach (SceneTreeNode node in _index.Values)
            node.IsSelected = false;

        for (int i = 0; i < selected.Count; i++)
        {
            if (_index.TryGetValue(selected[i], out SceneTreeNode? node))
                node.IsSelected = true;
        }
    }

    private void RequestRebuild()
    {
        if (_rebuildPending)
            return;

        _rebuildPending = true;
        _logger.LogDebug("Scene tree fell behind or the scene was swapped; asking for the whole graph");

        // Walked on the render thread, because that is the only thread allowed
        // to read a live node. What comes back is a flat pre-order list of
        // values, which is why holding it on the UI thread is safe.
        _host.EnqueueCommand(scene =>
        {
            var flattened = new List<SceneChange>();
            Flatten(scene.Root, flattened);

            Dispatcher.UIThread.Post(() =>
            {
                Roots.Clear();
                _index.Clear();

                for (int i = 0; i < flattened.Count; i++)
                    ApplyChange(flattened[i]);

                _rebuildPending = false;
                _logger.LogDebug("Scene tree rebuilt: {Count} node(s)", _index.Count);
            });
        });
    }

    /// <summary>
    /// Walks <paramref name="node"/> and its descendants in pre-order into the
    /// same <see cref="SceneChangeKind.Added"/> shape the engine's change log
    /// emits. <b>Render thread only</b>: it reads live nodes.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the order can be pinned without a
    /// dispatcher. Pre-order is not incidental: a child inserted before its
    /// parent exists lands at the top of the tree instead of under it.
    /// </remarks>
    internal static void Flatten(SceneNode node, List<SceneChange> into)
    {
        into.Add(new SceneChange(
            SceneChangeKind.Added,
            node.Id,
            node.Parent?.Id ?? Guid.Empty,
            node.Name,
            node.IndexInParent));

        IReadOnlyList<SceneNode> children = node.Children;
        for (int i = 0; i < children.Count; i++)
            Flatten(children[i], into);
    }
}
