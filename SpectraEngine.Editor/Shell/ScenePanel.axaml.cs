using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Editing.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The scene tree panel: filter, flat virtualized list, and the choreography
/// that keeps the list in step with the engine's selection.
/// </summary>
/// <remarks>
/// <b>Extracted from the window so a docking layout can move it.</b> The panel
/// owns everything about ITS controls — the selection-sync guards, the reveal
/// gates, the scroll arithmetic, the tree keyboard — and reaches outward only
/// through <see cref="NodeSelected"/> and the <see cref="ShellModel"/> it is
/// given as a DataContext. What stays with the window is the snapshot drain,
/// which must happen exactly once regardless of how many panels exist; the
/// window hands each drained batch to the tree model and each newest snapshot
/// to <see cref="SyncSelection"/> here.
/// </remarks>
public partial class ScenePanel : UserControl
{
    /// <summary>
    /// The user changed the tree's selection; the whole id set goes to the
    /// engine as one replace batch.
    /// </summary>
    public event Action<IReadOnlyList<Guid>>? SelectionRequested;

    /// <summary>An in-place rename was committed: id and the new name.</summary>
    public event Action<Guid, string>? RenameRequested;

    /// <summary>A tree key or context-menu verb that maps to a host command.</summary>
    public event Action<EditorHostCommand>? CommandRequested;

    /// <summary>Frame the selection in the viewport (double-click, F, menu).</summary>
    public event Action? FrameRequested;

    /// <summary>
    /// A drag-and-drop asked for a reparent: the dragged ids, the new parent,
    /// and the child index to insert at (-1 appends).
    /// </summary>
    public event Action<IReadOnlyList<Guid>, Guid, int>? ReparentRequested;

    /// <summary>Where the panel's own diagnostics go. Set by the host window.</summary>
    public ILogger? Logger { get; set; }

    // Guards tree -> engine -> tree. Without it a click sets the engine's
    // selection, the next snapshot writes it back into the tree, and the tree
    // reports that as a fresh user selection. The symptom is not a hang: it is
    // a selection that collapses to a single node, which reads like a broken
    // keyboard rather than a loop.
    private bool _syncingSelection;

    // The node the reveal last scrolled to, and the one the TREE last asked
    // for. Together they answer "did this selection come from the viewport, and
    // is it new?", which is the whole gate on scrolling the panel.
    private Guid _revealedId;
    private Guid _treeRequestedId;

    // The modifiers of the most recent gesture that could change the list's
    // selection. SelectionChanged itself carries none, and the difference
    // decides whether engine-selected nodes hidden under a collapsed parent
    // survive the change: a plain click means "the selection is now exactly
    // this", a Ctrl/Shift gesture means "extend", and dropping the hidden part
    // of a selection because the list cannot see it would be silent data loss.
    private KeyModifiers _gestureModifiers;

    // The row being renamed in place, if any. At most one; view state only.
    private SceneTreeNode? _renaming;

    // Drag state: the in-process payload format, the movement that separates a
    // click from a drag, and the rows involved on either end. The press args
    // are kept because Avalonia 12's DoDragDropAsync wants the PRESS that
    // started the gesture, while the threshold is only crossed during a move.
    private static readonly DataFormat<Guid[]> DragFormat =
        DataFormat.CreateInProcessFormat<Guid[]>("spectra-scene-nodes");

    private const double DragThresholdPixels = 4.0;
    private SceneTreeNode? _pressedRow;
    private PointerPressedEventArgs? _pressEvent;
    private Point _pressPoint;
    private bool _dragInProgress;
    private SceneTreeNode? _deferredCollapse;
    private SceneTreeNode? _dropRow;

    // Scratch collections reused per sync, because this runs at the snapshot
    // rate against a selection that is usually unchanged.
    private readonly HashSet<SceneTreeNode> _listSelectionScratch = [];
    private readonly List<SceneTreeNode> _desiredListSelection = [];

    public ScenePanel()
    {
        InitializeComponent();

        // TUNNEL, not the bubbling handler XAML would attach. ListBox handles
        // every arrow key itself: on a vertical panel a Left or Right press
        // still runs its selection move, which re-selects the row it is already
        // on, returns true, and marks the event handled. A bubbling handler for
        // the tree's own collapse/expand would therefore never run at all.
        SceneTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);

