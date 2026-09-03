using Avalonia.Threading;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// One map in the open project's panel: the manifest's, the folder's, or both.
/// </summary>
/// <param name="RelativePath">Project-relative bundle path, the manifest's key.</param>
/// <param name="Name">The bundle's folder name without its extension.</param>
/// <param name="IsStartup">Whether the manifest boots this one.</param>
/// <param name="IsUnlisted">
/// On disk but not in the manifest — the reconciliation the format docs assign
/// to the editor, shown rather than silently resolved either way.
/// </param>
public sealed record ProjectMapRow(string RelativePath, string Name, bool IsStartup, bool IsUnlisted);

/// <summary>
/// Everything the window binds to: the engine's reported state, the readouts,
/// and the one message line the engine never writes.
/// </summary>
/// <remarks>
/// <b>Written only by the UI thread's pump, from an immutable snapshot.</b>
/// Nothing here reads the engine directly, and nothing the engine owns is
/// referenced: the model holds numbers, strings and booleans copied out of a
/// <see cref="FrameSnapshot"/>, which is what makes binding to it from XAML
/// safe at all.
/// <para>
/// <b>The message line is deliberately not snapshot-driven.</b> Startup
/// failures and platform refusals are written here once and must survive every
/// subsequent frame; a pump that rewrote the whole status line each tick would
/// erase the one diagnostic a user has when the viewport never appears.
/// </para>
/// </remarks>
public sealed class ShellModel : ObservableObject
{
    // ─── Optimistic state ────────────────────────────────
    //
    // Every one of these is a control the user clicks and the engine answers,
    // and every one of them used to sit unchanged for a publish plus a pump
    // after the click. See OptimisticValue for the mechanism and for why a
    // BOUND on the local opinion is the whole design rather than a detail.
    //
    // What is NOT here, deliberately: the pipeline dropdown, because switching
    // a pipeline legitimately takes time and can legitimately fail, so the
    // engine's answer is the one worth showing; and the snap increment, which
    // is a typed field with its own focus guard - a field that stopped taking
    // refreshes AND held an unconfirmed value would have two reasons to
    // disagree with the engine and no way to tell them apart.

    private readonly OptimisticValue<string> _modeOpt = new("move", StringComparer.Ordinal);
    private readonly OptimisticValue<string> _styleOpt = new("Studio", StringComparer.Ordinal);
    private readonly OptimisticValue<string> _orientationOpt = new("world", StringComparer.Ordinal);
    private readonly OptimisticValue<bool> _snapOpt = new(false);
    private readonly OptimisticValue<DebugVisualization> _debugOpt = new(DebugVisualization.None);

    // Play mode holds longer than the rest: entering it is real work (the
    // editor is suspended, a gesture is rolled back, the cursor changes hands)
    // and it is legitimately refused on a scene with no character, so twelve
    // ticks is the difference between "it is starting" and "it said no".
    private readonly OptimisticValue<bool> _playOpt = new(false) { HoldTicks = 12 };

    // Undo and redo depth move TOGETHER, so they are one value. The pair is
    // what makes the prediction possible at all: undo means one fewer to undo
    // and one more to redo, and predicting only half of that would light the
    // redo button against a depth that had not moved.
    //
    // This is the nastiest of the lot without optimism: click Undo, the button
    // stays lit for up to 65ms because the depth has not come back yet, click
    // again, and two edits are gone.
    private readonly OptimisticValue<(int Undo, int Redo)> _historyOpt = new((0, 0));

    private SceneTreeModel? _tree;
    private string _gizmoMode = "move";
    private string _gizmoStyle = "Studio";
    private string _orientation = "world";
    private string _navigation = "-";
    private bool _snapEnabled;
    private float _snapIncrement;
    private int _selectionCount;
    private int _undoDepth;
    private int _redoDepth;
    private int _compileCount;
    private int _nodeCount;
    private int _matchCount;
    private double _fps;
    private double _frameTimeMs;
    private int _viewportWidth;
    private int _viewportHeight;
    private string _message = string.Empty;
    private bool _isError;
    private string _filterText = string.Empty;
    private PropertyPanelModel? _properties;

    private DispatcherTimer? _filterDebounce;

    /// <summary>The scene graph mirror, once a session has started.</summary>
    public SceneTreeModel? Tree
    {
        get => _tree;
        set
        {
            if (Set(ref _tree, value))
                Raise(nameof(HasTree));
        }
    }

    /// <summary>Whether there is a tree to show at all.</summary>
    public bool HasTree => _tree is not null;

    private bool _hasSession;

    /// <summary>
    /// Whether an engine session is running — what separates the editor view
    /// from the start page, and what the toolbar and the session-only menu
    /// items key their visibility and enabling on.
    /// </summary>
    public bool HasSession
    {
        get => _hasSession;
        set => Set(ref _hasSession, value);
    }

    private bool _hasProject;

    /// <summary>Whether a project is open, for the maps panel.</summary>
    public bool HasProject
    {
        get => _hasProject;
        set
        {
            if (Set(ref _hasProject, value)) Raise(nameof(CanValidateCooked));
        }
    }

    private bool _isValidatingCooked;

    /// <summary>Whether a cooked-content validation is running right now.</summary>
    /// <remarks>
    /// <b>It gates its own menu item.</b> A cook is seconds of real work on a
    /// background thread, and a second run started on top of the first would have
    /// two cooks writing one <c>cooked/</c> folder - which the cache and the
    /// writer are both safe against per file and neither promises for a whole
    /// artifact. The item greys out instead, which is also the only thing on
    /// screen saying the first run is still going.
    /// </remarks>
    public bool IsValidatingCooked
    {
        get => _isValidatingCooked;
        set
        {
            if (Set(ref _isValidatingCooked, value)) Raise(nameof(CanValidateCooked));
        }
    }

