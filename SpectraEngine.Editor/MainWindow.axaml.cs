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
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Commands;
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

    // The per-user shell state: today, the recent projects the start page
    // shows. Loaded once; written whenever a project is opened or created.
    private readonly EditorSettings _settings;

    // The live viewport control, created when a session launches and removed
    // when it closes: a NativeControlHost's child window lives exactly as long
    // as the control is in the visual tree, so "no session" and "no viewport
    // control" are the same state on purpose.
    private EngineViewport? _viewport;

    // What the next OnSurfaceCreated should build: the project (if any), the
    // asset content root, and the map to open once the engine is up. Set by
    // LaunchSession, consumed by the surface callback.
    private sealed record SessionLaunch(ProjectLayout? Project, string? ContentRoot, string? OpenMapPath);
    private SessionLaunch? _pendingLaunch;

    // The live panels, one instance each for the window's whole life. Fields
    // rather than XAML names, because their dock tools would template XAML
    // children instead of keeping an instance.
    private readonly ScenePanel _sceneView;
    private readonly PropertiesPanel _propertiesView;
    private readonly MapsPanel _mapsView;

    /// <summary>Creates the window and wires the viewport's lifetime to the engine's.</summary>
    public MainWindow()
    {
        InitializeComponent();

        DataContext = _shell;

        _loggerFactory = new SerilogLoggerFactory(Serilog.Log.Logger, dispose: false);
        _logger = _loggerFactory.CreateLogger<MainWindow>();

        _settings = EditorSettings.Load(_logger);

        // The start page raises intents; the window owns every consequence,
        // because it owns the storage provider, the dialogs and the session.
        StartView.NewProjectRequested += () => _ = CreateProjectFlowAsync();
        StartView.OpenProjectRequested += () => _ = OpenProjectFlowAsync();
        StartView.OpenMapRequested += () => _ = OpenLooseMapFlowAsync();
        StartView.RecentProjectPicked += recent => _ = OpenRecentProjectAsync(recent);
        StartView.ShowRecents(_settings.RecentProjects);

        // The panels are built HERE and handed to the dock tools as live
        // controls: the dock's builder returns a Control content instance
        // as-is, so one event-wired panel survives every re-dock and float.
        // DataContext is set explicitly rather than inherited, because a
        // floated panel leaves this window's logical tree and inherited
        // bindings would go quietly null.
        _sceneView = new ScenePanel
        {
            DataContext = _shell,
            Logger = _loggerFactory.CreateLogger<ScenePanel>(),
        };
        _sceneView.NodeSelected += id => _session?.Select(id);
        SceneTool.Content = _sceneView;

        _propertiesView = new PropertiesPanel { DataContext = _shell };
        _propertiesView.EscapePressed += () => _viewport?.Focus();
        PropertiesTool.Content = _propertiesView;

        _mapsView = new MapsPanel { DataContext = _shell };
        _mapsView.MapClicked += row => _ = OpenProjectMapAsync(row);
        MapsTool.Content = _mapsView;

        // The title is the only place the shell says what is open and whether
        // it is saved, so it follows the document rather than being set once.
        _document.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(EditorDocument.Title))
                Title = _document.Title;
        };
        Title = _document.Title;

        _shell.Properties = new PropertyPanelModel(OnPropertyEdit);

        // The pipeline dropdown's user choice, forwarded as a request. Wired
        // once: the session is resolved when the event fires, so it follows
        // whichever session is live.
        _shell.PipelineRequested += name => _session?.Host.RequestPipeline(name);

        // Document chords as real key bindings, so they also work while an
        // Avalonia control has focus - the tree, the filter, a property field.
        // The viewport intercepts the same four itself (ShellChord), because
        // while IT has focus Avalonia sees no keyboard at all; two routes, one
        // handler each, is what makes Ctrl+S work everywhere.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.N, KeyModifiers.Control),
            Command = new RelayCommand(() => OnNewMapClicked(this, new RoutedEventArgs())),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.O, KeyModifiers.Control),
            Command = new RelayCommand(() => OnOpenMapClicked(this, new RoutedEventArgs())),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.S, KeyModifiers.Control),
            Command = new RelayCommand(() => OnSaveClicked(this, new RoutedEventArgs())),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift),
            Command = new RelayCommand(() => OnSaveAsClicked(this, new RoutedEventArgs())),
        });

        _pump = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, OnPump);

        // The one frame customisation this shell makes: paint the OS caption to
        // match the window instead of the user's accent colour. It is a DWM
        // attribute rather than a custom title bar, so it costs nothing in
        // hit-testing, keeps Aero Snap and the maximise flyout, and simply does
        // nothing on Windows versions that do not know the attribute.
        Opened += (_, _) =>
        {
            DarkCaption.Apply(this, _logger);
            OpenFromStartupArgs();
        };

        if (!EngineViewport.IsSupported)
        {
            _shell.SetError(
                "This platform cannot host the viewport yet: the embedded surface is Windows-only in v1.");
        }
    }

    /// <summary>
    /// Opens whatever a startup argument names: a manifest, a project folder,
    /// or a loose map bundle. First match wins; backend switches are skipped.
    /// </summary>
    /// <remarks>
    /// This is what makes a <c>.spectraproj</c> double-clickable once the OS
    /// association exists, and it costs nothing until then.
    /// </remarks>
    private void OpenFromStartupArgs()
    {
        foreach (string arg in Program.StartupArgs)
        {
            if (arg.Length == 0 || arg[0] == '-')
                continue;

            switch (arg.ToLowerInvariant())
            {
                case "d3d11" or "d3d12" or "opengl":
                    continue;
            }

            if (File.Exists(arg) &&
                arg.EndsWith(ProjectFormat.Extension, StringComparison.OrdinalIgnoreCase))
            {
                OpenProjectAt(arg);
                return;
            }

            if (Directory.Exists(arg))
            {
                if (MapBundle.IsBundle(arg))
                {
                    _document.SetProject(null);
                    LaunchSession(new SessionLaunch(null, null, Path.GetFullPath(arg)));
                    return;
                }

                if (Directory.GetFiles(arg, "*" + ProjectFormat.Extension).Length >= 1)
                {
                    OpenProjectAt(arg);
                    return;
                }
            }
        }
    }

    // --- Engine lifetime -----------------------------------------------------

    /// <summary>
    /// Creates the viewport control and switches to the editor view; the
    /// engine session itself is built when the native surface arrives.
    /// </summary>
    /// <remarks>
    /// <b>The view switches before the child attaches</b>, so the viewport is
    /// laid out at its real size and the first swap chain is not built against
    /// a collapsed cell. From the attach onwards the airspace rule applies:
    /// nothing Avalonia draws may cross the viewport's cell.
    /// </remarks>
    private void LaunchSession(SessionLaunch launch)
    {
        if (_viewport is not null)
        {
            // Callers close the running session first; stacking two engines
            // over one window is never what anyone meant.
            _logger.LogWarning("A session is already running; ignoring the launch request");
            return;
        }

        if (!EngineViewport.IsSupported)
        {
            _shell.SetError(
                "This platform cannot host the viewport yet: the embedded surface is Windows-only in v1.");
            return;
        }

        _pendingLaunch = launch;

        var viewport = new EngineViewport();
        viewport.SurfaceCreated += OnSurfaceCreated;
        viewport.SurfaceDestroying += OnSurfaceDestroying;

        // The viewport hands up the document chords it intercepted, because
        // while it has focus the OS gives it the keyboard and Avalonia never
        // sees the menu accelerators at all.
        viewport.ShellChord += OnShellChord;
        _viewport = viewport;

        StartView.IsVisible = false;
        EditorView.IsVisible = true;
        _shell.HasSession = true;

        // Attach last: creating the native child is what eventually raises
        // SurfaceCreated, and everything above must be in place by then.
        ViewportHost.Child = viewport;
        viewport.Focus();
    }

    /// <summary>
    /// Tears the session and its viewport down and returns to the start page.
    /// Callers have already confirmed any unsaved work.
    /// </summary>
    private void CloseSessionView()
    {
        if (_viewport is not { } viewport)
            return;

        // The child leaves the tree FIRST: destroying the native window raises
        // SurfaceDestroying, which stops the engine before the HWND dies. The
        // explicit stop after it covers a viewport that never got a surface.
        ViewportHost.Child = null;
        StopSession();

        viewport.SurfaceCreated -= OnSurfaceCreated;
        viewport.SurfaceDestroying -= OnSurfaceDestroying;
        viewport.ShellChord -= OnShellChord;
        _viewport = null;
        _pendingLaunch = null;

        _tree = null;
        _shell.Tree = null;
        _shell.HasSession = false;
        _shell.HasProject = false;
        _shell.ProjectMaps.Clear();

        // The readouts and the filter describe a scene that no longer exists;
        // left alone, a stale "3 selected / undo 12" keeps verbs enabled on
        // the start page and the next session's tree opens pre-filtered by a
        // search nobody typed into it.
        _shell.ClearFilter();
        _shell.ApplySnapshot(FrameSnapshot.Empty);

        EditorView.IsVisible = false;
        StartView.IsVisible = true;
        StartView.ShowRecents(_settings.RecentProjects);
    }

    private void OnSurfaceCreated(IRenderSurface surface)
    {
        try
        {
            SessionLaunch? launch = _pendingLaunch;
            _pendingLaunch = null;

            var session = new EditorSession(_loggerFactory, ResolveBackend(), launch?.ContentRoot);

            // Input is armed before the engine starts: the host exists from
            // construction, so a click during the first frames reaches a real
            // state machine rather than being dropped.
            _viewport!.Host = session.Host;
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

            // The engine is up on a baseplate; whatever should really be open
            // goes through the ordinary map path, so a broken bundle reports
            // instead of silently falling back.
            if (launch?.OpenMapPath is { } mapPath)
            {
                OpenMapAt(mapPath);
            }
            else
            {
                _document.MarkNew();

                // The greeting must not stomp a standing failure: a missing
                // startup map was reported moments ago, and overwriting it
                // with "New baseplate scene" hides the one line that explains
                // why the level is not on screen.
                if (!_shell.IsError)
                {
                    _shell.SetMessage(_document.HasProject
                        ? $"New baseplate scene. Save it to add a first map to {_document.ProjectLabel}."
                        : "Ready.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The editor session could not start");
            _shell.SetError($"The engine could not start: {ex.Message}");

            // Rolled back to the start page, not left half-open: without this
            // the shell sits in the editor view with HasSession true and no
            // session behind it, where every enabled verb silently does
            // nothing and LaunchSession's own guard refuses a retry. Posted,
            // because this handler runs during the native child's attach and
            // tearing the child out from inside its own attach is asking the
            // framework a question nobody needs answered.
            Dispatcher.UIThread.Post(CloseSessionView);
        }
    }

    private void OnSurfaceDestroying()
    {
        // Before the window goes, never after: the render thread owns the swap
        // chain presenting into it.
        StopSession();
    }

    // Set once the typed confirmation has been given, so the programmatic
    // Close that follows it is not asked again.
    private bool _closeConfirmed;

    /// <inheritdoc/>
    /// <remarks>
    /// The close button gets the same typed confirmation every menu route
    /// gets: it is the one gesture people reach for fastest, and the undo
    /// history goes with the scene. The dialog is async and window closing is
    /// not, so a dirty close is cancelled, asked, and re-issued.
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_document.IsDirty && !_closeConfirmed)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
        }
        else
        {
            StopSession();
        }

        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync()
    {
        if (!await ConfirmDiscardAsync("closing the editor")) return;

        _closeConfirmed = true;
        Close();
    }

    private void StopSession()
    {
        if (_stopping || _session is null)
            return;

        _stopping = true;
        _pump.Stop();
        if (_viewport is { } viewport)
            viewport.Host = null;

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

        // Per-session UI state, reset so nothing leaks into the next session:
        // the snapshot queue is this session's history, and the dirty baseline
        // and the panel's reveal gates describe a scene that no longer exists.
        _latest = FrameSnapshot.Empty;
        while (_published.TryDequeue(out _))
            Interlocked.Decrement(ref _queuedSnapshots);
        _droppedSnapshots = false;
        _lastUndoDepth = 0;
        _lastRedoDepth = 0;
        _sceneView.ResetSelectionMemory();

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
        _viewport?.PumpCursorMode();

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
        // from the newest snapshot instead of once per drained one; the panel
        // owns the sync guards and the reveal choreography.
        _sceneView.SyncSelection(snapshot);
        TrackDirty(snapshot);

        _shell.ApplySnapshot(snapshot);
        RefreshSnapFields(snapshot);
    }

    // --- Snap increment fields -----------------------------------------------
    //
    // Three small boxes with the property panel's commit contract: a focused
    // field stops taking refreshes, Enter and blur commit, Escape reverts, and
    // unparseable or non-positive text reverts rather than sticking. Plain
    // code-behind over the controls, like the scroll offsets: the state is two
    // floats and a focus flag, and a model would be ceremony.

    private void RefreshSnapFields(FrameSnapshot snapshot)
    {
        RefreshSnapField(SnapMoveBox, snapshot.MoveSnapIncrement);
        RefreshSnapField(SnapRotateBox, snapshot.RotateSnapIncrement);
        RefreshSnapField(SnapResizeBox, snapshot.ResizeSnapIncrement);
    }

    private static void RefreshSnapField(TextBox box, float value)
    {
        // A focused field is being typed into; writing the published value
        // back would delete characters as they arrive, which reads as a broken
        // keyboard. The blur or Enter that ends the edit commits it.
        if (box.IsFocused)
            return;

        string text = PropertyFieldModel.Format(value);
        if (box.Text != text)
            box.Text = text;
    }

    private void OnSnapFieldFocused(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox box)
            box.SelectAll();
    }

    private void OnSnapFieldBlurred(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            CommitSnapField(box);
    }

    private void OnSnapFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                CommitSnapField(box);
                e.Handled = true;
                break;

            case Key.Escape:
                RevertSnapField(box);
                _viewport?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void CommitSnapField(TextBox box)
    {
        // Refused before anything is posted, the panel's rule: zero and
        // negative would throw inside the setting on the render thread, and
        // clamping would write a number nobody asked for.
        if (PropertyFieldModel.TryParseNumber(box.Text ?? string.Empty, out float value) && value > 0f)
            _session?.SetSnapIncrement(SnapToolFor(box), value);
        else
            RevertSnapField(box);
    }

    private void RevertSnapField(TextBox box)
    {
        FrameSnapshot latest = _latest;
        float value = ReferenceEquals(box, SnapRotateBox) ? latest.RotateSnapIncrement
            : ReferenceEquals(box, SnapResizeBox) ? latest.ResizeSnapIncrement
            : latest.MoveSnapIncrement;
        box.Text = PropertyFieldModel.Format(value);
    }

    private GizmoMode SnapToolFor(TextBox box) =>
        ReferenceEquals(box, SnapRotateBox) ? GizmoMode.Rotate
            : ReferenceEquals(box, SnapResizeBox) ? GizmoMode.Scale
            : GizmoMode.Translate;

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

    // Raised on the UI thread by the viewport's own window procedure, beside
    // the renderer's size latch rather than instead of it.
    private void OnViewportResized(Vector2D<int> size)
    {
        _shell.SetViewportSize(size.X, size.Y);
        _logger.LogDebug("Viewport resized to {Width}x{Height}", size.X, size.Y);
    }

    // --- Driving the editor --------------------------------------------------

    private void OnHomeTabClicked(object? sender, RoutedEventArgs e) => _shell.ActiveTab = "home";
    private void OnModelTabClicked(object? sender, RoutedEventArgs e) => _shell.ActiveTab = "model";
    private void OnViewTabClicked(object? sender, RoutedEventArgs e) => _shell.ActiveTab = "view";

    private void OnInsertWorldBrushClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.WorldBrush);
    private void OnInsertPartBrushClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.PartBrush);
    private void OnInsertSubtractiveBrushClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.SubtractiveBrush);
    private void OnInsertLightClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.PointLight);
    private void OnInsertGroupClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.Group);

    // Set semantics against the displayed state, never a toggle verb: a
    // toggle sent against a snapshot one publish stale flips the wrong way
    // exactly when the user clicks fastest, while re-requesting the state
    // already shown is a no-op.
    private void OnPlayClicked(object? sender, RoutedEventArgs e) =>
        _session?.Host.RequestPlayMode(!_shell.IsPlaying);

    private void OnDebugWireClicked(object? sender, RoutedEventArgs e) =>
        _session?.Host.RequestDebugVisualization(DebugVisualization.Wireframe, !_shell.DebugWireframe);
    private void OnDebugVerticesClicked(object? sender, RoutedEventArgs e) =>
        _session?.Host.RequestDebugVisualization(DebugVisualization.Vertices, !_shell.DebugVertices);
    private void OnDebugAabbsClicked(object? sender, RoutedEventArgs e) =>
        _session?.Host.RequestDebugVisualization(DebugVisualization.Aabbs, !_shell.DebugAabbs);
    private void OnDebugNormalsClicked(object? sender, RoutedEventArgs e) =>
        _session?.Host.RequestDebugVisualization(DebugVisualization.Normals, !_shell.DebugNormals);
    private void OnDebugSceneGraphClicked(object? sender, RoutedEventArgs e) =>
        _session?.Host.RequestDebugVisualization(DebugVisualization.SceneGraph, !_shell.DebugSceneGraph);

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

    // --- Menu ----------------------------------------------------------------

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

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


    /// <summary>
    /// Queues one property edit onto the render thread.
    /// </summary>
    /// <remarks>
    /// The panel says which property and which value; which nodes that means is
    /// the editor's answer, given at the moment the edit runs. A UI's view of
    /// the selection is a frame or two behind, so an edit carrying its own node
    /// list would occasionally write to nodes the user had already deselected.
    /// </remarks>
    private void OnPropertyEdit(PropertyEdit edit) => _session?.ApplyProperty(edit);

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
            // The session boundary guard: this callback was produced by the
            // render thread of a PARTICULAR session, and by the time the post
            // runs that session can be dead and another live. A stale post
            // rebinding the fresh document is how the next Ctrl+S overwrites a
            // different project's bundle.
            if (!ReferenceEquals(session, _session))
                return;

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
            // See NewMap for the session boundary guard: a stale post from a
            // torn-down session must not mark ITS bundle open on the document
            // the next session is editing.
            if (!ReferenceEquals(session, _session))
                return;

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

    // --- Projects ------------------------------------------------------------
    //
    // A session per project, the way it is a window per project in every IDE:
    // the asset content root is fixed at session birth, so opening a different
    // project closes the running session and launches a fresh one over the new
    // project's Assets folder. Every flow below confirms unsaved work BEFORE
    // touching anything.

    private void OnNewProjectClicked(object? sender, RoutedEventArgs e) => _ = CreateProjectFlowAsync();
    private void OnOpenProjectClicked(object? sender, RoutedEventArgs e) => _ = OpenProjectFlowAsync();

    private async void OnCloseProjectClicked(object? sender, RoutedEventArgs e)
    {
        // Guarded on the VIEWPORT, not the session: a session that failed to
        // start leaves the viewport up with no session behind it, and this is
        // the verb that has to work in exactly that state.
        if (_viewport is null) return;
        if (!await ConfirmDiscardAsync("closing the project")) return;

        CloseSessionView();
        _document.SetProject(null);
        _document.MarkNew();
        _shell.SetMessage(string.Empty);
    }

    private async Task CreateProjectFlowAsync()
    {
        if (!await ConfirmDiscardAsync("creating a new project")) return;

        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Folder to create the project in", AllowMultiple = false });
        if (picked.Count == 0) return;

        string? name = await NameDialog.AskAsync(
            this, "New project", "Name for the project:", "MyGame");
        if (name is null) return;

        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _shell.SetError("A project name has to work as a folder name.");
            return;
        }

        string root = Path.Combine(picked[0].Path.LocalPath, name);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            // Refused for ANY non-empty folder, not just one with a manifest:
            // scaffolding adopts whatever it finds, so pointing "create" at an
            // existing folder of files would quietly claim them as a project
            // (and beside an existing manifest it writes a second one, which
            // Open then refuses). Creating means creating.
            _shell.SetError($"'{root}' already exists and is not empty; open it as a project, or pick another name.");
            return;
        }

        ProjectLayout layout;
        try
        {
            layout = ProjectLayout.Create(root, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFormatException)
        {
            _shell.SetError($"Could not create the project: {ex.Message}");
            return;
        }

        OpenProjectLayout(layout);
    }

    private async Task OpenProjectFlowAsync()
    {
        if (!await ConfirmDiscardAsync("opening another project")) return;

        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Open project folder", AllowMultiple = false });
        if (picked.Count == 0) return;

        OpenProjectAt(picked[0].Path.LocalPath);
    }

    private async Task OpenRecentProjectAsync(RecentProject recent)
    {
        if (!Directory.Exists(recent.Path))
        {
            // Forgotten rather than left to fail on every click: the folder
            // moved or was deleted, and a card that errors forever is worse
            // than one that says goodbye once.
            _settings.ForgetProject(recent.Path);
            _settings.Save(_logger);
            StartView.ShowRecents(_settings.RecentProjects);
            _shell.SetError($"'{recent.Path}' is gone; removed it from the recent list.");
            return;
        }

        if (!await ConfirmDiscardAsync("opening another project")) return;
        OpenProjectAt(recent.Path);
    }

    private async Task OpenLooseMapFlowAsync()
    {
        // The start page's third door: one bundle, no project. A level
        // designer handed a folder should not have to scaffold a project to
        // look at it.
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Open map bundle", AllowMultiple = false });
        if (picked.Count == 0) return;

        string path = picked[0].Path.LocalPath;
        if (!MapBundle.IsBundle(path))
        {
            _shell.SetError($"That folder is not a map bundle: it has no {MapFormat.DocumentFileName}.");
            return;
        }

        _document.SetProject(null);
        LaunchSession(new SessionLaunch(null, null, Path.GetFullPath(path)));
    }

    /// <summary>Opens the project at a path, replacing any running session.</summary>
    private void OpenProjectAt(string path)
    {
        ProjectLayout layout;
        try
        {
            layout = ProjectLayout.Open(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ProjectFormatException)
        {
            _shell.SetError($"Could not open the project: {ex.Message}");
            return;
        }

        OpenProjectLayout(layout);
    }

    private void OpenProjectLayout(ProjectLayout layout)
    {
        CloseSessionView();

        _document.SetProject(layout);
        _document.MarkNew();

        _settings.TouchProject(layout.Root, layout.Project.Name, DateTime.UtcNow);
        _settings.Save(_logger);
        StartView.ShowRecents(_settings.RecentProjects);

        // Opening a project opens its startup map, because a project with a
        // level in it and an empty viewport is a state nobody asked for. A
        // manifest naming a bundle that is not there is said out loud and the
        // session still starts: the person who can fix the manifest needs the
        // editor open to do it.
        string? mapToOpen = null;
        if (layout.Project.StartupMap is { Length: > 0 } startup)
        {
            string resolved = layout.Resolve(startup);
            if (MapBundle.IsBundle(resolved))
                mapToOpen = resolved;
            else
                _shell.SetError($"The project's startup map '{startup}' is not on disk; starting on a baseplate.");
        }

        LaunchSession(new SessionLaunch(layout, layout.AssetsPath, mapToOpen));
        RefreshProjectMaps();
    }

    /// <summary>
    /// Rebuilds the maps panel: the manifest's list in the author's order,
    /// then whatever is on disk that the manifest does not name.
    /// </summary>
    private void RefreshProjectMaps()
    {
        _shell.ProjectMaps.Clear();

        if (_document.Project is not { } project)
        {
            _shell.HasProject = false;
            return;
        }

        _shell.HasProject = true;
        string? startup = project.Project.StartupMap;

        // Keyed on the normalised form, so a hand-authored backslash spelling
        // and the discovered forward-slash one are one row, not two.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string relative in project.Project.Maps)
        {
            if (!seen.Add(relative.Replace('\\', '/'))) continue;
            _shell.ProjectMaps.Add(new ProjectMapRow(
                relative,
                MapDisplayName(relative),
                IsStartup: startup is not null && ManifestPathsEqual(relative, startup),
                IsUnlisted: false));
        }

        foreach (string relative in project.DiscoverMaps())
        {
            if (!seen.Add(relative.Replace('\\', '/'))) continue;
            _shell.ProjectMaps.Add(new ProjectMapRow(
                relative, MapDisplayName(relative), IsStartup: false, IsUnlisted: true));
        }
    }

    private static string MapDisplayName(string projectRelative) =>
        Path.GetFileNameWithoutExtension(projectRelative.Replace('/', Path.DirectorySeparatorChar));

    private async Task OpenProjectMapAsync(ProjectMapRow row)
    {
        if (_document.Project is not { } project || _session is null) return;

        string resolved = project.Resolve(row.RelativePath);
        if (!MapBundle.IsBundle(resolved))
        {
            _shell.SetError($"'{row.RelativePath}' is in the manifest but not on disk.");
            return;
        }

        // Clicking the map that is already open must not discard-and-reload.
        if (_document.MapPath is { } current &&
            string.Equals(Path.GetFullPath(resolved), current, StringComparison.OrdinalIgnoreCase))
        {
            _shell.SetMessage($"{row.Name} is already open");
            return;
        }

        if (!await ConfirmDiscardAsync($"opening {row.Name}")) return;
        OpenMapAt(resolved);
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
            // See NewMap for the session boundary guard.
            if (!ReferenceEquals(session, _session))
                return;

            if (error is not null)
            {
                _shell.SetError($"Could not save the map: {error.Message}");
                return;
            }

            _document.MarkSaved(bundlePath);
            ResetDirtyBaseline();

            string manifestNote = UpdateManifestAfterSave();

            // An incomplete save is still a save: the scene held something the
            // format cannot name, such as a mesh built in code. Reported rather
            // than silent, because the alternative is a map that quietly forgets
            // a prop.
            _shell.SetMessage(report?.Describe() is { } lost
                ? $"Saved {_document.MapLabel}.{manifestNote} {lost}"
                : $"Saved {_document.MapLabel}.{manifestNote}");
        }));
    }

    /// <summary>
    /// Adds a just-saved map to the open project's manifest, and makes it the
    /// startup map when the project had none. Returns a short note for the
    /// status line, or an empty string when nothing applied.
    /// </summary>
    /// <remarks>
    /// <b>This is the write-back half of the maps story.</b> The manifest is
    /// the author's ordered list and what a cook bakes; a map saved into
    /// <c>Maps/</c> and never listed would run in the editor and silently miss
    /// the shipped game. A map saved OUTSIDE the project folder is legal and
    /// deliberately not listed â€” <see cref="EditorDocument.MapPathWithinProject"/>
    /// answers that. Removal stays a hand edit: the editor adds what you save
    /// and never deletes an entry, because the manifest is the author's file.
    /// </remarks>
    private string UpdateManifestAfterSave()
    {
        if (_document.Project is not { } stale)
            return string.Empty;

        if (_document.MapPathWithinProject() is not { } relative)
            return string.Empty;

        // Re-read from DISK and edit that, never the in-memory copy. The
        // manifest is the author's file â€” the format's whole promise is that a
        // person edits it in VS Code and the editor does not fight them â€” and
        // writing the copy loaded at open time would silently revert every
        // hand edit made since. Re-reading also makes a retry work after a
        // failed write: the fresh read still lacks the entry, so it is added
        // and saved again rather than assumed done.
        ProjectLayout project;
        try
        {
            project = ProjectLayout.Open(stale.ManifestPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ProjectFormatException)
        {
            _logger.LogWarning(ex, "Saved the map but could not re-read the project manifest");
            _shell.SetError("The map saved, but the project manifest could not be read to list it.");
            return string.Empty;
        }

        bool listed = project.Project.Maps.Any(m => ManifestPathsEqual(m, relative));
        bool becameStartup = false;

        if (!listed)
            project.Project.Maps.Add(relative);

        if (string.IsNullOrEmpty(project.Project.StartupMap))
        {
            project.Project.StartupMap = relative;
            becameStartup = true;
        }

        try
        {
            // A byte-identical manifest is left untouched by Save itself, so
            // calling it when nothing changed costs a read and writes nothing.
            project.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Saved the map but could not update the project manifest");
            _shell.SetError("The map saved, but the project manifest could not be written.");
            return string.Empty;
        }

        // The shell adopts the fresh layout: the maps panel now reflects the
        // file as it is, hand edits included.
        _document.SetProject(project);
        RefreshProjectMaps();
        return !listed
            ? becameStartup ? " Added to the project as its startup map." : " Added to the project."
            : string.Empty;
    }

    // Manifest paths are authored text: the codec writes forward slashes but a
    // hand edit legitimately arrives with backslashes or different case, and
    // treating those as a different map duplicates the entry.
    private static bool ManifestPathsEqual(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

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