        // Tunnel as well: this observes the gesture (its modifiers, which row
        // a right-press landed on, whether a drag might start) before the list
        // runs its own selection logic and before a context menu opens. It
        // claims the press in exactly one case, the deferred multi-selection
        // collapse.
        SceneTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        SceneTree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        SceneTree.AddHandler(PointerReleasedEvent, OnTreePointerReleased, RoutingStrategies.Tunnel);

        // The drop side: rows accept sibling and reparent drops, with the
        // indicator drawn from model state like every other row visual.
        DragDrop.SetAllowDrop(SceneTree, true);
        SceneTree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        SceneTree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        SceneTree.AddHandler(DragDrop.DragLeaveEvent, OnTreeDragLeave);
    }

    private ShellModel? Model => DataContext as ShellModel;

    /// <summary>Whether the filter box has keyboard focus, for the reveal gate.</summary>
    public bool IsFilterFocused => FilterBox.IsFocused;

    /// <summary>
    /// Forgets which selection was last revealed and which row the tree last
    /// asked for. Called when a session ends, so the next session's first pick
    /// is revealed rather than mistaken for an echo.
    /// </summary>
    public void ResetSelectionMemory()
    {
        _revealedId = Guid.Empty;
        _treeRequestedId = Guid.Empty;
        CancelRename();
    }

    /// <summary>
    /// Applies the engine's reported selection to the tree and reveals it.
    /// Called by the window's pump with the NEWEST snapshot only — selection
    /// is a state, not a history.
    /// </summary>
    public void SyncSelection(FrameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (Model?.Tree is not { } tree)
            return;

        _syncingSelection = true;
        tree.ApplySelection(snapshot.SelectedIds);
        SyncListSelection(tree, snapshot.SelectedIds);
        _syncingSelection = false;

        RevealSelection(tree, snapshot.SelectedIds);
    }

    /// <summary>
    /// Reconciles the ListBox's own selection with the engine's. The row
    /// highlight comes from model flags, but the LIST's selection is the input
    /// to the next Ctrl/Shift gesture, and left stale it makes that gesture
    /// compute against a selection nobody has any more (a viewport pick
    /// followed by a Ctrl-click would drop the picked node silently).
    /// </summary>
    private void SyncListSelection(SceneTreeModel tree, IReadOnlyList<Guid> selected)
    {
        if (SceneTree.SelectedItems is not { } items)
            return;

        _desiredListSelection.Clear();
        for (int i = 0; i < selected.Count; i++)
        {
            // Only rows the list can see: an item outside ItemsSource cannot
            // be selected, and hidden nodes keep their model flag instead.
            if (tree.TryGetNode(selected[i], out SceneTreeNode node) && tree.IsRowVisible(node))
                _desiredListSelection.Add(node);
        }

        // Usually identical to last time; compare before mutating, because
        // clearing and re-adding fires selection-changed churn per row.
        if (items.Count == _desiredListSelection.Count)
        {
            _listSelectionScratch.Clear();
            foreach (object? item in items)
            {
                if (item is SceneTreeNode node)
                    _listSelectionScratch.Add(node);
            }

            bool same = true;
            for (int i = 0; i < _desiredListSelection.Count && same; i++)
                same = _listSelectionScratch.Contains(_desiredListSelection[i]);

            if (same)
                return;
        }

        items.Clear();
        for (int i = 0; i < _desiredListSelection.Count; i++)
            items.Add(_desiredListSelection[i]);
    }

    /// <summary>
    /// Scrolls the tree to whatever was just picked in the viewport, expanding
    /// the collapsed parents in its way.
    /// </summary>
    /// <remarks>
    /// <b>Three gates, and each of them is a way this feature becomes
    /// annoying.</b> It reveals only when the selection actually CHANGED, or
    /// every pump tick would re-scroll a panel the user is trying to browse.
    /// It reveals only when the change did not come from the tree itself, since
    /// a row somebody just clicked is already on screen and yanking the
    /// viewport under them is pure noise. And it stands down while the filter
    /// box has focus, because scrolling the list out from under someone
    /// mid-search is the single most-complained-about behaviour in editors that
    /// ship this.
    /// <para>
    /// <b>The LAST id, not the first.</b> They arrive in selection order, so
    /// the last is the most recently added and the one the user just acted on;
    /// revealing the first would mean a marquee over fifty objects scrolls to
    /// whichever happened to be picked up earliest.
    /// </para>
    /// </remarks>
    private void RevealSelection(SceneTreeModel tree, IReadOnlyList<Guid> selected)
    {
        if (selected.Count == 0)
        {
            _revealedId = Guid.Empty;
            return;
        }

        Guid target = selected[^1];
        if (target == _revealedId)
            return;

        _revealedId = target;

        // The tree already knows about this one: it is the echo of a row the
        // user clicked, coming back a frame later.
        if (target == _treeRequestedId)
            return;

        if (FilterBox.IsFocused)
            return;

        if (!tree.TryReveal(target, out SceneTreeNode node))
        {
            // Not in the tree yet, so nothing to scroll to. Forgetting that we
            // "revealed" it lets the next tick try again once its Added change
            // has drained.
            _revealedId = Guid.Empty;
            return;
        }

        // Posted rather than done here: expanding a parent is a change to the
        // MODEL, and the rows it brings into existence have no extent until the
        // layout pass that follows — an offset written now is computed against
        // an estimate the panel has not finished. The list's own selection is
        // deliberately NOT touched: under multi-select, assigning SelectedItem
        // would collapse a marquee's fifty rows to one.
        Dispatcher.UIThread.Post(() =>
        {
            if (Model?.Tree is not { } current || !ReferenceEquals(current, tree))
                return;

            ScrollWithContext(tree, node);
        }, DispatcherPriority.Loaded);
    }

    // Where a revealed row should sit in the panel, as a fraction of the way
    // down it. A third leaves roughly twice as much hierarchy visible below the
    // node as above, which is the direction a tree is usually read.
    private const double RevealRestingFraction = 1.0 / 3.0;

    /// <summary>
    /// Places a revealed row a third of the way down the panel instead of flush
    /// against whichever edge it was scrolled past.
    /// </summary>
    /// <remarks>
    /// <b>Minimal scrolling is technically "in view" and practically
    /// useless.</b> What a user wants after picking an object is to see what is
    /// AROUND it in the hierarchy, and a row on the last pixel of the panel has
    /// neighbours on one side only.
    /// <para>
    /// <b>The position is computed from the row's INDEX, not from its
    /// container.</b> Under virtualization a container exists only if the row
    /// is already on screen, which is precisely not the case when something
    /// needs revealing; a flat list of uniform rows makes the arithmetic exact
    /// without one. The row height comes from the scroller's own extent divided
    /// by the row count, so it stays right if the row height ever changes.
    /// </para>
    /// <para>
    /// <b>Setting the offset directly is not fussiness either.</b> The tidy
    /// alternative, asking for a deliberately oversized <c>BringIntoView</c>
    /// rect, does nothing: the rect is clamped to the control. Measured, with
    /// the row landing on the top edge one time and the bottom the next.
    /// </para>
    /// </remarks>
    private void ScrollWithContext(SceneTreeModel tree, SceneTreeNode node)
    {
        int index = tree.Rows.IndexOf(node);
        if (index < 0 || tree.Rows.Count == 0)
            return;

        // The list exposes its own scroller: a public property bound to the
        // template's PART_ScrollViewer. Walking the visual tree for one works
        // and is a guess about somebody else's template.
        if (SceneTree.Scroll is not { } scroller)
            return;

        double rowHeight = scroller.Extent.Height / tree.Rows.Count;
        if (rowHeight <= 0)
            return;

        double resting = (scroller.Viewport.Height - rowHeight) * RevealRestingFraction;
        double target = Math.Clamp(
            (index * rowHeight) - resting,
            0,
            Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));

        scroller.Offset = scroller.Offset.WithY(target);
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The pump is writing the model's selection flags right now; what the
        // control is reporting is the engine's own answer coming back, not a
        // user's click.
        if (_syncingSelection)
            return;

        var ids = new List<Guid>();
        if (SceneTree.SelectedItems is { } items)
        {
            foreach (object? item in items)
            {
                if (item is SceneTreeNode node)
                    ids.Add(node.Id);
            }
        }

        // The list can only report rows it can SEE. Under an additive gesture
        // (Ctrl or Shift held), engine-selected nodes folded away under a
        // collapsed parent are still part of what the user means, so they are
        // unioned back in; a plain click really does mean "exactly this".
        if ((_gestureModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0)
            Model?.Tree?.CollectHiddenSelected(ids);

        // Remembered so the echo of this selection, arriving from the engine a
        // frame later, does not scroll the panel to a row that is already under
        // the user's cursor.
        _treeRequestedId = ids.Count > 0 ? ids[^1] : Guid.Empty;
        SelectionRequested?.Invoke(ids);
    }

    /// <summary>
    /// Observes each press before the list acts on it: records the gesture's
    /// modifiers, arms a possible drag, and gives a right-press the
    /// select-before-menu behaviour every editor shares (an unselected row
    /// becomes the selection; a selected one keeps the whole set for the menu
    /// to act on).
    /// </summary>
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _gestureModifiers = e.KeyModifiers;
        PointerPointProperties props = e.GetCurrentPoint(SceneTree).Properties;

        if (props.IsRightButtonPressed)
        {
            if (RowNodeFrom(e.Source) is not { } node)
                return;

            if (!node.IsSelected && SceneTree.SelectedItems is { } items)
            {
                // Through the list's own selection, so the ordinary
                // changed-event path posts it exactly like a left click would.
                items.Clear();
                items.Add(node);
            }

            return;
        }

        if (!props.IsLeftButtonPressed || _renaming is not null)
            return;

        // Only a press on the row body arms a drag: the chevron and the rename
        // editor are controls in their own right, and a drag that starts from
        // an expander click is how trees grow accidental reparents.
        if (RowNodeForDrag(e.Source) is not { } row)
            return;

        _pressedRow = row;
        _pressEvent = e;
        _pressPoint = e.GetPosition(SceneTree);

        // Deferred collapse, the same trick the viewport's press arbitration
        // uses: a plain press on one row of a multi-selection must NOT collapse
        // the selection yet, or dragging three rows would always drag one. The
        // press is claimed, and the collapse happens on release if no drag
        // began.
        if (row.IsSelected && e.KeyModifiers == KeyModifiers.None &&
            SceneTree.SelectedItems is { Count: > 1 })
        {
            _deferredCollapse = row;
            SceneTree.Focus();
            e.Handled = true;
        }
    }

    private void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedRow is not { } origin || _pressEvent is not { } press || _dragInProgress)
            return;

        if (!e.GetCurrentPoint(SceneTree).Properties.IsLeftButtonPressed)
        {
            _pressedRow = null;
            _pressEvent = null;
            return;
        }

        Point position = e.GetPosition(SceneTree);
        if (Math.Abs(position.X - _pressPoint.X) < DragThresholdPixels &&
            Math.Abs(position.Y - _pressPoint.Y) < DragThresholdPixels)
        {
            return;
        }

        StartDrag(press, origin);
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedRow = null;
        _pressEvent = null;

        if (_deferredCollapse is not { } node)
            return;

        _deferredCollapse = null;

        // No drag happened, so the press means what a press means: select
        // exactly this row.
        if (!_dragInProgress && ReferenceEquals(RowNodeFrom(e.Source), node) &&
            SceneTree.SelectedItems is { } items)
        {
            items.Clear();
            items.Add(node);
        }
    }

    private async void StartDrag(PointerPressedEventArgs trigger, SceneTreeNode origin)
    {
        _dragInProgress = true;
        _deferredCollapse = null;
        _pressedRow = null;
        _pressEvent = null;

        // Dragging a selected row drags the whole selection, hidden rows
        // included; dragging an unselected one drags just it (the list will
        // have selected it on press anyway, but the drag must not depend on
        // that race).
        var ids = new List<Guid>();
        if (origin.IsSelected)
        {
            if (SceneTree.SelectedItems is { } items)
            {
                foreach (object? item in items)
                {
                    if (item is SceneTreeNode node)
                        ids.Add(node.Id);
                }
            }

            Model?.Tree?.CollectHiddenSelected(ids);
        }

        if (!ids.Contains(origin.Id))
            ids.Add(origin.Id);

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DragFormat, ids.ToArray()));

        try
        {
            await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragInProgress = false;
            ClearDropIndicator();
        }
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;

        if (TryResolveDrop(e, out SceneTreeNode? row, out SceneTreeDropZone zone, out _, out _))
        {
            SetDropIndicator(row, zone);
            e.DragEffects = DragDropEffects.Move;
        }
        else
        {
            ClearDropIndicator();
        }
    }

    private void OnTreeDragLeave(object? sender, DragEventArgs e) => ClearDropIndicator();

    private void OnTreeDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        bool valid = TryResolveDrop(e, out _, out _, out Guid parentId, out int index);
        ClearDropIndicator();
        if (!valid)
            return;

        if (e.DataTransfer.TryGetValue(DragFormat) is { Length: > 0 } ids)
            ReparentRequested?.Invoke(ids, parentId, index);
    }

    /// <summary>
    /// Turns a drag position into a drop decision: which row indicates, and
    /// which (parent, index) the engine would be asked for. Returns false for
    /// anything that must not drop — foreign data, a target inside the dragged
    /// subtree, a sibling slot beside a top-level row.
    /// </summary>
    private bool TryResolveDrop(
        DragEventArgs e, out SceneTreeNode? row, out SceneTreeDropZone zone, out Guid parentId, out int index)
    {
        row = null;
        zone = SceneTreeDropZone.None;
        parentId = Guid.Empty;
        index = -1;

        if (Model?.Tree is not { } tree ||
            e.DataTransfer.TryGetValue(DragFormat) is not { Length: > 0 } ids)
        {
            return false;
        }

        if (RowBorderFrom(e.Source) is not { } border || border.DataContext is not SceneTreeNode target)
        {
            // Empty space below the rows: append into the scene root, which is
            // where an insert puts new things too.
            if (tree.Roots.Count != 1 || ContainsId(ids, tree.Roots[0].Id))
                return false;

            parentId = tree.Roots[0].Id;
            return true;
        }

        double y = e.GetPosition(border).Y;
        double height = border.Bounds.Height;
        zone = y < height * 0.3 ? SceneTreeDropZone.Before
            : y > height * 0.7 ? SceneTreeDropZone.After
            : SceneTreeDropZone.Into;

        SceneTreeNode? parent = zone == SceneTreeDropZone.Into ? target : tree.ParentOf(target);
        if (parent is null)
        {
            // Beside a top-level row there is no sibling slot to name: the
            // scene root is not a row and its children ARE the top level.
            return false;
        }

        // A drop anywhere inside the dragged subtree is a cycle; the engine
        // refuses it too, but the cursor must already say no.
        for (SceneTreeNode? ancestor = parent; ancestor is not null; ancestor = tree.ParentOf(ancestor))
        {
            if (ContainsId(ids, ancestor.Id))
                return false;
        }

        row = target;
        parentId = parent.Id;
        index = zone switch
        {
            SceneTreeDropZone.Before => parent.Children.IndexOf(target),
            SceneTreeDropZone.After => parent.Children.IndexOf(target) + 1,
            _ => -1,
        };

        return true;
    }

    private static bool ContainsId(Guid[] ids, Guid id)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i] == id)
                return true;
        }

        return false;
    }

    private void SetDropIndicator(SceneTreeNode? row, SceneTreeDropZone zone)
    {
        if (!ReferenceEquals(_dropRow, row))
            ClearDropIndicator();

        _dropRow = row;
        if (row is not null)
            row.DropZone = zone;
    }

    private void ClearDropIndicator()
    {
        if (_dropRow is { } row)
            row.DropZone = SceneTreeDropZone.None;

        _dropRow = null;
    }

    /// <summary>The row a visual-tree event source belongs to, or null.</summary>
    private static SceneTreeNode? RowNodeFrom(object? source)
    {
        for (Visual? current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Control { DataContext: SceneTreeNode node })
                return node;
        }

        return null;
    }

    /// <summary>
    /// Like <see cref="RowNodeFrom"/> but refuses a press that started inside
    /// an interactive child (the chevron, the rename editor).
    /// </summary>
    private static SceneTreeNode? RowNodeForDrag(object? source)
    {
        for (Visual? current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Button or TextBox)
                return null;

            if (current is Border { Classes: { } classes, DataContext: SceneTreeNode node } &&
                classes.Contains("row"))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>The row's root Border, for position arithmetic within it.</summary>
    private static Border? RowBorderFrom(object? source)
    {
        for (Visual? current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Border border && border.Classes.Contains("row") &&
                border.DataContext is SceneTreeNode)
            {
                return border;
            }
        }

        return null;
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Unity's muscle memory: double-click means "take me there". Expansion
        // has the chevron and the keyboard; rename has F2 and the menu.
        if (_renaming is not null)
            return;

        FrameRequested?.Invoke();
        e.Handled = true;
    }

    // Claimed before the row can act on it: clicking an expander is not a way
    // of selecting the thing it belongs to, which is what every file tree does
    // and what a user pressing it repeatedly to browse expects.
    private void OnChevronPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnChevronClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SceneTreeNode node })
            return;

        Model?.Tree?.ToggleExpanded(node);
        Dispatcher.UIThread.Post(LogRealization, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Reports how many rows the panel actually built against how many it is
    /// showing.
    /// </summary>
    /// <remarks>
    /// <b>The whole point of the flat projection is that these two numbers
    /// differ</b>, and nothing else in the app would say if they stopped. A
    /// panel that quietly reverted to realising a container per row would look
    /// completely correct and simply get slower with the scene, which is the
    /// failure this replaced. Debug level: it costs one enumeration of the
    /// realised set, on a user action.
    /// </remarks>
    private void LogRealization()
    {
        if (Model?.Tree is not { } tree || SceneTree.ItemsPanelRoot is not { } panel)
            return;

        // Children is the realised set for a virtualizing panel: the containers
        // it has actually built. GetRealizedContainers is protected, and this
        // is the same number from the outside.
        Logger?.LogDebug(
            "Scene tree: {Realized} row(s) realised of {Visible} visible, {Total} in the scene ({Panel})",
            panel.Children.Count, tree.Rows.Count, tree.Count, panel.GetType().Name);
    }

    /// <summary>
    /// Left and right collapse and walk out of the hierarchy, which is the tree
    /// keyboard pattern every file browser uses.
    /// </summary>
    /// <remarks>
    /// Up and down are the list's own and are left alone. The flat projection is
    /// what makes "go to my parent" a backwards scan for the first shallower
    /// row rather than a walk of the graph.
    /// </remarks>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        _gestureModifiers = e.KeyModifiers;

        // A rename in progress owns the keyboard: this tunnel handler fires
        // before the TextBox sees anything, and claiming Left/Right here would
        // break the caret.
        if (_renaming is not null)
            return;

        if (Model?.Tree is not { } tree || SceneTree.SelectedItem is not SceneTreeNode node)
            return;

        // The verbs every outliner owes its keyboard. They fire here, with the
        // tree focused, because the engine keymap only hears keys the native
        // viewport receives.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            EditorHostCommand? chord = e.Key switch
            {
                Key.D => EditorHostCommand.Duplicate,
                Key.G when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => EditorHostCommand.Ungroup,
                Key.G => EditorHostCommand.Group,
                Key.T => EditorHostCommand.ToggleBrushKind,
                _ => null,
            };

            if (chord is { } command)
            {
                CommandRequested?.Invoke(command);
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Delete:
                CommandRequested?.Invoke(EditorHostCommand.Delete);
                e.Handled = true;
                return;

            case Key.F2:
                BeginRename(node);
                e.Handled = true;
                return;

            case Key.F when e.KeyModifiers == KeyModifiers.None:
                FrameRequested?.Invoke();
                e.Handled = true;
                return;
        }

        if (e.Key == Key.Right)
        {
            if (node.HasChildren && !node.IsExpanded)
                tree.ToggleExpanded(node);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Left)
            return;

        if (node.IsExpanded)
        {
            tree.ToggleExpanded(node);
            e.Handled = true;
            return;
        }

        int index = tree.Rows.IndexOf(node);
        for (int i = index - 1; i >= 0; i--)
        {
            if (tree.Rows[i].Depth >= node.Depth)
                continue;

            SceneTree.SelectedItem = tree.Rows[i];
            break;
        }

        e.Handled = true;
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Model?.ClearFilter();
        e.Handled = true;
    }

    private void OnClearFilterClicked(object? sender, RoutedEventArgs e) => Model?.ClearFilter();

    // --- In-place rename -----------------------------------------------------

    /// <summary>
    /// Puts a row into rename mode: F2, and the context menu's Rename.
    /// </summary>
    private void BeginRename(SceneTreeNode node)
    {
        CancelRename();

        _renaming = node;
        node.IsRenaming = true;

        // The editor only exists after the visibility change lays out; focus
        // aimed at an invisible control lands nowhere and the edit looks dead.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_renaming, node))
                return;

            if (SceneTree.ContainerFromItem(node) is not Control container)
                return;

            if (container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is not { } box)
                return;

            // Set imperatively, never bound: a binding would let the ~30 Hz
            // snapshot republish rewrite the text mid-keystroke.
            box.Text = node.Name;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void CancelRename()
    {
        if (_renaming is not { } node)
            return;

        _renaming = null;
        node.IsRenaming = false;
    }

    private void CommitRename(TextBox box)
    {
        if (_renaming is not { } node)
            return;

        _renaming = null;
        node.IsRenaming = false;

        // The empty and unchanged refusals live in the engine verb; repeating
        // them here would just be a second copy to keep honest. Whitespace is
        // filtered because raising a request that is certain to be refused
        // reads as a rename that silently failed.
        string text = (box.Text ?? string.Empty).Trim();
        if (text.Length > 0 && !string.Equals(text, node.Name, StringComparison.Ordinal))
            RenameRequested?.Invoke(node.Id, text);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        if (e.Key == Key.Enter)
        {
            CommitRename(box);
            SceneTree.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Cancel FIRST: the focus change fires LostFocus, whose commit
            // must find nothing to commit.
            CancelRename();
            SceneTree.Focus();
            e.Handled = true;
        }
    }

    private void OnRenameBlurred(object? sender, RoutedEventArgs e)
    {
        // Blur commits, exactly like the property panel's fields: Escape is
        // what abandoning a half-typed name looks like, not clicking away.
        if (sender is TextBox box)
            CommitRename(box);
    }

    // --- Context menu --------------------------------------------------------

    // Each handler reads the row that opened the menu from its own
    // DataContext, which Avalonia inherits from the row the menu was attached
    // to. The verbs act on the engine's selection; the right-press handler has
    // already ensured the row is part of it.

    private static SceneTreeNode? MenuNode(object? sender) =>
        (sender as Control)?.DataContext as SceneTreeNode;

    private void OnMenuRename(object? sender, RoutedEventArgs e)
    {
        if (MenuNode(sender) is { } node)
            BeginRename(node);
    }

    private void OnMenuFrame(object? sender, RoutedEventArgs e) => FrameRequested?.Invoke();

    private void OnMenuDuplicate(object? sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(EditorHostCommand.Duplicate);

    private void OnMenuDelete(object? sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(EditorHostCommand.Delete);

    private void OnMenuGroup(object? sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(EditorHostCommand.Group);

    private void OnMenuUngroup(object? sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(EditorHostCommand.Ungroup);

    private void OnMenuConvertKind(object? sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(EditorHostCommand.ToggleBrushKind);

    private void OnMenuExpandAll(object? sender, RoutedEventArgs e)
    {
        if (MenuNode(sender) is { } node)
            Model?.Tree?.SetSubtreeExpanded(node, expanded: true);
    }

    private void OnMenuCollapseAll(object? sender, RoutedEventArgs e)
    {
        if (MenuNode(sender) is { } node)
            Model?.Tree?.SetSubtreeExpanded(node, expanded: false);
    }
}