    /// <summary>Whether the Validate Cooked verb can be asked for.</summary>
    /// <remarks>
    /// A project rather than a session, deliberately: the cook reads the folder
    /// on disk and never touches the scene, so it is answerable with the engine
    /// stopped and while play mode owns the graph.
    /// </remarks>
    public bool CanValidateCooked => _hasProject && !_isValidatingCooked;

    /// <summary>
    /// The open project's maps: the manifest's list in the author's order,
    /// then anything on disk the manifest does not name. Rebuilt whole on the
    /// UI thread when the project or its manifest changes — it is at most a
    /// handful of rows, so the patch discipline the tree needs would be
    /// ceremony here.
    /// </summary>
    public ObservableCollection<ProjectMapRow> ProjectMaps { get; } = [];

    // ─── Document identity ───────────────────────────────
    //
    // Mirrored from EditorDocument rather than bound through it, for the same
    // reason everything else here is a copy: the window binds to ONE model, and
    // a second DataContext half way down a StackPanel is a thing every later
    // reader has to notice.

    private string _documentName = "untitled";
    private string _projectName = string.Empty;
    private bool _isDocumentDirty;

    /// <summary>The open level's name, which is its bundle folder's name.</summary>
    public string DocumentName
    {
        get => _documentName;
        private set => Set(ref _documentName, value);
    }

    /// <summary>The open project's name, or empty when a bundle was opened alone.</summary>
    public string ProjectName
    {
        get => _projectName;
        private set => Set(ref _projectName, value);
    }

    /// <summary>Whether the level has unsaved edits, for the mark beside its name.</summary>
    public bool IsDocumentDirty
    {
        get => _isDocumentDirty;
        private set => Set(ref _isDocumentDirty, value);
    }

    /// <summary>Takes the document's identity. UI thread.</summary>
    public void SetDocument(string name, string project, bool dirty)
    {
        DocumentName = name;
        ProjectName = project;
        IsDocumentDirty = dirty;
    }

    /// <summary>The Help menu's version line.</summary>
    public string AboutLabel { get; set; } = "Spectra Editor";

    // ─── Command bar ─────────────────────────────────────

    private bool _isPlaying;
    private bool _canPlay;

    /// <summary>Whether play mode is active, for the Play/Stop button's face.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value))
                return;

