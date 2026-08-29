using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Hosting;
using System;
using System.Collections.Generic;

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
    /// <summary>The user selected a row; the id goes to the engine.</summary>
    public event Action<Guid>? NodeSelected;

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

    public ScenePanel()
    {
        InitializeComponent();

        // TUNNEL, not the bubbling handler XAML would attach. ListBox handles
        // every arrow key itself: on a vertical panel a Left or Right press
        // still runs its selection move, which re-selects the row it is already
        // on, returns true, and marks the event handled. A bubbling handler for
        // the tree's own collapse/expand would therefore never run at all.
        SceneTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
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
        _syncingSelection = false;

        RevealSelection(tree, snapshot.SelectedIds);
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
        // MODEL, and the containers it brings into existence do not exist until
        // the layout pass that follows. Setting the control's selection now
        // would be aiming at a row that has not been built.
        Dispatcher.UIThread.Post(() =>
        {
            if (Model?.Tree is not { } current || !ReferenceEquals(current, tree))
                return;

            // Avalonia scrolls the selected item into view on its own; what it
            // must not do is treat this as the user selecting something and
            // post it straight back to the engine.
            _syncingSelection = true;
            SceneTree.SelectedItem = node;
            _syncingSelection = false;

            // A second hop, and not one to skip. Selecting an item starts the
            // framework's own scroll AND realises the row; both land in the
            // layout pass after this callback, and an offset written before
            // that is computed against an extent the panel has not finished
            // estimating.
            Dispatcher.UIThread.Post(() => ScrollWithContext(tree, node), DispatcherPriority.Loaded);
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

        if (SceneTree.SelectedItem is not SceneTreeNode node)
            return;

        // Remembered so the echo of this selection, arriving from the engine a
        // frame later, does not scroll the panel to a row that is already under
        // the user's cursor.
        _treeRequestedId = node.Id;
        NodeSelected?.Invoke(node.Id);
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
        if (Model?.Tree is not { } tree || SceneTree.SelectedItem is not SceneTreeNode node)
            return;

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
}
