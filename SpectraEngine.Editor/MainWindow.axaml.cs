using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using SpectraEngine.Editor.Shell;
using SpectraEngine.Editor.Viewport;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace SpectraEngine.Editor;

/// <summary>
/// The shell window: a menu, a toolbar, a scene tree, the viewport, and a
/// status bar.
/// </summary>
/// <remarks>
/// <b>Everything crossing between the two threads crosses here, and only in two
/// directions.</b> The engine publishes immutable snapshots, which the UI thread
/// reads; the UI thread posts commands, which the render thread runs. Nothing
/// in this window ever touches a <c>Scene</c>, a <c>SceneNode</c> or the
/// renderer.
/// <para>
/// <b>The pump is not decoration.</b> A cursor lock is window-thread work that
/// the engine asks for from the render thread, exactly as it is in the
/// standalone path, so the shell needs a slot of its own to apply it in.
/// <see cref="DispatcherTimer"/> at roughly display rate is that slot.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherTimer _pump;
    private readonly ShellModel _shell = new();

    // Every published snapshot, not just the newest. The engine's contract is
    // that a structural change rides the NEXT snapshot out and is then gone, so
    // a shell that keeps only the latest silently drops graph edits: the very
    // first snapshot carries the "you have nothing, rebuild" flag, and losing
    // exactly that one leaves a tree view permanently showing four nodes of a
    // 257-node scene with nothing anywhere reporting a problem. Measured, not
    // hypothesised.
    private readonly ConcurrentQueue<FrameSnapshot> _published = new();

    // Bounded like the engine's own change log, and for the same reason: a
    // stalled UI thread must not turn into unbounded growth on the render
    // thread's publish path. Eight seconds at the publish rate.
    private const int MaxQueuedSnapshots = 240;
    private int _queuedSnapshots;
    private volatile bool _droppedSnapshots;

    private EditorSession? _session;
    private SceneTreeModel? _tree;
    private IRenderSurface? _surface;
    private FrameSnapshot _latest = FrameSnapshot.Empty;
    private bool _stopping;

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

    /// <summary>Creates the window and wires the viewport's lifetime to the engine's.</summary>
    public MainWindow()
    {
        InitializeComponent();

        DataContext = _shell;

        _loggerFactory = new SerilogLoggerFactory(Serilog.Log.Logger, dispose: false);
        _logger = _loggerFactory.CreateLogger<MainWindow>();

        Viewport.SurfaceCreated += OnSurfaceCreated;
        Viewport.SurfaceDestroying += OnSurfaceDestroying;

        _pump = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, OnPump);

        // The one frame customisation this shell makes: paint the OS caption to
        // match the window instead of the user's accent colour. It is a DWM
        // attribute rather than a custom title bar, so it costs nothing in
        // hit-testing, keeps Aero Snap and the maximise flyout, and simply does
        // nothing on Windows versions that do not know the attribute.
        Opened += (_, _) => DarkCaption.Apply(this, _logger);

        if (!EngineViewport.IsSupported)
        {
            _shell.SetError(
                "This platform cannot host the viewport yet: the embedded surface is Windows-only in v1.");
        }
    }

    // --- Engine lifetime -----------------------------------------------------

    private void OnSurfaceCreated(IRenderSurface surface)
    {
        try
        {
            var session = new EditorSession(_loggerFactory, ResolveBackend());

            // Input is armed before the engine starts: the host exists from
            // construction, so a click during the first frames reaches a real
            // state machine rather than being dropped.
            Viewport.Host = session.Host;
            session.Host.FrameCompleted += OnFrameCompleted;

            _tree = new SceneTreeModel(session.Host, _loggerFactory.CreateLogger<SceneTreeModel>());
            _shell.Tree = _tree;
            SceneTree.ItemsSource = _tree.Roots;

            _surface = surface;
            _shell.SetViewportSize(surface.PixelSize.X, surface.PixelSize.Y);
            surface.Resized += OnViewportResized;

            session.Start(surface);
            _session = session;
            _pump.Start();

            _shell.SetMessage("Ready.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The editor session could not start");
            _shell.SetError($"The engine could not start: {ex.Message}");
        }
    }

    private void OnSurfaceDestroying()
    {
        // Before the window goes, never after: the render thread owns the swap
        // chain presenting into it.
        StopSession();
    }

    /// <inheritdoc/>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        StopSession();
        base.OnClosing(e);
    }

    private void StopSession()
    {
        if (_stopping || _session is null)
            return;

        _stopping = true;
        _pump.Stop();
        Viewport.Host = null;

        // Detached, not merely ignored: the surface outlives this subscription
        // by exactly as long as the window takes to tear down, and a resize
        // arriving in that gap would reach a shell that has stopped.
        if (_surface is { } surface)
        {
            surface.Resized -= OnViewportResized;
            _surface = null;
        }

        _session.Host.FrameCompleted -= OnFrameCompleted;
        _session.Stop();
        _session.Dispose();
        _session = null;
        _stopping = false;
    }

    // --- The two crossings ---------------------------------------------------

    // Raised ON the render thread, inside the engine's frame. Everything here
    // must be cheap and must not touch a control: the snapshot is queued and the
    // UI reads it from its own pump, which is the shape the host documents.
    private void OnFrameCompleted(FrameSnapshot snapshot)
    {
        // The newest one always wins for the readouts, which want current
        // values rather than a history.
        _latest = snapshot;

        if (Interlocked.Increment(ref _queuedSnapshots) > MaxQueuedSnapshots)
        {
            // Reported rather than silently discarded: the tree rebuilds from
            // the live graph instead of continuing to look correct.
            Interlocked.Decrement(ref _queuedSnapshots);
            _droppedSnapshots = true;
            return;
        }

        _published.Enqueue(snapshot);
    }

    private void OnPump(object? sender, EventArgs e)
    {
        // The cursor first: a freelook that started this frame should capture
        // before anything else looks at the pointer.
        Viewport.PumpCursorMode();

        // Drained, never sampled: each snapshot's change list is a batch that
        // exists once.
        while (_published.TryDequeue(out FrameSnapshot? queued))
        {
            Interlocked.Decrement(ref _queuedSnapshots);
            _tree?.ApplyChanges(queued);
        }

        if (_droppedSnapshots)
        {
            _droppedSnapshots = false;
            _logger.LogWarning("The shell fell behind the engine's snapshots; rebuilding the scene tree");
            _tree?.MarkStale();
        }

        FrameSnapshot snapshot = _latest;
        if (ReferenceEquals(snapshot, FrameSnapshot.Empty))
            return;

        // Selection is a state rather than a history, so it is applied once
        // from the newest snapshot instead of once per drained one.
        _syncingSelection = true;
        _tree?.ApplySelection(snapshot.SelectedIds);
        _syncingSelection = false;

        RevealSelection(snapshot.SelectedIds);

        _shell.ApplySnapshot(snapshot);
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
    private void RevealSelection(IReadOnlyList<Guid> selected)
    {
        if (_tree is not { } tree)
            return;

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
            if (_tree is null || !ReferenceEquals(_tree, tree))
                return;

            // Avalonia scrolls the selected item into view on its own; what it
            // must not do is treat this as the user selecting something and
            // post it straight back to the engine.
            _syncingSelection = true;
            SceneTree.SelectedItem = node;
            _syncingSelection = false;

            // A second hop, and not one to skip. Selecting an item starts the
            // framework's own scroll, which does not take effect until the
            // layout pass after this callback; measuring the row here would
            // read its pre-scroll position and nudge it by an offset that has
            // already been applied, sending it off the other end of the panel.
            // Measured: the row landed a few positions above the visible range.
            Dispatcher.UIThread.Post(() => ScrollWithContext(node), DispatcherPriority.Loaded);
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
    /// <b>The scroll offset is set directly, and the obvious alternative does
    /// not work.</b> Asking the row to bring a deliberately oversized rectangle
    /// into view is the tidy version of this and it does nothing: the rect is
    /// clamped to the control, so it behaves exactly like a plain
    /// <c>BringIntoView</c>. Measured both ways, with the row landing on the
    /// top edge one time and the bottom edge the next.
    /// </para>
    /// <para>
    /// Best-effort by design: with no realised row or no scroller there is
    /// nothing to place, and the framework's own scroll stands.
    /// </para>
    /// </remarks>
    private void ScrollWithContext(SceneTreeNode node)
    {
        if (SceneTree.TreeContainerFromItem(node) is not Control container)
        {
            _logger.LogDebug("Revealed {Node}, but its row is not realised; leaving the scroll alone", node.Name);
            return;
        }

        if (SceneTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroller)
            return;

        if (container.TranslatePoint(default, scroller) is not { } position)
            return;

        // Screen position plus the current offset is the row's place in the
        // scrolled content, which is the only frame a target offset means
        // anything in.
        double rowInContent = position.Y + scroller.Offset.Y;
        double resting = (scroller.Viewport.Height - container.Bounds.Height) * RevealRestingFraction;
        double target = Math.Clamp(
            rowInContent - resting,
            0,
            Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));

        scroller.Offset = scroller.Offset.WithY(target);
    }

    // Raised on the UI thread by the viewport's own window procedure, beside
    // the renderer's size latch rather than instead of it.
    private void OnViewportResized(Vector2D<int> size)
    {
        _shell.SetViewportSize(size.X, size.Y);
        _logger.LogDebug("Viewport resized to {Width}x{Height}", size.X, size.Y);
    }

    // --- Driving the editor --------------------------------------------------

    private void OnMoveClicked(object? sender, RoutedEventArgs e) => _session?.Post(GizmoCommand.UseTranslate);
    private void OnRotateClicked(object? sender, RoutedEventArgs e) => _session?.Post(GizmoCommand.UseRotate);
    private void OnResizeClicked(object? sender, RoutedEventArgs e) => _session?.Post(GizmoCommand.UseScale);
    private void OnOrientationClicked(object? sender, RoutedEventArgs e) => _session?.Post(GizmoCommand.ToggleOrientation);
    private void OnStyleClicked(object? sender, RoutedEventArgs e) => _session?.Post(GizmoCommand.ToggleStyle);
    private void OnSnapClicked(object? sender, RoutedEventArgs e) => _session?.Post(GizmoCommand.ToggleSnap);

    private void OnUndoClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Undo);
    private void OnRedoClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Redo);
    private void OnDuplicateClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Duplicate);
    private void OnDeleteClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Delete);
    private void OnGroupClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Group);
    private void OnUngroupClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Ungroup);
    private void OnToggleBrushKindClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.ToggleBrushKind);
    private void OnToggleNavigationClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.ToggleNavigation);

    private void OnFrameClicked(object? sender, RoutedEventArgs e) =>
        _session?.Post(EditorCameraCommand.FrameSelection);

    // --- Tree ----------------------------------------------------------------

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
        _session?.Select(node.Id);
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        _shell.ClearFilter();
        e.Handled = true;
    }

    private void OnClearFilterClicked(object? sender, RoutedEventArgs e) => _shell.ClearFilter();

    // --- Menu ----------------------------------------------------------------

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnFocusViewportClicked(object? sender, RoutedEventArgs e) => Viewport.Focus();

    // --- Startup -------------------------------------------------------------

    private GraphicsBackend ResolveBackend()
    {
        foreach (string arg in Program.StartupArgs)
        {
            switch (arg.ToLowerInvariant())
            {
                case "d3d11": return GraphicsBackend.D3D11;
                case "d3d12": return GraphicsBackend.D3D12;
                case "opengl":
                    // Named explicitly and refused explicitly: an embedded GL
                    // surface needs its own context, and letting the renderer
                    // discover that would report it as a driver failure.
                    throw new NotSupportedException(
                        "The editor viewport cannot host OpenGL yet; use d3d11 or d3d12.");
            }
        }

        return GraphicsBackend.D3D11;
    }
}
