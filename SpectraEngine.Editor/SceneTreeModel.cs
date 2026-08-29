using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SpectraEngine.Editor.Shell;

namespace SpectraEngine.Editor;

/// <summary>Where a drag hovering over a row would drop, for the indicator.</summary>
public enum SceneTreeDropZone
{
    /// <summary>Not a drop target right now.</summary>
    None,

    /// <summary>Insert as the row's earlier sibling.</summary>
    Before,

    /// <summary>Reparent into the row's node.</summary>
    Into,

    /// <summary>Insert as the row's later sibling.</summary>
    After,
}

/// <summary>How a node stands against the tree's current filter.</summary>
public enum SceneTreeMatch
{
    /// <summary>The node itself matches, or there is no filter.</summary>
    Match,

    /// <summary>The node does not match but something under it does.</summary>
    Ancestor,

    /// <summary>Neither the node nor anything under it matches.</summary>
    None,
}

/// <summary>One node in the shell's tree view.</summary>
/// <remarks>
/// A value copied out of the graph, never a live <see cref="SceneNode"/>: the
/// real node belongs to the render thread and is mutated again the instant the
/// frame ends.
/// <para>
/// <b>It raises change notification, and that is a fix rather than a
/// feature.</b> Without it a binding reads each property exactly once, so the
/// two paths that legitimately rewrite a node in place — a reparent, and a
/// re-add under the same id when a delete is undone — left the tree showing a
/// name that was no longer the node's. Nothing reported it, because the tree
/// was structurally correct and only the text was stale.
/// </para>
/// </remarks>
public sealed class SceneTreeNode(Guid id, string name) : ObservableObject
{
    private string _name = name;
    private bool _isSelected;
    private bool _isExpanded;
    private bool _hasChildren;
    private int _depth;
    private SceneNodeKind _kind;
    private SceneTreeMatch _match = SceneTreeMatch.Match;

    /// <summary>The node's stable identity, and how the engine is addressed about it.</summary>
    public Guid Id { get; } = id;

    /// <summary>The node's name at the last change that mentioned it.</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>What the node is, for the row's icon.</summary>
    public SceneNodeKind Kind
    {
        get => _kind;
        set => Set(ref _kind, value);
    }

    /// <summary>The node's children, in the graph's own sibling order.</summary>
    public ObservableCollection<SceneTreeNode> Children { get; } = [];

    /// <summary>Whether the engine reports this node as selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>How this node stands against the filter.</summary>
    public SceneTreeMatch Match
    {
        get => _match;
        set
        {
            if (!Set(ref _match, value))
                return;

            Raise(nameof(IsDimmed));
            Raise(nameof(IsContext));
        }
    }

    /// <summary>
    /// Whether this node's children are shown. Bound two-way to the row's
    /// expander, so the user and the engine write the same flag.
    /// </summary>
    /// <remarks>
    /// <b>It lives on the model rather than on the container so that revealing
    /// a node is possible at all.</b> A container that does not exist yet has
    /// no <c>IsExpanded</c> to set, and the containers under a collapsed parent
    /// are exactly the ones that do not exist; expanding the chain has to be
    /// something the data can express before any of it is realised.
    /// </remarks>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>
    /// How deep this node sits, with a top-level node at zero. The row's indent.
    /// </summary>
    /// <remarks>
    /// <b>Carried on the node because the flat row list has no nesting left to
    /// read it from.</b> Virtualizing a tree means handing the panel a list of
    /// visible rows, and a list cannot say how far in a row belongs; the depth
    /// travels with the row instead. It is observable because a reparent
    /// changes it.
    /// </remarks>
    public int Depth
    {
        get => _depth;
        internal set => Set(ref _depth, value);
    }

    /// <summary>Whether this node has children, and therefore an expander.</summary>
    /// <remarks>
    /// Observable because a group emptied by a delete stops having one, and a
    /// chevron on a node with nothing under it is a control that does nothing.
    /// </remarks>
    public bool HasChildren
    {
        get => _hasChildren;
        internal set => Set(ref _hasChildren, value);
    }

