using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Projects;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Hosting;
using SpectraEngine.Editor.Shell;
using SpectraEngine.Editor.Viewport;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
    private readonly EditorDocument _document = new();

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
    private int _lastUndoDepth;
    private int _lastRedoDepth;

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

        // TUNNEL, not the bubbling handler XAML would attach. ListBox handles
        // every arrow key itself: on a vertical panel a Left or Right press
        // still runs its selection move, which re-selects the row it is already
        // on, returns true, and marks the event handled. A bubbling handler for
        // the tree's own collapse/expand would therefore never run at all.
        SceneTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);

        // The title is the only place the shell says what is open and whether
        // it is saved, so it follows the document rather than being set once.
        _document.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(EditorDocument.Title))
                Title = _document.Title;
        };
        Title = _document.Title;

        // The viewport hands up the document chords it intercepted, because
        // while it has focus the OS gives it the keyboard and Avalonia never
        // sees the menu accelerators at all.
        Viewport.ShellChord += OnShellChord;

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

            // The list binds to the model's flat row projection through the
            // shell model; assigning ItemsSource here would replace that binding
            // with the hierarchy it was flattened FROM, which shows exactly the
            // top-level nodes and never changes again.
            _tree = new SceneTreeModel(session.Host, _loggerFactory.CreateLogger<SceneTreeModel>());
            _shell.Tree = _tree;

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
        TrackDirty(snapshot);

        _shell.ApplySnapshot(snapshot);
    }

    /// <summary>
    /// Marks the document edited when the undo history has moved at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Any movement, rather than a comparison against the depth at save
    /// time.</b> Depth alone cannot tell the two apart: undo one entry, then
    /// make a different edit, and the depth returns to what it was with entirely
    /// different content behind it. So this errs towards dirty, which costs a
    /// redundant write, instead of towards clean, which costs the work.
    /// </para>
    /// <para>
    /// The history is the right signal because it is the only thing that moves
    /// for an edit and stays still for everything else: the demo scene animates
    /// a brush every frame, the world recompiles hundreds of times a second,
    /// and neither goes through a command.
    /// </para>
    /// </remarks>
    private void TrackDirty(FrameSnapshot snapshot)
    {
        if (snapshot.UndoDepth == _lastUndoDepth && snapshot.RedoDepth == _lastRedoDepth)
            return;

        _lastUndoDepth = snapshot.UndoDepth;
        _lastRedoDepth = snapshot.RedoDepth;
        _document.MarkDirty();
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
            // framework's own scroll AND realises the row; both land in the
            // layout pass after this callback, and an offset written before
            // that is computed against an extent the panel has not finished
            // estimating.
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
    private void ScrollWithContext(SceneTreeNode node)
    {
        if (_tree is not { } tree)
            return;

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

    // Claimed before the row can act on it: clicking an expander is not a way
    // of selecting the thing it belongs to, which is what every file tree does
    // and what a user pressing it repeatedly to browse expects.
    private void OnChevronPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnChevronClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SceneTreeNode node })
            return;

        _tree?.ToggleExpanded(node);
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
        if (_tree is not { } tree || SceneTree.ItemsPanelRoot is not { } panel)
            return;

        // Children is the realised set for a virtualizing panel: the containers
        // it has actually built. GetRealizedContainers is protected, and this
        // is the same number from the outside.
        _logger.LogDebug(
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
        if (_tree is not { } tree || SceneTree.SelectedItem is not SceneTreeNode node)
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

        _shell.ClearFilter();
        e.Handled = true;
    }

    private void OnClearFilterClicked(object? sender, RoutedEventArgs e) => _shell.ClearFilter();

    // --- Menu ----------------------------------------------------------------

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnFocusViewportClicked(object? sender, RoutedEventArgs e) => Viewport.Focus();

    private void OnShellChord(ShellChord chord)
    {
        switch (chord)
        {
            case ShellChord.NewMap: OnNewMapClicked(this, new RoutedEventArgs()); break;
            case ShellChord.OpenMap: OnOpenMapClicked(this, new RoutedEventArgs()); break;
            case ShellChord.SaveMap: OnSaveClicked(this, new RoutedEventArgs()); break;
            case ShellChord.SaveMapAs: OnSaveAsClicked(this, new RoutedEventArgs()); break;
        }
    }

    // --- File ----------------------------------------------------------------
    //
    // Every one of these runs the filesystem work on the UI thread and the
    // SCENE work on the render thread, through EditorSession. That split is the
    // whole contract: a map bundle is ordinary file I/O and belongs where the
    // dialogs are, while the graph, the static-world compile and the GPU
    // resources belong to the thread that owns the frame.

    private async void OnNewMapClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is not { } session) return;
        if (!await ConfirmDiscardAsync("starting a new map")) return;

        string? name = await NameDialog.AskAsync(
            this, "New map", "Name for the new scene:", "Untitled");
        if (name is null) return;

        session.NewMap(name, error => Dispatcher.UIThread.Post(() =>
        {
            if (error is not null)
            {
                _shell.SetError($"Could not start a new map: {error.Message}");
                return;
            }

            _document.MarkNew();
            ResetDirtyBaseline();
            _shell.SetMessage($"New map: {name}");
        }));
    }

    private async void OnOpenMapClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        if (!await ConfirmDiscardAsync("opening another map")) return;

        // A FOLDER picker, because a map bundle is a directory. Pointing a file
        // picker at map.json would work and would then show the same file name
        // for every level anybody ever opened.
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Open map bundle",
                AllowMultiple = false,
                SuggestedStartLocation = await SuggestedStartAsync(_document.SuggestedMapFolder),
            });

        if (picked.Count == 0) return;
        OpenMapAt(picked[0].Path.LocalPath);
    }

    private void OpenMapAt(string bundlePath)
    {
        if (_session is not { } session) return;

        if (!MapBundle.IsBundle(bundlePath))
        {
            _shell.SetError(
                $"That folder is not a map bundle: it has no {MapFormat.DocumentFileName}.");
            return;
        }

        session.OpenMap(bundlePath, (report, error) => Dispatcher.UIThread.Post(() =>
        {
            if (error is not null)
            {
                _shell.SetError($"Could not open the map: {error.Message}");
                return;
            }

            _document.MarkOpened(bundlePath);
            ResetDirtyBaseline();

            // A map that names a model this project does not have still loads,
            // with that node standing where it belongs and drawing nothing. It
            // has to be said out loud, or the level looks subtly wrong with
            // nothing anywhere explaining why.
            _shell.SetMessage(report?.Describe() is { } missing
                ? $"Opened {_document.MapLabel}. {missing}"
                : $"Opened {_document.MapLabel}");
        }));
    }

    private async void OnOpenProjectClicked(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Open project folder", AllowMultiple = false });

        if (picked.Count == 0) return;

        ProjectLayout layout;
        try
        {
            layout = ProjectLayout.Open(picked[0].Path.LocalPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ProjectFormatException)
        {
            _shell.SetError($"Could not open the project: {ex.Message}");
            return;
        }

        _document.SetProject(layout);

        // Opening a project opens its startup map, because a project with a
        // level in it and an empty viewport is a state nobody asked for.
        if (layout.Project.StartupMap is { } startup)
        {
            OpenMapAt(layout.Resolve(startup));
            return;
        }

        int found = layout.DiscoverMaps().Count;
        _shell.SetMessage(found == 0
            ? $"Opened project {layout.Project.Name}; it has no maps yet"
            : $"Opened project {layout.Project.Name}; it names no startup map, but {found} are on disk");
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_document.MapPath is { } path)
            SaveMapTo(path);
        else
            OnSaveAsClicked(sender, e);
    }

    private async void OnSaveAsClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Folder to save the map bundle into",
                AllowMultiple = false,
                SuggestedStartLocation = await SuggestedStartAsync(_document.SuggestedMapFolder),
            });

        if (picked.Count == 0) return;

        string? name = await NameDialog.AskAsync(
            this, "Save map as", "Name for the map bundle:", _document.MapLabel);
        if (name is null) return;

        SaveMapTo(Path.Combine(picked[0].Path.LocalPath, name + MapFormat.BundleExtension));
    }

    private void SaveMapTo(string bundlePath)
    {
        if (_session is not { } session) return;

        session.SaveMap(bundlePath, (report, error) => Dispatcher.UIThread.Post(() =>
        {
            if (error is not null)
            {
                _shell.SetError($"Could not save the map: {error.Message}");
                return;
            }

            _document.MarkSaved(bundlePath);
            ResetDirtyBaseline();

            // An incomplete save is still a save: the scene held something the
            // format cannot name, such as a mesh built in code. Reported rather
            // than silent, because the alternative is a map that quietly forgets
            // a prop.
            _shell.SetMessage(report?.Describe() is { } lost
                ? $"Saved {_document.MapLabel}. {lost}"
                : $"Saved {_document.MapLabel}");
        }));
    }

    /// <summary>
    /// Asks before throwing away unsaved work, and returns whether to go ahead.
    /// </summary>
    /// <remarks>
    /// Typed confirmation rather than a Yes/No pair, because this is the one
    /// dialog in the shell whose wrong answer destroys work that cannot be
    /// recovered: the undo history goes with the scene. A button people learn
    /// to dismiss without reading is exactly what should not guard it.
    /// </remarks>
    private async Task<bool> ConfirmDiscardAsync(string what)
    {
        if (!_document.IsDirty) return true;

        string? answer = await NameDialog.AskAsync(
            this, "Unsaved changes",
            $"{_document.MapLabel} has unsaved changes, and {what} will discard them. "
            + "Type discard to continue.", string.Empty);

        return string.Equals(answer, "discard", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IStorageFolder?> SuggestedStartAsync(string? path)
    {
        if (path is null || !Directory.Exists(path)) return null;
        try { return await StorageProvider.TryGetFolderFromPathAsync(path); }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Re-baselines the dirty tracker after a save or a load, so the history
    /// movement those cause does not immediately mark the document dirty again.
    /// </summary>
    private void ResetDirtyBaseline()
    {
        _lastUndoDepth = _latest.UndoDepth;
        _lastRedoDepth = _latest.RedoDepth;
    }

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
