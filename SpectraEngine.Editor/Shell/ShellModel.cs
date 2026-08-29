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
        set => Set(ref _hasProject, value);
    }

    /// <summary>
    /// The open project's maps: the manifest's list in the author's order,
    /// then anything on disk the manifest does not name. Rebuilt whole on the
    /// UI thread when the project or its manifest changes — it is at most a
    /// handful of rows, so the patch discipline the tree needs would be
    /// ceremony here.
    /// </summary>
    public ObservableCollection<ProjectMapRow> ProjectMaps { get; } = [];

    // ─── Command bar ─────────────────────────────────────

    private string _activeTab = "home";

    /// <summary>Which command strip is showing: <c>home</c> or <c>view</c>.</summary>
    /// <remarks>
    /// Purely a UI preference — nothing engine-side changes with the tab, so
    /// it lives here rather than riding a snapshot.
    /// </remarks>
    public string ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!Set(ref _activeTab, value))
                return;

            Raise(nameof(IsHomeTab));
            Raise(nameof(IsViewTab));
        }
    }

    /// <summary>Whether the Home strip is showing.</summary>
    public bool IsHomeTab => _activeTab == "home";

    /// <summary>Whether the View strip is showing.</summary>
    public bool IsViewTab => _activeTab == "view";

    private bool _isPlaying;
    private bool _canPlay;

    /// <summary>Whether play mode is active, for the Play/Stop button's face.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (Set(ref _isPlaying, value))
                Raise(nameof(PlayTip));
        }
    }

    /// <summary>Whether play mode can be entered at all (the scene has a character).</summary>
    public bool CanPlay
    {
        get => _canPlay;
        private set => Set(ref _canPlay, value);
    }

    /// <summary>The play button's tooltip, which names the exit key while playing.</summary>
    public string PlayTip => _isPlaying ? "Stop playing  (F8 or Esc)" : "Play  (F8)";

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
            Raise(nameof(SnapLabel));
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
            if (Set(ref _gizmoStyle, value))
                Raise(nameof(IsStudioStyle));
        }
    }

    /// <summary>Whether the Studio handle roster is live.</summary>
    public bool IsStudioStyle => _gizmoStyle == "Studio";

    /// <summary>The axis frame: <c>world</c> or <c>local</c>.</summary>
    public string Orientation
    {
        get => _orientation;
        private set
        {
            if (Set(ref _orientation, value))
                Raise(nameof(IsWorldSpace));
        }
    }

    /// <summary>Whether drags resolve against world axes.</summary>
    public bool IsWorldSpace => _orientation == "world";

    /// <summary>Whether the live manipulator quantises its drags.</summary>
    public bool SnapEnabled
    {
        get => _snapEnabled;
        private set
        {
            if (Set(ref _snapEnabled, value))
                Raise(nameof(SnapLabel));
        }
    }

    /// <summary>The live manipulator's increment.</summary>
    public float SnapIncrement
    {
        get => _snapIncrement;
        private set
        {
            if (Set(ref _snapIncrement, value))
                Raise(nameof(SnapLabel));
        }
    }

    /// <summary>
    /// Snap state as one readable phrase, with the unit the live tool actually
    /// edits in.
    /// </summary>
    /// <remarks>
    /// The unit is not decoration: the three snaps are absolute quantities of
    /// the thing being edited, so the same number means world units under move
    /// and degrees under rotate. A bare "0.25" beside a rotate tool would be a
    /// lie.
    /// </remarks>
    public string SnapLabel => !_snapEnabled
        ? "snap off"
        : _gizmoMode == "rotate"
            ? $"snap {_snapIncrement:0.##}°"
            : $"snap {_snapIncrement:0.##} su";

    /// <summary>Which camera is driving.</summary>
    public string Navigation
    {
        get => _navigation;
        private set => Set(ref _navigation, value);
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

    /// <summary>The selection as a phrase.</summary>
    public string SelectionLabel => _selectionCount switch
    {
        0 => "nothing selected",
        1 => "1 selected",
        _ => $"{_selectionCount} selected",
    };

    /// <summary>How many edits can be undone.</summary>
    public int UndoDepth
    {
        get => _undoDepth;
        private set
        {
            if (Set(ref _undoDepth, value))
                Raise(nameof(CanUndo));
        }
    }

    /// <summary>How many undone edits can be redone.</summary>
    public int RedoDepth
    {
        get => _redoDepth;
        private set
        {
            if (Set(ref _redoDepth, value))
                Raise(nameof(CanRedo));
        }
    }

    /// <summary>Whether the undo button should be live.</summary>
    public bool CanUndo => _undoDepth > 0;

    /// <summary>Whether the redo button should be live.</summary>
    public bool CanRedo => _redoDepth > 0;

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

    /// <summary>Frame time, formatted.</summary>
    public string FrameTimeLabel => $"{_frameTimeMs,6:0.00} ms";

    /// <summary>How many static-world compiles have landed.</summary>
    public int CompileCount
    {
        get => _compileCount;
        private set => Set(ref _compileCount, value);
    }

    /// <summary>How many nodes the tree holds.</summary>
    public int NodeCount
    {
        get => _nodeCount;
        private set
        {
            if (Set(ref _nodeCount, value))
                Raise(nameof(TreeCountLabel));
        }
    }

    /// <summary>How many nodes pass the filter.</summary>
    public int MatchCount
    {
        get => _matchCount;
        private set
        {
            if (Set(ref _matchCount, value))
                Raise(nameof(TreeCountLabel));
        }
    }

    /// <summary>
    /// The tree's population, shown as a fraction only while a filter narrows
    /// it. An unfiltered "284 / 284" is noise.
    /// </summary>
    public string TreeCountLabel =>
        _matchCount == _nodeCount ? $"{_nodeCount}" : $"{_matchCount} / {_nodeCount}";

    /// <summary>The viewport's pixel size, which is not the window's.</summary>
    public string ViewportLabel => $"{_viewportWidth}x{_viewportHeight}";

    /// <summary>Records the viewport's current pixel size.</summary>
    public void SetViewportSize(int width, int height)
    {
        _viewportWidth = width;
        _viewportHeight = height;
        Raise(nameof(ViewportLabel));
    }

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

    /// <summary>Reports something that went normally.</summary>
    public void SetMessage(string text)
    {
        Message = text;
        IsError = false;
    }

    /// <summary>Reports a failure, which stays until something replaces it.</summary>
    public void SetError(string text)
    {
        Message = text;
        IsError = true;
    }

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
            FrameTimeMs = snapshot.FrameTimeMs;
            SelectionCount = snapshot.SelectedIds.Count;
            UndoDepth = snapshot.UndoDepth;
            RedoDepth = snapshot.RedoDepth;
            CompileCount = snapshot.StaticWorldCompileCount;

            GizmoMode = snapshot.GizmoModeName ?? "move";
            GizmoStyle = snapshot.GizmoStyleName ?? "Studio";
            Orientation = snapshot.GizmoOrientationName ?? "world";
            SnapEnabled = snapshot.SnapEnabled;
            SnapIncrement = snapshot.SnapIncrement;
            Navigation = snapshot.NavigationModeName ?? "-";

            IsPlaying = snapshot.IsPlaying;
            CanPlay = snapshot.CanPlay;
            PipelineNames = snapshot.PipelineNames;
            PipelineName = snapshot.PipelineName;

            if (_debugFlags != snapshot.DebugFlags)
            {
                _debugFlags = snapshot.DebugFlags;
                Raise(nameof(DebugWireframe));
                Raise(nameof(DebugVertices));
                Raise(nameof(DebugAabbs));
                Raise(nameof(DebugNormals));
                Raise(nameof(DebugSceneGraph));
            }

            _properties?.Apply(snapshot.SelectionProperties, snapshot.SelectedIds.Count);

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
