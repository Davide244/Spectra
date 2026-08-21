using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using SpectraEngine.Editing.Viewport;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Executable.Editing;

/// <summary>
/// The demo's synthetic editing self-test: every few seconds it drives the
/// entire manipulation chain against a known brush node — screen-ray pick, grab
/// the x translate handle (checking it drew something), drag it over several
/// frames to a known world delta, commit, verify the node moved by exactly that
/// much and that the static world recompiled the cells it moved through, then
/// undo, verify the transform is restored bit-for-bit, redo, verify it moved
/// again — and logs one PASS/FAIL line with the measured delta.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> A gizmo is the one part of an engine that normally
/// can only be verified by a human moving a mouse and looking at a screen. Unit
/// tests cover the maths; nothing covers the assembled chain in the live
/// process, where the camera is real, the viewport is the real framebuffer, the
/// scene is the real demo world and the CSG recompile is really asynchronous.
/// This runs that chain headlessly, on a cadence, and says PASS or FAIL in a
/// smoke log.
/// <para>
/// <b>It is opt-in, and off by default.</b> It is gate instrumentation, not
/// demo behaviour: the drag is a real one-world-unit move of a real brush node
/// that stays displaced for the frames the async recompile needs, so with it
/// running the scene pops every few seconds with nobody touching the mouse —
/// indistinguishable, to someone using the editor, from a jitter bug. The host
/// therefore constructs this only when <c>--selftest</c> (or
/// <c>SPECTRA_SELFTEST</c>) asked for it; see <see cref="DemoStartupOptions"/>,
/// and note that <see cref="DemoEditorHost"/> skips the whole test on a null
/// subject node.
/// </para>
/// <para>
/// <b>Determinism, and the three things it borrows.</b> Real mouse input is
/// never consulted: every frame the drag sees is a synthesized
/// <see cref="EditorInputFrame"/> whose cursor is a world point projected
/// through the camera, exactly as the headless gizmo tests do it. To make the
/// projection meaningful the run briefly borrows the scene <em>camera</em>
/// (parked at a fixed offset from the node, so the answer does not depend on
/// where the user has navigated), the <em>selection</em>, and — implicitly — the
/// node's transform. All three are handed back before the frame ends; the camera
/// and selection within the same frame, so nothing is ever drawn from them.
/// </para>
/// <para>
/// <b>It runs on a private rig, not on the user's viewport.</b> The undo stack
/// and manipulator below are this test's own instances of the very same types
/// the viewport runs. Sharing the live ones would mean either polluting the
/// user's history with a phantom "Move" entry every few seconds or clearing a
/// history they were relying on — and it would fight any gesture in progress.
/// The scene, camera and selection are genuinely shared, because those are what
/// the test is about.
/// </para>
/// <para>
/// <b>Why it takes several engine frames.</b> The drag itself is many input
/// frames inside ONE engine frame (that is what proves each drag frame
/// recomputes from the grab rather than accumulating), but the recompile
/// assertion cannot be. The static world compiles in the background, and the
/// scene's dirty-cell diff compares placements between snapshots: a move that
/// was undone before the next snapshot dirties <em>nothing</em>, by design. So
/// the undo may only be issued once the compile carrying the move has actually
/// been launched and landed — which is exactly what
/// <see cref="Stage.WatchCompile"/> waits for. The node therefore sits one unit
/// away for a handful of frames, which is also the only visible sign the test
/// exists.
/// </para>
/// <para>
/// <b>It always leaves the scene where it found it.</b> Every exit path — pass,
/// fail, or a human grabbing the pointer mid-run — restores the node's captured
/// transform (an absolute value, so replaying it is idempotent) and drops the
/// rig's history, so the run can repeat forever without drifting.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like everything it drives.
/// </para>
/// </remarks>
internal sealed class EditingSelfTest
{
    /// <summary>
    /// Seconds between runs, measured from the end of the previous one.
    /// Internal so the startup banner can quote the real cadence rather than a
    /// number that drifts out of date beside it.
    /// </summary>
    internal const double IntervalSeconds = 5.0;

