using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Cameras;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Selection;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Viewport;

/// <summary>
/// The viewport's traffic control: one press, three possible meanings, and
/// exactly one of them gets the gesture. It runs the gizmo, the marquee, the
/// click-select and (when nothing has claimed the pointer) the camera, in that
/// order of precedence.
/// </summary>
/// <remarks>
/// <b>The arbitration, and why this order.</b> On the frame the drag button
/// goes down, <see cref="ClassifyPress"/> answers with a
/// <see cref="ViewportDragMode"/>:
/// <list type="number">
///   <item><description>
///     <b>A gizmo handle under the cursor ⇒ <see cref="ViewportDragMode.Manipulate"/>.</b>
///     Handles are drawn on top of the thing they manipulate and are picked
///     with a pixel tolerance, so they must also be <em>tested</em> first —
///     otherwise the object underneath would swallow every grab and the gizmo
///     would be unusable at exactly the moment it matters.
///   </description></item>
///   <item><description>
///     <b>An object under the cursor ⇒ <see cref="ViewportDragMode.SelectAndMove"/>.</b>
///     The selection changes on the press (so the user sees what they grabbed)
///     and the gesture is handed straight to the move tool's free-move handle.
///   </description></item>
///   <item><description>
///     <b>Nothing under the cursor ⇒ <see cref="ViewportDragMode.BoxSelect"/>.</b>
///   </description></item>
/// </list>
/// <see cref="ClassifyPress"/> is a pure function of the frame and the current
/// scene — it mutates nothing — so the rule can be tested directly rather than
/// inferred from what the viewport did afterwards.
/// <para>
/// <b>Three rules sit above that list, and they are what keep the camera and the
/// tools out of each other's way now that right-drag is a freelook rather than
/// an orbit:</b>
/// <list type="number">
///   <item><description>
///     <b>A gesture that owns the pointer keeps it — and the rule runs both
///     ways.</b> While <see cref="DragMode"/> is anything but
///     <see cref="ViewportDragMode.None"/> the camera does not run at all, so a
///     gizmo drag can never be stolen mid-manipulation — not by a right press,
///     not by anything. Symmetrically, while
///     <see cref="EditorCameraController.OwnsPointer"/> says a navigation
///     gesture is still being held, the <em>camera</em> owns the pointer and
///     this class starts nothing — so a stray left click during a middle-drag
///     pan or a right-drag freelook cannot select an object, open an undo
///     transaction and kill the navigation gesture underneath it.
///   </description></item>
///   <item><description>
///     <b>A press the camera claims is never re-interpreted.</b>
///     <see cref="EditorCameraController.ClaimsPress"/> names the button and
///     modifier combinations that belong to navigation; on such a frame this
///     class starts no gesture, <see cref="ClassifyPress"/> answers
///     <see cref="ViewportDragMode.None"/>, and the manipulator is run with its
///     grab disabled so a handle under the cursor cannot swallow the press
///     either. That is what makes right-drag reach the camera <em>wherever the
///     cursor happens to be</em>, and it is also what stops Alt+left-drag —
///     the orbit modifier — from being read as a box select. <b>Claiming is a
///     press-edge test and therefore only ever describes <em>this</em> frame</b>,
///     which is exactly why rule 1 has to carry the frames after it.
///   </description></item>
///   <item><description>
///     <b>A locked cursor makes every absolute-position path inert.</b> During a
///     freelook the OS reports no meaningful cursor position, so picking,
///     hit-testing and the marquee all test
///     <see cref="EditorInputFrame.IsPointerUsable"/> rather than merely
///     "inside the viewport". Nothing can begin a pointer gesture while the
///     pointer does not exist.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Clicking an already-selected node does not collapse a multi-selection on
/// the press.</b> If it did, grabbing one of five selected crates to drag all
/// five would drop four of them at the instant of the grab. The collapse is
/// deferred to the release and applied only if the gesture turned out to be a
/// click — which is what every editor does, and what makes dragging a group by
/// one of its members work.
/// </para>
/// <para>
/// <b>Camera navigation only runs when nothing has claimed the pointer.</b>
/// Right-drag looks around, but right-click during a manipulation cancels it;
/// the two cannot both be live, and the mode is what separates them. Every frame
/// the camera is withheld it is told so
/// (<see cref="EditorCameraController.SuspendNavigation"/>), because a
/// controller that measures its drag against the last cursor it saw would
/// otherwise deliver the entire withheld gesture as one snap the moment it
/// runs again.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like everything it drives. Steady state
/// allocates nothing.
/// </para>
/// </remarks>
public sealed class ViewportInteractionController
{
    private SceneNode? _deferredSelect;

