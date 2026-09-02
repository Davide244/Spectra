using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Viewport;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Hosting;

/// <summary>
/// The demo's editor: everything needed to turn the running engine into a
/// manipulable viewport — an <see cref="EditorInputFrame"/> built per frame from
/// the live input manager and the renderer's framebuffer latch, the viewport's
/// pick/gizmo/marquee arbitration, an orbit camera, an undo history, and the
/// keyboard that drives them.
/// </summary>
/// <remarks>
/// <b>This class is the whole of the host seam.</b> It is the only type in the
/// process that names both a keyboard and <c>SpectraEngine.Editing</c>: the
/// editing assembly carries no keyboard vocabulary at all (see
/// <see cref="GizmoShortcuts"/>), so somebody has to resolve a physical key into
/// a <see cref="GizmoCommand"/>, and that somebody is the host. Re-hosting the
/// viewport in the Avalonia shell means writing a sibling of this class — the
/// tools, the history and the arbitration below the seam are untouched.
/// <para>
/// The keys it names are <see cref="InputKey"/>, the engine's own vocabulary,
/// so this host no longer references a windowing backend either: the shell
/// translates its own key events into the same enum and every binding here
/// works unchanged behind it.
/// </para>
/// <para>
/// <b>Two navigation models, one toggle.</b> The default is the editor's own
/// Roblox-Studio-shaped navigation: hold the right mouse button and the cursor
/// locks and hides, the mouse looks around in place, and W/A/S/D/Q/E fly.
/// <see cref="NavigationToggleKey"/> hands the camera back to the engine's
/// original <c>FlyCameraController</c>, which is still there and still works.
/// <see cref="Update"/> returns whether the editor drove the camera this frame,
/// and the engine parks the fly camera exactly on the frames it did. The toggle
/// only chooses the camera — picking, manipulation and box select run in both
/// modes.
/// </para>
/// <para>
/// <b>The movement keys are only fed to the camera while the look button is
/// held</b>, which is both what the requirement asks for ("hold right click to
/// lock mouse and wasd to move") and what resolves the one genuine keyboard
/// conflict in this host: <c>W</c> means "move tool" to the manipulator and
/// "forward" to a camera, and <c>E</c> is claimed by both the rotate tool and
/// the rise/fall pair. Gating movement on the button means the letter row keeps
/// meaning "switch tool" whenever you are not actually flying, so nothing had to
/// be given up. The engine's fly camera reads the keyboard directly and cannot
/// be gated that way, so while <em>it</em> is driving the conflicting letter-row
/// tool bindings stand down as before (the 2/3/4 row keeps working in both).
/// </para>
/// <para>
/// <b>Key names are resolved once, not per frame.</b> The shortcut tables match
/// on key <em>names</em>, which would mean a <c>ToString()</c> allocation per
/// key per frame if it were done in the loop. The bindings are therefore
/// resolved in the constructor into a flat array, and the per-frame path is an
/// array walk over already-decided verbs — so a steady-state frame with no
/// keypress allocates nothing at all.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the scene it edits and the
/// <see cref="DebugDraw"/> it fills. It is constructed on the render thread too
/// (from <c>SceneManager.EditorFactory</c>, inside the scene load).
/// </para>
/// </remarks>
public sealed class SceneEditorHost : ISceneEditor
{
    /// <summary>Toggles between the editor's freelook camera and the engine's fly camera.</summary>
    private const InputKey NavigationToggleKey = InputKey.F7;

    // Ctrl+T, echoing Hammer's Ctrl+T (tie to entity) — the nearest thing in
    // that editor to "change what this brush fundamentally is". A Control chord
    // deliberately, so the bare letter row stays free for tool switching.
    private const InputKey BrushKindToggleKey = InputKey.T;

    // Interned labels for ISceneEditor.NavigationModeName — the stats line that
    // reads it is otherwise allocation-free, so this must never be a formatted
    // enum.
    private const string EditorNavigationLabel = "editor freelook";
    private const string FlyCameraNavigationLabel = "fly camera";

    // Candidate manipulator keys, offered to the editing layer's own default
    // table. Anything it does not recognise is silently dropped, so this list
    // can stay a superset of whatever the defaults happen to bind today.
    private static readonly InputKey[] GizmoKeyCandidates =
    [
        InputKey.W, InputKey.E, InputKey.R,
        InputKey.Number2, InputKey.Number3, InputKey.Number4,
        InputKey.X, InputKey.Y, InputKey.G, InputKey.LeftBracket, InputKey.RightBracket,
    ];

    // Keys a camera owns while it is driving. Only the overlap with
    // GizmoKeyCandidates matters (today: W and E), but listing the whole set
    // keeps the conflict rule honest if either table grows.
    private static readonly InputKey[] CameraKeys =
    [
        InputKey.W, InputKey.A, InputKey.S, InputKey.D, InputKey.Q, InputKey.E,
        InputKey.Space, InputKey.ControlLeft, InputKey.ShiftLeft,
    ];

    private readonly ILogger<SceneEditorHost> _logger;
    private readonly Scene _scene;
    private readonly Renderer _renderer;
    private readonly InputManager _input;

    private readonly UndoStack _undo;
    private readonly GizmoController _gizmos;
    private readonly EditorCameraController _camera;
    private readonly ViewportInteractionController _viewport;
    private readonly EngineEditorInputSource _inputSource;
    private readonly GizmoBinding[] _gizmoBindings;
    private readonly IEditorFrameProbe? _probe;

    // Editor-only, and deliberately not in Core: a shipped game has no reason
    // to outline its own parts, and this assembly is the one that never gets
    // linked into one.
    private readonly PartBrushOverlay _partOutlines = new();

    // Subtractive brushes render nothing at all, so this pass is not an
    // affordance — it is the only way one can be seen.
    private readonly SubtractiveBrushOverlay _negativeOutlines = new();

    /// <summary>The ground grid and the world axes, drawn depth-TESTED.</summary>
    public GroundGrid Grid { get; } = new();

    /// <summary>
    /// When the grid shows. <see cref="Viewport.GridMode.Auto"/> by default:
    /// during move and resize gestures, when the squares on the floor are the
    /// squares the object will land on, and faded out the rest of the time.
    /// </summary>
    public GridMode GridMode { get; set; } = GridMode.Auto;

    // The grid's fade envelope: 0..1, ramped toward where GridMode says it
    // should be and written into Grid.Opacity each frame. A ramp rather than a
    // cut because a full-viewport lattice appearing in one frame is an EVENT
    // in peripheral vision (the same mechanism the shell's motion system is
    // built on), and the thing it announces — "a drag started" — the user
    // already knows.
    private float _gridOpacity;

    // In faster than out: the grid is information at the moment a drag starts
    // and mere afterglow at the moment it ends, so it arrives promptly and
    // takes its leave without flashing.
    private const float GridFadeInSeconds = 0.10f;
    private const float GridFadeOutSeconds = 0.25f;

    /// <summary>The corner axis widget, drawn depth-OFF like the manipulators.</summary>
    public AxisCompass Compass { get; } = new();

    /// <summary>What is selected, and what the cursor is over.</summary>
    public SelectionOutline Selection { get; } = new();

    /// <summary>Every light's icon, and the selected lights' shapes.</summary>
    public LightOverlay Lights { get; } = new();

    /// <summary>The light's own handles: reach and aim.</summary>
    public LightGizmo LightGizmo { get; }

    // Last frame's framebuffer latch, kept so Draw can size the marquee without
    // asking the renderer a second time.
    private Vector2 _viewportSize;