    // The frame duration handed to every synthesized input frame. Nothing in a
    // translate drag integrates time, so the value only has to be positive and
    // constant; 60 Hz keeps it recognisable in a debugger.
    private const float SyntheticFrameSeconds = 1f / 60f;

    // How far, and how the drag gets there. The intermediate steps are what
    // make it a multi-frame drag rather than a teleport: a tool that
    // accumulated per-frame deltas instead of recomputing from the grab would
    // land at 1.85 units, not 1.
    private static readonly Vector3 DragDelta = new(1f, 0f, 0f);
    private static readonly float[] DragSteps = [0.25f, 0.6f, 1f];

    // Where the borrowed camera is parked, relative to the node. Close enough
    // that the whole gesture stays well inside any sane window, angled so all
    // three axes are separated on screen, and inside the demo's deliberately
    // part-free centre region so nothing can occlude the node.
    private static readonly Vector3 CameraOffset = new(2.5f, 1.6f, 3f);

    // Tolerance on the committed world delta, in world units. The only error in
    // the chain is the camera round trip (project the aim point to a pixel,
    // unproject it back to a ray, project that ray onto the constraint line),
    // which is a part in ~10^5 at these distances — so this is loose enough
    // never to flake and tight enough that a genuinely wrong drag cannot hide.
    private const float DeltaTolerance = 1e-3f;

    // Give up waiting for the recompile after this many frames. The demo
    // recompiles continuously (the bobbing pillar), so the launch normally
    // happens on the very next frame; this only has to be short enough to
    // finish inside one interval.
    private const int MaxCompileWatchFrames = 120;

    private readonly ILogger<EditingSelfTest> _logger;
    private readonly Scene _scene;
    private readonly SceneNode _node;

    // The private rig — this test's own instances of the types the viewport
    // runs. See the type remarks for why they are not the live ones.
    private readonly UndoStack _rigUndo;
    private readonly GizmoController _rigGizmos;
    private readonly ViewportInteractionController _rigViewport;

    // Reused across runs so a run allocates nothing for the selection handover.
    private readonly List<SceneNode> _savedSelection = [];

    // A throwaway line buffer the grabbed manipulator is drawn into, purely to
    // count what it emitted. Drawing into the frame's real buffer would put a
    // second gizmo on screen for one frame; drawing into nothing at all would
    // leave the one part of the chain a headless run most needs checked — that
    // the handles you are told you grabbed actually have visible geometry —
    // completely unverified.
    private readonly DebugDraw _drawProbe = new();

    private Stage _stage = Stage.Idle;
    private double _elapsed;
    private double _nextRunTime = IntervalSeconds;

    // Captured by the drag stage and consumed by the later ones.
    private Transform _restLocal;
    private Transform _committedLocal;
    private Vector3 _measuredDelta;
    private ChunkCoord _expectedCell;
    private IReadOnlyList<ChunkCoord> _dirtyBaseline = [];
    private int _compileCountAtLaunch;
    private int _observedDirtyCells;
    private int _compileWatchFrames;
    private int _gizmoVertices;
    private bool _sawDirtySet;

    /// <summary>
    /// Creates the self-test over the scene it verifies and the node it
    /// manipulates.
    /// </summary>
    /// <param name="logger">Where the PASS/FAIL line goes.</param>
    /// <param name="scene">The live scene; its camera and selection are borrowed per run.</param>
    /// <param name="node">
    /// The brush node to drag. Must be a node whose chunk cells nothing else in
    /// the scene dirties, or the recompile assertion proves nothing — see
    /// <c>SceneManager.SelfTestNode</c>.
    /// </param>
    public EditingSelfTest(ILogger<EditingSelfTest> logger, Scene scene, SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(node);

        _logger = logger;
        _scene = scene;
        _node = node;

        _rigUndo = new UndoStack(scene);
        _rigGizmos = new GizmoController(scene, _rigUndo);
        _rigViewport = new ViewportInteractionController(scene, _rigGizmos);

        // Snapping off, permanently and only on this rig: with the grid on, a
        // drag that applied 0.6 units would still round to 1 and the assertion
        // would pass on a broken tool. The rig is private, so this cannot
        // disturb the user's snap setting.
        _rigGizmos.Translate.Snap.Enabled = false;
    }