    /// <summary>
    /// Creates a viewport over a scene and its manipulator. The marquee is
    /// created here; a camera controller is optional and can be attached
    /// through <see cref="CameraController"/>.
    /// </summary>
    public ViewportInteractionController(Scene scene, GizmoController gizmos)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(gizmos);
        if (!ReferenceEquals(gizmos.Scene, scene))
        {
            throw new ArgumentException(
                $"The manipulator edits scene '{gizmos.Scene.Name}', not '{scene.Name}'.", nameof(gizmos));
        }

        Scene = scene;
        Gizmos = gizmos;
        BoxSelect = new BoxSelectController(scene);
    }

    /// <summary>The scene this viewport looks at.</summary>
    public Scene Scene { get; }

    /// <summary>The manipulator.</summary>
    public GizmoController Gizmos { get; }

    /// <summary>The marquee.</summary>
    public BoxSelectController BoxSelect { get; }

    /// <summary>
    /// The camera driver, run on frames where nothing has claimed the pointer.
    /// Null leaves navigation entirely to the host.
    /// </summary>
    public EditorCameraController? CameraController { get; set; }

    /// <summary>The button that selects, box-selects and grabs handles.</summary>
    public PointerButtons DragButton { get; set; } = PointerButtons.Left;

    /// <summary>
    /// Whether a press on an object drags it (through the live tool's
    /// <see cref="GizmoTool.FreeMoveHandle"/>) or merely selects it. Off gives
    /// the stricter "you may only move things by their handles" workflow some
    /// level editors prefer.
    /// </summary>
    public bool ObjectDragEnabled { get; set; } = true;

    /// <summary>
    /// How far a picking ray may travel before an object stops counting as
    /// clickable. Infinite by default — an open world has no natural limit.
    /// </summary>
    public float PickDistance { get; set; } = float.PositiveInfinity;

    /// <summary>What currently owns the pointer.</summary>
    public ViewportDragMode DragMode { get; private set; }

    /// <summary>
    /// The node the last press landed on, or null when it landed on a handle or
    /// on empty space. Cleared when the gesture ends.
    /// </summary>
    public SceneNode? PressedNode { get; private set; }

    /// <summary>Raised when <see cref="DragMode"/> changes, carrying the new mode.</summary>
    public event Action<ViewportDragMode>? DragModeChanged;

    /// <summary>
    /// Decides what a press at this frame's cursor would mean, without changing
    /// anything. Returns <see cref="ViewportDragMode.None"/> for a cursor
    /// outside the viewport, which belongs to whatever panel it is over.
    /// </summary>
    public ViewportDragMode ClassifyPress(in EditorInputFrame frame)
    {
        // Rule 3: no usable pointer — outside the viewport, or locked for a
        // freelook — means the press belongs to nobody here.
        if (!frame.IsPointerUsable)
            return ViewportDragMode.None;

        // Rules 1 and 2: navigation asked for this press, or is already in the
        // middle of a gesture the press would otherwise cut short.
        if (CameraOwnsPointer(in frame))
            return ViewportDragMode.None;

        if (Gizmos.Active.PickAt(in frame).IsHit)
            return ViewportDragMode.Manipulate;

        return TryPickNode(in frame, out _)
            ? ViewportDragMode.SelectAndMove
            : ViewportDragMode.BoxSelect;
    }

    /// <summary>
    /// Advances the viewport by one frame: routes the pointer to whatever owns
    /// it, arbitrates a fresh press, and runs the camera when nothing does.
    /// </summary>
    /// <param name="frame">This frame's input snapshot.</param>
    /// <param name="cancelRequested">
    /// True on the frame the user asked to abort — Escape, or a viewport that
    /// lost focus. Cancels whichever gesture is live.
    /// </param>
    /// <returns>The mode that owns the pointer after this frame.</returns>
    public ViewportDragMode Update(in EditorInputFrame frame, bool cancelRequested = false)
    {
        if (DragMode == ViewportDragMode.BoxSelect)
        {
            UpdateBoxSelect(in frame, cancelRequested);
            return WithheldFromCamera();
        }

        // Rules 1 and 2, evaluated once for the whole frame: a press that
        // belongs to navigation — because the combination is the camera's, or
        // because the camera is already mid-gesture — is invisible to every tool
        // below, starting with the manipulator's grab.
        bool cameraOwnsPointer = CameraOwnsPointer(in frame);

        // Everything else routes through the manipulator, which owns the
        // handle hover even on frames where no gesture is in progress.
        GizmoUpdateResult gizmoResult = Gizmos.Update(in frame, cancelRequested, !cameraOwnsPointer);

        if (DragMode != ViewportDragMode.None)
        {
            FinishGizmoGesture(gizmoResult, cancelRequested);
            return WithheldFromCamera();
        }

        if (gizmoResult == GizmoUpdateResult.DragBegan)
        {
            // The cursor was over a handle and the manipulator took the press
            // itself — nothing to arbitrate.
            SetMode(ViewportDragMode.Manipulate);
            return WithheldFromCamera();
        }

        if (frame.WasPressed(DragButton) && frame.IsPointerUsable && !cameraOwnsPointer)
        {
            BeginGesture(in frame);
            return WithheldFromCamera();
        }

        // Nothing owns the pointer: the camera may have it.
        CameraController?.Update(in frame);
        return DragMode;
    }

    /// <summary>
    /// Draws whatever the viewport contributes this frame: the manipulator and,
    /// while one is being dragged, the marquee.
    /// </summary>
    public void Draw(DebugDraw output, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(output);
        Gizmos.Draw(output);
        BoxSelect.Draw(output, viewportSize);
    }

    /// <summary>
    /// Abandons whatever gesture is live and returns the viewport to
    /// <see cref="ViewportDragMode.None"/>. For a host that lost focus or is
    /// tearing the viewport down.
    /// </summary>
    public void Reset()
    {
        BoxSelect.Cancel();
        Gizmos.Reset();
        _deferredSelect = null;
        PressedNode = null;
        // Same reason as the withheld frames below: a host that resets while
        // the orbit button is down (a minimized window, an undo mid-gesture)
        // must not have the travel since the last camera frame land as one
        // orbit step when navigation resumes.
        CameraController?.SuspendNavigation();
        SetMode(ViewportDragMode.None);
    }

    // The pointer belonged to a gesture this frame, so the camera did not run.
    // Telling it so is not optional: EditorCameraController measures its drag
    // against the cursor position it last SAW, and it only sees the frames it is
    // given — so a whole gizmo drag's worth of withheld travel would otherwise
    // be applied as a single orbit or pan step on the first idle frame
    // afterwards. Returns the mode so the callers stay one-liners.
    private ViewportDragMode WithheldFromCamera()
    {
        CameraController?.SuspendNavigation();
        return DragMode;
    }

    // Rules 1 and 2 together: does navigation have this frame's pointer?
    //
    // Asking ClaimsPress alone is not enough, and the gap is a definition rather
    // than a race: claiming is a pure function of THIS frame's press edges, so a
    // camera gesture that began on an earlier frame is invisible to it. A left
    // click landing while the middle button is already panning — or while the
    // right button is already looking, before the cursor-lock latch has landed —
    // would answer "not claimed", start a gizmo drag, open an undo transaction,
    // and take the pointer away from a gesture that was already using it, with
    // SuspendNavigation quietly killing the pan or the look mid-stroke.
    // EditorCameraController.OwnsPointer answers both halves.
    //
    // Null-safe so a host running without a camera controller behaves exactly as
    // it did before navigation existed.
    private bool CameraOwnsPointer(in EditorInputFrame frame) =>
        CameraController is { } camera && camera.OwnsPointer(in frame);

    // --- Gesture start -------------------------------------------------------

    private void BeginGesture(in EditorInputFrame frame)
    {
        if (TryPickNode(in frame, out SceneNode? node))
        {
            BeginObjectGesture(in frame, node!);
            return;
        }

        BoxSelect.DragButton = DragButton;
        BoxSelect.Begin(frame.CursorPosition);
        PressedNode = null;
        SetMode(ViewportDragMode.BoxSelect);
    }

    private void BeginObjectGesture(in EditorInputFrame frame, SceneNode node)
    {
        PressedNode = node;
        SelectionUpdate update = SelectionModifiers.Resolve(frame.Modifiers);

        if (update == SelectionUpdate.Replace && Scene.Selection.Contains(node))
        {
            // Defer the collapse to the release (see the type remarks): the user
            // may be about to drag the whole selection by this member.
            _deferredSelect = node;
        }
        else
        {
            _deferredSelect = null;
            ApplySingle(node, update);
        }

        GizmoTool tool = Gizmos.Active;
        if (ObjectDragEnabled && tool.TryBeginDrag(in frame, tool.FreeMoveHandle))
        {
            SetMode(ViewportDragMode.SelectAndMove);
            return;
        }

        // No free-move handle (the rotate and resize tools have none) or the
        // tool refused the constraint: the press stays a plain click-select, and
        // a deferred collapse has to happen right now because no release will
        // come back through the gizmo to trigger it.
        ResolveDeferredSelect();
        PressedNode = null;
        SetMode(ViewportDragMode.None);
    }

    // A single-node selection change, expressed through the batched API so that
    // click-select and box select cannot drift apart on what a modifier means.
    private void ApplySingle(SceneNode node, SelectionUpdate update)
    {
        switch (update)
        {
            case SelectionUpdate.Add:
                Scene.Selection.Add(node);
                break;
            case SelectionUpdate.Toggle:
                Scene.Selection.Toggle(node);
                break;
            default:
                Scene.Selection.Select(node);
                break;
        }
    }

    // --- Gesture end ---------------------------------------------------------

    private void UpdateBoxSelect(in EditorInputFrame frame, bool cancelRequested)
    {
        // A marquee tracks the cursor by its absolute position, so a cursor that
        // became locked underneath it has nothing left to track: abandon the
        // rectangle rather than committing whatever frozen corner it last saw.
        // Unreachable while the arbitration holds — a marquee owns the pointer,
        // so the camera never sees the press that would lock it — which is
        // exactly why it is worth stating here rather than assuming.
        BoxSelectResult result = BoxSelect.Update(in frame, cancelRequested || frame.IsCursorLocked);
        if (result == BoxSelectResult.Dragging)
            return;

        SetMode(ViewportDragMode.None);
    }

    private void FinishGizmoGesture(GizmoUpdateResult result, bool cancelRequested)
    {
        if (result is GizmoUpdateResult.DragBegan or GizmoUpdateResult.DragUpdated)
            return;

        // DragCommitted means the object actually moved, so the press was a
        // drag and the deferred collapse must NOT happen; DragCancelled on a
        // SelectAndMove gesture is the "it never moved" case — a click. An
        // abort is neither: Escape means "forget this gesture happened", so it
        // leaves the selection exactly as the press found it.
        if (result == GizmoUpdateResult.DragCancelled && !cancelRequested)
            ResolveDeferredSelect();

        _deferredSelect = null;
        PressedNode = null;
        SetMode(ViewportDragMode.None);
    }

    private void ResolveDeferredSelect()
    {
        // The node may have been deleted between the press and the release, and
        // SelectionSet rejects a node that no longer belongs to the scene.
        // Asking the id index is the public way to check.
        if (_deferredSelect is { } node && Scene.TryFindById(node.Id, out SceneNode? live) && ReferenceEquals(live, node))
            Scene.Selection.Select(node);

        _deferredSelect = null;
    }

    // --- Helpers -------------------------------------------------------------

    private bool TryPickNode(in EditorInputFrame frame, out SceneNode? node)
    {
        node = null;
        if (frame.ViewportSize.X <= 0f || frame.ViewportSize.Y <= 0f)
            return false;

        Ray3 ray = Scene.Camera.ScreenPointToRay(frame.CursorPosition, frame.ViewportSize);
        if (!Scene.Raycast(in ray, out SceneRaycastHit hit, PickDistance))
            return false;

        node = hit.Node;
        return true;
    }

    private void SetMode(ViewportDragMode mode)
    {
        if (DragMode == mode)
            return;

        DragMode = mode;
        DragModeChanged?.Invoke(mode);
    }
}