            Raise(nameof(PlayTip));
            Raise(nameof(PlayLabel));
        }
    }

    /// <summary>Whether play mode can be entered at all (the scene has a character).</summary>
    public bool CanPlay
    {
        get => _canPlay;
        private set
        {
            if (Set(ref _canPlay, value))
                Raise(nameof(PlayTip));
        }
    }

    /// <summary>The play button's tooltip, which names the exit key while playing.</summary>
    public string PlayTip => _isPlaying
        ? "Stop the run and put the camera back.  F8 or Esc"
        : _canPlay
            ? "Walk the level in first person.  F8"
            : "This level has no character to walk with.";

    /// <summary>The play button's word. It is a button with room for one.</summary>
    public string PlayLabel => _isPlaying ? "Stop" : "Play";

    private DebugVisualization _debugFlags;

    /// <summary>Whether the wireframe overlay is on.</summary>
    public bool DebugWireframe => (_debugFlags & DebugVisualization.Wireframe) != 0;

    /// <summary>Whether the vertex-marker overlay is on.</summary>
    public bool DebugVertices => (_debugFlags & DebugVisualization.Vertices) != 0;

    /// <summary>Whether the bounds overlay is on.</summary>
    public bool DebugAabbs => (_debugFlags & DebugVisualization.Aabbs) != 0;

    /// <summary>Whether the normals overlay is on.</summary>
    public bool DebugNormals => (_debugFlags & DebugVisualization.Normals) != 0;

    /// <summary>Whether the scene-graph overlay is on.</summary>
    public bool DebugSceneGraph => (_debugFlags & DebugVisualization.SceneGraph) != 0;

    /// <summary>
    /// Whether one overlay is on, for a caller holding the FLAG rather than the
    /// name.
    /// </summary>
    /// <remarks>
    /// The ribbon's overlay buttons carry their flag in the roster, so they ask
    /// this rather than switching over five named properties: a sixth overlay
    /// would otherwise need a case here as well as a row there, and the one
    /// that got forgotten would silently always request "turn it on".
    /// </remarks>
    public bool IsDebugEnabled(DebugVisualization flag) => (_debugFlags & flag) != 0;

    private IReadOnlyList<string> _pipelineNames = Array.Empty<string>();
    private string? _pipelineName;

    /// <summary>Every pipeline the backend offers, for the View strip's dropdown.</summary>
    public IReadOnlyList<string> PipelineNames
    {
        get => _pipelineNames;
        private set => Set(ref _pipelineNames, value);
    }

    /// <summary>
    /// The live pipeline. Two-way: the dropdown writes a USER choice here,
    /// which raises <see cref="PipelineRequested"/>; the snapshot writes the
    /// engine's answer back under the guard, which raises nothing.
    /// </summary>
    /// <remarks>
    /// The guard is the checkbox rule from the property panel: assigning the
    /// published value back looks exactly like a selection, and without the
    /// flag every snapshot would re-request the pipeline it just reported.
    /// </remarks>
    public string? PipelineName
    {
        get => _pipelineName;
        set
        {
            if (!Set(ref _pipelineName, value))
                return;

            // Null is the dropdown clearing itself while its items change,
            // not a person choosing nothing; there is no "no pipeline".
            if (!_applyingSnapshot && value is { Length: > 0 } requested)
                PipelineRequested?.Invoke(requested);
        }
    }

    /// <summary>Raised when the user picks a pipeline, with its name.</summary>
    public event Action<string>? PipelineRequested;

    private bool _applyingSnapshot;

    // ─── Tool state ──────────────────────────────────────
    // Mirrored as booleans as well as labels, because a toolbar needs to know
    // which of three buttons is lit and a XAML class binding cannot compare
    // strings.

    /// <summary>The live manipulator: <c>move</c>, <c>rotate</c> or <c>resize</c>.</summary>
    public string GizmoMode
    {
        get => _gizmoMode;
        private set
        {
            if (!Set(ref _gizmoMode, value))
                return;

            Raise(nameof(IsMoveActive));
            Raise(nameof(IsRotateActive));
            Raise(nameof(IsResizeActive));
            Raise(nameof(SnapUnitLabel));
            Raise(nameof(SnapSummary));
            GizmoModeChanged?.Invoke();
        }
    }

    /// <summary>Whether the move tool is live.</summary>
    public bool IsMoveActive => _gizmoMode == "move";

    /// <summary>Whether the rotate tool is live.</summary>
    public bool IsRotateActive => _gizmoMode == "rotate";

    /// <summary>Whether the resize tool is live.</summary>
    public bool IsResizeActive => _gizmoMode == "resize";

    /// <summary>The handle style: <c>Studio</c> or <c>Classic</c>.</summary>
    public string GizmoStyle
    {
        get => _gizmoStyle;
        private set
        {
            if (!Set(ref _gizmoStyle, value))
                return;

            Raise(nameof(IsStudioStyle));
            Raise(nameof(GizmoStyleMenuLabel));
        }
    }

    /// <summary>Whether the Studio handle roster is live.</summary>
    public bool IsStudioStyle => _gizmoStyle == "Studio";

    // ─── What the user just asked for ────────────────────
    //
    // Each of these shows the requested value at once and starts the hold-off.
    // The caller still posts the verb to the engine; these do not talk to it,
    // because a model that also drove the engine would be a second path from a
    // gesture to an edit, and there is exactly one.

    /// <summary>The user picked a tool. Lights it now; the engine confirms.</summary>
    public void RequestGizmoMode(string mode)
    {
        _modeOpt.Request(mode);
        GizmoMode = _modeOpt.Value;
    }

    /// <summary>The user picked a handle style.</summary>
    public void RequestGizmoStyle(string style)
    {
        _styleOpt.Request(style);
        GizmoStyle = _styleOpt.Value;
    }

    /// <summary>The user picked an axis frame.</summary>
    public void RequestOrientation(string orientation)
    {
        _orientationOpt.Request(orientation);
        Orientation = _orientationOpt.Value;
    }

    /// <summary>The user turned snapping on or off.</summary>
    public void RequestSnapEnabled(bool enabled)
    {
        _snapOpt.Request(enabled);
        SnapEnabled = _snapOpt.Value;
    }

    /// <summary>The user asked to start or stop play mode.</summary>
    public void RequestPlaying(bool playing)
    {
        _playOpt.Request(playing);
        IsPlaying = _playOpt.Value;
    }

    /// <summary>The user turned one debug visualisation on or off.</summary>
    public void RequestDebugVisualization(DebugVisualization flag, bool enabled)
    {
        DebugVisualization wanted = enabled ? _debugFlags | flag : _debugFlags & ~flag;
        _debugOpt.Request(wanted);
        SetDebugFlags(_debugOpt.Value);
    }

    /// <summary>
    /// The user asked to undo. Predicts the depths so the buttons settle on
    /// the click rather than a snapshot later.
    /// </summary>
    /// <remarks>
    /// A refused undo - the editor is mid-gesture, or suspended - re-lights the
    /// button when the engine disagrees for long enough, which is the visible
    /// refusal the plain version never gave.
    /// </remarks>
    public void RequestUndo() =>
        ApplyHistory(_historyOpt.Request((Math.Max(0, _undoDepth - 1), _redoDepth + 1)));

    /// <summary>The user asked to redo.</summary>
    public void RequestRedo() =>
        ApplyHistory(_historyOpt.Request((_undoDepth + 1, Math.Max(0, _redoDepth - 1))));

    private void ApplyHistory(bool _)
    {
        (int undo, int redo) = _historyOpt.Value;
        UndoDepth = undo;
        RedoDepth = redo;
    }

    private void SetDebugFlags(DebugVisualization flags)
    {
        if (_debugFlags == flags)
            return;

        _debugFlags = flags;
        Raise(nameof(DebugWireframe));
        Raise(nameof(DebugVertices));
        Raise(nameof(DebugAabbs));
        Raise(nameof(DebugNormals));
        Raise(nameof(DebugSceneGraph));
    }

    /// <summary>
    /// Drops every unconfirmed request, because the engine they were aimed at
    /// is gone.
    /// </summary>
    /// <remarks>
    /// Called when a session closes. Without it, a tool picked in the last
    /// second of one project is still pending when the next one opens, and its
    /// first six snapshots are ignored - so a fresh session shows the previous
    /// session's tool for a tenth of a second, on a scene that never had it.
    /// </remarks>
    public void ResetOptimisticState()
    {
        _modeOpt.Reset(_gizmoMode);
        _styleOpt.Reset(_gizmoStyle);
        _orientationOpt.Reset(_orientation);
        _snapOpt.Reset(_snapEnabled);
        _debugOpt.Reset(_debugFlags);
        _playOpt.Reset(_isPlaying);
        _historyOpt.Reset((_undoDepth, _redoDepth));
    }

    /// <summary>
    /// Raised when the engine reports a different live tool.
    /// </summary>
    /// <remarks>
    /// The command bar's single snap field belongs to whichever tool is live,
    /// so switching tools has to re-read the increment into it. UI thread.
    /// </remarks>
    public event Action? GizmoModeChanged;

    /// <summary>The axis frame: <c>world</c> or <c>local</c>.</summary>
    public string Orientation
    {
        get => _orientation;
        private set
        {
            if (!Set(ref _orientation, value))
                return;

            Raise(nameof(IsWorldSpace));
            Raise(nameof(OrientationMenuLabel));
        }
    }

    /// <summary>Whether drags resolve against world axes.</summary>
    public bool IsWorldSpace => _orientation == "world";

    /// <summary>
    /// The Edit menu's wording for the axis toggle.
    /// </summary>
    /// <remarks>
    /// A menu item that toggles state should say what the state IS, not name
    /// the mechanism. "Drag axes: world" toggles to local and reads correctly
    /// either way; "Toggle orientation" tells the reader nothing about what
    /// they will get.
    /// </remarks>
    public string OrientationMenuLabel => $"Drag axes: {_orientation}";

    /// <summary>Whether the live manipulator quantises its drags.</summary>
    public bool SnapEnabled
    {
        get => _snapEnabled;
        private set
        {
            // Inside the guard, both of them: these setters run on every pump,
            // and an unguarded Raise makes every binding re-read (and the
            // summary re-interpolate its string) hundreds of times a second
            // for a value that has not moved.
            if (Set(ref _snapEnabled, value))
            {
                Raise(nameof(SnapUnitLabel));
                Raise(nameof(SnapSummary));
            }
        }
    }

    /// <summary>The live manipulator's increment.</summary>
    public float SnapIncrement
    {
        get => _snapIncrement;
        private set
        {
            if (Set(ref _snapIncrement, value))
            {
                Raise(nameof(SnapUnitLabel));
                Raise(nameof(SnapSummary));
            }
        }
    }

    /// <summary>
    /// The unit the LIVE tool's snap increment is measured in.
    /// </summary>
    /// <remarks>
    /// <b>Not decoration.</b> The three snaps are absolute quantities of the
    /// thing being edited, so the same number means world units under move and
    /// degrees under rotate; a bare "0.25" beside a rotate tool would be a lie.
    /// The command bar shows one increment field rather than three, and this is
    /// what stops that being ambiguous: the unit beside the number changes when
    /// the tool does.
    /// </remarks>
    public string SnapUnitLabel => _gizmoMode == "rotate" ? "deg" : "su";

    /// <summary>
    /// Snap state as one phrase, for the inspector's empty state.
    /// </summary>
    /// <remarks>
    /// The command bar shows the increment as a field beside a lit toggle, which
    /// is the right shape for something you change. This is the right shape for
    /// something you are merely being told, in a panel that would otherwise be
    /// blank.
    /// </remarks>
    public string SnapSummary => _snapEnabled
        ? $"{_snapIncrement:0.##} {SnapUnitLabel}"
        : "off";

    /// <summary>The Edit menu's wording for the handle-style toggle.</summary>
    public string GizmoStyleMenuLabel => $"Handles: {_gizmoStyle}";

    /// <summary>Which camera is driving.</summary>
    public string Navigation
    {
        get => _navigation;
        private set
        {
            if (Set(ref _navigation, value))
                Raise(nameof(NavigationMenuLabel));
        }
    }

    /// <summary>The View menu's wording for the camera toggle.</summary>
    public string NavigationMenuLabel => $"Camera: {_navigation}";

    // The engine's answer, snapshot-followed with no optimistic hold: the menu
    // is closed by the time the echo lands, so there is nothing to flicker.
    private string _gridMode = "auto";

    /// <summary>Whether the grid shows during move/resize gestures only.</summary>
    public bool GridAuto => _gridMode == "auto";

    /// <summary>Whether the grid is always drawn.</summary>
    public bool GridOn => _gridMode == "on";

    /// <summary>Whether the grid is off.</summary>
    public bool GridOff => _gridMode == "off";

    /// <summary>The grid mode as the header chip's value word.</summary>
    public string GridModeLabel => _gridMode;

    private void ApplyGridMode(string mode)
    {
        if (_gridMode == mode)
            return;

        _gridMode = mode;
        Raise(nameof(GridAuto));
        Raise(nameof(GridOn));
        Raise(nameof(GridOff));
        Raise(nameof(GridModeLabel));
    }

    // ─── Selection and history ───────────────────────────

    /// <summary>How many nodes the engine reports as selected.</summary>
    public int SelectionCount
    {
        get => _selectionCount;
        private set
        {
            if (!Set(ref _selectionCount, value))
                return;

            Raise(nameof(HasSelection));
            Raise(nameof(SelectionLabel));
        }
    }

    /// <summary>Whether anything is selected, for the structural buttons.</summary>
    public bool HasSelection => _selectionCount > 0;

    /// <summary>
    /// The selection as a phrase, NAMING the object in hand: "1 selected"
    /// answers how-many, never what, and every engine editor's status surface
    /// says what. The name resolves through the tree mirror, which the pump
    /// has already brought up to date by the time the snapshot is applied, so
    /// a rename is reflected on the same pump.
    /// </summary>
    public string SelectionLabel => _selectionCount switch
    {
        0 => "nothing selected",
        1 => _selectionName ?? "1 selected",
        _ => _selectionName is { } name
            ? $"{name} +{_selectionCount - 1}"
            : $"{_selectionCount} selected",
    };

    private string? _selectionName;

    private void UpdateSelectionName(FrameSnapshot snapshot)
    {
        string? name = null;
        if (snapshot.SelectedIds.Count > 0 && _tree is { } tree
            && tree.TryGetNode(snapshot.SelectedIds[0], out var node))
        {
            name = node.Name;
        }

        if (!string.Equals(_selectionName, name, StringComparison.Ordinal))
        {
            _selectionName = name;
            Raise(nameof(SelectionLabel));
        }
    }

    /// <summary>How many edits can be undone.</summary>
    public int UndoDepth
    {
        get => _undoDepth;
        private set
        {
            if (!Set(ref _undoDepth, value))
                return;

            Raise(nameof(CanUndo));
            Raise(nameof(UndoTip));
        }
    }

    /// <summary>How many undone edits can be redone.</summary>
    public int RedoDepth
    {
        get => _redoDepth;
        private set
        {
            if (!Set(ref _redoDepth, value))
                return;

            Raise(nameof(CanRedo));
            Raise(nameof(RedoTip));
        }
    }

    /// <summary>Whether the undo button should be live.</summary>
    public bool CanUndo => _undoDepth > 0;

    /// <summary>Whether the redo button should be live.</summary>
    public bool CanRedo => _redoDepth > 0;

    /// <summary>
    /// The undo button's tooltip, which says how deep the history is.
    /// </summary>
    /// <remarks>
    /// A disabled icon button with a bare "Undo" tooltip leaves the user
    /// guessing whether the tool refused them or had nothing to do. The depth
    /// answers that, and it is already published on every snapshot.
    /// </remarks>
    public string UndoTip => _undoDepth == 0
        ? "Nothing to undo"
        : $"Undo, {_undoDepth} step(s) back.  Ctrl+Z";

    /// <summary>The redo button's tooltip. See <see cref="UndoTip"/>.</summary>
    public string RedoTip => _redoDepth == 0
        ? "Nothing to redo"
        : $"Redo, {_redoDepth} step(s) forward.  Ctrl+Y";

    // ─── Readouts ────────────────────────────────────────

    /// <summary>The engine's smoothed frame rate.</summary>
    public double Fps
    {
        get => _fps;
        private set
        {
            if (Set(ref _fps, value))
                Raise(nameof(FpsLabel));
        }
    }

    /// <summary>The engine's smoothed frame time.</summary>
    public double FrameTimeMs
    {
        get => _frameTimeMs;
        private set
        {
            if (!Set(ref _frameTimeMs, value))
                return;

            Raise(nameof(FrameTimeLabel));
            Raise(nameof(FrameTimeOverBudget));
        }
    }

    /// <summary>Whether the frame is over a 30 Hz budget, for the readout's colour.</summary>
    public bool FrameTimeOverBudget => _frameTimeMs > 33.0;

    /// <summary>Frame rate, formatted.</summary>
    public string FpsLabel => $"{_fps,5:0} fps";

    private float _sharedAcquirePeakMs;

    /// <summary>
    /// The longest wait the render thread spent on the shared target's key in
    /// the last publish window, in milliseconds.
    /// </summary>
    /// <remarks>
    /// <b>A composited viewport's frame rate is coupled to this window's own
    /// responsiveness, and no other instrument can see it.</b> The keyed mutex
    /// is the clock: the engine cannot begin a frame until the consumer
    /// releases key 0, and the consumer releases it from a continuation on this
    /// dispatcher. So a frame rate that falls while this stays near zero is the
    /// engine's own drawing cost, and a frame rate that falls while this rises
    /// is the UI thread holding the engine up, which no amount of render work
    /// will fix. The distinction is invisible in frame time, because the wait
    /// happens inside the frame.
    /// </remarks>
    public float SharedAcquirePeakMs
    {
        get => _sharedAcquirePeakMs;
        private set
        {
            if (Set(ref _sharedAcquirePeakMs, value))
            {
                Raise(nameof(SharedAcquireLabel));
                Raise(nameof(SharedAcquireVisible));
            }
        }
    }

    /// <summary>
    /// Whether the producer waited long enough to be worth showing.
    /// </summary>
    /// <remarks>
    /// A windowed session never waits at all and would otherwise carry a
    /// permanent "0.0 ms" beside its frame rate, which is a reading nobody
    /// needs and a slot that then teaches people to ignore it. The floor is a
    /// fifth of a 60 Hz frame: below that the wait cannot be what anyone is
    /// feeling.
    /// </remarks>
    public bool SharedAcquireVisible => _sharedAcquirePeakMs >= 3.3f;

    /// <summary>The wait, as the status bar shows it.</summary>
    public string SharedAcquireLabel => $"UI stall {_sharedAcquirePeakMs,4:0.0} ms";

    /// <summary>Frame time, formatted.</summary>
    public string FrameTimeLabel => $"{_frameTimeMs,6:0.00} ms";

    /// <summary>How many static-world compiles have landed.</summary>
    public int CompileCount
    {
        get => _compileCount;
        private set => Set(ref _compileCount, value);
    }

    /// <summary>
    /// The viewport camera's position, formatted for the header strip: mono,
    /// one decimal, invariant, so it reads as an instrument.
    /// </summary>
    public string CameraPositionLabel { get; private set; } = string.Empty;

    // Compared ROUNDED, so the label re-formats only when a shown digit
    // actually moves rather than on every sub-millimetre drift; NaN seeds the
    // first publish, since NaN never equals itself.
    private System.Numerics.Vector3 _cameraShown = new(float.NaN);

    private void UpdateCameraReadout(System.Numerics.Vector3 position)
    {
        var rounded = new System.Numerics.Vector3(
            MathF.Round(position.X, 1), MathF.Round(position.Y, 1), MathF.Round(position.Z, 1));
        if (rounded == _cameraShown)
            return;

        _cameraShown = rounded;
        CameraPositionLabel = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{rounded.X:0.0}  {rounded.Y:0.0}  {rounded.Z:0.0}");
        Raise(nameof(CameraPositionLabel));
    }

    /// <summary>How many nodes the tree holds.</summary>
    public int NodeCount
    {
        get => _nodeCount;
        private set
        {
            if (Set(ref _nodeCount, value))
            {
                Raise(nameof(TreeCountLabel));
                Raise(nameof(NodeCountLabel));
            }
        }
    }

    /// <summary>The scene's population, for the status bar.</summary>
    public string NodeCountLabel => _nodeCount == 1 ? "1 node" : $"{_nodeCount} nodes";

    /// <summary>How many nodes pass the filter.</summary>
    public int MatchCount
    {
        get => _matchCount;
        private set
        {
            if (!Set(ref _matchCount, value))
                return;

            Raise(nameof(TreeCountLabel));
            Raise(nameof(HasNoMatches));
            Raise(nameof(NoMatchLabel));
        }
    }

    /// <summary>
    /// The tree's population, shown as a fraction only while a filter narrows
    /// it. An unfiltered "284 / 284" is noise.
    /// </summary>
    public string TreeCountLabel =>
        _matchCount == _nodeCount ? $"{_nodeCount}" : $"{_matchCount} / {_nodeCount}";

    /// <summary>
    /// Whether a filter is on and nothing passes it.
    /// </summary>
    /// <remarks>
    /// <b>The tree DIMS rather than hides</b>, which is right - removing rows
    /// collapses the hierarchy around every match and destroys the only thing a
    /// user has after two hundred nodes, which is knowing where things live. It
    /// does mean a zero-match filter looks exactly like a panel that stopped
    /// working: every row is still there, greyed, and the only signal is a
    /// counter reading "0 /". Hence this, and the line the panel shows.
    /// </remarks>
    public bool HasNoMatches => _filterText.Length > 0 && _matchCount == 0;

    /// <summary>What to say when the filter matched nothing.</summary>
    public string NoMatchLabel =>
        _tree is { FilterIsUnknown: true }
            ? $"“{_filterText}” is not a kind. Try t:block, t:part, t:cut, t:light, t:mesh or t:group."
            : $"Nothing here is called “{_filterText}”.";

    /// <summary>The viewport's pixel size, which is not the window's.</summary>
    public string ViewportLabel => $"{_viewportWidth}x{_viewportHeight}";

    /// <summary>Records the viewport's current pixel size.</summary>
    public void SetViewportSize(int width, int height)
    {
        _viewportWidth = width;
        _viewportHeight = height;
        Raise(nameof(ViewportLabel));
    }

    // ─── The drop overlay ────────────────────────────────

    private ViewportDropPrompt _dropPrompt = ViewportDropPrompt.None;

    /// <summary>
    /// What the viewport draws over the picture while an asset drag is over it.
    /// </summary>
    /// <remarks>
    /// <b>Assigned whole and compared whole, because the source fires at
    /// pointer rate.</b> A record struct's equality is the guard: an unchanged
    /// prompt raises nothing, so a drag crossing the pane costs one comparison
    /// per pointer move rather than five property-changed notifications and the
    /// bindings behind them. The four bindable halves below are read-only views
    /// of this one value, so they cannot disagree with each other.
    /// </remarks>
    public ViewportDropPrompt DropPrompt
    {
        get => _dropPrompt;
        set
        {
            if (_dropPrompt == value)
                return;

            _dropPrompt = value;
            Raise(nameof(DropVisible));
            Raise(nameof(DropAccepts));
            Raise(nameof(DropHeadline));
            Raise(nameof(DropSubject));
            Raise(nameof(DropReason));
        }
    }

    /// <summary>Whether the drop overlay is drawn.</summary>
    public bool DropVisible => _dropPrompt.IsVisible;

    /// <summary>Whether letting go would place something.</summary>
    public bool DropAccepts => _dropPrompt.Accepts;

    /// <summary>The overlay's verdict.</summary>
    public string DropHeadline => _dropPrompt.Headline;

    /// <summary>What would be placed, as the path the engine names it by.</summary>
    public string DropSubject => _dropPrompt.Subject;

    /// <summary>Why not, when the answer is no.</summary>
    public string DropReason => _dropPrompt.Reason;

    // ─── Message line ────────────────────────────────────

    /// <summary>The transient message zone, at the left of the status bar.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
                Raise(nameof(HasMessage));
        }
    }

    /// <summary>Whether the current message is a failure.</summary>
    public bool IsError
    {
        get => _isError;
        private set => Set(ref _isError, value);
    }

    /// <summary>Whether there is anything to show in the message zone.</summary>
    public bool HasMessage => _message.Length > 0;

    private string? _worldDefect;

    /// <summary>
    /// Why the level has stopped rebuilding, or null when it is current.
    /// </summary>
    /// <remarks>
    /// <b>Its own channel, not the message line.</b> The message line is
    /// last-writer-wins and every save, open and refusal writes to it, so a
    /// standing failure put there is gone by the next thing that happens. This
    /// one is a state rather than an event: it is true until it is fixed, and it
    /// means edits are landing in a level the viewport is no longer showing.
    /// </remarks>
    public string? WorldDefect
    {
        get => _worldDefect;
        private set
        {
            if (!Set(ref _worldDefect, value))
                return;

            Raise(nameof(HasWorldDefect));
            Raise(nameof(WorldDefectTip));
        }
    }

    /// <summary>Whether to show the standing world warning.</summary>
    public bool HasWorldDefect => !string.IsNullOrEmpty(_worldDefect);

    /// <summary>The warning's full text, for its tooltip.</summary>
    public string WorldDefectTip =>
        $"The level has stopped rebuilding, so the viewport is showing the last version that compiled. {_worldDefect}";

    private int _debugLayerErrors;
    private bool _debugLayerActive;

    /// <summary>
    /// How many errors the graphics validation layer has reported this session.
    /// </summary>
    /// <remarks>
    /// <b>Its own standing slot beside the world defect, for the same reason
    /// that one has one.</b> The faults this counts - a missing barrier, a
    /// pipeline state bound to a format it was not compiled for - still draw a
    /// picture, so the viewport cannot show them and the message line, which is
    /// last-writer-wins, would lose them to the next thing that happens.
    /// </remarks>
    public int DebugLayerErrors
    {
        get => _debugLayerErrors;
        private set
        {
            if (!Set(ref _debugLayerErrors, value))
                return;

            Raise(nameof(HasDebugLayerErrors));
            Raise(nameof(DebugLayerLabel));
            Raise(nameof(DebugLayerClean));
            Raise(nameof(DebugLayerTip));
        }
    }

    /// <summary>Whether the layer producing that count is running at all.</summary>
    public bool DebugLayerActive
    {
        get => _debugLayerActive;
        private set
        {
            if (!Set(ref _debugLayerActive, value))
                return;

            Raise(nameof(DebugLayerClean));
            Raise(nameof(DebugLayerTip));
        }
    }

    /// <summary>Whether to show the standing graphics warning.</summary>
    public bool HasDebugLayerErrors => _debugLayerErrors > 0;

    /// <summary>
    /// Whether the detector is running AND has reported nothing.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not "the count is zero".</b> On D3D the count exists only
    /// while validation is on, so an inactive layer reports zero for the same
    /// reason a clean session does, and reading the number alone turns "nothing
    /// is watching" into "nothing is wrong".
    /// </remarks>
    public bool DebugLayerClean => _debugLayerActive && _debugLayerErrors == 0;

    /// <summary>What the graphics detector has to say, for the slot's tooltip.</summary>
    public string DebugLayerTip
    {
        get
        {
            if (!_debugLayerActive)
            {
                return "The graphics validation layer is not running, so nothing is watching for " +
                    "driver-level faults. Re-run with --debug-layer=true for the full check.";
            }

            return _debugLayerErrors == 0
                ? "The graphics validation layer is running and has reported nothing."
                : $"The graphics validation layer has reported {_debugLayerErrors} error(s). These draw a " +
                    "picture rather than stopping the frame, so the viewport cannot show them; the run log has the detail.";
        }
    }

    /// <summary>The standing warning's text.</summary>
    public string DebugLayerLabel =>
        _debugLayerErrors == 1 ? "1 graphics error" : $"{_debugLayerErrors} graphics errors";

    /// <summary>Reports something that went normally.</summary>
    public void SetMessage(string text)
    {
        Message = text;
        IsError = false;
        Output.Append(OutputSeverity.Info, text);
    }

    /// <summary>Reports a failure, which stays until something replaces it.</summary>
    public void SetError(string text)
    {
        Message = text;
        IsError = true;
        Output.Append(OutputSeverity.Error, text);
    }

    /// <summary>Reports something that is not right but did not stop anything.</summary>
    public void SetWarning(string text)
    {
        Message = text;
        IsError = false;
        Output.Append(OutputSeverity.Warning, text);
    }

    /// <summary>
    /// Everything the shell has reported, oldest first.
    /// </summary>
    /// <remarks>
    /// <b>The status line is a VIEW of this, not the storage.</b> It used to be
    /// the storage, which meant about thirty call sites shared one string and
    /// each one silently destroyed whatever the last had written - so a failure
    /// reported while the user was looking somewhere else was gone by the time
    /// they looked back. The line still shows the newest entry; what changed is
    /// that the entry before it survives.
    /// </remarks>
    public OutputLog Output { get; } = new();

    /// <summary>
    /// The project's assets, browsed. Assigned by the window, which is the only
    /// thing that knows where a project's content root is.
    /// </summary>
    public ContentBrowserModel? Content { get; set; }

    // ─── Filter ──────────────────────────────────────────

    /// <summary>
    /// The scene filter's text. Applied to the tree after a short pause rather
    /// than per keystroke.
    /// </summary>
    /// <remarks>
    /// <b>Debounced because a filter pass touches every node.</b> Typing five
    /// characters into an unthrottled filter runs five full walks in under a
    /// second, and the visible symptom is a keyboard that lags rather than a
    /// tree that is slow, which is much harder to attribute.
    /// </remarks>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!Set(ref _filterText, value))
                return;

            Raise(nameof(HasFilter));
            Raise(nameof(HasNoMatches));
            Raise(nameof(NoMatchLabel));
            _filterDebounce?.Stop();
            _filterDebounce ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => ApplyFilterNow());
            _filterDebounce.Start();
        }
    }

    /// <summary>
    /// The property panel, or null until the session exists to apply edits
    /// through.
    /// </summary>
    public PropertyPanelModel? Properties
    {
        get => _properties;
        set
        {
            if (Set(ref _properties, value))
                Raise(nameof(HasProperties));
        }
    }

    /// <summary>Whether the panel is wired up at all.</summary>
    public bool HasProperties => _properties is not null;

    /// <summary>
    /// The entity classes the Insert menu offers, in catalogue order.
    /// </summary>
    /// <remarks>
    /// <b>A list rather than six more Click handlers</b>, because which classes
    /// exist is a fact about the open project rather than about this build:
    /// they are read out of a <c>.sentdef</c> at session start and cannot be
    /// written into XAML at all. See <see cref="EntityInsertMenu"/> for why the
    /// source has to be the PARSED catalogue.
    /// </remarks>
    public ObservableCollection<EntityInsertItem> EntityClasses { get; } = [];

    /// <summary>
    /// Whether there is anything to put in the entity submenu, so an empty one
    /// is hidden rather than opened onto nothing.
    /// </summary>
    public bool HasEntityClasses => EntityClasses.Count > 0;

    /// <summary>
    /// Replaces the entity submenu's entries. Called when a session opens with
    /// its catalogue, and with null when one closes.
    /// </summary>
    /// <remarks>
    /// Patched by replacement rather than reused, unlike the tree's rows and
    /// the panel's: this list changes exactly twice per session and holds no
    /// state a user can be halfway through.
    /// </remarks>
    public void SetEntityClasses(IReadOnlyList<EntityInsertItem>? items)
    {
        EntityClasses.Clear();
        if (items is not null)
        {
            foreach (EntityInsertItem item in items)
                EntityClasses.Add(item);
        }

        Raise(nameof(HasEntityClasses));
    }

    /// <summary>Whether a filter is narrowing the tree, for the clear button.</summary>
    public bool HasFilter => _filterText.Length > 0;

    /// <summary>Clears the filter immediately.</summary>
    public void ClearFilter()
    {
        FilterText = string.Empty;
        ApplyFilterNow();
    }

    private void ApplyFilterNow()
    {
        _filterDebounce?.Stop();

        if (_tree is not { } tree)
            return;

        tree.ApplyFilter(_filterText);
        MatchCount = tree.MatchCount;

        // Raised unconditionally, because FilterIsUnknown can flip while the
        // count does not: going from "t:zz" to "zzz" leaves MatchCount at 0, so
        // its setter stays silent and the panel would go on offering a list of
        // kind names for a filter that is now an ordinary name search.
        Raise(nameof(NoMatchLabel));
    }

    // ─── The one crossing ────────────────────────────────

    /// <summary>
    /// Copies one finished frame's values in. UI thread, from the pump.
    /// </summary>
    /// <remarks>
    /// Every property here is guarded by an equality check, so a steady frame
    /// with nothing moving raises nothing and the binding layer does no work.
    /// </remarks>
    public void ApplySnapshot(FrameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Guards the two-way bindings (the pipeline dropdown today): writing
        // the engine's reported value back must not read as the user picking
        // it and echo a request straight back at the engine.
        _applyingSnapshot = true;
        try
        {
            Fps = snapshot.Fps;
            SharedAcquirePeakMs = snapshot.SharedAcquirePeakMs;
            FrameTimeMs = snapshot.FrameTimeMs;
            SelectionCount = snapshot.SelectedIds.Count;
            UpdateSelectionName(snapshot);
            UpdateCameraReadout(snapshot.CameraPosition);
            CompileCount = snapshot.StaticWorldCompileCount;
            WorldDefect = snapshot.StaticWorldDefect;

            // Active first: the count's meaning depends on it, and writing the
            // count against a stale activity flag makes the tooltip describe a
            // detector state that is one publish out of date.
            DebugLayerActive = snapshot.DebugLayerActive;
            DebugLayerErrors = snapshot.DebugLayerErrorCount;

            // Everything below that goes through an OptimisticValue is reported
            // BY the engine and possibly still pending FROM the user; Apply is
            // what decides which of the two the UI shows this tick.
            _historyOpt.Apply((snapshot.UndoDepth, snapshot.RedoDepth));
            ApplyHistory(true);

            _modeOpt.Apply(snapshot.GizmoModeName ?? "move");
            GizmoMode = _modeOpt.Value;

            _styleOpt.Apply(snapshot.GizmoStyleName ?? "Studio");
            GizmoStyle = _styleOpt.Value;

            _orientationOpt.Apply(snapshot.GizmoOrientationName ?? "world");
            Orientation = _orientationOpt.Value;

            _snapOpt.Apply(snapshot.SnapEnabled);
            SnapEnabled = _snapOpt.Value;

            // The increment belongs to whichever tool is LIVE, so while a tool
            // switch is unconfirmed the reported increment still describes the
            // previous tool. Writing it would put the move grid in a field
            // labelled degrees for one tick.
            if (!_modeOpt.HasPending)
                SnapIncrement = snapshot.SnapIncrement;

            Navigation = snapshot.NavigationModeName ?? "-";
            ApplyGridMode(snapshot.GridModeName ?? "auto");

            _playOpt.Apply(snapshot.IsPlaying);
            IsPlaying = _playOpt.Value;

            CanPlay = snapshot.CanPlay;
            PipelineNames = snapshot.PipelineNames;
            PipelineName = snapshot.PipelineName;

            _debugOpt.Apply(snapshot.DebugFlags);
            SetDebugFlags(_debugOpt.Value);

            _properties?.Apply(
                snapshot.SelectionProperties, snapshot.SelectedIds.Count, snapshot.SelectionEntity);

            if (_tree is { } tree)
            {
                NodeCount = tree.Count;
                MatchCount = _filterText.Length == 0 ? tree.Count : tree.MatchCount;
            }
        }
        finally
        {
            _applyingSnapshot = false;
        }
    }
}