    /// <summary>
    /// Advances the self-test by one engine frame.
    /// </summary>
    /// <param name="deltaTime">The frame's duration in seconds.</param>
    /// <param name="viewportSize">The viewport the run projects its cursor into.</param>
    /// <param name="viewportIdle">
    /// Whether the live viewport is free of any human gesture <em>and</em> free
    /// of a press that is about to start one. A run never starts while it is
    /// false, and a run in progress abandons itself — a person dragging in the
    /// viewport outranks a self-test every time. The host must evaluate this
    /// and call this method <em>before</em> it runs the viewport for the frame:
    /// a run in the compile watch leaves the node one unit off its rest pose,
    /// and a gizmo grab that captured that pose would commit (and undo to) a
    /// transform the user never authored.
    /// </param>
    public void Update(double deltaTime, Vector2 viewportSize, bool viewportIdle)
    {
        _elapsed += deltaTime;

        if (_stage == Stage.Idle)
        {
            // Not due, mid-gesture, or minimized: all three simply mean "not
            // this frame". Nothing is logged, because none of them is a failure.
            if (_elapsed < _nextRunTime || !viewportIdle ||
                viewportSize.X <= 0f || viewportSize.Y <= 0f)
            {
                return;
            }

            RunDrag(viewportSize);
            return;
        }

        if (!viewportIdle)
        {
            Abandon("a viewport gesture started");
            return;
        }

        switch (_stage)
        {
            case Stage.WatchCompile:
                WatchCompile();
                break;
            case Stage.Undo:
                RunUndo();
                break;
            case Stage.Redo:
                RunRedo();
                break;
            default:
                RunRestore();
                break;
        }
    }

    // --- Stage 1: pick, grab, drag, commit -----------------------------------

    private void RunDrag(Vector2 viewportSize)
    {
        Camera camera = _scene.Camera;

        // Everything the run borrows, captured so it can be handed back exactly.
        Vector3 savedPosition = camera.Position;
        float savedYaw = camera.Yaw;
        float savedPitch = camera.Pitch;
        float savedAspect = camera.AspectRatio;
        SaveSelection();

        _restLocal = _node.LocalTransform;
        _committedLocal = _restLocal;
        _measuredDelta = Vector3.Zero;
        _gizmoVertices = 0;

        Vector3 restWorld = _node.WorldPosition;
        _expectedCell = ChunkCoord.FromPosition(restWorld);

        try
        {
            // A viewpoint of the test's own. The aspect goes with it: the engine
            // writes the camera's aspect from the same framebuffer latch, but a
            // frame after a resize it is still last frame's, and a projected aim
            // point computed against the wrong aspect misses the handle.
            camera.AspectRatio = viewportSize.X / viewportSize.Y;
            camera.Position = restWorld + CameraOffset;
            camera.LookAt(restWorld);
            _scene.Selection.Clear();

            if (!TryDriveChain(viewportSize, restWorld))
                return;
        }
        finally
        {
            // Reset first: on a failure path it is what cancels a half-open
            // transaction (restoring the node) before anything else runs.
            _rigGizmos.Reset();
            RestoreSelection();
            camera.AspectRatio = savedAspect;
            camera.Position = savedPosition;
            camera.Yaw = savedYaw;
            camera.Pitch = savedPitch;
        }

        // The compile that carries this move has not been launched yet — the
        // engine pumps it at the end of this frame. Remember what the dirty set
        // looked like before that, so the watch below can tell the new one apart.
        _dirtyBaseline = _scene.LastCompileDirtyCells;
        _compileWatchFrames = 0;
        _sawDirtySet = false;
        _observedDirtyCells = 0;
        _stage = Stage.WatchCompile;
    }