    /// <summary>
    /// Whether the row is showing its in-place rename editor. Pure view state:
    /// it lives on the node because virtualization recycles containers, so a
    /// flag on the container would follow the recycling rather than the row.
    /// The panel keeps at most one node renaming at a time.
    /// </summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => Set(ref _isRenaming, value);
    }

    private bool _isRenaming;

    /// <summary>
    /// Where a drag hovering this row would drop. View state like
    /// <see cref="IsRenaming"/>; the panel keeps at most one row indicating.
    /// Raises the three class-bound booleans below.
    /// </summary>
    public SceneTreeDropZone DropZone
    {
        get => _dropZone;
        set
        {
            if (Set(ref _dropZone, value))
            {
                Raise(nameof(IsDropBefore));
                Raise(nameof(IsDropInto));
                Raise(nameof(IsDropAfter));
            }
        }
    }

    private SceneTreeDropZone _dropZone;

    /// <summary>Style-class views of <see cref="DropZone"/>.</summary>
    public bool IsDropBefore => _dropZone == SceneTreeDropZone.Before;

    /// <inheritdoc cref="IsDropBefore"/>
    public bool IsDropInto => _dropZone == SceneTreeDropZone.Into;

    /// <inheritdoc cref="IsDropBefore"/>
    public bool IsDropAfter => _dropZone == SceneTreeDropZone.After;

    /// <summary>Whether the filter excludes this node. Bound as a style class.</summary>
    public bool IsDimmed => _match == SceneTreeMatch.None;

    /// <summary>Whether this node is only present to hold a match below it.</summary>
    public bool IsContext => _match == SceneTreeMatch.Ancestor;

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

    // The ids currently flagged selected, and the scratch set the next
    // selection is read into. Swapped rather than copied.
    private HashSet<Guid> _selectedIds = [];
    private HashSet<Guid> _incoming = [];

    // The live filter. Empty means everything matches.
    private string _filter = string.Empty;

    // Scratch for the flat projection: the list being computed, and its
    // membership. Reused so a rebuild allocates nothing.
    private readonly List<SceneTreeNode> _desired = [];
    private readonly HashSet<SceneTreeNode> _desiredSet = [];

    // Child to parent, maintained at the two sites that can change parentage
    // (Insert and RemoveFromParent) rather than derived on demand.
    //
    // It earns its keep three times over: detaching a node was a walk of the
    // whole index, the filter needs the chain above every match, and revealing
    // a node picked in the viewport needs the chain above THAT. A tree stores
    // parentage implicitly in each node's Children, which is fine to read one
    // way and hopeless to read the other.
    private readonly Dictionary<SceneTreeNode, SceneTreeNode> _parents = [];

    /// <summary>Creates a tree fed by <paramref name="host"/>.</summary>
    public SceneTreeModel(EngineHost host, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        _host = host;
        _logger = logger;
    }

    /// <summary>The top-level nodes, which for a live scene is its root.</summary>
    /// <remarks>
    /// The structure. What the panel actually binds to is <see cref="Rows"/>;
    /// this stays the model of the graph that the flat projection is computed
    /// from, and is what every structural operation edits.
    /// </remarks>
    public ObservableCollection<SceneTreeNode> Roots { get; } = [];

    /// <summary>
    /// The currently visible rows, flattened in display order with each node's
    /// depth recorded on it. <b>This is what a virtualizing list binds to.</b>
    /// </summary>
    /// <remarks>
    /// <b>A tree control that keeps its own hierarchy realises a container per
    /// node, which is the wall a scene of any size hits.</b> The demo alone is
    /// 257 nodes; the engine's own documented ceiling is 25,000, and at that
    /// size a panel showing thirty-five rows was building twenty-five thousand
    /// of them. Flattening to only what is visible is what lets a panel realise
    /// the thirty-five it can actually show.
    /// <para>
    /// <b>It is patched, never rebuilt.</b> Replacing the collection resets the
    /// scroll position and the selection, so expanding one group would throw the
    /// user back to the top of the scene. The recompute produces a desired list
    /// and then inserts and removes the difference, which for the common cases
    /// (expand a group, collapse it, add a node) is one contiguous run.
    /// </para>
    /// </remarks>
    public ObservableCollection<SceneTreeNode> Rows { get; } = [];

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

        if (snapshot.Changes.Count == 0)
            return;

        // A node added under a live filter starts out matching, because that is
        // the only sane default for an unfiltered tree. Re-running the filter is
        // what stops a duplicate appearing at full strength beside the dimmed
        // row it was copied from.
        if (_filter.Length > 0)
            ApplyFilter(_filter);

        // Once per batch, not once per change: a hundred adds in one snapshot
        // are one recompute.
        RebuildRows();
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

    /// <summary>
    /// Expands whatever is needed for the node with this id to be visible, and
    /// hands back the node so a caller can scroll to it. Returns false when the
    /// tree has never heard of the id.
    /// </summary>
    /// <remarks>
    /// <b>Expanding is all this does.</b> It never collapses anything, because
    /// the expansion set is the user's, and a reveal that tidied the tree on
    /// its way past would undo their work every time they clicked something in
    /// the viewport.
    /// <para>
    /// <b>The node itself is deliberately not expanded either.</b> Picking a
    /// group in the viewport means "show me this", not "show me everything
    /// inside it", and a group with two hundred children would push its own row
    /// off the screen it was just scrolled onto.
    /// </para>
    /// <para>
    /// <b>An id the tree does not have is ordinary.</b> A viewport selection
    /// can name a node whose Added change has not been drained yet, which is a
    /// frame of ordinary lag rather than a fault; the reveal is simply skipped
    /// and the next selection snapshot retries it.
    /// </para>
    /// </remarks>
    public bool TryReveal(Guid nodeId, out SceneTreeNode node)
    {
        if (!_index.TryGetValue(nodeId, out SceneTreeNode? found))
        {
            node = null!;
            return false;
        }

        node = found;

        bool opened = false;
        SceneTreeNode current = found;
        while (_parents.TryGetValue(current, out SceneTreeNode? parent))
        {
            opened |= !parent.IsExpanded;
            parent.IsExpanded = true;
            current = parent;
        }

        if (opened)
            RebuildRows();

        return true;
    }

    /// <summary>
    /// Opens or closes a node's children. UI thread; what the row's expander
    /// calls.
    /// </summary>
    /// <remarks>
    /// Expansion goes through the model rather than being a property the view
    /// sets, because it is the input to the flat projection: a chevron that
    /// wrote the flag directly would leave <see cref="Rows"/> describing a tree
    /// nobody has any more.
    /// </remarks>
    public void ToggleExpanded(SceneTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.HasChildren)
            return;

        node.IsExpanded = !node.IsExpanded;
        RebuildRows();
    }

    /// <summary>
    /// Resolves an id to its row object, for the panel's gesture arithmetic.
    /// Returns false for an id the tree has never heard of.
    /// </summary>
    public bool TryGetNode(Guid nodeId, out SceneTreeNode node)
    {
        if (_index.TryGetValue(nodeId, out SceneTreeNode? found))
        {
            node = found;
            return true;
        }

        node = null!;
        return false;
    }

    /// <summary>
    /// Whether a node currently has a visible row (no collapsed ancestor).
    /// </summary>
    public bool IsRowVisible(SceneTreeNode node) => _desiredSet.Contains(node);

    /// <summary>
    /// The node's parent in the mirrored hierarchy, or null for a top-level
    /// row. What the panel's drop arithmetic walks: "before this row" means an
    /// index under this row's parent, and a drop that would land inside the
    /// dragged subtree is found by walking up from the target.
    /// </summary>
    public SceneTreeNode? ParentOf(SceneTreeNode node) =>
        _parents.TryGetValue(node, out SceneTreeNode? parent) ? parent : null;

    /// <summary>
    /// Collects the engine-selected ids whose rows are currently hidden under a
    /// collapsed parent. A Ctrl-click in the list can only report the rows the
    /// list can see, so an additive gesture unions these back in — without
    /// that, extending a selection would silently deselect everything folded
    /// away.
    /// </summary>
    public void CollectHiddenSelected(List<Guid> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        foreach (Guid id in _selectedIds)
        {
            if (_index.TryGetValue(id, out SceneTreeNode? node) && !_desiredSet.Contains(node))
                into.Add(id);
        }
    }

    /// <summary>
    /// Expands or collapses a node and everything under it, recomputing the
    /// rows once — the context menu's "expand all" / "collapse all". UI thread.
    /// </summary>
    public void SetSubtreeExpanded(SceneTreeNode node, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(node);

        SetExpandedRecursive(node, expanded);
        RebuildRows();
    }

    private static void SetExpandedRecursive(SceneTreeNode node, bool expanded)
    {
        if (node.Children.Count > 0)
            node.IsExpanded = expanded;

        for (int i = 0; i < node.Children.Count; i++)
            SetExpandedRecursive(node.Children[i], expanded);
    }

    /// <summary>Recomputes the visible rows and patches the difference in.</summary>
    private void RebuildRows()
    {
        _desired.Clear();
        for (int i = 0; i < Roots.Count; i++)
            Flatten(Roots[i], 0);

        SyncRows();
    }

    private void Flatten(SceneTreeNode node, int depth)
    {
        node.Depth = depth;
        node.HasChildren = node.Children.Count > 0;
        _desired.Add(node);

        if (!node.IsExpanded)
            return;

        for (int i = 0; i < node.Children.Count; i++)
            Flatten(node.Children[i], depth + 1);
    }

    // Walks the two lists together, removing what is gone and inserting what is
    // new. Every common case (expand, collapse, add, delete) is one contiguous
    // run, so this issues one notification per row that genuinely moved rather
    // than one per row in the tree.
    private void SyncRows()
    {
        _desiredSet.Clear();
        for (int i = 0; i < _desired.Count; i++)
            _desiredSet.Add(_desired[i]);

        int index = 0;
        while (index < _desired.Count)
        {
            if (index >= Rows.Count)
            {
                Rows.Add(_desired[index]);
                index++;
                continue;
            }

            if (ReferenceEquals(Rows[index], _desired[index]))
            {
                index++;
                continue;
            }

            // A row that is not wanted anywhere any more: drop it and look at
            // whatever slid into its place, without advancing.
            if (!_desiredSet.Contains(Rows[index]))
            {
                Rows.RemoveAt(index);
                continue;
            }

            Rows.Insert(index, _desired[index]);
            index++;
        }

        while (Rows.Count > _desired.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }

    /// <summary>How many nodes pass the current filter. Equals <see cref="Count"/> when there is none.</summary>
    public int MatchCount { get; private set; }

    /// <summary>
    /// Narrows the tree to nodes whose name contains <paramref name="text"/>,
    /// or to a kind with a <c>t:</c> prefix (<c>t:light</c>, <c>t:part</c>).
    /// Empty text clears the filter. UI thread.
    /// </summary>
    /// <remarks>
    /// <b>Non-matching rows are DIMMED, not removed</b>, and that is a
    /// deliberate choice rather than a shortcut. Hiding rows collapses the
    /// hierarchy around every match, so the one thing a user has after two
    /// hundred nodes — a spatial memory of where things live — is destroyed on
    /// the first keystroke and rebuilt differently on the second. Dimming keeps
    /// the shape still and lets the eye do the work.
    /// <para>
    /// <b>Nothing is rebuilt.</b> The filter walks the flat index setting a
    /// per-node flag; <see cref="Roots"/> and every <c>Children</c> collection
    /// are untouched. Rebuilding an observable collection on each keystroke is
    /// the documented way to make a tree this size stutter, and it would also
    /// discard the user's expansion state while they typed.
    /// </para>
    /// </remarks>
    public void ApplyFilter(string? text)
    {
        string query = (text ?? string.Empty).Trim();
        _filter = query;

        if (query.Length == 0)
        {
            foreach (SceneTreeNode node in _index.Values)
                node.Match = SceneTreeMatch.Match;
            MatchCount = _index.Count;
            return;
        }

        SceneNodeKind? kindQuery = null;
        if (query.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
        {
            kindQuery = ParseKind(query[2..]);
            query = string.Empty;
        }

        int matches = 0;
        foreach (SceneTreeNode node in _index.Values)
        {
            bool hit = kindQuery is { } wanted
                ? node.Kind == wanted
                : node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

            node.Match = hit ? SceneTreeMatch.Match : SceneTreeMatch.None;
            if (hit)
                matches++;
        }

        MatchCount = matches;

        // A match buried three levels down is invisible if its parents are
        // dimmed to nothing, so the chain above every hit is promoted to
        // context: present, legible, and visibly not itself a result.
        //
        // The parent map is maintained rather than searched: walking every
        // node's Children per ancestor per hit is the product of two large
        // numbers on a keystroke.
        foreach (SceneTreeNode node in _index.Values)
        {
            if (node.Match != SceneTreeMatch.Match)
                continue;

            SceneTreeNode current = node;
            while (_parents.TryGetValue(current, out SceneTreeNode? parent))
            {
                // Somebody else already promoted this chain, so the rest of it
                // is done too.
                if (parent.Match != SceneTreeMatch.None)
                    break;

                parent.Match = SceneTreeMatch.Ancestor;
                current = parent;
            }
        }
    }

    // A kind name, matched loosely enough that "light", "Light" and "lights"
    // all work: a filter that silently returns nothing because the user typed a
    // plural is worse than no filter.
    private static SceneNodeKind? ParseKind(string text)
    {
        string wanted = text.Trim().TrimEnd('s');
        foreach (SceneNodeKind kind in Enum.GetValues<SceneNodeKind>())
        {
            if (kind.ToString().Contains(wanted, StringComparison.OrdinalIgnoreCase))
                return kind;
        }

        return null;
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
                    moved.Kind = change.NodeKind;
                }
                else
                {
                    Attach(change);
                }
                break;

            case SceneChangeKind.Renamed:
                // In-place: same node object, same position, new name. The row
                // updates through the node's change notification; nothing
                // structural moves, so no detach/insert.
                if (_index.TryGetValue(change.NodeId, out SceneTreeNode? renamed))
                {
                    renamed.Name = change.Name;
                    renamed.Kind = change.NodeKind;
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
            existing.Kind = change.NodeKind;
            RemoveFromParent(existing);
            Insert(existing, change.ParentId, change.SiblingIndex);
            return;
        }

        var node = new SceneTreeNode(change.NodeId, change.Name) { Kind = change.NodeKind };
        _index[change.NodeId] = node;
        Insert(node, change.ParentId, change.SiblingIndex);
    }

    private void Insert(SceneTreeNode node, Guid parentId, int siblingIndex)
    {
        SceneTreeNode? parent = null;
        bool hasParent = parentId != Guid.Empty && _index.TryGetValue(parentId, out parent);
        ObservableCollection<SceneTreeNode> siblings = hasParent ? parent!.Children : Roots;

        // Clamped rather than trusted: a parent's add can legitimately arrive
        // in the same batch as a child's, and a child reported at index 3 of a
        // list the tree has only two of is a batch mid-replay, not a bug.
        int index = siblingIndex < 0 || siblingIndex > siblings.Count ? siblings.Count : siblingIndex;
        siblings.Insert(index, node);

        if (hasParent) _parents[node] = parent!;
        else _parents.Remove(node);
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
        // Straight to the parent rather than a scan of every node in the tree.
        // The map is maintained at the two sites that can change parentage, so
        // it cannot disagree with the collections; before it existed, detaching
        // one node walked the whole index.
        if (_parents.TryGetValue(node, out SceneTreeNode? parent))
        {
            parent.Children.Remove(node);
            _parents.Remove(node);
            return;
        }

        Roots.Remove(node);
    }

    private void Forget(SceneTreeNode node)
    {
        _index.Remove(node.Id);
        _parents.Remove(node);
        for (int i = 0; i < node.Children.Count; i++)
            Forget(node.Children[i]);
        node.Children.Clear();
    }

    private void ApplySelectionCore(IReadOnlyList<Guid> selected)
    {
        // Only what CHANGED. A snapshot publishes about thirty times a second
        // and the selection is almost always identical to last time, so the
        // obvious "clear every flag, then set the selected ones" walks the
        // whole index twice per publish. Against this engine's own documented
        // ceiling of ~25,000 nodes that is a real UI-thread cost for nothing,
        // and it becomes a notification storm the moment the nodes start
        // raising property changes.
        _incoming.Clear();
        for (int i = 0; i < selected.Count; i++)
            _incoming.Add(selected[i]);

        foreach (Guid id in _selectedIds)
        {
            if (!_incoming.Contains(id) && _index.TryGetValue(id, out SceneTreeNode? gone))
                gone.IsSelected = false;
        }

        foreach (Guid id in _incoming)
        {
            if (!_selectedIds.Contains(id) && _index.TryGetValue(id, out SceneTreeNode? added))
                added.IsSelected = true;
        }

        (_selectedIds, _incoming) = (_incoming, _selectedIds);
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
                _parents.Clear();
                _selectedIds.Clear();

                for (int i = 0; i < flattened.Count; i++)
                    ApplyChange(flattened[i]);

                if (_filter.Length > 0)
                    ApplyFilter(_filter);

                RebuildRows();

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
            node.IndexInParent,
            SceneNodeClassifier.Classify(node)));

        IReadOnlyList<SceneNode> children = node.Children;
        for (int i = 0; i < children.Count; i++)
            Flatten(children[i], into);
    }
}