    // The SHAPE seam, beside the lock. Held as the interface rather than the
    // manager for the same reason the camera holds ICursorLock: the editing
    // layer must never see a window, and a test can hand it a fake.
    private readonly ICursorShape _cursorShape;
    private CursorShape _lastCursorShape = CursorShape.Arrow;
    private bool _editorNavigation = true;

    /// <summary>
    /// Builds an editor over a freshly loaded scene.
    /// </summary>
    /// <param name="loggerFactory">Used for this host's log and its tools'.</param>
    /// <param name="scene">The scene to edit; supplies the camera and the selection.</param>
    /// <param name="renderer">Supplies the framebuffer latch the viewport is sized from.</param>
    /// <param name="input">The live input manager the per-frame snapshot is built from.</param>
    /// <param name="probe">
    /// Optional per-frame instrumentation the host hangs off this editor, or
    /// null for none, which is the ordinary case. See
    /// <see cref="IEditorFrameProbe"/>.
    /// </param>
    public SceneEditorHost(
        ILoggerFactory loggerFactory,
        Scene scene,
        Renderer renderer,
        InputManager input,
        IEditorFrameProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(input);

        _logger = loggerFactory.CreateLogger<SceneEditorHost>();
        _scene = scene;
        _renderer = renderer;
        _input = input;

        _undo = new UndoStack(scene);
        _gizmos = new GizmoController(scene, _undo);
        // The resize tool's one diagnostic: a target with no measurable size
        // cannot honour a world-unit increment and says so instead of quietly
        // behaving like a factor drag.
        _gizmos.Scale.Logger = loggerFactory.CreateLogger<ScaleGizmo>();
        // The input manager is the engine's ICursorLock: the camera asks for a
        // locked cursor from here (the render thread) and the main thread
        // applies it during its event pump. Handing over the interface rather
        // than the manager is what keeps the editing layer from ever seeing a
        // window.
        _camera = new EditorCameraController(scene) { CursorLock = input };
        _cursorShape = input;
        // The light tool shares the MOVE tool's snap ladder rather than owning a
        // fourth: a range is a length, so it belongs on the same grid as a
        // position, and a second ladder could drift out of step with the one
        // drawn on the floor.
        LightGizmo = new LightGizmo(scene, _undo)
        {
            Snap = _gizmos.Translate.Snap,
            AngleSnap = _gizmos.Rotate.Snap,
        };

        _viewport = new ViewportInteractionController(scene, _gizmos)
        {
            CameraController = _camera,
            LightTool = LightGizmo,
        };
        _inputSource = new EngineEditorInputSource(input, renderer);
        _gizmoBindings = BuildGizmoBindings();

        // Seed the viewport size before the first frame so a pick that happens
        // on frame one is not measured against a zero-sized viewport.
        renderer.GetFramebufferSize(out int width, out int height);
        _viewportSize = new Vector2(width, height);

        _probe = probe;

        _logger.LogInformation(
            "Editor viewport ready: hold right mouse to lock the cursor and look, W/A/S/D + Q/E " +
            "(or Space/Ctrl) fly while you do, Shift boosts, the wheel trims fly speed while " +
            "looking and zooms otherwise; Alt+drag orbits, middle-drag pans, F frames the " +
            "selection; left-drag picks/moves, marquee on empty space; W/E/R or 2/3/4 pick the " +
            "tool, X flips world/local, Y flips the handle style, G and [ ] drive snap, " +
            "Ctrl+Z / Ctrl+Y walk {Capacity} " +
            "entries of history; Ctrl+D duplicates, Delete removes, Ctrl+G groups and Ctrl+Shift+G " +
            "ungroups the selection; Ctrl+T converts the selected brushes between world geometry and " +
            "parts (parts leave the CSG carve, so they stop merging with what they touch and cost " +
            "no recompile when they move — they are outlined in cyan); F7 toggles between the " +
            "editor camera and the engine fly camera (starting: {Mode}, gizmos: {Gizmos})",
            _undo.Capacity, NavigationModeName, $"{GizmoModeName}/{GizmoStyleName}");
    }

    /// <inheritdoc/>
    public int SelectionCount => _scene.Selection.Count;

    /// <inheritdoc/>
    /// <remarks>
    /// Interned literals, never a formatted enum: see <see cref="ISceneEditor"/>.
    /// <para>
    /// Carries the manipulator STYLE as well, because the two styles disagree
    /// about what a resize holds still and about how many handles there are, and
    /// a smoke run reading "resize" alone cannot tell which one it got. One
    /// interned literal per combination, so the stats line stays allocation-free.
    /// </para>
    /// </remarks>
    public string GizmoModeName => _gizmos.Mode switch
    {
        GizmoMode.Rotate => "rotate",
        GizmoMode.Scale => "resize",
        _ => "move",
    };

    /// <inheritdoc/>
    public string GizmoStyleName =>
        _gizmos.Style.Kind == GizmoStyleKind.Classic ? "Classic" : "Studio";

    /// <inheritdoc/>
    public string GizmoOrientationName =>
        _gizmos.Orientation == GizmoOrientation.Local ? "local" : "world";

    /// <inheritdoc/>
    public bool SnapEnabled => _gizmos.SnapEnabled;

    /// <inheritdoc/>
    /// <remarks>
    /// The viewport's own drag mode covers all three viewport gestures at once
    /// (manipulate, select-and-move, marquee), which is exactly the arbitration
    /// that type exists to own - asking the gizmo controller separately would
    /// be a second answer to one question. The property gesture is the other
    /// half: an inspector scrub moves the object without the viewport knowing.
    /// </remarks>
    public bool IsInteracting =>
        _viewport.DragMode != ViewportDragMode.None || _propertyGestureOpen;

    /// <inheritdoc/>
    public float SnapIncrement => _gizmos.Mode switch
    {
        GizmoMode.Rotate => _gizmos.Rotate.Snap.Increment,
        GizmoMode.Scale => _gizmos.Scale.Snap.Increment,
        _ => _gizmos.Translate.Snap.Increment,
    };

    /// <inheritdoc/>
    public float MoveSnapIncrement => _gizmos.Translate.Snap.Increment;

    /// <inheritdoc/>
    public float RotateSnapIncrement => _gizmos.Rotate.Snap.Increment;

    /// <inheritdoc/>
    public float ResizeSnapIncrement => _gizmos.Scale.Snap.Increment;

    /// <inheritdoc/>
    /// <remarks>Interned literals, never a formatted enum — see <see cref="ISceneEditor"/>.</remarks>
    public string NavigationModeName => _editorNavigation ? EditorNavigationLabel : FlyCameraNavigationLabel;

    /// <inheritdoc/>
    /// <remarks>Interned literals, never a formatted enum — see <see cref="ISceneEditor"/>.</remarks>
    public string GridModeName => GridMode switch
    {
        GridMode.On => "on",
        GridMode.Off => "off",
        _ => "auto",
    };

    /// <inheritdoc/>
    public int UndoDepth => _undo.UndoCount;

    /// <inheritdoc/>
    public int RedoDepth => _undo.RedoCount;