    // The chain itself. Returns false having already reported the failure.
    private bool TryDriveChain(Vector2 viewportSize, Vector3 restWorld)
    {
        Camera camera = _scene.Camera;

        // (1) Screen-ray pick. Checked against the node's identity rather than
        // "something was hit": a ray that struck the floor in front of the
        // pillar would otherwise look like a successful pick and then fail
        // mysteriously three steps later.
        Vector2 nodePixel = WorldToScreen(restWorld, viewportSize);
        Ray3 pickRay = camera.ScreenPointToRay(nodePixel, viewportSize);
        if (!_scene.Raycast(in pickRay, out SceneRaycastHit hit))
            return Fail("pick", "the screen ray through the node's own pixel hit nothing");
        if (!ReferenceEquals(hit.Node, _node))
            return Fail("pick", $"the screen ray hit '{hit.Node.Name}' instead of the target node");

        // Through the viewport controller, not by calling Select directly: the
        // press/release pair is what a real click is, and it exercises the drag
        // arbitration (object under cursor ⇒ SelectAndMove) on the way.
        _rigViewport.Update(Frame(nodePixel, viewportSize, down: PointerButtons.Left, pressed: PointerButtons.Left));
        _rigViewport.Update(Frame(nodePixel, viewportSize, released: PointerButtons.Left));

        if (_scene.Selection.Count != 1 || !_scene.Selection.Contains(_node))
            return Fail("pick", "the click did not leave the target node as the sole selection");
        if (_rigUndo.UndoCount != 0)
            return Fail("pick", "a click that moved nothing landed an entry in the history");

        // (2) Grab the x translate handle, aiming at the middle of its shaft.
        GizmoTool tool = _rigGizmos.Translate;
        GizmoGeometry geometry = GizmoGeometry.Build(
            camera, restWorld, Quaternion.Identity, viewportSize, tool.HandlePixelSize);
        if (geometry.IsBehindCamera)
            return Fail("grab", "the gizmo pivot projected behind the borrowed camera");

        geometry.TryGetAxisSegment(GizmoHandle.AxisX, out Vector3 axisStart, out Vector3 axisEnd);
        Vector3 grabAim = Vector3.Lerp(axisStart, axisEnd, 0.5f);

        _rigViewport.Update(Frame(
            WorldToScreen(grabAim, viewportSize), viewportSize,
            down: PointerButtons.Left, pressed: PointerButtons.Left));

        if (_rigViewport.DragMode != ViewportDragMode.Manipulate)
            return Fail("grab", $"the press on the x handle arbitrated as {_rigViewport.DragMode}, not Manipulate");
        if (tool.ActiveHandle != GizmoHandle.AxisX)
            return Fail("grab", $"the press grabbed {tool.ActiveHandle}, not the x axis handle");

        // The grabbed manipulator must have something to show for itself. This
        // is the closest a headless run gets to "is the gizmo on screen": the
        // same geometry that was just hit-tested, pushed through the same
        // renderer the frame's depth-off line pass consumes.
        _drawProbe.Clear();
        tool.Draw(_drawProbe);
        _gizmoVertices = _drawProbe.VertexCount;
        if (_gizmoVertices == 0)
            return Fail("grab", "the grabbed manipulator drew no line geometry");

        // (3) The drag, one input frame per waypoint. The aim is absolute — the
        // grab point displaced by a fraction of the target delta — so this is a
        // sequence of cursor POSITIONS, which is what a real mouse produces.
        for (int i = 0; i < DragSteps.Length; i++)
        {
            _rigViewport.Update(Frame(
                WorldToScreen(grabAim + DragDelta * DragSteps[i], viewportSize), viewportSize,
                down: PointerButtons.Left));
        }

        // (4) Release commits: one history entry for the whole gesture.
        _rigViewport.Update(Frame(
            WorldToScreen(grabAim + DragDelta, viewportSize), viewportSize,
            released: PointerButtons.Left));

        _measuredDelta = _node.WorldPosition - restWorld;
        _committedLocal = _node.LocalTransform;

        float error = Vector3.Distance(_measuredDelta, DragDelta);
        if (error > DeltaTolerance)
            return Fail("commit", $"the committed move is off by {error:0.000000} world units");
        if (_rigUndo.UndoCount != 1)
            return Fail("commit", $"the gesture landed {_rigUndo.UndoCount} history entries, not exactly one");
        if (_rigUndo.RedoCount != 0)
            return Fail("commit", "the gesture left redoable entries behind");

        return true;
    }

    // --- Stage 2: the static world must actually have recompiled -------------

