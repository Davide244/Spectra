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
using Spectra.Kitchen.Diagnostics;
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
using SpectraEngine.Editor.Shell.Ribbon;
using SpectraEngine.Editor.Viewport;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpectraEngine.Editor;

/// <summary>
/// The shell window: the start page, the tabbed command bar, the docked
/// panels around a pinned viewport, and the status bar.
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
    // thread's publish path.
    //
    // SIZED FOR THE FASTER OF THE TWO PUBLISH RATES. The host raises its rate
    // to about 120Hz while a gesture is in flight, so a bound stated in COUNT
    // means something different depending on what the user is doing: 240 was
    // eight seconds at rest and under two while dragging - and dragging is
    // exactly when a shell is most likely to fall behind. Five seconds at the
    // interactive rate, half a minute at rest.
    private const int MaxQueuedSnapshots = 600;
    private int _queuedSnapshots;
    private volatile bool _droppedSnapshots;

    // Set on the render thread when a snapshot is published, cleared on the UI
    // thread as the pump begins. One post per UI frame however many snapshots
    // arrive inside it.
    private int _pumpPosted;

    private ContentPanel? _contentView;
    private OutputPanel? _outputView;
    private ConsolePanel? _consoleView;
    private ConsoleCommands? _console;

    private EditorSession? _session;
    private SceneTreeModel? _tree;
    private IRenderSurface? _surface;
    private FrameSnapshot _latest = FrameSnapshot.Empty;

    // The last snapshot the pump fully applied, so the 8 ms watchdog — which
    // exists only for the cursor-mode latch — stops re-applying an unchanged
    // one. Reference identity is the right comparison: the engine publishes a
    // fresh instance per snapshot.
    private FrameSnapshot _lastApplied = FrameSnapshot.Empty;
    private bool _stopping;
    private int _lastUndoDepth;
    private int _lastRedoDepth;

    // The per-user shell state: today, the recent projects the start page
    // shows. Loaded once; written whenever a project is opened or created.
    private readonly EditorSettings _settings;

    // The live viewport control, created when a session launches and removed
    // when it closes: a native child's window and a composited viewport's
    // imported texture both live exactly as long as the control is in the
    // visual tree, so "no session" and "no viewport control" are the same state
    // on purpose.
    private IEngineViewport? _viewport;

    // Where the viewport pane is living right now. Reset at every session close
    // so a launch always starts from the same place, whichever way the last one
    // went.
    private ViewportPlacement _placement = ViewportPlacement.PinnedCell;

    // The viewport tool's content for the window's whole life; the pane moves
    // into it for a composited session and back out at the close. A Border
    // rather than the pane itself, so Dock is never handed a content change
    // after its layout is built.
    private readonly Border _viewportDockHost = new();

    // Where the pane sits among the editor grid's children, so a session that
    // docked it puts it back in its own slot rather than on top of everything.
    private readonly int _viewportPaneIndex;

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

    // ─── The ribbon ──────────────────────────────────────
    //
    // One live instance per page for the window's whole life, moved between
    // the inline host and the flyout's rather than rebuilt: a page carries an
    // event-wired snap field and a DataContext, and a template would rebuild
    // both on every collapse.
    private readonly RibbonBuildTab _buildTab = new();
    private readonly RibbonViewTab _viewTab = new();
    private readonly Dictionary<string, RibbonTabView> _ribbonPages = new(StringComparer.Ordinal);

    // The collapse state machine's current value. Pure transitions live in
    // RibbonSurface; this window holds the value and mirrors it into controls.
    private RibbonSurfaceState _ribbon;

    // Applying the state closes the popup, which raises Closed, which would
    // apply the state again. One flag rather than a subtler dance, because the
    // re-entry is real and its symptom is a flyout that will not open.
    private bool _applyingRibbonState;

    /// <summary>Creates the window and wires the viewport's lifetime to the engine's.</summary>
    public MainWindow()
    {
        InitializeComponent();

        DataContext = _shell;

        // The neutral place focus can land when a field must blur before a
        // document chord runs — see CommitFocusedEdit.
        Focusable = true;

        _loggerFactory = new SerilogLoggerFactory(Serilog.Log.Logger, dispose: false);
        _logger = _loggerFactory.CreateLogger<MainWindow>();

        _settings = EditorSettings.Load(_logger);

        // --viewport= is a preference rather than a one-run override: there is
        // no UI for this yet, so the switch is the only way to say it, and a
        // switch whose effect vanished on the next launch would mean typing it
        // forever. --viewport=auto is how it is put back.
        if (ViewportModePolicy.RequestedMode(Program.StartupArgs) is { } requestedViewport)
        {
            _settings.SetViewportMode(requestedViewport);
            _settings.Save(_logger);
            _logger.LogInformation(
                "Viewport mode set to {Mode} by the command line ({Usage}).",
                ViewportModePolicy.NameOf(requestedViewport), ViewportModePolicy.Usage);
        }

        VersionLabel.Text = SpectraEngine.Core.EngineInfo.VersionString;

        // The start page raises intents; the window owns every consequence,
        // because it owns the storage provider, the dialogs and the session.
        StartView.NewProjectRequested += () => _ = CreateProjectFlowAsync();
        StartView.OpenProjectRequested += () => _ = OpenProjectFlowAsync();
        StartView.OpenMapRequested += () => _ = OpenLooseMapFlowAsync();
        StartView.RecentProjectPicked += recent => _ = OpenRecentProjectAsync(recent);
        StartView.RecentProjectForgotten += ForgetRecent;
        StartView.RecentProjectRevealRequested += recent => RevealInExplorer(recent.Path);
        RefreshRecents();

        // ONE factory, shared by every dock control, assigned before the
        // window attaches. All three clauses are load-bearing: without any
        // factory, DockControl.Initialize returns before InitLayout and the
        // dock columns render EMPTY with every docking gesture dead (proven
        // with a headless repro against the shipped package); with one
        // factory per control, a drag's target list is that factory's own
        // DockControls, so a panel could never cross from the left dock to
        // the right one. The centre dock is the fourth and joins the same one,
        // or a composited viewport could be dragged nowhere and nothing could
        // be dragged beside it.
        var dockFactory = new Dock.Model.Avalonia.Factory();
        LeftDock.Factory = dockFactory;
        RightDock.Factory = dockFactory;
        BottomDock.Factory = dockFactory;
        CenterDock.Factory = dockFactory;

        // The viewport tool's content is a Border this window owns for its whole
        // life, and the PANE moves in and out of it. Dock never sees a content
        // change, which is the same reason every other tool's content is
        // assigned once as a live instance: a Tool whose Content is reassigned
        // after its layout has been built is a shape nothing here has tested,
        // and the failure mode of guessing wrong is a blank pane with a running
        // engine behind it.
        SetToolContent(ViewportTool, _viewportDockHost);
        _viewportPaneIndex = EditorView.Children.IndexOf(ViewportPane);

        // The resting layout, written rather than assumed: XAML sets CanPin on
        // the six panel tools and the viewport tool has no attribute to set, so
        // without this the one tool nobody may pin beside a native child would
        // be the only one offering the glyph.
        ApplyPlacement(ViewportPlacement.PinnedCell);

        // The panels are built HERE and handed to the dock tools as live
        // controls: the dock's builder returns a Control content instance
        // as-is, so one event-wired panel survives every re-dock and float.
        // DataContext is set explicitly rather than inherited, because a
        // floated panel leaves this window's logical tree and inherited
        // bindings would go quietly null.
        _sceneView = new ScenePanel
        {
            Logger = _loggerFactory.CreateLogger<ScenePanel>(),
        };
        _sceneView.SelectionRequested += ids => _session?.SelectMany(ids);
        _sceneView.RenameRequested += (id, name) => _session?.Rename(id, name);
        _sceneView.CommandRequested += command => _session?.Post(command);
        _sceneView.FrameRequested += () => _session?.Post(EditorCameraCommand.FrameSelection);
        _sceneView.ReparentRequested += (ids, parentId, index) => _session?.Reparent(ids, parentId, index);
        SetToolContent(SceneTool, _sceneView);

        _propertiesView = new PropertiesPanel();
        _propertiesView.EscapePressed += () => _viewport?.FocusEngine();
        SetToolContent(PropertiesTool, _propertiesView);

        _mapsView = new MapsPanel();
        _mapsView.MapClicked += row => _ = OpenProjectMapAsync(row);
        _mapsView.SetStartupRequested += SetStartupMap;
        _mapsView.RevealRequested += row =>
        {
            if (_document.Project is { } project)
                RevealInExplorer(project.Resolve(row.RelativePath));
        };
        SetToolContent(MapsTool, _mapsView);

        // ─── The bottom region ────────────────────────────
        //
        // The three panels an editor is expected to have and this one did not:
        // somewhere to see the project's files, somewhere its diagnostics
        // survive being replaced, and a line to type a verb into.

        _shell.Content = new ContentBrowserModel(_loggerFactory.CreateLogger<ContentBrowserModel>());

        _contentView = new ContentPanel();
        _contentView.EntryActivated += OnContentActivated;
        SetToolContent(ContentTool, _contentView);

        _outputView = new OutputPanel();
        SetToolContent(OutputTool, _outputView);

        _consoleView = new ConsolePanel();
        _consoleView.CommandSubmitted += OnConsoleCommand;
        SetToolContent(ConsoleTool, _consoleView);

        // Every entry resolves to a verb a button or a key chord also sends,
        // which is what keeps the console from being a second path into the
        // editor. The lambdas return false when there is no session, and the
        // console says so rather than appearing to have worked.
        _console = new ConsoleCommands(
            postHost: command => _session is { } s && Post(() => s.Post(command)),
            postGizmo: command => _session is { } s && Post(() => s.Post(command)),
            postCamera: command => _session is { } s && Post(() => s.Post(command)),
            insert: kind => _session is { } s && Post(() => s.Insert(kind)),
            setSnap: (tool, value) => _session is { } s && Post(() => s.SetSnapIncrement(tool, value)),
            setPipeline: name => _session is { } s && Post(() => s.Host.RequestPipeline(name)),
            setPlaying: playing =>
            {
                _shell.RequestPlaying(playing);
                _session?.Host.RequestPlayMode(playing);
            });

        static bool Post(Action action)
        {
            action();
            return true;
        }

        // The title is the only place the shell says what is open and whether
        // it is saved, so it follows the document rather than being set once.
        _document.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(EditorDocument.Title))
                RefreshDocumentIdentity();

            // The content browser is fixed to the OPEN PROJECT's assets folder,
            // exactly as the session's content root is: opening a different
            // project is a different content root, and a browser still showing
            // the previous one would offer files this scene cannot resolve.
            if (args.PropertyName is nameof(EditorDocument.Project))
                _shell.Content?.SetRoot(_document.Project?.AssetsPath);
        };
        RefreshDocumentIdentity();
        _shell.AboutLabel = $"Version {SpectraEngine.Core.EngineInfo.VersionString}";

        _shell.Properties = new PropertyPanelModel(
            OnPropertyEdit,
            name => _session?.BeginPropertyGesture(name),
            commit => _session?.EndPropertyGesture(commit),
            OnEntityConnectionsEdit);

        // The pipeline dropdown's user choice, forwarded as a request. Wired
        // once: the session is resolved when the event fires, so it follows
        // whichever session is live.
        _shell.PipelineRequested += name => _session?.Host.RequestPipeline(name);

        // The ribbon carries ONE snap field, and it belongs to whichever tool
        // is live, so a tool switch has to re-read the increment into it.
        // Without this the box keeps showing the previous tool's number beside
        // the new tool's unit, which is a worse lie than showing nothing.
        _shell.GizmoModeChanged += () => RefreshSnapField(_latest);

        BuildRibbon();

        // Document chords as real key bindings, so they also work while an
        // Avalonia control has focus - the tree, the filter, a property field.
        // The viewport intercepts the same four itself (ShellChord), because
        // while IT has focus Avalonia sees no keyboard at all; two routes, one
        // handler each, is what makes Ctrl+S work everywhere.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.N, KeyModifiers.Control),
            Command = new RelayCommand(() => { CommitFocusedEdit(); OnNewMapClicked(this, new RoutedEventArgs()); }),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.O, KeyModifiers.Control),
            Command = new RelayCommand(() => { CommitFocusedEdit(); OnOpenMapClicked(this, new RoutedEventArgs()); }),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.S, KeyModifiers.Control),
            Command = new RelayCommand(() => { CommitFocusedEdit(); OnSaveClicked(this, new RoutedEventArgs()); }),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift),
            Command = new RelayCommand(() => { CommitFocusedEdit(); OnSaveAsClicked(this, new RoutedEventArgs()); }),
        });

        // History, window-wide. The engine keymap owns Ctrl+Z only while the
        // native viewport has focus, and the tree owns it only while the tree
        // does, which left the chord dead in the property panel and the maps
        // list - the two places a person is most likely to have just made the
        // edit they want back. A focused field commits first, so undo takes
        // back the value that was typed rather than the one before it.
        //
        // Not blocked while a field is focused: a TextBox handles Ctrl+Z for
        // its own text and marks the event handled, so its editing history
        // still wins where it should.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Z, KeyModifiers.Control),
            Command = new RelayCommand(() => { CommitFocusedEdit(); _session?.Post(EditorHostCommand.Undo); }),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Y, KeyModifiers.Control),
            Command = new RelayCommand(() => { CommitFocusedEdit(); _session?.Post(EditorHostCommand.Redo); }),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift),
            Command = new RelayCommand(() => { CommitFocusedEdit(); _session?.Post(EditorHostCommand.Redo); }),
        });

        // Insert, window-wide - and intercepted in the viewport as well
        // (ShellChord), because those are the two halves of one shortcut. The
        // engine keymap has no chord for an insert, so a window binding alone
        // would fire only while an Avalonia control had focus: that is, only
        // while the user was NOT looking at the place they wanted to insert
        // into. Two routes, one handler each, exactly as the document chords
        // do it.
        AddChord(Key.D1, KeyModifiers.Control, () => _session?.Insert(InsertKind.WorldBrush));
        AddChord(Key.D2, KeyModifiers.Control, () => _session?.Insert(InsertKind.PartBrush));
        AddChord(Key.D3, KeyModifiers.Control, () => _session?.Insert(InsertKind.SubtractiveBrush));
        AddChord(Key.D4, KeyModifiers.Control, () => _session?.Insert(InsertKind.PointLight));

        // Mode verbs, window-wide, for the same reason the document chords are:
        // the engine only sees the keyboard while its native child window holds
        // focus, so F8 did nothing whenever a tree row or a property field had
        // been clicked - while the button's own tooltip went on promising it.
        // Neither of these is a scene edit, so both are safe from anywhere.
        AddChord(Key.F8, KeyModifiers.None, () => _session?.Host.RequestPlayMode(!_latest.IsPlaying));
        AddChord(Key.F, KeyModifiers.None, () => _session?.Post(EditorCameraCommand.FrameSelection));

        // Drop a project or a level folder anywhere on the window. The engine's
        // viewport is a native child and never sees Avalonia's drag events, so
        // the drop target is the window itself and the chrome around the
        // viewport is where it lands.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // A WATCHDOG, not the clock the shell runs on. The pump is driven by
        // the engine publishing (OnFrameCompleted posts one), so it does work
        // when and only when there is work; this catches the one thing that is
        // not snapshot-driven - the cursor-mode latch, where landing 33ms late
        // is a visible jump at the start of every freelook - and keeps the
        // shell alive if publishing ever stops.
        //
        // 8ms is a real 8ms only because TimerResolution asks for it. Left
        // alone, Windows rounds an 8ms timer up to 15.6 and reports success.
        //
        // Normal outranks Render in Avalonia's priority order, so a value the
        // pump writes reaches the screen in the SAME frame rather than the
        // next. (This is the opposite of the WPF ordering people expect, and
        // getting it wrong costs a whole frame.)
        _pump = new DispatcherTimer(
            TimeSpan.FromMilliseconds(8), DispatcherPriority.Normal, OnPump);

        // The one frame customisation this shell makes: paint the OS caption to
        // match the window instead of the user's accent colour. It is a DWM
        // attribute rather than a custom title bar, so it costs nothing in
        // hit-testing, keeps Aero Snap and the maximise flyout, and simply does
        // nothing on Windows versions that do not know the attribute.
        Opened += (_, _) =>
        {
            DarkCaption.Apply(this, _logger);

            // The interop probe runs INSTEAD of opening anything, because it is
            // a measurement of this machine rather than a feature: it needs a
            // real compositor (there is no headless form of the question) and
            // it must not compete with an engine session for the GPU while it
            // asks. The window closes itself when it is done, so the switch can
            // be run from a script on five machines.
            if (InteropProbe.Requested(Program.StartupArgs))
            {
                _ = RunInteropProbeAsync();
                return;
            }

            OpenFromStartupArgs();
        };

        if (!EngineViewports.IsSupported)
        {
            _shell.SetError(
                "This platform cannot host the viewport yet: the embedded surface is Windows-only in v1.");
        }
        else
        {
            // The status bar exists before a session does, and an empty strip
            // along the bottom of a launcher is 26 pixels of nothing. One line
            // saying what to do next costs the same space.
            _shell.SetMessage("Open a project to start building, or drop one on this window.");
        }
    }

    /// <summary>
    /// Adds one window-level chord that commits any focused field first.
    /// </summary>
    /// <remarks>
    /// The commit is not optional. A key binding fires with focus still in
    /// whatever box the user was typing in, so without it Ctrl+S writes the
    /// bundle WITHOUT the number just typed and then reports "Saved", and F8
    /// enters play mode leaving a half-typed value in a field that is about to
    /// stop taking refreshes.
    /// </remarks>
    private void AddChord(Key key, KeyModifiers modifiers, Action run)
    {
        // A window-level binding on a PRINTABLE key with no modifier fires even
        // while a text box has focus: Avalonia's TextBox marks KeyDown handled
        // only for caret and editing keys, so an ordinary letter bubbles to the
        // window and the binding runs. Typing "Floor" into a rename box would
        // therefore commit the half-typed name on the "F" (CommitFocusedEdit
        // blurs, which commits) and frame the selection, and the same letter
        // would break every filter search containing it. Function keys carry no
        // such risk, which is why the guard is on the key rather than on the
        // binding.
        bool printable = modifiers == KeyModifiers.None
            && (key is >= Key.A and <= Key.Z || key is >= Key.D0 and <= Key.D9);

        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(key, modifiers),
            Command = new RelayCommand(() =>
            {
                if (printable && FocusManager?.GetFocusedElement() is TextBox)
                    return;

                CommitFocusedEdit();
                run();
            }),
        });
    }

    /// <summary>
    /// Mirrors the document's identity onto the shell model and the OS title.
    /// </summary>
    /// <remarks>
    /// <b>The title names the app once and the level once.</b> It used to be
    /// "<c>{map} - {project} - Spectra Editor</c>" unconditionally, which on
    /// the common case of a project whose startup level shares its name renders
    /// "Demo - Demo - Spectra Editor", and with nothing open at all renders
    /// "untitled - no project - Spectra Editor": two placeholders and a product
    /// name, describing a window that is showing a launcher.
    /// </remarks>
    private void RefreshDocumentIdentity()
    {
        string project = _document.Project?.Project.Name ?? string.Empty;
        _shell.SetDocument(_document.MapLabel, project, _document.IsDirty);

        if (!_shell.HasSession)
        {
            Title = "Spectra Editor";
            return;
        }

        string mark = _document.IsDirty ? "*" : string.Empty;
        Title = project.Length == 0 || string.Equals(project, _document.MapLabel, StringComparison.Ordinal)
            ? $"{_document.MapLabel}{mark} - Spectra Editor"
            : $"{_document.MapLabel}{mark} - {project} - Spectra Editor";
    }

    /// <summary>
    /// Fills the View menu's Renderer submenu from the pipelines the running
    /// backend actually offers.
    /// </summary>
    /// <remarks>
    /// Built in code rather than templated, because a generated
    /// <c>MenuItem</c> container gives no place to hang a click handler, and
    /// because which pipelines exist is a property of the renderer that
    /// started rather than of the shell. Radio-checked: this is a choice of
    /// one, and a list of checkboxes would suggest otherwise.
    /// </remarks>
    private void RefreshRendererMenu()
    {
        RendererMenu.Items.Clear();

        foreach (string name in _shell.PipelineNames)
        {
            string pipeline = name;
            var item = new MenuItem
            {
                Header = pipeline,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "renderer",
                IsChecked = string.Equals(pipeline, _shell.PipelineName, StringComparison.Ordinal),
            };
            item.Click += (_, _) => _session?.Host.RequestPipeline(pipeline);
            RendererMenu.Items.Add(item);
        }

        RendererMenu.IsEnabled = RendererMenu.Items.Count > 0;
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

            if (TryOpenPath(arg))
                return;
        }
    }

    /// <summary>
    /// Opens whatever a path names, if it names anything this shell can open.
    /// </summary>
    /// <returns>True when the path was recognised and a launch has started.</returns>
    /// <remarks>
    /// <b>One classifier, two callers.</b> The rules are the same whether a
    /// path arrives on the command line or under a dropped file, and writing
    /// them twice is how a manifest becomes double-clickable but not
    /// droppable.
    /// </remarks>
    private bool TryOpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (File.Exists(path) &&
            path.EndsWith(ProjectFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            OpenProjectAt(path);
            return true;
        }

        if (!Directory.Exists(path))
            return false;

        if (MapBundle.IsBundle(path))
        {
            _document.SetProject(null);
            LaunchSession(new SessionLaunch(null, null, Path.GetFullPath(path)));
            return true;
        }

        if (Directory.GetFiles(path, "*" + ProjectFormat.Extension).Length >= 1)
        {
            OpenProjectAt(path);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Takes a project or a level dropped onto the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gesture people try first.</b> Everything this shell opens is a
    /// FOLDER, and the only routes in were a modal folder picker and a command
    /// line - while the classification a drop needs was already written for
    /// startup arguments.
    /// </para>
    /// <para>
    /// <b>Guarded by the same unsaved check every other open goes through</b>,
    /// and refused outright while a run is in progress: a drop is easy to make
    /// by accident and replacing the scene under a character somebody is
    /// walking around in is not a thing to do on one gesture.
    /// </para>
    /// </remarks>
    private async Task DropAsync(IEnumerable<Avalonia.Platform.Storage.IStorageItem> items)
    {
        if (_latest.IsPlaying)
        {
            _shell.SetMessage("Stop the run before opening something else.");
            return;
        }

        foreach (Avalonia.Platform.Storage.IStorageItem item in items)
        {
            string? path = item.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
                continue;

            // Classified BEFORE the unsaved-work prompt, so dropping something
            // unopenable does not first ask about discarding work.
            bool openable = (File.Exists(path)
                    && path.EndsWith(ProjectFormat.Extension, StringComparison.OrdinalIgnoreCase))
                || (Directory.Exists(path)
                    && (MapBundle.IsBundle(path)
                        || Directory.GetFiles(path, "*" + ProjectFormat.Extension).Length >= 1));

            if (!openable)
                continue;

            if (!await ConfirmDiscardAsync("opening what you dropped"))
                return;

            if (TryOpenPath(path))
                return;
        }

        _shell.SetError("That is not a Spectra project or level folder.");
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
        if (_viewport is not null || _launchInFlight)
        {
            // Callers close the running session first; stacking two engines
            // over one window is never what anyone meant. The in-flight flag
            // covers the gap the machine measurement opens: for as long as the
            // compositor is being asked, there is no viewport yet and the field
            // above would let a second launch through.
            _logger.LogWarning("A session is already running; ignoring the launch request");
            return;
        }

        if (!EngineViewports.IsSupported)
        {
            _shell.SetError(
                "This platform cannot host the viewport yet: the embedded surface is Windows-only in v1.");
            return;
        }

        _launchInFlight = true;
        _ = LaunchSessionAsync(launch);
    }

    // True from the moment a launch is asked for until its viewport is in the
    // tree. See LaunchSession.
    private bool _launchInFlight;

    // What the machine turned out to be, for the session that is running now.
    // Read again when it closes, because the colour verdict and the machine's
    // identity are both part of whether the session counted as green.
    private ViewportCapabilities _sessionCapabilities = ViewportCapabilities.NotMeasured;
    private bool _sessionIsComposited;
    private bool _sessionFaulted;
    private int _sessionDebugLayerErrors;

    /// <summary>
    /// Chooses the viewport, measuring the machine first when the choice
    /// depends on it, and then launches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rehearsal import happens BEFORE the session is constructed, and
    /// that ordering is the whole reason this is asynchronous.</b> A compositor
    /// that is going to refuse the engine's texture must be discovered while
    /// nothing is running against it: found afterwards, it is an engine with a
    /// render thread, a device and a scene behind a pane that will never show a
    /// frame, torn down out of the order the keyed-mutex hand-over requires.
    /// </para>
    /// <para>
    /// <b>Nothing here may throw into the caller.</b> Every failure has the same
    /// answer - the native child, with the reason said out loud - because a
    /// launch that reported a driver problem by not opening an editor would be
    /// the silent fallback this stage exists to remove.
    /// </para>
    /// </remarks>
    private async Task LaunchSessionAsync(SessionLaunch launch)
    {
        ViewportDecision decision = new(
            UseComposition: false,
            ViewportChoiceReason.ExplicitNative,
            ViewportModePolicy.Describe(ViewportChoiceReason.ExplicitNative));

        try
        {
            GraphicsBackend backend = ResolveRequestedBackend();
            ViewportPreference preference = _settings.ViewportPreference;
            var capabilities = ViewportCapabilities.NotMeasured;

            if (ViewportModePolicy.RequiresMeasurement(preference, backend))
            {
                capabilities = await ViewportProbe.MeasureAsync(
                    this, backend, _loggerFactory.CreateLogger(nameof(ViewportProbe)));
            }

            decision = ViewportModePolicy.Decide(preference, capabilities, backend);
            _sessionCapabilities = capabilities;

            // AFTER the decision and whichever way it went, but only with a
            // machine that was actually measured. After, because a count earned
            // on another adapter or driver is exactly what produces the
            // AdapterChanged and DriverChanged reasons and must still be there
            // to produce them; whichever way it went, because leaving a stale
            // count on disk would report the same change forever instead of
            // starting the run again. An unmeasured launch is skipped, or it
            // would overwrite a real history with empty strings, which reads
            // afterwards as an adapter that changed.
            if (capabilities.AdapterLuid.Length > 0)
            {
                _settings.RebaseViewport(capabilities.AdapterLuid, capabilities.DriverVersion);
                _settings.Save(_logger);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Choosing the viewport failed; falling back to the native child");
        }
        finally
        {
            _launchInFlight = false;
        }

        // Named in the log whichever way it went, and so is the layout that
        // follows from it. A composited pane and a native child render the same
        // picture, so a fallback nobody announced only shows up weeks later as
        // an overlay that mysteriously does not draw - and a pane that silently
        // got the pinned layout shows up as a tab that refuses to be dragged.
        ViewportPlacement placement = ViewportLayout.For(decision);

        _logger.LogInformation(
            "Viewport: {Choice} ({Reason}) - {Explanation} Layout: {Placement} - {Rule}",
            decision.UseComposition ? "composition" : "native child",
            decision.Reason,
            decision.Explanation,
            placement,
            ViewportLayout.Describe(placement));

        StartViewport(launch, decision.UseComposition, placement);
    }

    private void StartViewport(SessionLaunch launch, bool composited, ViewportPlacement placement)
    {
        _pendingLaunch = launch;
        _sessionIsComposited = composited;
        _sessionFaulted = false;
        _sessionDebugLayerErrors = 0;

        IEngineViewport viewport = EngineViewports.Create(
            composited, _loggerFactory, _shell.SetError, OnViewportFailed);
        viewport.SurfaceCreated += OnSurfaceCreated;
        viewport.SurfaceDestroying += OnSurfaceDestroying;

        // The viewport hands up the document chords it intercepted, because
        // while it has focus the OS gives it the keyboard and Avalonia never
        // sees the menu accelerators at all.
        viewport.ShellChord += OnShellChord;
        viewport.ContextMenuRequested += OnViewportContextMenu;
        viewport.AssetDropped += OnViewportAssetDropped;
        viewport.AssetDragChanged += OnViewportAssetDragChanged;
        _viewport = viewport;

        // A one-millisecond timer for as long as a session is open. Without it
        // the 8ms pump silently becomes 15.6ms, which is most of the shell's
        // worst-case lag; with it the start page and a closed editor still cost
        // nothing.
        TimerResolution.Acquire(_logger);

        StartView.IsVisible = false;
        EditorView.IsVisible = true;
        _shell.HasSession = true;
        RefreshDocumentIdentity();

        // The pane goes to its home BEFORE the viewport control attaches, so the
        // first surface is created at the size it will actually be rather than
        // at whatever the other home measured.
        ApplyPlacement(placement);

        // Attach last: creating the native child is what eventually raises
        // SurfaceCreated, and everything above must be in place by then. The
        // engine focus makes the tool keys live from the first frame instead
        // of after a first click into the scene.
        // Index 0, so the drop overlay declared in the markup stays LAST in the
        // list and therefore on top. Children.Add would put the picture over
        // the overlay and nothing would report it.
        ViewportHost.Children.Insert(0, viewport.Control);
        viewport.FocusEngine();
    }

    /// <summary>
    /// Moves the viewport pane between its two homes and sets what the dock
    /// tools may do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pane is one control and it is moved, never duplicated.</b> Avalonia
    /// refuses a control with two parents, so it leaves the old home first; the
    /// pinned home is the editor grid itself, where its <c>Grid.Column</c> and
    /// <c>Grid.Row</c> ride on the control and survive the round trip, so
    /// putting it back is an <c>Add</c> and nothing else.
    /// </para>
    /// <para>
    /// <b>CanPin travels with the placement because it IS the airspace rule.</b>
    /// Dock draws a pinned flyout in this window's own Avalonia layer, which a
    /// native child composites over, so beside one the pin glyph is an invitation
    /// to make a panel invisible. Beside a composited pane the flyout draws like
    /// anything else, so it comes back - on every tool, not only on the viewport,
    /// because the constraint was never about the viewport's own header.
    /// </para>
    /// </remarks>

    /// <summary>
    /// Hands a dock tool its live content control, and its DataContext with it.
    /// </summary>
    /// <remarks>
    /// <b>One call sets both, because setting only one is not an error anywhere
    /// and the symptom is a pane of blank controls.</b> Dock supplies a tool's
    /// content presenter with its own DataContext (the <c>Tool</c>), and a
    /// floated tool leaves this window's logical tree entirely, so a content
    /// control that relies on inheritance resolves every binding against an
    /// object carrying none of the properties it names. Avalonia reports
    /// nothing for that: a failed binding leaves the target property at its own
    /// default, so <c>Text</c> goes empty, an <c>ItemsSource</c> goes empty, and
    /// <b><c>IsVisible</c> stays TRUE</b>, which is how the viewport header
    /// strip came to show every debug overlay chip at once while every value
    /// beside them was blank. Six panels set the DataContext inline and the
    /// seventh host, added when the viewport became dockable, did not; the two
    /// assignments live in one place now so a future tool cannot have one
    /// without the other.
    /// </remarks>
    private void SetToolContent(Dock.Model.Avalonia.Controls.Tool tool, Control content)
    {
        content.DataContext = _shell;
        tool.Content = content;
    }

    private void ApplyPlacement(ViewportPlacement placement)
    {
        ViewportPlacementRules rules = ViewportLayout.RulesFor(placement);

        if (_placement != placement)
        {
            if (rules.Docked)
            {
                EditorView.Children.Remove(ViewportPane);
                _viewportDockHost.Child = ViewportPane;
            }
            else
            {
                _viewportDockHost.Child = null;

                // Back at the index it was declared at, never appended. A Grid
                // draws its children in list order, and the splitters below the
                // pane in the XAML overhang into its cell by a pixel on purpose
                // - appended, the pane would be painted over the top of that
                // overhang and take a strip of the grab area with it, on a
                // control whose hit band has already been tuned twice.
                if (!EditorView.Children.Contains(ViewportPane))
                    EditorView.Children.Insert(_viewportPaneIndex, ViewportPane);
            }

            CenterDock.IsVisible = rules.Docked;
            _placement = placement;
        }

        ViewportTool.CanPin = rules.CanPin;
        ViewportTool.CanFloat = rules.CanFloat;
        ViewportTool.CanClose = ViewportLayout.ViewportCanClose;

        foreach (Dock.Model.Avalonia.Controls.Tool tool in PanelTools)
            tool.CanPin = rules.CanPin;
    }

    /// <summary>Every tool in the window that is not the viewport.</summary>
    private Dock.Model.Avalonia.Controls.Tool[] PanelTools =>
        [MapsTool, SceneTool, PropertiesTool, ContentTool, OutputTool, ConsoleTool];

    /// <summary>
    /// A composited viewport that was already running has stopped working.
    /// </summary>
    /// <remarks>
    /// <b>An error and a way out, never a hot swap.</b> Rebuilding the pane as a
    /// native child would tear down a live engine, destroy every GPU resource it
    /// owns and reshape the window under whatever the user was in the middle of,
    /// and it would leave one session's log describing two viewports - a bug
    /// report nobody can write. So the session says what happened and names the
    /// switch that avoids it, and the history remembers that this one was not
    /// green.
    /// </remarks>
    private void OnViewportFailed(ViewportChoiceReason reason)
    {
        _sessionFaulted = true;
        _shell.SetError($"The composited viewport failed: {ViewportModePolicy.Describe(reason)}.");
    }

    /// <summary>
    /// Folds the session that is ending into the composited history.
    /// </summary>
    /// <remarks>
    /// <b>Only a session that actually composited is recorded</b>, because a
    /// native one says nothing either way about the composited path. Green is
    /// three conditions and the third is the one that would be forgotten:
    /// see <see cref="ViewportModePolicy.IsSessionGreen"/>.
    /// </remarks>
    private void RecordSessionOutcome()
    {
        if (!_sessionIsComposited)
            return;

        bool green = ViewportModePolicy.IsSessionGreen(
            _sessionDebugLayerErrors, _sessionFaulted, _sessionCapabilities.CompareGreen);

        _settings.RecordCompositedSession(green);
        _settings.Save(_logger);

        _logger.LogInformation(
            "Composited session recorded as {Verdict}: {Errors} counted debug-layer error(s), " +
            "{Faults}, colour comparison {Compare}. {Count} of {Required} consecutive green session(s) " +
            "on this adapter and driver.",
            green ? "green" : "not green",
            _sessionDebugLayerErrors,
            _sessionFaulted ? "the hand-over faulted" : "no hand-over fault",
            _sessionCapabilities.CompareGreen ? "green" : "missing or red for this adapter and backend",
            _settings.ViewportPreference.GreenSessions,
            ViewportModePolicy.RequiredGreenSessions);

        _sessionIsComposited = false;
    }

    /// <summary>
    /// Tears the session and its viewport down and returns to the start page.
    /// Callers have already confirmed any unsaved work.
    /// </summary>
    private void CloseSessionView()
    {
        if (_viewport is not { } viewport)
            return;

        // Before the teardown, while the counters still describe the session
        // that ran.
        RecordSessionOutcome();

        // FIRST, and this is what tells a composited viewport that the detach
        // about to happen is the end rather than a re-dock. Without it the two
        // are indistinguishable from inside the control, and a dock drag would
        // stop the engine and build a second session on the re-attach - a new
        // scene, an empty history and the level gone, with nothing reporting an
        // error. The native child answers this with nothing at all: its surface
        // IS the HWND and the destroy below is what ends it.
        viewport.Shutdown();

        // The child leaves the tree: destroying the native window raises
        // SurfaceDestroying, which stops the engine before the HWND dies. The
        // explicit stop after it covers a viewport that never got a surface.
        ViewportHost.Children.Remove(viewport.Control);
        StopSession();

        // A drag in flight when a session closes leaves a prompt behind, and
        // the next session would open with a frame painted over its first
        // frame. The viewport clears it on its own Shutdown too; this is the
        // half that covers a viewport that never reached the tree.
        _shell.DropPrompt = ViewportDropPrompt.None;

        // The engine those requests were aimed at is gone: an unconfirmed tool
        // pick would otherwise still be pending when the next project opens,
        // and its first snapshots would be ignored - a fresh session showing
        // the previous session's tool, on a scene that never had it.
        _shell.ResetOptimisticState();
        TimerResolution.Release();

        viewport.SurfaceCreated -= OnSurfaceCreated;
        viewport.SurfaceDestroying -= OnSurfaceDestroying;
        viewport.ShellChord -= OnShellChord;
        viewport.ContextMenuRequested -= OnViewportContextMenu;
        viewport.AssetDropped -= OnViewportAssetDropped;
        viewport.AssetDragChanged -= OnViewportAssetDragChanged;
        _viewport = null;
        _pendingLaunch = null;

        // The pane comes home BEFORE the floats are closed, so a viewport that
        // was floated is back in this window rather than inside a window that is
        // about to be destroyed. Back to pinned whichever way the session went,
        // because the next one decides its own layout from its own measurement
        // and a leftover docked pane would be a native child in a tool.
        ApplyPlacement(ViewportPlacement.PinnedCell);

        // A panel floated into its own OS window is not inside EditorView, so
        // hiding the grid would leave it standing over the start page showing
        // a dead session's data. Every root closes its own windows - all four,
        // because a viewport dragged out of the centre dock leaves its float
        // exactly as a properties panel does.
        LeftRoot.ExitWindows?.Execute(null);
        RightRoot.ExitWindows?.Execute(null);
        BottomRoot.ExitWindows?.Execute(null);
        CenterRoot.ExitWindows?.Execute(null);

        _tree = null;
        _shell.Tree = null;

        // The classes belonged to the session's catalogue: left standing, the
        // start page's Object menu would offer entities for a project that is
        // closed, and the next project's menu would open showing the previous
        // one's classes until its own session came up.
        // A class name from one project's .sentdef means nothing in the next,
        // and a remembered one would survive into a window that cannot place it.
        _lastEntityClass = null;
        _shell.SetEntityClasses(null);
        RefreshEntityInsertTip();
        if (_shell.Properties is { } panel)
            panel.Schemas = null;

        _shell.HasSession = false;

        // The ribbon hides with the session, but its FLYOUT is a popup and
        // does not: a page left flown out would hang over the start page as a
        // floating strip of verbs that no longer reach anything. It closes
        // rather than collapsing, so the pin state a user chose survives.
        _ribbon = RibbonSurface.Dismiss(_ribbon);
        ApplyRibbonState();

        RefreshDocumentIdentity();
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
        RefreshRecents();
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

            // The session's PARSED catalogue, and the same instance the render
            // thread stamps every scene with - so the Insert menu offers what
            // the panel can describe, and neither can drift from the .sentdef
            // the other read. The insert lambda resolves the session when the
            // entry is chosen rather than capturing it, exactly as every other
            // verb in this window does.
            _shell.SetEntityClasses(EntityInsertMenu.Build(session.EntitySchemas));
            RefreshEntityInsertTip();
            if (_shell.Properties is { } panel)
                panel.Schemas = session.EntitySchemas;

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
            // Before the stop, while the counters still describe the session
            // that ran. Closing the window is how most sessions actually end,
            // so recording only in CloseSessionView would mean the history
            // almost never moved.
            RecordSessionOutcome();

            // The same signal CloseSessionView gives, for the same reason: a
            // composited viewport treats every detach as a re-parent unless it
            // has been told otherwise, and the window going away is the one
            // detach that is not.
            _viewport?.Shutdown();
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
        _lastApplied = FrameSnapshot.Empty;
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

        // PHASE-LOCK. Without this the shell's timer beats against the
        // engine's publish clock, so a snapshot landing just after a tick waits
        // a whole tick for no reason and the wait is different every time -
        // which the eye reads as unreliability rather than as latency. Posting
        // makes the two ends meet: the UI does its work as soon as there is
        // work, and never wakes up when there is none.
        //
        // Coalesced, because several snapshots can be published inside one UI
        // frame while a gesture is in flight and the pump drains all of them in
        // one pass anyway.
        if (Interlocked.Exchange(ref _pumpPosted, 1) != 0)
            return;

        try
        {
            Dispatcher.UIThread.Post(() => OnPump(null, EventArgs.Empty), DispatcherPriority.Normal);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher has shut down under us: the window is closing and
            // this snapshot has nowhere to go. Not an error, and not something
            // the render thread should hear about.
            Interlocked.Exchange(ref _pumpPosted, 0);
        }
    }

    private void OnPump(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _pumpPosted, 0);

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

        // The watchdog timer lands here ~125 times a second, and it exists for
        // exactly one thing: the cursor-mode latch, already pumped above. A
        // snapshot this pump has applied before has nothing new to say, so the
        // full apply (selection sync, the whole property panel, the snap
        // field) runs only when a NEW snapshot arrived — without this gate the
        // shell re-applied the same snapshot four times over per publish,
        // which is idle UI-thread work at its purest.
        if (ReferenceEquals(snapshot, _lastApplied))
            return;
        _lastApplied = snapshot;

        // The high-water mark rather than the latest value, because the count is
        // cumulative for the session and a composited session's greenness is a
        // claim about the whole of it. These are the COUNTED errors, which on a
        // composited D3D12 surface already exclude the one forgiven
        // ReflectSharedProperties message per bridge wrap.
        if (snapshot.DebugLayerErrorCount > _sessionDebugLayerErrors)
            _sessionDebugLayerErrors = snapshot.DebugLayerErrorCount;

        // Selection is a state rather than a history, so it is applied once
        // from the newest snapshot instead of once per drained one; the panel
        // owns the sync guards and the reveal choreography.
        _sceneView.SyncSelection(snapshot);
        TrackDirty(snapshot);

        // A menu opened in the moment before play mode was reported would
        // otherwise stand over a running session for as long as the user left
        // it there, sending edits at a scene somebody is walking around in.
        // The engine refuses those now; this is what stops the menu being on
        // screen at all.
        if (snapshot.IsPlaying && _viewportMenu is { IsOpen: true } menu)
            menu.Close();

        string? pipelineBefore = _shell.PipelineName;
        int pipelineCountBefore = _shell.PipelineNames.Count;

        _shell.ApplySnapshot(snapshot);
        RefreshSnapField(snapshot);

        // Rebuilt only when the answer changed. A MenuItem collection rebuilt
        // at the publish rate is UI-thread garbage for a surface nobody is
        // looking at, and the menu is re-read every time it opens anyway.
        if (pipelineCountBefore != _shell.PipelineNames.Count
            || !string.Equals(pipelineBefore, _shell.PipelineName, StringComparison.Ordinal))
        {
            RefreshRendererMenu();
        }
    }

    // --- Snap increment field ------------------------------------------------
    //
    // ONE box with the property panel's commit contract: a focused field stops
    // taking refreshes, Enter and blur commit, Escape reverts, and unparseable
    // or non-positive text reverts rather than sticking. Plain code-behind over
    // the control, like the scroll offsets: the state is one float and a focus
    // flag, and a model would be ceremony.
    //
    // ONE rather than three, because the three were labelled "mv", "rot" and
    // "sz" and asked the reader to hold a mapping from abbreviation to tool in
    // their head, for a tool they had already chosen. The box holds the live
    // tool's increment, and the unit beside it says which unit that is.

    private void RefreshSnapField(FrameSnapshot snapshot)
    {
        // A focused field is being typed into; writing the published value
        // back would delete characters as they arrive, which reads as a broken
        // keyboard. The blur or Enter that ends the edit commits it.
        TextBox box = _buildTab.SnapField;
        if (box.IsFocused)
            return;

        string text = PropertyFieldModel.Format(IncrementFor(snapshot, LiveSnapTool));
        if (box.Text != text)
            box.Text = text;
    }

    private static float IncrementFor(FrameSnapshot snapshot, GizmoMode tool) => tool switch
    {
        GizmoMode.Rotate => snapshot.RotateSnapIncrement,
        GizmoMode.Scale => snapshot.ResizeSnapIncrement,
        _ => snapshot.MoveSnapIncrement,
    };

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
                _viewport?.FocusEngine();
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
            _session?.SetSnapIncrement(LiveSnapTool, value);
        else
            RevertSnapField(box);
    }

    /// <summary>
    /// Commits whatever editable field currently holds keyboard focus, by
    /// blurring it, before a document chord runs.
    /// </summary>
    /// <remarks>
    /// The menu route gets this for free — clicking File moves focus, the
    /// field's LostFocus commits — but a key binding fires with focus still in
    /// the box, so Ctrl+S would write the bundle WITHOUT the value the user
    /// just typed while reporting "Saved". One blur closes the gap for the
    /// property fields and the snap fields alike, on their own commit paths.
    /// </remarks>
    private void CommitFocusedEdit()
    {
        // Blur by taking focus, not by a ClearFocus API (Avalonia 12 has
        // none): the window is made focusable in the constructor exactly so
        // it can be the neutral place focus lands, which raises the field's
        // LostFocus and runs its commit.
        if (FocusManager?.GetFocusedElement() is TextBox)
            Focus();
    }

    private void RevertSnapField(TextBox box)
    {
        // Before the first snapshot there is nothing published to revert TO,
        // and formatting FrameSnapshot.Empty's zeros into the box would show
        // an increment the engine never had; the editor's defaults are the
        // honest resting value.
        FrameSnapshot latest = _latest;
        GizmoMode tool = LiveSnapTool;
        float value = ReferenceEquals(latest, FrameSnapshot.Empty)
            ? (tool == GizmoMode.Rotate ? 15f : 1f)
            : IncrementFor(latest, tool);
        box.Text = PropertyFieldModel.Format(value);
    }

    /// <summary>
    /// Which tool the command bar's snap field is currently editing.
    /// </summary>
    /// <remarks>
    /// Read from the shell model rather than from the snapshot, because the
    /// model is what the unit label beside the box is bound to: taking the two
    /// from different sources is how a field ends up showing degrees under a
    /// move tool for one publish interval.
    /// </remarks>
    private GizmoMode LiveSnapTool => _shell.GizmoMode switch
    {
        "rotate" => GizmoMode.Rotate,
        "resize" => GizmoMode.Scale,
        _ => GizmoMode.Translate,
    };

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

    // --- Panels --------------------------------------------------------------
    //
    // "Show" rather than "toggle": a menu entry that hides a panel the user is
    // looking at, because they picked it from a list to find it, is the same
    // set-versus-toggle mistake the command bar already avoids. Closing is the
    // dock chrome's own X, which is where a user looks for it.
    //
    // The dock's factory is what actually moves a dockable, and asking it to
    // set the active dockable is enough: a tool that was closed is still in the
    // layout, so making it active brings it back into view.

    private void OnShowScenePanel(object? sender, RoutedEventArgs e) => ShowTool(SceneTool);

    private void OnShowMapsPanel(object? sender, RoutedEventArgs e) => ShowTool(MapsTool);

    private void OnShowPropertiesPanel(object? sender, RoutedEventArgs e) => ShowTool(PropertiesTool);

    private void OnShowContentPanel(object? sender, RoutedEventArgs e) => ShowTool(ContentTool);

    private void OnShowOutputPanel(object? sender, RoutedEventArgs e) => ShowTool(OutputTool);

    private void OnShowConsolePanel(object? sender, RoutedEventArgs e)
    {
        ShowTool(ConsoleTool);

        // The caret goes into the line, because a console you have to click
        // into after asking for it is a console you stop using.
        _consoleView?.FocusInput();
    }

    private static void ShowTool(Dock.Model.Avalonia.Controls.Tool tool)
    {
        if (tool.Owner is Dock.Model.Core.IDock owner)
            owner.ActiveDockable = tool;
    }

    private async Task RunInteropProbeAsync()
    {
        await InteropProbe.RunAsync(this, _logger);

        // Long enough for the log to flush and for a human running it by hand
        // to read the console; short enough to be scriptable.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Close();
    }

    // --- The bottom region ---------------------------------------------------

    /// <summary>
    /// Runs one console line and prints both halves.
    /// </summary>
    /// <remarks>
    /// <b>The command is echoed before it runs, and the reply after.</b> A
    /// console that printed only replies makes a scrollback nobody can read
    /// back: three "nothing is open" lines in a row say nothing about which
    /// three commands produced them.
    /// </remarks>
    private void OnConsoleCommand(string line)
    {
        _shell.Output.Append(OutputSeverity.Command, "> " + line);

        if (_console is not { } console)
            return;

        ConsoleResult result = console.Execute(line);

        if (result.Reply == ConsoleCommands.ClearMarker)
        {
            _shell.Output.Clear();
            return;
        }

        if (!string.IsNullOrEmpty(result.Reply))
            _shell.Output.Append(result.Severity, result.Reply);
    }

    /// <summary>
    /// A file was double-clicked in the content browser.
    /// </summary>
    /// <remarks>
    /// <b>A model inserts; everything else is revealed on disk.</b> Dropping a
    /// texture or a material into the viewport needs a target face and a
    /// material assignment, neither of which the editor has a verb for yet -
    /// and a double-click that silently did nothing would be worse than one
    /// that opens the folder. Dragging into the 3D view waits on the composited
    /// viewport: Avalonia's drag events cannot reach a native child window.
    /// </remarks>
    private void OnContentActivated(ContentEntry entry)
    {
        if (entry.Kind == ContentKind.Model)
        {
            // SAY SO rather than doing something adjacent. Inserting a block
            // and calling it a model would be the worst available answer: the
            // user asked for one thing, got another, and the message explaining
            // that is a status line they may not be looking at.
            _shell.SetWarning($"Placing {entry.Name} in the scene is not built yet; opened its folder instead.");
        }

        RevealInExplorer(entry.FullPath);
    }

    // --- Splitters -----------------------------------------------------------
    //
    // The hover lives on the INK, not on the splitter, and it is driven from
    // code rather than from a selector, for two reasons that compound. The
    // splitter is nine pixels wide so it can be grabbed by a hand; the line a
    // user sees is one pixel, and a nine-pixel accent band appearing under the
    // cursor is a different control from the one that is there. And a child
    // cannot read its parent's pseudo-classes from a selector at all, so
    // ".splitink" has no way to know the splitter above it is hovered.
    //
    // The ink is reached through Tag rather than by name because there are two
    // of these and there will be a third the moment the bottom region lands.

    private static void OnSplitterEntered(object? sender, PointerEventArgs e) =>
        SetSplitterHot(sender, hot: true);

    private void OnSplitterExited(object? sender, PointerEventArgs e)
    {
        // Not while dragging: a fast drag leaves the nine-pixel band and the
        // exit would revert the accent exactly while the user is pulling it.
        if (!ReferenceEquals(sender, _draggingSplitter))
            SetSplitterHot(sender, hot: false);
    }

    // The drag pins the hot class for its whole duration; the pointer is
    // often far outside the band by the time the gesture ends.
    private object? _draggingSplitter;

    private void OnSplitterDragStarted(object? sender, VectorEventArgs e)
    {
        _draggingSplitter = sender;
        SetSplitterHot(sender, hot: true);
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        _draggingSplitter = null;
        SetSplitterHot(sender, hot: false);
    }

    private static void SetSplitterHot(object? sender, bool hot)
    {
        if (sender is Control { Tag: Control ink })
            ink.Classes.Set("hot", hot);
    }

    // --- Driving the editor --------------------------------------------------

    private void OnSnapFinerClicked(object? sender, RoutedEventArgs e) =>
        _session?.Post(GizmoCommand.FinerSnap);

    private void OnSnapCoarserClicked(object? sender, RoutedEventArgs e) =>
        _session?.Post(GizmoCommand.CoarserSnap);

    private void OnKeyboardReferenceClicked(object? sender, RoutedEventArgs e) =>
        _ = new KeyboardReferenceWindow().ShowDialog(this);

    /// <summary>
    /// Cooks the open project and checks the pack resolves with nothing else
    /// mounted.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the only view of the game the editor cannot otherwise
    /// give.</b> A session resolves loose files above the pack, which is the
    /// whole editor workflow and is exactly why a texture the cook never produced
    /// looks perfectly correct in this window right up until somebody plays a
    /// shipped build.</para>
    /// <para><b>Off the UI thread, and it names no scene.</b> The cook reads the
    /// project folder and writes <c>cooked/</c>; nothing in it may touch the
    /// graph, so there is no <c>EnqueueCommand</c> here and there should never
    /// be one.</para>
    /// <para><b>Every diagnostic goes to the output log, one line each.</b> The
    /// status line is last-writer-wins and a validation run legitimately produces
    /// dozens of findings, so putting them there would show whichever happened to
    /// be last and destroy the rest.</para>
    /// </remarks>
    private async void OnValidateCookedClicked(object? sender, RoutedEventArgs e)
    {
        if (_document.Project is not { } project || _shell.IsValidatingCooked)
            return;

        _shell.IsValidatingCooked = true;
        _shell.SetMessage($"Cooking {project.Project.Name} and validating the pack...");

        try
        {
            CookedValidationReport report = await Task.Run(() => CookedValidation.Run(project));

            foreach (CookDiagnostic diagnostic in report.Diagnostics)
            {
                _shell.Output.Append(
                    diagnostic.Severity switch
                    {
                        CookDiagnosticSeverity.Error => OutputSeverity.Error,
                        CookDiagnosticSeverity.Warning => OutputSeverity.Warning,
                        _ => OutputSeverity.Info,
                    },
                    diagnostic.ToBuildLine("scook"));
            }

            if (report.Succeeded) _shell.SetMessage(report.Summary);
            else _shell.SetError(report.Summary);
        }
        catch (Exception ex)
        {
            // Caught here rather than left to the dispatcher: an async void
            // handler that throws takes the application down, and a validation
            // run that failed for its own reasons is a message rather than a
            // crash.
            _shell.SetError($"The cooked-content validation did not finish: {ex.Message}");
        }
        finally
        {
            _shell.IsValidatingCooked = false;
        }
    }

    /// <remarks>
    /// <b>Two drags reach this handler and they mean opposite things.</b> A
    /// FILE drag comes from outside the application and opens a project or a
    /// level. An ASSET drag is one of this shell's own, and reaching the WINDOW
    /// means no viewport claimed it: either it is over a panel, which is an
    /// ordinary miss, or it is over a viewport that cannot take drops at all,
    /// which is the case that has to be said out loud rather than answered with
    /// a cursor.
    /// </remarks>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(ContentDrag.Format))
        {
            // Claimed only over the viewport's own rectangle, so a drop can land
            // and be REFUSED IN WORDS. Everywhere else in the window an asset
            // drag really is a miss, and the "no entry" pointer is the right
            // answer there.
            e.DragEffects = IsOverViewport(e) ? DragDropEffects.Copy : DragDropEffects.None;
            return;
        }

        // Copy rather than Move: nothing on disk is touched by opening, and a
        // Move cursor over a file manager's own window promises otherwise.
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(ContentDrag.Format) is { } payload)
        {
            // A composited viewport handled this itself and marked it handled,
            // so anything arriving here is the refusal path. AssetDropPolicy
            // knows which of the reasons it is.
            _shell.SetWarning(
                AssetDropPolicy.Refuse(payload, _session is not null, _viewport?.AcceptsAssetDrops ?? false)
                ?? $"{payload.Name} was not dropped into the scene.");
            return;
        }

        if (e.DataTransfer.TryGetFiles() is { } files)
            _ = DropAsync(files);
    }

    // Bounds rather than hit testing, because the answer must be the same over a
    // native child: Avalonia hit-tests the NativeControlHost as an opaque
    // rectangle and never learns anything about the HWND inside it, so asking
    // where the pointer is relative to the control is the only question with a
    // reliable answer on both paths.
    private bool IsOverViewport(DragEventArgs e)
    {
        if (_viewport?.Control is not { } control || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            return false;

        Point point = e.GetPosition(control);
        return point.X >= 0 && point.Y >= 0 &&
            point.X < control.Bounds.Width && point.Y < control.Bounds.Height;
    }

    /// <summary>
    /// A model dropped into the viewport becomes a node, through the same
    /// insert the Object menu uses.
    /// </summary>
    /// <remarks>
    /// <b>The placement is not decided here and must never be.</b> The ray, the
    /// pick that sees parts and meshes, the snap along the hit surface, the
    /// single history entry and the selection afterwards all live in
    /// <c>SceneEditorHost</c>, on the render thread, because they are decisions
    /// about a scene graph this thread is a frame or two behind on. This handler
    /// says which file and which pixel; the editor answers with what it did.
    /// </remarks>
    private void OnViewportAssetDropped(ContentDragPayload payload, int x, int y)
    {
        if (AssetDropPolicy.Refuse(payload, _session is not null, viewportAcceptsDrops: true) is { } refusal)
        {
            _shell.SetWarning(refusal);
            return;
        }

        if (_session is not { } session)
            return;

        session.InsertModel(
            payload.ContentPath,
            new System.Numerics.Vector2(x, y),
            report => Dispatcher.UIThread.Post(() => ReportModelInsert(report)));
    }

    /// <summary>
    /// Draws the drop affordance over the picture, or takes it away.
    /// </summary>
    /// <remarks>
    /// <b>The overlay's verdict is asked of the same policy the drop is, and
    /// that is the whole of what makes it honest.</b> A frame drawn from any
    /// other reasoning would be free to promise a placement that
    /// <see cref="OnViewportAssetDropped"/> then refuses, and the moment it
    /// would be discovered is the moment somebody let go of the mouse.
    /// </remarks>
    private void OnViewportAssetDragChanged(ContentDragPayload? payload) =>
        _shell.DropPrompt = ViewportDropPrompt.For(
            payload, _session is not null, _viewport?.AcceptsAssetDrops ?? false);

    // Marshalled back deliberately: EditorSession runs its completion on the
    // RENDER thread, which is the whole point of that contract.
    private void ReportModelInsert(ModelInsertReport report)
    {
        // Three outcomes and three voices. A refusal is a warning because
        // nothing happened and the gesture is worth repeating; an unresolved
        // model is an ERROR because a node IS in the scene and in the history
        // and somebody has to know it is empty; a clean insert is a message.
        if (report.Refused is not null)
            _shell.SetWarning(report.Describe());
        else if (report.Unresolved is not null)
            _shell.SetError(report.Describe());
        else
            _shell.SetMessage(report.Describe());
    }

    // --- The ribbon ----------------------------------------------------------

    /// <summary>
    /// Builds the tab strip from the roster, wires both pages, and applies the
    /// persisted collapse state.
    /// </summary>
    /// <remarks>
    /// <b>The strip is built from <see cref="RibbonLayout.Tabs"/> rather than
    /// written in the markup</b>, so a page in the roster with no button, or a
    /// button naming a page nobody built, cannot happen. Two buttons is nothing
    /// to build by hand and it removes a whole class of drift for the price.
    /// </remarks>
    private void BuildRibbon()
    {
        _ribbonPages[RibbonLayout.DefaultTabId] = _buildTab;
        _ribbonPages[RibbonLayout.ViewTabId] = _viewTab;

        foreach (RibbonTab tab in RibbonLayout.Tabs)
        {
            if (!_ribbonPages.TryGetValue(tab.Id, out RibbonTabView? page))
            {
                throw new InvalidOperationException(
                    $"The ribbon roster names a '{tab.Id}' page this window does not build.");
            }

            // Explicitly, not inherited: a page spends part of its life inside
            // a popup, which is a separate visual root, and the shell has been
            // caught once already by a content host that assumed inheritance.
            page.DataContext = _shell;
            page.Invoked += OnRibbonVerb;

            var button = new Button
            {
                Classes = { "ribbontab" },
                Tag = tab.Id,
                Content = new TextBlock { Text = tab.Title },
            };
            button.Click += OnRibbonTabClicked;
            RibbonTabs.Children.Add(button);
        }

        // The snap field keeps its commit rule in this window - parse, refuse
        // zero and negatives rather than clamping, revert anything
        // unparseable, and stop taking refreshes while it has focus - so the
        // page exposes the box and the handlers stay here.
        _buildTab.SnapField.GotFocus += OnSnapFieldFocused;
        _buildTab.SnapField.LostFocus += OnSnapFieldBlurred;
        _buildTab.SnapField.KeyDown += OnSnapFieldKeyDown;

        WireEntitySplit();

        RibbonFlyout.Closed += OnRibbonFlyoutClosed;

        _ribbon = RibbonSurface.Create(_settings.RibbonExpanded);
        ApplyRibbonState();
    }

    private void OnRibbonTabClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string tabId })
            return;

        _ribbon = RibbonSurface.SelectTab(_ribbon, tabId);
        ApplyRibbonState();
    }

    private void OnRibbonPinClicked(object? sender, RoutedEventArgs e)
    {
        _ribbon = RibbonSurface.SetExpanded(_ribbon, !_ribbon.Expanded);
        ApplyRibbonState();

        // A surface whose size resets every launch is a preference nobody
        // keeps. The ACTIVE TAB deliberately does not go with it.
        _settings.SetRibbonExpanded(_ribbon.Expanded);
        _settings.Save(_logger);
    }

    /// <summary>The flyout was light-dismissed by a click outside it.</summary>
    private void OnRibbonFlyoutClosed(object? sender, EventArgs e)
    {
        // Applying the state is what closed it; without this guard that
        // re-entry writes the state a second time and the next tab click
        // reads a value the machine never produced.
        if (_applyingRibbonState)
            return;

        _ribbon = RibbonSurface.Dismiss(_ribbon);
        ApplyRibbonState();
    }

    /// <summary>
    /// Mirrors the state machine's value onto the controls: which tab is lit,
    /// where the active page lives, and which way the pin points.
    /// </summary>
    private void ApplyRibbonState()
    {
        _applyingRibbonState = true;
        try
        {
            foreach (Control child in RibbonTabs.Children)
            {
                child.Classes.Set(
                    "active",
                    child.Tag is string id && string.Equals(id, _ribbon.ActiveTabId, StringComparison.Ordinal));
            }

            RibbonBodyHost host = RibbonSurface.HostFor(_ribbon);
            _ribbonPages.TryGetValue(_ribbon.ActiveTabId, out RibbonTabView? page);

            // A control has one parent, so the loser is cleared before the
            // winner is assigned - in that order, always.
            if (host != RibbonBodyHost.Inline)
                RibbonInlineHost.Content = null;
            if (host != RibbonBodyHost.Flyout)
                RibbonFlyoutHost.Content = null;

            switch (host)
            {
                case RibbonBodyHost.Inline:
                    RibbonInlineHost.Content = page;
                    break;
                case RibbonBodyHost.Flyout:
                    RibbonFlyoutHost.Content = page;
                    break;
            }

            RibbonBody.IsVisible = host == RibbonBodyHost.Inline;
            RibbonFlyout.IsOpen = host == RibbonBodyHost.Flyout;

            RibbonPin.Classes.Set("collapsed", !_ribbon.Expanded);
            ToolTip.SetTip(
                RibbonPin,
                _ribbon.Expanded ? "Collapse the ribbon to its tabs" : "Pin the ribbon open");
        }
        finally
        {
            _applyingRibbonState = false;
        }
    }

    /// <summary>
    /// One ribbon control was clicked. Resolves to the verb the roster names
    /// and hands it to the SAME handler a key chord and a menu item use.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here is a second command path.</b> Undo, redo, the tool
    /// verbs and the three two-way choices go through the window's own
    /// optimistic handlers rather than posting directly, or the ribbon would
    /// light a frame later than the menu does for the same verb.
    /// </remarks>
    private void OnRibbonVerb(RibbonVerb verb)
    {
        // A command posted out of a flown-out page closes the page, which is
        // what makes a collapsed ribbon usable rather than sticky.
        if (RibbonSurface.HostFor(_ribbon) == RibbonBodyHost.Flyout)
        {
            _ribbon = RibbonSurface.Invoke(_ribbon);
            ApplyRibbonState();
        }

        var args = new RoutedEventArgs();
        switch (verb.Kind)
        {
            case RibbonVerbKind.Insert:
                _session?.Insert(verb.Insert);
                break;

            case RibbonVerbKind.Camera:
                _session?.Post(verb.Camera);
                break;

            case RibbonVerbKind.Debug:
                RequestDebug(verb.Debug, !_shell.IsDebugEnabled(verb.Debug));
                break;

            case RibbonVerbKind.Host when verb.Host == EditorHostCommand.Undo:
                OnUndoClicked(this, args);
                break;

            case RibbonVerbKind.Host when verb.Host == EditorHostCommand.Redo:
                OnRedoClicked(this, args);
                break;

            case RibbonVerbKind.Host:
                _session?.Post(verb.Host);
                break;

            case RibbonVerbKind.Gizmo when verb.Gizmo == GizmoCommand.UseTranslate:
                UseTool("move", GizmoCommand.UseTranslate);
                break;

            case RibbonVerbKind.Gizmo when verb.Gizmo == GizmoCommand.UseRotate:
                UseTool("rotate", GizmoCommand.UseRotate);
                break;

            case RibbonVerbKind.Gizmo when verb.Gizmo == GizmoCommand.UseScale:
                UseTool("resize", GizmoCommand.UseScale);
                break;

            case RibbonVerbKind.Gizmo:
                _session?.Post(verb.Gizmo);
                break;

            case RibbonVerbKind.Toggle when verb.Toggle == RibbonToggle.Axes:
                OnOrientationClicked(this, args);
                break;

            case RibbonVerbKind.Toggle when verb.Toggle == RibbonToggle.Handles:
                OnStyleClicked(this, args);
                break;

            case RibbonVerbKind.Toggle when verb.Toggle == RibbonToggle.Snap:
                OnSnapClicked(this, args);
                break;

            case RibbonVerbKind.SnapIncrement:
                // The field commits through its own focus and Enter handlers;
                // there is no click to answer.
                break;

            case RibbonVerbKind.InsertEntity:
                // The one verb whose target is resolved HERE rather than named
                // by the roster: an entity class comes from the project, not
                // from this build. Nothing at all when a project declares none,
                // which the button's own IsEnabled has already said.
                if (ResolveEntityClass() is { } className)
                    _session?.InsertEntity(className);
                break;
        }
    }

    /// <summary>
    /// The entity class the ribbon's split button places, which is whichever
    /// one was chosen last.
    /// </summary>
    /// <remarks>
    /// <b>Null means "the first the catalogue offers", never "none".</b> A
    /// split button whose main half does nothing until you have used its caret
    /// once is a button that is broken on first use and works afterwards, which
    /// is the hardest kind of report to act on. Cleared when a session ends,
    /// because a class name from one project's <c>.sentdef</c> means nothing in
    /// the next.
    /// </remarks>
    private string? _lastEntityClass;

    /// <summary>The entity class list the split button's caret opens.</summary>
    private MenuFlyout? _entityFlyout;

    /// <summary>
    /// Hangs the entity class list on the split button's caret and re-reads the
    /// main half's tooltip.
    /// </summary>
    /// <remarks>
    /// The flyout refills on OPEN rather than at construction, exactly as both
    /// entity submenus do: this window outlives every session, and entries
    /// built once would still offer the first project's classes in the third
    /// project's window.
    /// </remarks>
    private void WireEntitySplit()
    {
        _entityFlyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        // SHOWN FROM THE CLICK, never left to Button.Flyout. A Button opening
        // its own Flyout is one line shorter and depends on the framework
        // choosing to do it; filling the list first and then showing it is the
        // order this needs anyway, since an empty MenuFlyout has nothing to
        // draw and no way to say so.
        _buildTab.EntityCaretButton.Click += OnEntityCaretClicked;
        RefreshEntityInsertTip();
    }

    /// <summary>Opens the entity class list under the split button's caret.</summary>
    /// <remarks>
    /// Refilled at every open rather than once, exactly as both entity submenus
    /// are: this window outlives every session, and entries built once would
    /// still offer the first project's classes in the third project's window.
    /// </remarks>
    private void OnEntityCaretClicked(object? sender, RoutedEventArgs e)
    {
        if (_entityFlyout is not { } flyout)
            return;

        FillEntityItems(flyout.Items, static () => null);
        if (flyout.Items.Count > 0)
            flyout.ShowAt(_buildTab.EntityCaretButton);
    }

    /// <summary>
    /// Names the class the split button's main half will place.
    /// </summary>
    /// <remarks>
    /// The LABEL stays "Entity" and the class goes in the tooltip, which is
    /// Office's own arrangement for a split button: the word names the family
    /// and the caret names the member. A label that rewrote itself per class
    /// would change width under the pointer and would wrap the moment somebody
    /// shipped a long classname.
    /// </remarks>
    private void RefreshEntityInsertTip()
    {
        string? className = ResolveEntityClass();

        ToolTip.SetTip(
            _buildTab.EntityInsertButton,
            className is null
                ? "This project declares no entity classes."
                : $"Place a {className}. The arrow chooses a different class.");
    }

    /// <summary>
    /// Which class the main half places: the last one used, or the first the
    /// catalogue offers.
    /// </summary>
    /// <remarks>
    /// The last choice is checked against the LIVE catalogue rather than
    /// trusted, because opening a second project replaces it: a remembered name
    /// no longer in the list would post an insert the editor answers with a
    /// placeholder, which is a real node in the scene and in the history for a
    /// class nobody asked for.
    /// </remarks>
    private string? ResolveEntityClass()
    {
        if (_lastEntityClass is { } remembered)
        {
            foreach (EntityInsertItem entry in _shell.EntityClasses)
            {
                if (string.Equals(entry.ClassName, remembered, StringComparison.Ordinal))
                    return remembered;
            }
        }

        return _shell.EntityClasses.Count > 0 ? _shell.EntityClasses[0].ClassName : null;
    }


    private void OnInsertWorldBrushClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.WorldBrush);
    private void OnInsertPartBrushClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.PartBrush);
    private void OnInsertSubtractiveBrushClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.SubtractiveBrush);
    private void OnInsertLightClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.PointLight);
    private void OnInsertSurfaceLightClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.SurfaceLight);
    private void OnInsertGroupClicked(object? sender, RoutedEventArgs e) => _session?.Insert(InsertKind.Group);

    // ─── Set semantics, and a local opinion with a bound ──
    //
    // SET, never a toggle verb: a toggle sent against a snapshot one publish
    // stale flips the wrong way exactly when the user clicks fastest, while
    // re-requesting the state already shown is a no-op.
    //
    // And the shell shows the request IMMEDIATELY rather than waiting for the
    // engine to echo it. Set semantics is exactly what makes that safe - a
    // stale echo can only be an older value, never the opposite of what was
    // asked for - and ShellModel bounds the wait, so an engine that refuses
    // (play mode on a scene with no character, an edit while a gesture is
    // open) still wins within about a tenth of a second, visibly.

    private void OnPlayClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is not { } session)
            return;

        bool wanted = !_shell.IsPlaying;
        _shell.RequestPlaying(wanted);
        session.Host.RequestPlayMode(wanted);
    }

    private void OnDebugWireClicked(object? sender, RoutedEventArgs e) =>
        RequestDebug(DebugVisualization.Wireframe, !_shell.DebugWireframe);
    private void OnDebugVerticesClicked(object? sender, RoutedEventArgs e) =>
        RequestDebug(DebugVisualization.Vertices, !_shell.DebugVertices);
    private void OnDebugAabbsClicked(object? sender, RoutedEventArgs e) =>
        RequestDebug(DebugVisualization.Aabbs, !_shell.DebugAabbs);
    private void OnDebugNormalsClicked(object? sender, RoutedEventArgs e) =>
        RequestDebug(DebugVisualization.Normals, !_shell.DebugNormals);
    private void OnDebugSceneGraphClicked(object? sender, RoutedEventArgs e) =>
        RequestDebug(DebugVisualization.SceneGraph, !_shell.DebugSceneGraph);

    private void RequestDebug(DebugVisualization flag, bool enabled)
    {
        if (_session is not { } session)
            return;

        // The model first, so the tick shows the new state, then the engine.
        // A debug toggle is the one of these the user watches while the menu is
        // still OPEN, so its lag was the most visible of the lot.
        _shell.RequestDebugVisualization(flag, enabled);
        session.Host.RequestDebugVisualization(flag, enabled);
    }

    private void OnMoveClicked(object? sender, RoutedEventArgs e) => UseTool("move", GizmoCommand.UseTranslate);
    private void OnRotateClicked(object? sender, RoutedEventArgs e) => UseTool("rotate", GizmoCommand.UseRotate);
    private void OnResizeClicked(object? sender, RoutedEventArgs e) => UseTool("resize", GizmoCommand.UseScale);

    private void UseTool(string mode, GizmoCommand command)
    {
        if (_session is not { } session)
            return;

        _shell.RequestGizmoMode(mode);
        session.Post(command);
    }

    // The two chips carry a VALUE, so the click resolves to the value it wants
    // rather than to "the other one". The toggle verbs still exist and are
    // still what the keyboard sends; a shell posting one would be computing
    // the answer from a snapshot it may already have superseded locally.
    private void OnOrientationClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is not { } session)
            return;

        bool toWorld = !_shell.IsWorldSpace;
        _shell.RequestOrientation(toWorld ? "world" : "local");
        session.Post(toWorld ? GizmoCommand.UseWorldOrientation : GizmoCommand.UseLocalOrientation);
    }

    private void OnStyleClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is not { } session)
            return;

        bool toStudio = !_shell.IsStudioStyle;
        _shell.RequestGizmoStyle(toStudio ? "Studio" : "Classic");
        session.Post(toStudio ? GizmoCommand.UseStudioStyle : GizmoCommand.UseClassicStyle);
    }

    private void OnSnapClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is not { } session)
            return;

        bool on = !_shell.SnapEnabled;
        _shell.RequestSnapEnabled(on);
        session.Post(on ? GizmoCommand.EnableSnap : GizmoCommand.DisableSnap);
    }

    private void OnUndoClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is not { } session)
            return;

        _shell.RequestUndo();
        session.Post(EditorHostCommand.Undo);
    }

    private void OnRedoClicked(object? sender, RoutedEventArgs e)
    {
        if (_session is not { } session)
            return;

        _shell.RequestRedo();
        session.Post(EditorHostCommand.Redo);
    }
    private void OnDuplicateClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Duplicate);
    private void OnDeleteClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Delete);
    private void OnGroupClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Group);
    private void OnUngroupClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.Ungroup);
    private void OnToggleBrushKindClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.ToggleBrushKind);
    private void OnToggleNavigationClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.ToggleNavigation);
    private void OnGridAutoClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.GridAuto);
    private void OnGridOnClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.GridOn);
    private void OnGridOffClicked(object? sender, RoutedEventArgs e) => _session?.Post(EditorHostCommand.GridOff);

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
            case ShellChord.InsertBlock: _session?.Insert(InsertKind.WorldBrush); break;
            case ShellChord.InsertPart: _session?.Insert(InsertKind.PartBrush); break;
            case ShellChord.InsertCut: _session?.Insert(InsertKind.SubtractiveBrush); break;
            case ShellChord.InsertLight: _session?.Insert(InsertKind.PointLight); break;
        }
    }

    // --- Viewport context menu -----------------------------------------------

    // Built once and reused; where it was opened, in framebuffer pixels, so
    // "insert here" means the spot that was right-clicked rather than wherever
    // the camera is pointing by the time the item is picked.
    private ContextMenu? _viewportMenu;
    private MenuItem? _viewportEntityMenu;
    private System.Numerics.Vector2 _viewportMenuPoint;

    /// <summary>
    /// A right-click in the viewport that never became a freelook drag. The
    /// menu itself is a real OS popup, which is the one kind of surface that
    /// may legally cross the viewport (the NameDialog rule).
    /// </summary>
    private void OnViewportContextMenu(int x, int y)
    {
        if (_session is null || _viewport is not { } viewport || _shell.IsPlaying)
            return;

        // Retarget first, the rule every editor shares: an unselected object
        // under the cursor becomes the selection, a selected one keeps the
        // set, empty space changes nothing. By the time a human picks a menu
        // item the render thread has long since applied it.
        _viewportMenuPoint = new System.Numerics.Vector2(x, y);
        _session.SelectAtPoint(_viewportMenuPoint);

        _viewportMenu ??= BuildViewportMenu();
        RefreshViewportEntityItems();

        // Client pixels are physical; Avalonia placement wants logical units.
        double scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        _viewportMenu.Placement = PlacementMode.AnchorAndGravity;
        _viewportMenu.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
        _viewportMenu.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
        _viewportMenu.PlacementRect = new Rect(x / scaling, y / scaling, 1, 1);
        _viewportMenu.Open(viewport.Control);
    }

    private ContextMenu BuildViewportMenu()
    {
        MenuItem Item(string header, string? gesture, Action action)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null)
                item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => action();
            return item;
        }

        // ONE vocabulary, everywhere. Block, part and cut are what the command
        // row says, what the Object menu says and what the keyboard reference
        // says, so they are what this says: the same five things went by two
        // sets of names depending on which surface the user reached them
        // through, which is how "world brush", "hole" and "part" become three
        // concepts instead of three words for two.
        var insert = new MenuItem { Header = "Insert here" };
        insert.Items.Add(Item("Block", "Ctrl+D1", () => _session?.Insert(InsertKind.WorldBrush, _viewportMenuPoint)));
        insert.Items.Add(Item("Part", "Ctrl+D2", () => _session?.Insert(InsertKind.PartBrush, _viewportMenuPoint)));
        insert.Items.Add(Item("Cut", "Ctrl+D3", () => _session?.Insert(InsertKind.SubtractiveBrush, _viewportMenuPoint)));
        insert.Items.Add(Item("Light", "Ctrl+D4", () => _session?.Insert(InsertKind.PointLight, _viewportMenuPoint)));

        // The one insert that genuinely wants the RIGHT-CLICK point rather than
        // the view centre: it mounts on the surface under the cursor, so "here"
        // is the whole gesture.
        insert.Items.Add(Item("Surface light", null, () => _session?.Insert(InsertKind.SurfaceLight, _viewportMenuPoint)));
        insert.Items.Add(Item("Empty group", null, () => _session?.Insert(InsertKind.Group, _viewportMenuPoint)));

        // Entities go under their own submenu rather than into this list: the
        // six above are a fixed vocabulary and the classes are a project's, so
        // one flat list would grow without bound and put "Block" beside forty
        // logic classes.
        _viewportEntityMenu = new MenuItem { Header = "Entity" };
        insert.Items.Add(_viewportEntityMenu);

        var menu = new ContextMenu();
        menu.Items.Add(insert);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Duplicate", "Ctrl+D", () => _session?.Post(EditorHostCommand.Duplicate)));
        menu.Items.Add(Item("Delete", "Delete", () => _session?.Post(EditorHostCommand.Delete)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Group", "Ctrl+G", () => _session?.Post(EditorHostCommand.Group)));
        menu.Items.Add(Item("Ungroup", "Ctrl+Shift+G", () => _session?.Post(EditorHostCommand.Ungroup)));
        menu.Items.Add(Item("Convert block / part", "Ctrl+T", () => _session?.Post(EditorHostCommand.ToggleBrushKind)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Frame selection", "F", () => _session?.Post(EditorCameraCommand.FrameSelection)));

        // The popup takes real focus, so closing it must hand the keyboard
        // back to the engine's HWND or the tool keys go dead until a click.
        menu.Closed += (_, _) => _viewport?.FocusEngine();
        return menu;
    }

    /// <summary>
    /// Refills the viewport menu's entity submenu, placing at the right-click
    /// point.
    /// </summary>
    private void RefreshViewportEntityItems() =>
        FillEntityMenu(_viewportEntityMenu, () => _viewportMenuPoint);

    /// <summary>
    /// Refills the Object menu's entity submenu, placing at the view centre.
    /// </summary>
    /// <remarks>
    /// On <c>SubmenuOpened</c> rather than at construction, for the same reason
    /// the viewport's is rebuilt per open: the window outlives every session,
    /// and entries built once would still offer the first project's classes in
    /// the third project's window.
    /// </remarks>
    private void OnInsertEntityMenuOpened(object? sender, RoutedEventArgs e) =>
        FillEntityMenu(InsertEntityMenu, static () => null);

    /// <summary>
    /// Rebuilds one entity submenu from the live session's classes, and hides
    /// it when there are none.
    /// </summary>
    /// <remarks>
    /// <b>Built in code, in ONE place, for both menus.</b> The alternative is
    /// an <c>ItemsSource</c> plus an <c>ItemContainerTheme</c> deriving from
    /// Fluent's own MenuItem theme, which this shell has no other instance of
    /// and whose failure mode is a submenu of correctly-sized blank rows -
    /// exactly the class of silent styling failure the shell's own notes on
    /// Avalonia style priority are about. The context menu could not use that
    /// route anyway, since it is assembled in code, so binding the other one
    /// would be two mechanisms for one list.
    /// </remarks>
    /// <param name="submenu">The parent item to refill, or null before it exists.</param>
    /// <param name="point">
    /// Where an entry places, evaluated at CLICK time rather than captured
    /// here: the viewport's point is written by the press that opened the menu,
    /// and reading it while building would freeze the first right-click's
    /// position into every later one.
    /// </param>
    private void FillEntityMenu(MenuItem? submenu, Func<System.Numerics.Vector2?> point)
    {
        if (submenu is null)
            return;

        submenu.IsVisible = _shell.EntityClasses.Count > 0;
        FillEntityItems(submenu.Items, point);
    }

    /// <summary>
    /// Fills one list of menu entries, one per class the session can place.
    /// </summary>
    /// <remarks>
    /// <b>Split out of the method above so the ribbon's split button is a THIRD
    /// caller rather than a second mechanism.</b> The remarks on that method
    /// are about exactly this: one list, built in one place, because two ways
    /// of building it drift and the failure is a submenu that quietly stops
    /// matching the other one. A MenuFlyout's items and a MenuItem's are the
    /// same collection type, so the split costs nothing.
    /// </remarks>
    private void FillEntityItems(ItemCollection items, Func<System.Numerics.Vector2?> point)
    {
        items.Clear();

        foreach (EntityInsertItem entry in _shell.EntityClasses)
        {
            // The class NAME is captured, never the item, because the item's
            // own ICommand carries the Object menu's placement and this may be
            // the viewport's.
            string className = entry.ClassName;
            var item = new MenuItem { Header = entry.Display };
            ToolTip.SetTip(item, entry.Tip);
            item.Click += (_, _) =>
            {
                // Remember it for the ribbon's split button. Choosing from ANY
                // of these lists sets what the split places, which is what "the
                // last one used" has to mean: a split button that only learned
                // from its own caret would disagree with the menu the user just
                // used.
                _lastEntityClass = className;
                RefreshEntityInsertTip();
                _session?.InsertEntity(className, point());
            };
            items.Add(item);
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

    // Addressed by node id rather than by the selection, because a wiring edit
    // replaces a whole list: see EditorSession.ApplyEntityConnections.
    private void OnEntityConnectionsEdit(
        Guid nodeId, IReadOnlyList<SpectraEngine.Core.Entities.EntityConnection> connections) =>
        _session?.ApplyEntityConnections(nodeId, connections);

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

    /// <summary>
    /// Repaints every surface the recents feed: the start page's cards and the
    /// File menu's submenu. One method so the two can never disagree.
    /// </summary>
    private void RefreshRecents()
    {
        StartView.ShowRecents(_settings.RecentProjects);

        // Rebuild the submenu's dynamic items: everything before the pinned
        // empty label / separator / clear entries declared in XAML.
        for (int i = RecentProjectsMenu.Items.Count - 1; i >= 0; i--)
        {
            if (RecentProjectsMenu.Items[i] is MenuItem { DataContext: RecentProject })
                RecentProjectsMenu.Items.RemoveAt(i);
        }

        IReadOnlyList<RecentProject> recents = _settings.RecentProjects;
        RecentProjectsEmptyItem.IsVisible = recents.Count == 0;
        RecentProjectsSeparator.IsVisible = recents.Count > 0;
        RecentProjectsClearItem.IsVisible = recents.Count > 0;

        for (int i = 0; i < recents.Count; i++)
        {
            RecentProject recent = recents[i];
            var item = new MenuItem
            {
                Header = recent.Name,
                DataContext = recent,
            };
            ToolTip.SetTip(item, recent.Path);
            item.Click += (_, _) => _ = OpenRecentProjectAsync(recent);
            RecentProjectsMenu.Items.Insert(i, item);
        }
    }

    private void ForgetRecent(RecentProject recent)
    {
        _settings.ForgetProject(recent.Path);
        _settings.Save(_logger);
        RefreshRecents();
    }

    private void OnClearRecentsClicked(object? sender, RoutedEventArgs e)
    {
        foreach (RecentProject recent in _settings.RecentProjects.ToArray())
            _settings.ForgetProject(recent.Path);

        _settings.Save(_logger);
        RefreshRecents();
    }

    /// <summary>
    /// Opens the OS file browser with the given file or folder selected. The
    /// escape hatch every editor owes its users: the shell manages folders on
    /// disk, and "where IS that" must never require retyping a path.
    /// </summary>
    private void RevealInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            _shell.SetError($"Not a usable path: {path}");
            return;
        }

        if (!File.Exists(full) && !Directory.Exists(full))
        {
            _shell.SetError($"'{full}' is not on disk any more.");
            return;
        }

        try
        {
            // /select shows the item IN its parent rather than opening it,
            // which for a map bundle (a folder) is the difference between
            // "here it is" and being dropped inside it.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _logger.LogWarning(ex, "Could not open Explorer at {Path}", full);
            _shell.SetError("Could not open the file browser.");
        }
    }

    /// <summary>
    /// Makes a map the project's startup map, through the same re-read-edit-save
    /// discipline every manifest write uses: the file on disk is the author's,
    /// and the copy in memory may be behind their hand edits.
    /// </summary>
    private void SetStartupMap(ProjectMapRow row)
    {
        if (_document.Project is not { } stale)
            return;

        ProjectLayout project;
        try
        {
            project = ProjectLayout.Open(stale.ManifestPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ProjectFormatException)
        {
            _logger.LogWarning(ex, "Could not re-read the project manifest");
            _shell.SetError("The project manifest could not be read.");
            return;
        }

        // An unlisted map that becomes the startup map is listed as part of
        // the same write: a manifest whose startupMap names a map its own
        // list does not is a file that reads as a mistake.
        if (!project.Project.Maps.Any(m => ManifestPathsEqual(m, row.RelativePath)))
            project.Project.Maps.Add(row.RelativePath);

        project.Project.StartupMap = row.RelativePath;

        try
        {
            project.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the project manifest");
            _shell.SetError("The project manifest could not be written.");
            return;
        }

        _document.SetProject(project);
        RefreshProjectMaps();
        _shell.SetMessage($"{row.Name} is now the startup map.");
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
            RefreshRecents();
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
        RefreshRecents();

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
        if (await PickSaveTargetAsync() is { } target)
            SaveMapTo(target);
    }

    /// <summary>
    /// Asks where a level should be written, or null when the user backed out.
    /// </summary>
    /// <remarks>
    /// A folder picker plus a name, because a level IS a folder: the platform
    /// save dialogs name files, and pointing one at a directory bundle means
    /// either lying about what is being created or depending on whether a
    /// backend happens to touch the path it returns.
    /// </remarks>
    private async Task<string?> PickSaveTargetAsync()
    {
        if (_session is null)
            return null;

        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Folder to save the level into",
                AllowMultiple = false,
                SuggestedStartLocation = await SuggestedStartAsync(_document.SuggestedMapFolder),
            });

        if (picked.Count == 0)
            return null;

        string? name = await NameDialog.AskAsync(
            this, "Save level as", "Name for the level folder:", _document.MapLabel);

        return name is null
            ? null
            : Path.Combine(picked[0].Path.LocalPath, name + MapFormat.BundleExtension);
    }

    private void SaveMapTo(string bundlePath) => _ = SaveMapToAsync(bundlePath);

    /// <summary>
    /// Writes the level and reports whether it landed.
    /// </summary>
    /// <remarks>
    /// <b>Awaitable because one caller has to know.</b> The unsaved-work prompt
    /// offers Save, and "the user chose Save" is not the same as "the level was
    /// saved": the write can fail, and proceeding anyway would discard the work
    /// the user had just asked to keep, having told them it was safe.
    /// </remarks>
    private Task<bool> SaveMapToAsync(string bundlePath)
    {
        if (_session is not { } session)
            return Task.FromResult(false);

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.SaveMap(bundlePath, (report, error) => Dispatcher.UIThread.Post(() =>
        {
            // See NewMap for the session boundary guard.
            if (!ReferenceEquals(session, _session))
                return;

            if (error is not null)
            {
                _shell.SetError($"Could not save the level: {error.Message}");
                done.TrySetResult(false);
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

            done.TrySetResult(true);
        }));

        // A session torn down before the callback runs would leave this pending
        // forever, and the prompt awaiting it with a modal already closed.
        return done.Task;
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
    /// deliberately not listed - <see cref="EditorDocument.MapPathWithinProject"/>
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
        // manifest is the author's file - the format's whole promise is that a
        // person edits it in VS Code and the editor does not fight them - and
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

        UnsavedChoice choice = await ConfirmDialog.AskAsync(this, _document.MapLabel, what);

        return choice switch
        {
            UnsavedChoice.Discard => true,

            // Handled HERE rather than at each of the eight call sites, so
            // every route that can discard work offers the same way to keep it
            // and none of them can forget to.
            UnsavedChoice.Save => await SaveFromPromptAsync(),

            _ => false,
        };
    }

    /// <summary>
    /// Writes the level on the user's behalf from the unsaved-work prompt.
    /// </summary>
    /// <returns>
    /// True when the level is on disk and the gesture that asked may continue.
    /// </returns>
    /// <remarks>
    /// A level that has never been saved needs a target, and choosing one can
    /// itself be cancelled - which means "no, go back", not "yes, discard".
    /// </remarks>
    private async Task<bool> SaveFromPromptAsync()
    {
        string? target = _document.MapPath ?? await PickSaveTargetAsync();
        return target is not null && await SaveMapToAsync(target);
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
        GraphicsBackend backend = ResolveRequestedBackend();

        // Named explicitly and refused explicitly: an embedded GL surface needs
        // its own context, and letting the renderer discover that would report
        // it as a driver failure.
        if (backend is GraphicsBackend.OpenGL)
        {
            throw new NotSupportedException(
                "The editor viewport cannot host OpenGL yet; use d3d11 or d3d12.");
        }

        return backend;
    }

    /// <summary>
    /// What the command line asked for, INCLUDING the backend the shell is going
    /// to refuse.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="ResolveBackend"/> because the viewport
    /// decision has to be able to name OpenGL.</b> Refusing it inside the
    /// resolver means the only way to learn it was asked for is to catch the
    /// exception, and a policy that reported "no compositor" for a request it
    /// refuses by name would be exactly the silent fallback this whole stage
    /// exists to prevent.
    /// </remarks>
    private static GraphicsBackend ResolveRequestedBackend()
    {
        foreach (string arg in Program.StartupArgs)
        {
            switch (arg.ToLowerInvariant())
            {
                case "d3d11": return GraphicsBackend.D3D11;
                case "d3d12": return GraphicsBackend.D3D12;
                case "opengl": return GraphicsBackend.OpenGL;
            }
        }

        return GraphicsBackend.D3D11;
    }
}