    /// <inheritdoc/>
    public bool Update(double deltaTime)
    {
        _renderer.GetFramebufferSize(out int width, out int height);
        if (width <= 0 || height <= 0)
        {
            // Minimized. There is no viewport to hit-test against, and every
            // ray through a zero-sized one is undefined — so abandon whatever
            // gesture was live rather than resuming it later against a viewport
            // that changed underneath it.
            _viewport.Reset();
            return _editorNavigation;
        }

        _viewportSize = new Vector2(width, height);

        HandleShortcuts();

        EditorInputFrame frame = _inputSource.CaptureFrame((float)deltaTime, CaptureNavigation());

        // BEFORE the viewport, and standing down on the very frame a press could
        // claim the pointer — both halves matter. The self-test deliberately
        // leaves its subject node displaced for the whole compile watch, so a
        // viewport update that ran first would let a gizmo grab CAPTURE that
        // displaced transform as the drag's start; the self-test would then see
        // a non-idle viewport in the same frame, restore the node underneath the
        // live gesture, and the drag would jump a unit and commit — and undo —
        // to a position the user never authored. Asking the frame whether a
        // press is arriving, rather than asking the viewport what it did with
        // one, is what closes that window: the node is back at rest before
        // anything can capture it.
        //
        // The self-test borrows and hands back the camera within its own call,
        // so running it first costs nothing: the camera controller writes the
        // pose afterwards either way.
        bool viewportIdle =
            _viewport.DragMode == ViewportDragMode.None && !frame.WasPressed(_viewport.DragButton);
        _probe?.Update(deltaTime, _viewportSize, viewportIdle);

        // Escape arrives as the cancel flag rather than as a GizmoCommand: it
        // has to reach the marquee as well as the manipulator, and Update is the
        // one call that routes it to whichever owns the pointer.
        _viewport.Update(in frame, _input.WasKeyPressed(InputKey.Escape));
        UpdateCursorShape();
        UpdateGridFade((float)deltaTime);

        return _editorNavigation;
    }

    // Whether a gesture the grid is the ladder for is live: a move or resize
    // drag through the transform gizmo, an object following the cursor after a
    // select-and-move press, or a light's length handle (range and extents
    // snap on the move grid; angles are degrees and get nothing from it). A
    // rotate drag deliberately shows no grid — it would read as a 15-unit
    // lattice beside a tool snapping in degrees.
    private bool MoveGestureLive => _viewport.DragMode switch
    {
        ViewportDragMode.SelectAndMove => true,
        ViewportDragMode.Manipulate => LightGizmo.IsDragging
            ? LightGizmo.IsDraggingLength
            : _gizmos.Mode is GizmoMode.Translate or GizmoMode.Scale,
        _ => false,
    };

    private void UpdateGridFade(float dt)
    {
        float target = GridMode switch
        {
            GridMode.On => 1f,
            GridMode.Off => 0f,
            _ => MoveGestureLive ? 1f : 0f,
        };

        float step = target > _gridOpacity
            ? dt / GridFadeInSeconds
            : dt / GridFadeOutSeconds;

        _gridOpacity = _gridOpacity < target
            ? MathF.Min(_gridOpacity + step, target)
            : MathF.Max(_gridOpacity - step, target);

        Grid.Opacity = _gridOpacity;
    }

    // --- Driving the editor from somewhere other than the keyboard -----------
    //
    // Every verb below was reachable only as a key chord. A shell with a
    // toolbar needs the same ones, and synthesising key presses to reach them
    // would be a second input path free to drift from the real one; these are
    // the SAME calls HandleShortcuts makes.
    //
    // Render thread only, like everything else here. A UI thread arrives
    // through EngineHost.EnqueueCommand.

    /// <summary>
    /// Whether the editor has handed the frame away (play mode). While true,
    /// every verb that would change the scene refuses and says so.
    /// </summary>
    /// <remarks>
    /// <b>The authoritative half of a gate a UI cannot hold.</b> A shell's
    /// play-mode state comes from a published snapshot up to a publish
    /// interval old, so a click in that window enqueues an edit that arrives
    /// here mid-play; a menu already open when play starts can keep sending
    /// them indefinitely. Checking on this side is the only place the answer
    /// is current.
    /// </remarks>
    public bool IsSuspended { get; private set; }

    // One refusal for every mutating verb: suspended (play mode owns the
    // scene) or mid-drag (a gizmo transaction is open, and transactions do not
    // nest). Never silent — a verb that does nothing reads as a broken
    // binding, which is why every caller of this logs through it.
    private bool RefuseEdit(string label, bool allowPropertyGesture = false)
    {
        if (IsSuspended)
        {
            _logger.LogDebug("{Label}: refused, play mode owns the scene", label);
            return true;
        }

        if (_gizmos.Active.State == GizmoInteractionState.Dragging)
        {
            _logger.LogDebug("{Label}: refused, a manipulation is in progress", label);
            return true;
        }

        // A property gesture owns an open transaction for as long as a pointer
        // is held, and a pointer capture does not block the keyboard: Ctrl+1
        // during a drag would insert a block INTO the drag's transaction, and
        // releasing without having moved cancels that transaction and rolls the
        // insert back - the object appears, is selected, and vanishes, with no
        // history entry and no message. Same rule as a gizmo drag, for the same
        // reason.
        if (_propertyGestureOpen && !allowPropertyGesture)
        {
            _logger.LogDebug("{Label}: refused, a property gesture is in progress", label);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Runs one host verb: history, a structural edit, or a mode toggle.
    /// </summary>
    public void Apply(EditorHostCommand command)
    {
        // The view-state verbs are not scene edits and stay live: navigation
        // is the editor's own camera, clearing a selection is how a play
        // session should start, and the grid mode changes what is DRAWN, not
        // what is authored — refusing it mid-drag would gate the one moment a
        // user reaches for it.
        if (command is not (EditorHostCommand.ToggleNavigation or EditorHostCommand.ClearSelection
                or EditorHostCommand.GridAuto or EditorHostCommand.GridOn or EditorHostCommand.GridOff) &&
            RefuseEdit(command.ToString()))
        {
            return;
        }

        switch (command)
        {
            case EditorHostCommand.Undo: Undo(); break;
            case EditorHostCommand.Redo: Redo(); break;
            case EditorHostCommand.Duplicate: RunStructuralEdit("Duplicate", StructuralEditor.TryDuplicate); break;
            case EditorHostCommand.Delete: RunStructuralEdit("Delete", StructuralEditor.TryDelete); break;
            case EditorHostCommand.Group: RunStructuralEdit("Group", (s, u, n) => StructuralEditor.TryGroup(s, u, n)); break;
            case EditorHostCommand.Ungroup: RunStructuralEdit("Ungroup", StructuralEditor.TryUngroup); break;
            case EditorHostCommand.ToggleBrushKind: ToggleSelectionBrushKind(); break;
            case EditorHostCommand.ToggleNavigation: ToggleNavigation(); break;
            case EditorHostCommand.SelectAll: SelectAll(); break;
            case EditorHostCommand.ClearSelection: ClearSelection(); break;
            case EditorHostCommand.GridAuto: GridMode = GridMode.Auto; break;
            case EditorHostCommand.GridOn: GridMode = GridMode.On; break;
            case EditorHostCommand.GridOff: GridMode = GridMode.Off; break;
        }
    }

    /// <summary>
    /// Runs one manipulator verb: pick a tool, flip the frame or the style,
    /// drive snap, cancel a drag.
    /// </summary>
    public bool Apply(GizmoCommand command) => _gizmos.Apply(command);

    /// <summary>
    /// Sets one tool's snap increment — the payload-carrying sibling of the
    /// snap verbs, for a field a user types a number into.
    /// </summary>
    /// <remarks>
    /// Refused before anything is written, the property panel's rule: a
    /// non-positive or non-finite increment would throw from inside
    /// <see cref="SnapSettings.Increment"/> on the render thread, and clamping
    /// instead would set a number nobody asked for and report nothing.
    /// </remarks>
    public void SetSnapIncrement(GizmoMode tool, float increment)
    {
        if (!float.IsFinite(increment) || increment <= 0f)
        {
            _logger.LogInformation(
                "Snap increment {Value} for {Tool} refused: it must be a positive number",
                increment, tool);
            return;
        }

        _gizmos.SetSnapIncrement(tool, increment);
    }

    /// <summary>
    /// Runs one camera verb, such as framing the selection.
    /// </summary>
    public void Apply(EditorCameraCommand command) => _camera.Apply(command);

    /// <summary>
    /// Selects the node with this id, replacing, extending or toggling the
    /// selection. An id the scene does not have clears the selection under
    /// <see cref="SelectionUpdate.Replace"/> and is otherwise ignored.
    /// </summary>
    /// <remarks>
    /// <b>This is how a tree view selects.</b> A shell holds ids and never
    /// nodes, so resolving one is the engine's job, on the thread that owns the
    /// graph. An id that no longer resolves is ordinary rather than
    /// exceptional: a UI's view of the scene is a frame or two behind, so it
    /// can genuinely ask for a node that has just been deleted.
    /// </remarks>
    public void SelectById(Guid nodeId, SelectionUpdate mode = SelectionUpdate.Replace)
    {
        // A selection change under a live gesture would leave the manipulator
        // holding a capture of nodes it is no longer editing.
        _viewport.Reset();

        if (!_scene.TryFindById(nodeId, out SceneNode? node))
        {
            if (mode == SelectionUpdate.Replace)
                _scene.Selection.Clear();
            return;
        }

        switch (mode)
        {
            case SelectionUpdate.Add: _scene.Selection.Add(node); break;
            case SelectionUpdate.Toggle: _scene.Selection.Toggle(node); break;
            default: _scene.Selection.Select(node); break;
        }
    }

    /// <summary>
    /// Selects a whole set of ids in one operation — how a multi-select tree
    /// view reports its selection. Ids the scene no longer has are skipped;
    /// under <see cref="SelectionUpdate.Replace"/> the selection becomes
    /// exactly the resolvable set, empty included.
    /// </summary>
    /// <remarks>
    /// One batch, not N <see cref="SelectById"/> calls: the selection raises
    /// one change event and the property panel unions once, instead of N
    /// times for a Ctrl-click spree reported as a set.
    /// </remarks>
    public void SelectByIds(IReadOnlyList<Guid> nodeIds, SelectionUpdate mode = SelectionUpdate.Replace)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);

        _viewport.Reset();

        var nodes = new List<SceneNode>(nodeIds.Count);
        for (int i = 0; i < nodeIds.Count; i++)
        {
            if (_scene.TryFindById(nodeIds[i], out SceneNode? node))
                nodes.Add(node);
        }

        _scene.Selection.Apply(nodes, mode);
    }