    private void WatchCompile()
    {
        // The dirty-cell set is replaced wholesale at every compile launch, so a
        // changed reference IS a launch — and a launch drains every cell pending
        // at that moment, which necessarily includes the ones the drag dirtied.
        if (!_sawDirtySet && !ReferenceEquals(_scene.LastCompileDirtyCells, _dirtyBaseline))
        {
            IReadOnlyList<ChunkCoord> dirty = _scene.LastCompileDirtyCells;
            _sawDirtySet = true;
            _observedDirtyCells = dirty.Count;
            _compileCountAtLaunch = _scene.StaticWorldCompileCount;

            if (!Contains(dirty, _expectedCell))
            {
                Fail("recompile",
                    $"the compile launched after the drag dirtied {dirty.Count} cell(s), " +
                    $"none of them ({_expectedCell.X}, {_expectedCell.Y}, {_expectedCell.Z})");
                return;
            }
        }

        // Only one compile is ever in flight, so the first landing after that
        // launch IS that launch's compile.
        if (_sawDirtySet && _scene.StaticWorldCompileCount > _compileCountAtLaunch)
        {
            _stage = Stage.Undo;
            return;
        }

        if (++_compileWatchFrames > MaxCompileWatchFrames)
        {
            Fail("recompile",
                _sawDirtySet
                    ? $"the compile covering ({_expectedCell.X}, {_expectedCell.Y}, {_expectedCell.Z}) " +
                      $"never landed within {MaxCompileWatchFrames} frames"
                    : $"no static-world compile was launched within {MaxCompileWatchFrames} frames of the drag");
        }
    }

    // --- Stages 3-5: undo, redo, and back to rest ----------------------------

    private void RunUndo()
    {
        if (!_rigUndo.Undo())
        {
            Fail("undo", "the history refused to undo the committed drag");
            return;
        }

        // Exact equality, not a tolerance: the command replays the absolute
        // transform it captured before the grab, so anything but bit-for-bit
        // means it stored a delta or re-derived the value.
        if (!SameTransform(_node.LocalTransform, _restLocal))
        {
            Fail("undo", "undo did not restore the node's transform exactly");
            return;
        }

        _stage = Stage.Redo;
    }

    private void RunRedo()
    {
        if (!_rigUndo.Redo())
        {
            Fail("redo", "the history refused to redo the undone drag");
            return;
        }

        if (!SameTransform(_node.LocalTransform, _committedLocal))
        {
            Fail("redo", "redo did not re-apply the committed transform exactly");
            return;
        }

        _stage = Stage.Restore;
    }

    private void RunRestore()
    {
        if (!_rigUndo.Undo() || !SameTransform(_node.LocalTransform, _restLocal))
        {
            Fail("restore", "the final undo did not put the node back at rest");
            return;
        }

        // Drop the rig's history so the next run starts from a known empty one
        // — that is what makes the UndoCount assertions above meaningful every
        // time rather than only the first.
        _rigUndo.Clear();
        _stage = Stage.Idle;
        _nextRunTime = _elapsed + IntervalSeconds;

        // ASCII only, and the chunk coordinate spelled out component-wise: this
        // line is read out of a redirected console in a smoke gate, where a
        // code page can mangle a dash and ChunkCoord's record ToString would
        // spill its computed bounds across half the line.
        _logger.LogInformation(
            "Editing self-test: PASS - screen-ray picked '{Node}', grabbed its x handle ({GizmoVertices} " +
            "gizmo line vertices), dragged over {Steps} frames to ({Dx:0.0000}, {Dy:0.0000}, {Dz:0.0000}) " +
            "against an expected ({Ex:0.0000}, {Ey:0.0000}, {Ez:0.0000}), error {Error:0.000000} units; " +
            "the commit recompiled {Dirty} dirty cell(s) including ({CellX}, {CellY}, {CellZ}) after " +
            "{Frames} frame(s); undo restored and redo re-applied the transform exactly; node left at rest",
            _node.Name, _gizmoVertices, DragSteps.Length,
            _measuredDelta.X, _measuredDelta.Y, _measuredDelta.Z,
            DragDelta.X, DragDelta.Y, DragDelta.Z,
            Vector3.Distance(_measuredDelta, DragDelta),
            _observedDirtyCells, _expectedCell.X, _expectedCell.Y, _expectedCell.Z,
            _compileWatchFrames);
    }