    /// <summary>
    /// Renames one node, addressed by id, as one history entry. Returns false
    /// (writing nothing) for an unknown id, an empty name after trimming, or a
    /// name the node already has.
    /// </summary>
    /// <remarks>
    /// The per-node sibling of the property panel's bulk
    /// <see cref="PropertyId.NodeName"/> edit, for the tree's in-place rename:
    /// the tree names exactly the row being edited, while the panel writes to
    /// whatever is selected when the edit lands.
    /// </remarks>
    public bool RenameById(Guid nodeId, string name)
    {
        // The same refusal its sibling verbs carry. Without it a rename
        // committed by the blur that a gizmo grab itself causes (clicking a
        // handle moves focus off the rename box) opens a transaction inside
        // the drag's own, which throws, is caught by the command drain, and
        // logs at Error while the typed name is dropped.
        if (RefuseEdit("Rename"))
            return false;

        if (!_scene.TryFindById(nodeId, out SceneNode? node))
            return false;

        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || string.Equals(node.Name, trimmed, StringComparison.Ordinal))
            return false;

        _undo.BeginTransaction("Rename");
        _undo.Execute(SetNodeNameCommand.Capture(node, trimmed));
        _undo.CommitTransaction();

        _logger.LogInformation("Rename: '{Id}' is now '{Name}'", nodeId, trimmed);
        return true;
    }

    /// <summary>
    /// Moves nodes, addressed by id, under a new parent at the given index
    /// (<c>-1</c> appends), keeping world transforms, as one history entry.
    /// What a tree drag-and-drop lands on.
    /// </summary>
    public void ReparentByIds(IReadOnlyList<Guid> nodeIds, Guid newParentId, int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);

        if (RefuseEdit("Reparent"))
            return;

        _viewport.Reset();

        if (!_scene.TryFindById(newParentId, out SceneNode? newParent))
        {
            _logger.LogInformation("Reparent: target parent {Id} is not in the scene", newParentId);
            return;
        }

        var nodes = new List<SceneNode>(nodeIds.Count);
        for (int i = 0; i < nodeIds.Count; i++)
        {
            if (_scene.TryFindById(nodeIds[i], out SceneNode? node))
                nodes.Add(node);
        }

        if (!StructuralEditor.TryReparent(_scene, _undo, nodes, newParent, insertIndex))
        {
            // Never a silent no-op: a drop that does nothing reads as a broken
            // gesture, so say why nothing moved.
            _logger.LogInformation(
                "Reparent: nothing to move ({Count} node(s) in, target '{Parent}')",
                nodes.Count, newParent.Name);
            return;
        }

        _logger.LogInformation(
            "Reparent: {Count} node(s) under '{Parent}' at {Index} (undo {UndoDepth})",
            nodes.Count, newParent.Name, insertIndex, _undo.UndoCount);
    }

    /// <summary>
    /// Selects whatever is under the given viewport point, the way a left-click
    /// pick would, unless it is already part of the selection — the
    /// right-click-before-a-context-menu rule every editor shares: clicking a
    /// selected object keeps the set (the menu acts on all of it), clicking an
    /// unselected one retargets to it, and clicking empty space keeps the
    /// selection so the menu's verbs still have their subject.
    /// </summary>
    public void SelectAtPoint(Vector2 viewportPoint)
    {
        if (_viewportSize.X <= 0f || _viewportSize.Y <= 0f)
            return;

        // A right-click that beat the shell's play-mode gate must not reach
        // into a scene somebody is walking around in.
        if (IsSuspended)
            return;

        _viewport.Reset();

        Ray3 ray = _scene.Camera.ScreenPointToRay(viewportPoint, _viewportSize);

        // The PICK's reach, read from the one controller that owns it, never
        // the insert clamp. They are different questions: an insert 300 units
        // away is useless, while an object 300 units away is ordinary in an
        // open world and left-clickable. Reusing the insert's 200 units here
        // made a right-click past it silently keep the previous selection,
        // which is indistinguishable from the empty-space rule and puts the
        // menu's Delete on the wrong object.
        if (!_scene.Raycast(
                in ray, out SceneRaycastHit hit, SceneQueryFilter.EditorPicking, _viewport.PickDistance))
        {
            _logger.LogDebug("Right-click at ({X:0.#}, {Y:0.#}) hit nothing; the selection stands",
                viewportPoint.X, viewportPoint.Y);
            return;
        }

        if (!_scene.Selection.Contains(hit.Node))
            _scene.Selection.Select(hit.Node);
    }

    /// <inheritdoc/>
    public void Suspend()
    {
        // Both halves are needed. Reset abandons the gesture and rolls back
        // whatever it had moved, so no half-drag lands in the history; suspending
        // the camera releases the cursor lock it may be holding, which is what
        // would otherwise fight the character controller's lock request for as
        // long as play mode lasted.
        _viewport.Reset();
        _camera.SuspendNavigation();
        IsSuspended = true;
    }

    /// <inheritdoc/>
    public void Resume() => IsSuspended = false;

    /// <summary>
    /// Applies a property-panel edit to the current selection.
    /// </summary>
    /// <returns>How many nodes actually changed.</returns>
    /// <remarks>
    /// <b>The selection is read HERE, not passed in.</b> A UI's view of the
    /// selection is a frame or two behind, so an edit carrying its own node
    /// list would occasionally write to nodes the user had already deselected.
    /// The panel says which property and which value; which objects that means
    /// is the editor's answer, given at the moment the edit runs.
    /// </remarks>
    public int ApplyProperty(PropertyEdit edit)
    {
        // Same gate as every other edit: a property field committed by the
        // blur that entering play mode causes must not write into a running
        // session, and a bulk edit opens a transaction that cannot nest inside
        // a live drag.
        // The one caller exempt from the gesture gate: these ARE the gesture's
        // own edits.
        if (RefuseEdit("Property edit", allowPropertyGesture: true))
            return 0;

        return PropertyEditor.Apply(_undo, _scene.Selection.Items, edit, _propertyGestureOpen);
    }

    // Whether a continuous property gesture owns the transaction right now.
    private bool _propertyGestureOpen;

    /// <summary>
    /// Opens one history entry to hold a continuous property gesture, such as
    /// a drag across a numeric field.
    /// </summary>
    /// <returns>
    /// True when the gesture was opened. False means the editor refused it, and
    /// the caller must not go on to emit edits or call
    /// <see cref="EndPropertyGesture"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The same shape a gizmo drag already uses</b>, and for the same
    /// reason: one user gesture is one undo entry, whatever the pointer did in
    /// between. Every <see cref="PropertyEdit"/> that arrives while this is
    /// open joins the entry rather than starting its own.
    /// </para>
    /// <para>
    /// <b>Refusal is reported rather than thrown</b>, because the caller is a
    /// pointer handler in a UI: a press that lands in the publish interval
    /// between play mode starting and the panel hearing about it is ordinary,
    /// not exceptional, and it must simply do nothing.
    /// </para>
    /// </remarks>
    public bool BeginPropertyGesture(string name)
    {
        if (_propertyGestureOpen || RefuseEdit("Property gesture"))
            return false;

        _undo.BeginTransaction(string.IsNullOrEmpty(name) ? "Edit" : name);
        _propertyGestureOpen = true;
        return true;
    }

    /// <summary>
    /// Closes a gesture opened by <see cref="BeginPropertyGesture"/>, keeping
    /// what it did or rolling it back.
    /// </summary>
    /// <remarks>
    /// <b>Cancelling has to roll back rather than simply stop recording</b>, or
    /// an abandoned drag would leave the scene holding the last value the
    /// pointer happened to pass over with no history entry to take it back.
    /// </remarks>
    public void EndPropertyGesture(bool commit)
    {
        if (!_propertyGestureOpen)
            return;

        _propertyGestureOpen = false;

        if (commit)
            _undo.CommitTransaction();
        else
            _undo.CancelTransaction();
    }

    /// <summary>
    /// Resets the editor after the scene's graph has been replaced wholesale,
    /// as a map load does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A lifecycle hook, deliberately not an <c>EditorHostCommand</c>.</b>
    /// Every other way a UI drives this host is a verb a key chord also uses,
    /// which is what keeps the two from drifting. This is not one of those: no
    /// user presses "the scene was replaced", and dressing it up as a verb
    /// would put something on the command enum that must never be bound to a
    /// key.
    /// </para>
    /// <para>
    /// <b>All three parts are needed, and each fails silently on its own.</b>
    /// An open gesture is manipulating nodes that are about to leave the graph,
    /// so it is rolled back first. The selection holds live <c>SceneNode</c>
    /// references, and one that outlives its scene keeps a detached subtree
    /// alive and draws a highlight around nothing. And the history addresses
    /// nodes by id in the old graph, where <c>Undo</c> no-ops on a missing
    /// target rather than failing, so the user would press Ctrl+Z and watch
    /// nothing happen.
    /// </para>
    /// </remarks>
    public void OnSceneReplaced()
    {
        // The gesture rollback WITHOUT the suspension latch, and the difference
        // is the whole editor.
        //
        // This used to call Suspend(), which does these two things and then
        // sets IsSuspended - a flag only ExitPlayMode ever clears. So opening a
        // map left the editor permanently refusing every mutating verb: insert,
        // delete, duplicate, group, rename and every property edit answered
        // "refused, play mode owns the scene" at Debug level and did nothing,
        // in an editor nobody was playing. Whether it bit depended on a race
        // between this queued command and the editor factory (an editor that
        // did not exist yet took the null-conditional and survived), which is
        // why the shell worked on some launches and was inert on others - the
        // hardest kind of fault to report, and the reason it stood.
        //
        // Not latching also preserves a suspension that IS real: a scene
        // replaced while play mode owns it stays suspended, because nothing
        // here writes the flag in either direction.
        _viewport.Reset();
        _camera.SuspendNavigation();
        _scene.Selection.Clear();
        _undo.Clear();
    }

    /// <inheritdoc/>
    public void Draw(DebugDraw output)
    {
        // Part outlines first, manipulators over them: a gizmo is what the user
        // is aiming at, an outline is context, and both share the depth-off
        // line pass so neither hides behind the geometry it describes.
        _partOutlines.Draw(output, _scene);
        _negativeOutlines.Draw(output, _scene);
        Lights.Draw(output, _scene, _scene.Camera, _viewportSize);
        LightGizmo.Draw(output, _viewportSize);

        // Between the context overlays and the manipulator, in that order: an
        // outline says what a press would act on, a handle says what a press
        // WILL do, and the handle has to be the one on top.
        Selection.Draw(output, _scene, _scene.Camera, _viewportSize, _viewport.HoveredNode);

        _viewport.Draw(output, _viewportSize);

        // Last, so nothing in the scene overlay can be mistaken for part of it,
        // and because it is the one thing here that is not about the scene at
        // all: it says which way you are facing.
        Compass.Draw(output, _scene.Camera, _viewportSize);
    }

    /// <summary>
    /// Tells the host what the pointer should look like, from what the viewport
    /// is currently doing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from the arbitration rather than set at each gesture's
    /// start.</b> Every gesture would otherwise have to remember to put the
    /// cursor back, and the one that forgets leaves a grab cursor over an empty
    /// viewport with nothing to attribute it to. Reading the live drag mode and
    /// the hover means the shape cannot get out of step with what a press would
    /// actually do, because both come from the same answer.
    /// </para>
    /// <para>
    /// <b>A selectable object gets an ARROW, not a hand.</b> The outline is the
    /// affordance; a hand cursor over 3D geometry reads as a hyperlink, which
    /// is the single most web-flavoured thing a viewport can do.
    /// </para>
    /// </remarks>
    private void UpdateCursorShape()
    {
        // The camera FIRST, because it owns the pointer whenever it is
        // gesturing and the viewport's drag mode is None throughout. A freelook
        // is excluded on purpose: it locks the cursor, so there is no shape to
        // show and asking for one would fight the lock.
        if (_camera.IsNavigating && !_camera.IsFreeLooking)
        {
            Request(CursorShape.SizeAll);
            return;
        }

        CursorShape shape = _viewport.DragMode switch
        {
            ViewportDragMode.Manipulate => CursorShape.Grabbing,
            ViewportDragMode.SelectAndMove => CursorShape.Grabbing,
            ViewportDragMode.BoxSelect => CursorShape.Crosshair,

            // Not dragging. A handle under the cursor is the only thing that
            // says "you can pick this up"; everything else is an arrow.
            _ => _viewport.HoverMode == ViewportDragMode.Manipulate
                ? CursorShape.Grab
                : IsSuspended ? CursorShape.No : CursorShape.Arrow,
        };

        Request(shape);

        // Only on a change: the request takes a lock, and this runs every frame.
        void Request(CursorShape wanted)
        {
            if (wanted == _lastCursorShape)
                return;

            _lastCursorShape = wanted;
            _cursorShape.RequestCursorShape(wanted);
        }
    }

    /// <inheritdoc/>
    public void DrawWorld(DebugDraw output)
    {
        // Nothing while play mode owns the scene. The engine stops calling
        // Update during play but keeps calling this, so the fade envelope is
        // frozen at whatever it held when play began - without the gate a grid
        // that was mid-gesture at F8 stays painted on the floor for the whole
        // session, in front of a person who asked to walk the level.
        if (IsSuspended)
            return;

        // The grid's spacing is the LIVE move increment, not a fixed size, so
        // the squares on the floor are the squares an object will land on. It
        // follows the translate tool specifically rather than the live tool: a
        // rotate snap is in degrees and would silently reinterpret the grid as
        // a 15-unit lattice.
        Grid.Draw(output, _scene.Camera, _gizmos.Translate.Snap.Increment, _viewportSize.Y);
    }

    // --- Navigation keyboard -------------------------------------------------

    /// <summary>
    /// This frame's movement axis, resolved from the host's own keys — the
    /// Roblox-Studio set: W/A/S/D across the ground plane, Q/E down and up, with
    /// Space/Ctrl as the second binding for the same pair, and Shift to boost.
    /// </summary>
    /// <remarks>
    /// <b>Only while the look button is held</b>, so the letter row keeps its
    /// tool meanings the rest of the time (see the type remarks). Returns the
    /// idle axis when the fly camera is driving instead — the engine's
    /// controller reads the same keys itself, and feeding both would double
    /// every step.
    /// </remarks>
    private EditorNavigationInput CaptureNavigation()
    {
        if (!_editorNavigation || !IsLookButtonHeld())
            return default;

        return EditorNavigationInput.FromKeys(
            forward: _input.IsKeyDown(InputKey.W),
            back: _input.IsKeyDown(InputKey.S),
            left: _input.IsKeyDown(InputKey.A),
            right: _input.IsKeyDown(InputKey.D),
            up: _input.IsKeyDown(InputKey.E) || _input.IsKeyDown(InputKey.Space),
            down: _input.IsKeyDown(InputKey.Q) || _input.IsKeyDown(InputKey.ControlLeft),
            boost: _input.IsKeyDown(InputKey.ShiftLeft) || _input.IsKeyDown(InputKey.ShiftRight));
    }

    // The one place this host maps the camera's look button back onto a physical
    // button, so the gate on the movement keys and the gate on the letter-row
    // tool bindings cannot drift apart.
    private bool IsLookButtonHeld() =>
        (_input.PointerButtonsDown & _camera.FreeLookButton) == _camera.FreeLookButton;

    // --- Keyboard ------------------------------------------------------------

    private void HandleShortcuts()
    {
        if (_input.WasKeyPressed(NavigationToggleKey))
            ToggleNavigation();

        KeyModifiers modifiers = _input.Modifiers;

        // Control chords are handled first and never fall through to a bare-key
        // verb: Ctrl+Z must not also be read as "Z", and no bare key the editor
        // binds is meant to fire while Control is held.
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            bool shift = (modifiers & KeyModifiers.Shift) != 0;

            // Ctrl doubles as a camera's DESCEND key, so a chord whose letter
            // is also a movement key would fire off ordinary flying — Ctrl+A
            // is descend-plus-strafe-left, Ctrl+D descend-plus-strafe-right.
            // Exactly those chords stand down while movement keys are feeding
            // a camera (editor freelook only while the look button is held;
            // the fly camera whenever it drives, because it reads the keyboard
            // itself), the same rule the letter-row tool bindings follow.
            // Chords on letters no camera reads stay live throughout.
            bool movementClaimed = !_editorNavigation || IsLookButtonHeld();

            if (_input.WasKeyPressed(InputKey.Z))
            {
                if (shift) Redo();
                else Undo();
            }
            else if (_input.WasKeyPressed(InputKey.Y))
            {
                Redo();
            }
            else if (_input.WasKeyPressed(BrushKindToggleKey))
            {
                ToggleSelectionBrushKind();
            }
            else if (!movementClaimed && _input.WasKeyPressed(InputKey.D))
            {
                RunStructuralEdit("Duplicate", StructuralEditor.TryDuplicate);
            }
            else if (!movementClaimed && _input.WasKeyPressed(InputKey.A))
            {
                SelectAll();
            }
            else if (_input.WasKeyPressed(InputKey.G))
            {
                if (shift) RunStructuralEdit("Ungroup", StructuralEditor.TryUngroup);
                else RunStructuralEdit("Group", (s, u, n) => StructuralEditor.TryGroup(s, u, n));
            }
            return;
        }

        if (_input.WasKeyPressed(InputKey.Delete))
            RunStructuralEdit("Delete", StructuralEditor.TryDelete);

        for (int i = 0; i < _gizmoBindings.Length; i++)
        {
            GizmoBinding binding = _gizmoBindings[i];

            // While the editor camera drives, movement keys are only forwarded
            // to it during a look (see the type remarks), so the letter row is
            // free the rest of the time. The engine's fly camera reads the
            // keyboard itself and cannot be gated, so it takes the whole set.
            if (binding.ConflictsWithCamera && (!_editorNavigation || IsLookButtonHeld()))
                continue;

            if (_input.WasKeyPressed(binding.Key))
                _gizmos.Apply(binding.Command);
        }

        // The camera verbs are a table of one, so asking by the literal name
        // costs nothing and keeps the binding where the editing layer documents
        // it rather than duplicated here.
        if (_input.WasKeyPressed(InputKey.F) &&
            EditorCameraShortcuts.TryResolve("F", modifiers, out EditorCameraCommand cameraCommand))
        {
            _camera.Apply(cameraCommand);
        }
    }

    private void ToggleNavigation()
    {
        _editorNavigation = !_editorNavigation;

        if (_editorNavigation)
        {
            // Adopt whatever pose the fly camera left behind, so the toggle
            // never teleports the view: the editor camera takes the current
            // position and angles and puts its focus a fixed distance ahead.
            _camera.AdoptCamera();
            _viewport.CameraController = _camera;
        }
        else
        {
            // Handing navigation back mid-gesture would leave the editor camera
            // chasing a target the fly camera is simultaneously walking away
            // from — and, worse, holding a cursor lock nothing would ever
            // release. Suspending does both jobs.
            _camera.SuspendNavigation();
            _viewport.CameraController = null;
        }

        _logger.LogInformation("Navigation: {Mode} ({Key} toggles)", NavigationModeName, NavigationToggleKey);
    }

    // Ctrl+T: convert the selected brushes between world geometry and parts —
    // the one edit that changes whether a brush is admitted to the fused static
    // world, and therefore the one a user must perform deliberately rather than
    // discover by dragging something somewhere.
    //
    // A mixed selection NORMALISES rather than flipping each node
    // independently: "toggle" on a set means the whole set ends up the same
    // way, and per-node flipping would leave a selection the user can never get
    // back into one state. If anything in it is still world geometry, the whole
    // selection becomes parts; only an all-part selection converts back.
    private void ToggleSelectionBrushKind()
    {
        // Mid-gesture the manipulator is holding an open transaction, and a
        // conversion inside it would land in the drag's undo entry.
        _viewport.Reset();

        IReadOnlyList<SceneNode> selected = _scene.Selection.Items;
        var commands = new List<IEditorCommand>();
        bool anyWorld = false;
        int skipped = 0;

        for (int i = 0; i < selected.Count; i++)
        {
            if (selected[i].Brush is null)
            {
                skipped++;
                continue;
            }
            if (selected[i].BrushKind == BrushKind.World)
                anyWorld = true;
        }

        BrushKind target = anyWorld ? BrushKind.Part : BrushKind.World;
        for (int i = 0; i < selected.Count; i++)
        {
            SceneNode node = selected[i];
            if (node.Brush is null || node.BrushKind == target)
                continue;
            commands.Add(SetBrushKindCommand.Capture(node, target));
        }

        if (commands.Count == 0)
        {
            // Never a silent no-op: "I pressed the key and nothing happened" is
            // indistinguishable from a broken binding.
            _logger.LogInformation(
                "Convert brush: nothing to convert ({Selected} selected, {Skipped} without a brush)",
                selected.Count, skipped);
            return;
        }

        string name = target == BrushKind.Part ? "Convert to Part" : "Convert to World";
        _undo.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand(name, commands));

        _logger.LogInformation(
            "{Name}: {Converted} brush(es), {Skipped} selected node(s) had no brush. " +
            "Part brushes leave the CSG carve — they no longer merge with the geometry around them, " +
            "and they cost no static-world recompile when they move.",
            name, commands.Count, skipped);
    }

    // Duplicate, delete, group and ungroup all have the same shape: snapshot the
    // selection, refuse mid-gesture, run one verb, say what happened.
    private void RunStructuralEdit(
        string label, Func<Scene, UndoStack, IReadOnlyList<SceneNode>, bool> operation)
    {
        // A structural edit inside a gizmo drag would open a transaction while
        // one is already open, which does not nest and throws. It would also be
        // meaningless: the gesture is still deciding where the thing it is
        // holding ends up. Play mode is refused for its own reason: the scene
        // belongs to whoever is walking around in it.
        if (RefuseEdit(label))
            return;

        // Copied because every one of these verbs rewrites the selection, and
        // SelectionSet.Items is the live list.
        var selection = new List<SceneNode>(_scene.Selection.Items);
        if (!operation(_scene, _undo, selection))
        {
            // Never a silent no-op, for the same reason the brush-kind convert
            // is not: a key that does nothing reads as a broken binding.
            _logger.LogInformation("{Label}: nothing to act on ({Selected} selected)", label, selection.Count);
            return;
        }

        _logger.LogInformation(
            "{Label}: {Selected} node(s) in, {Now} selected now (undo {UndoDepth} / redo {RedoDepth})",
            label, selection.Count, _scene.Selection.Count, _undo.UndoCount, _undo.RedoCount);
    }

    // --- Insert --------------------------------------------------------------

    // A fresh brush is 2x2x2: big enough to grab a Studio handle on, small
    // enough that a doorway does not vanish the moment a hole lands in a wall.
    private const float InsertHalfExtent = 1f;

    // How far ahead an insert lands when the centre ray hits nothing: close
    // enough to be inside the working view, far enough that it is not inside
    // the camera.
    private const float InsertFallbackDistance = 12f;

    // How far the centre ray looks for a surface before giving up.
    private const float InsertRayReach = 200f;

    /// <summary>
    /// Creates one thing where the user is looking, as one history entry, and
    /// selects it.
    /// </summary>
    /// <remarks>
    /// <b>Placement is the centre-of-view ray against the static world</b>,
    /// which is what Studio trained everyone to expect: the new thing rests on
    /// the surface in the middle of the screen, pushed out along the surface
    /// normal so it sits flush rather than buried, and falls back to a fixed
    /// distance ahead when the ray hits nothing. The position snaps to the
    /// move grid when snapping is on, so inserted geometry starts life
    /// aligned instead of needing a corrective nudge.
    /// <para>
    /// Render thread only, like every other verb; a UI arrives through
    /// <c>EngineHost.EnqueueCommand</c>.
    /// </para>
    /// </remarks>
    /// <param name="kind">What to create.</param>
    /// <param name="viewportPoint">
    /// Where to aim, in viewport pixels; null means the centre of the view.
    /// A viewport context menu passes the right-click position, so "insert
    /// here" means where the menu was opened rather than where the camera
    /// happens to point.
    /// </param>
    public void Insert(InsertKind kind, Vector2? viewportPoint = null)
    {
        if (RefuseEdit("Insert"))
            return;

        // Any OTHER live gesture — a marquee mid-sweep — is abandoned before
        // the insert rewrites the selection under it, the same rule
        // SelectById follows.
        _viewport.Reset();

        float clearance = kind switch
        {
            // A light AT a surface lights half of nothing.
            InsertKind.PointLight => 1.5f,

            // A SURFACE light sits on the surface, barely clear of it. It is
            // one-sided and faces away from the wall, so unlike a point light
            // there is no half of its output to lose; the millimetre of
            // clearance only keeps it out of the z-fighting the coincident
            // plane would otherwise cause with the overlay drawn on it.
            InsertKind.SurfaceLight => 0.01f,

            // Centre ON the surface, half-buried: a hole resting flush shares
            // only the boundary plane with the solid, and the carve treats a
            // resting negative as a no-op by design — it would sit on an
            // intact floor cutting nothing, forever.
            InsertKind.SubtractiveBrush => 0f,

            // A group is a point; it marks the spot rather than resting on it.
            InsertKind.Group => 0f,

            _ => InsertHalfExtent,
        };
        Vector3 position = FindInsertPosition(clearance, viewportPoint);

        SceneNode node = BuildInsert(kind);

        // A surface light needs the hit's FACE, not just a point: its normal is
        // the direction the panel faces, and the node that owns the surface is
        // the parent it belongs to. Everything else places from a point alone,
        // which is why this is the one kind that asks a second question.
        SceneNode parent = _scene.Root;

        if (kind == InsertKind.SurfaceLight && TryFindSurface(viewportPoint, out SceneRaycastHit surface))
        {
            parent = surface.Node;

            // RotationForDirection takes the direction the light TRAVELS, and a
            // face normal points OUT of the solid - so travel is +normal.
            // Backwards gives a panel shining into the wall it is mounted on:
            // silent, dark, and precisely what that method's remarks exist to
            // prevent.
            Quaternion facing = Light.RotationForDirection(surface.Normal);

            // Placed in the PARENT's space, because it is about to become the
            // parent's child: a world position assigned to LocalPosition would
            // be offset by wherever the wall happens to be.
            Matrix4x4 toLocal = InverseOf(parent.WorldMatrix);
            Vector3 world = position;

            node.LocalTransform = new Transform
            {
                Position = Vector3.Transform(world, toLocal),
                Rotation = Quaternion.Concatenate(
                    facing, Quaternion.Inverse(RotationOf(parent.WorldMatrix))),
                Scale = Vector3.One,
            };
        }
        else
        {
            node.LocalPosition = position;
        }

        // Appended at the end of its parent, which is where a person expects a
        // new thing to show up in the tree.
        _undo.Execute(new AddNodesCommand(
            [new NodePlacement(node, parent.Id, parent.Children.Count)])
        {
            Name = $"Insert {node.Name}",
        });

        _scene.Selection.Select(node);

        _logger.LogInformation(
            "Insert {Kind}: '{Name}' at ({X:0.##}, {Y:0.##}, {Z:0.##}) (undo {UndoDepth})",
            kind, node.Name, position.X, position.Y, position.Z, _undo.UndoCount);
    }

    private Vector3 FindInsertPosition(float clearance, Vector2? viewportPoint)
    {
        // A degenerate viewport (minimised) has no centre ray worth casting;
        // fall back to straight ahead of the camera.
        if (_viewportSize.X <= 0f || _viewportSize.Y <= 0f)
            return SnapAllAxes(_scene.Camera.Position + _scene.Camera.Forward * InsertFallbackDistance);

        Ray3 ray = _scene.Camera.ScreenPointToRay(viewportPoint ?? _viewportSize * 0.5f, _viewportSize);

        // The same query PICKING uses — part brushes and meshes included —
        // so the insert lands on the surface the user is looking at, not on
        // whatever compiled world geometry happens to be behind it. A ray
        // through the static world alone passes straight through a platform
        // built of parts and buries the new thing underneath it.
        if (!_scene.Raycast(in ray, out SceneRaycastHit hit, SceneQueryFilter.EditorPicking, InsertRayReach))
            return SnapAllAxes(ray.PointAt(InsertFallbackDistance));

        Vector3 point = hit.Point;
        SnapSettings snap = _gizmos.Translate.Snap;
        if (snap.Enabled)
        {
            // Grid-align ALONG the surface only: the snapped point is
            // re-projected back onto the hit plane, so a coarse grid can
            // neither bury the insert in the surface nor float it off —
            // both of which snapping all three axes after the clearance
            // could do, by up to half an increment.
            var snapped = new Vector3(
                snap.SnapScalar(point.X), snap.SnapScalar(point.Y), snap.SnapScalar(point.Z));
            snapped += hit.Normal * Vector3.Dot(point - snapped, hit.Normal);
            point = snapped;
        }

        // Clearance after the snap, for the same reason.
        return point + hit.Normal * clearance;
    }

    // The surface the insert ray hits, for the one kind that needs the face
    // rather than the point. Deliberately a second cast rather than a return
    // value threaded through FindInsertPosition: every other kind wants only a
    // position, and widening that method's contract for one caller would make
    // four call sites carry a value they ignore.
    private bool TryFindSurface(Vector2? viewportPoint, out SceneRaycastHit hit)
    {
        hit = default;

        if (_viewportSize.X <= 0f || _viewportSize.Y <= 0f)
            return false;

        Ray3 ray = _scene.Camera.ScreenPointToRay(viewportPoint ?? _viewportSize * 0.5f, _viewportSize);
        return _scene.Raycast(in ray, out hit, SceneQueryFilter.EditorPicking, InsertRayReach);
    }

    // A world matrix with no scale is invertible by construction, but a scaled
    // parent is legal for a mesh node - so the failure is answered with the
    // identity rather than an exception, which places the light at the parent's
    // origin instead of throwing out of an insert.
    private static Matrix4x4 InverseOf(Matrix4x4 world) =>
        Matrix4x4.Invert(world, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;

    private static Quaternion RotationOf(Matrix4x4 world) =>
        Matrix4x4.Decompose(world, out _, out Quaternion rotation, out _)
            ? rotation
            : Quaternion.Identity;

    private Vector3 SnapAllAxes(Vector3 point)
    {
        SnapSettings snap = _gizmos.Translate.Snap;
        if (!snap.Enabled)
            return point;

        return new Vector3(
            snap.SnapScalar(point.X), snap.SnapScalar(point.Y), snap.SnapScalar(point.Z));
    }

    private static SceneNode BuildInsert(InsertKind kind)
    {
        var half = new Vector3(InsertHalfExtent);
        switch (kind)
        {
            case InsertKind.PartBrush:
            {
                var node = new SceneNode("Part") { Brush = Brush.CreateBox(-half, half) };
                node.BrushKind = BrushKind.Part;
                return node;
            }

            case InsertKind.SubtractiveBrush:
                // World kind, never part: a subtractive part carves nothing
                // and draws nothing — the inert pairing the engine counts
                // rather than throws on.
                return new SceneNode("Hole")
                {
                    Brush = Brush.CreateBox(-half, half).WithOperation(BrushOperation.Subtractive),
                };

            case InsertKind.SurfaceLight:
                // The extents are a FIXED default, deliberately not fitted to
                // the face. A face polygon is derived data that changes on
                // every carve, so auto-fitting would put a light inside the
                // pipeline Scene.StaticWorld is kept pure of - and would
                // silently resize itself the next time somebody cut a doorway
                // through the same wall.
                return new SceneNode("Panel")
                {
                    Light = new Light
                    {
                        Kind = LightKind.Rect,
                        Color = ColorSpace.SrgbToLinear(new Vector3(1f, 0.97f, 0.92f)),
                        Intensity = 25f,
                        Range = 8f,
                        Width = 1f,
                        Height = 1f,
                    },
                };

            case InsertKind.PointLight:
                return new SceneNode("Light")
                {
                    Light = new Light
                    {
                        Kind = LightKind.Point,
                        Color = ColorSpace.SrgbToLinear(new Vector3(1f, 0.95f, 0.85f)),
                        Intensity = 40f,
                        Range = 8f,
                    },
                };

            case InsertKind.Group:
                return new SceneNode("Group");

            default:
                return new SceneNode("Brush") { Brush = Brush.CreateBox(-half, half) };
        }
    }

    // Top-level nodes only — see the verb's own remarks for why the whole
    // graph would be the wrong selection three different ways.
    private void SelectAll()
    {
        // A selection change under a live gesture would leave the manipulator
        // holding a capture of nodes it is no longer editing — the same rule
        // SelectById follows.
        _viewport.Reset();

        _scene.Selection.Clear();
        _scene.Selection.AddRange(_scene.Root.Children);

        _logger.LogInformation("Select all: {Count} top-level node(s)", _scene.Selection.Count);
    }

    private void ClearSelection()
    {
        _viewport.Reset();
        _scene.Selection.Clear();
    }

    private void Undo()
    {
        // A history step in the middle of a gesture would interleave with the
        // edit in progress — the stack refuses it outright while a transaction
        // is open — so abandon the gesture first, which also restores whatever
        // it had moved so far.
        _viewport.Reset();
        if (_undo.Undo())
            _logger.LogInformation("Undo '{Name}' (undo {UndoDepth} / redo {RedoDepth})", _undo.RedoName, _undo.UndoCount, _undo.RedoCount);
    }

    private void Redo()
    {
        _viewport.Reset();
        if (_undo.Redo())
            _logger.LogInformation("Redo '{Name}' (undo {UndoDepth} / redo {RedoDepth})", _undo.UndoName, _undo.UndoCount, _undo.RedoCount);
    }

    // Resolves every candidate key through the editing layer's default table
    // once, at construction. Keys the table does not bind are dropped rather
    // than mapped to a fallback: an unbound key must stay unbound.
    private static GizmoBinding[] BuildGizmoBindings()
    {
        var bindings = new List<GizmoBinding>(GizmoKeyCandidates.Length);
        foreach (InputKey key in GizmoKeyCandidates)
        {
            if (!GizmoShortcuts.TryResolve(key.ToString(), out GizmoCommand command))
                continue;

            bindings.Add(new GizmoBinding(key, command, Array.IndexOf(CameraKeys, key) >= 0));
        }

        return [.. bindings];
    }

    // One pre-resolved keyboard binding. ConflictsWithCamera marks the keys a
    // camera claims while it is driving, which must therefore stop meaning
    // "switch tool" for as long as it does.
    private readonly record struct GizmoBinding(InputKey Key, GizmoCommand Command, bool ConflictsWithCamera);
}