    // --- Exits ---------------------------------------------------------------

    // Reports a failure at Error, puts the node back where the run found it, and
    // arms the next run. Returns false so the drag stage can `return Fail(...)`.
    private bool Fail(string stage, string reason)
    {
        RestoreNodeAndRig();

        _logger.LogError(
            "Editing self-test: FAIL at {Stage} - {Reason}. Node '{Node}', measured delta " +
            "({Dx:0.0000}, {Dy:0.0000}, {Dz:0.0000}) against expected ({Ex:0.0000}, {Ey:0.0000}, {Ez:0.0000})",
            stage, reason, _node.Name,
            _measuredDelta.X, _measuredDelta.Y, _measuredDelta.Z,
            DragDelta.X, DragDelta.Y, DragDelta.Z);

        return false;
    }

    // A human took the pointer. Not a failure and not logged at Error — just
    // rolled back and rescheduled.
    private void Abandon(string reason)
    {
        // Read before the rollback: RestoreNodeAndRig puts the stage back to
        // Idle, so logging it afterwards would say "Idle" every time.
        Stage stage = _stage;
        RestoreNodeAndRig();
        _logger.LogDebug("Editing self-test: run abandoned at {Stage} because {Reason}", stage, reason);
    }

    private void RestoreNodeAndRig()
    {
        // Cancels any open transaction first — Clear refuses to run while one is
        // open, and the cancel is also what rolls a half-finished drag back.
        _rigGizmos.Reset();
        _rigViewport.Reset();

        // An absolute transform, so replaying it is idempotent however far
        // through the run we got; the setter early-outs when it already matches,
        // so a clean exit dirties nothing.
        _node.LocalTransform = _restLocal;
        _rigUndo.Clear();

        _stage = Stage.Idle;
        _nextRunTime = _elapsed + IntervalSeconds;
    }

    // --- Helpers -------------------------------------------------------------

    // Projects a world point to viewport pixels: the exact inverse of the
    // pixel-to-NDC mapping Camera.ScreenPointToRay applies, so aiming at a world
    // point produces a ray that passes back through it.
    private Vector2 WorldToScreen(Vector3 world, Vector2 viewportSize)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), _scene.Camera.GetViewProjection());
        return new Vector2(
            (clip.X / clip.W + 1f) * 0.5f * viewportSize.X,
            (1f - clip.Y / clip.W) * 0.5f * viewportSize.Y);
    }

    private static EditorInputFrame Frame(
        Vector2 cursor,
        Vector2 viewportSize,
        PointerButtons down = PointerButtons.None,
        PointerButtons pressed = PointerButtons.None,
        PointerButtons released = PointerButtons.None) =>
        new(cursor, viewportSize, down, pressed, released,
            KeyModifiers.None, Vector2.Zero, SyntheticFrameSeconds);

    // Transform is a plain mutable struct with no equality operators, so it is
    // compared field-wise — component-exact, exactly like SceneNode's own
    // transform setters do it.
    private static bool SameTransform(in Transform a, in Transform b) =>
        a.Position == b.Position && a.Rotation == b.Rotation && a.Scale == b.Scale;

    private static bool Contains(IReadOnlyList<ChunkCoord> cells, ChunkCoord cell)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i] == cell)
                return true;
        }
        return false;
    }

    private void SaveSelection()
    {
        _savedSelection.Clear();
        IReadOnlyList<SceneNode> items = _scene.Selection.Items;
        for (int i = 0; i < items.Count; i++)
            _savedSelection.Add(items[i]);
    }

    private void RestoreSelection()
    {
        _scene.Selection.SetRange(_savedSelection);
        _savedSelection.Clear();
    }

    // Where a run is between engine frames. See the type remarks for why the
    // recompile watch has to sit between the commit and the undo.
    private enum Stage
    {
        Idle,
        WatchCompile,
        Undo,
        Redo,
        Restore,
    }
}
