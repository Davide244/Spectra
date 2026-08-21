using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Everything the move, rotate and resize tools do identically: place the gizmo
/// at the selection's pivot in the chosen frame, size it to a constant number of
/// pixels, hit-test it, run the grab → drag → commit/cancel state machine, open
/// and close the undo transaction, capture the selection's starting transforms,
/// and highlight the handle under the cursor.
/// </summary>
/// <remarks>
/// <b>The three gizmos differ in four hooks and nothing else</b>: what shape they
/// pick against (<see cref="HitTest"/>), how a grab sets up its constraint
/// (<see cref="TryPrepareDrag"/>), what one frame of the drag writes
/// (<see cref="ApplyDrag"/>), and what they draw
/// (<see cref="DrawHandles"/>). Everything above lives here exactly once — the
/// renderer's six-copy-pasted-pipelines mistake is not one to repeat one floor
/// up.
/// <para>
/// <b>The gesture is the same shape for all three.</b> The grab captures the
/// pivot, the constraint, and every selected root's starting transform; each
/// later frame recomputes the whole answer from that capture (never from the
/// previous frame — see <see cref="GizmoDragTarget"/>); the release commits one
/// history entry, and Escape or right-click rolls the transaction back so the
/// selection is restored exactly.
/// </para>
/// <para>
/// <b>Undo:</b> one transaction per gesture, whatever it lasted. A gesture that
/// changed nothing (a click that turned out not to be a drag) cancels rather
/// than committing, so it never litters the history with no-op entries.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only — it mutates the scene, reads the
/// camera, and fills the frame's <see cref="DebugDraw"/>. Hovering allocates
/// nothing; what a drag allocates is documented per tool.
/// </para>
/// </remarks>
public abstract class GizmoTool
{
    // Retained across gestures so a drag of N nodes allocates only on the first
    // gesture that wide.
    private readonly List<GizmoDragTarget> _targets = [];

    private GizmoGeometry _geometry;
    private bool _hasGeometry;

    private GizmoInteractionState _state = GizmoInteractionState.Idle;
    private GizmoHandle _hovered = GizmoHandle.None;
    private GizmoHandle _active = GizmoHandle.None;

    private Vector3 _grabPivot;
    private Vector3 _livePivot;
    private Quaternion _grabFrame = Quaternion.Identity;

    /// <summary>
    /// Creates a tool over a scene and the history its edits land in.
    /// </summary>
    /// <param name="scene">The scene whose selection this gizmo manipulates.</param>
    /// <param name="undo">
    /// The history to open a transaction in per drag. Must be the history for
    /// <paramref name="scene"/>.
    /// </param>
    /// <param name="transactionName">The initial <see cref="TransactionName"/>.</param>
    protected GizmoTool(Scene scene, UndoStack undo, string transactionName)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(transactionName);
        if (!ReferenceEquals(undo.Scene, scene))
        {
            throw new ArgumentException(
                $"The undo history edits scene '{undo.Scene.Name}', not '{scene.Name}'.", nameof(undo));
        }

        Scene = scene;
        Undo = undo;
        TransactionName = transactionName;
    }

    /// <summary>The scene this gizmo manipulates.</summary>
    public Scene Scene { get; }

    /// <summary>The history each drag lands in.</summary>
    public UndoStack Undo { get; }

    /// <summary>Which manipulator this is.</summary>
    public abstract GizmoMode Mode { get; }

    /// <summary>
    /// Whether <see cref="Orientation"/> means anything for this tool. False for
    /// <see cref="ScaleGizmo"/>, which is local-only by construction.
    /// </summary>
    public virtual bool SupportsOrientation => true;

    /// <summary>
    /// The frame the handles are laid out in. Ignored when
    /// <see cref="SupportsOrientation"/> is false.
    /// </summary>
    public GizmoOrientation Orientation { get; set; } = GizmoOrientation.World;

    /// <summary>
    /// The gizmo's on-screen size in pixels — the length of one axis handle,
    /// held constant at any camera distance.
    /// </summary>
    public float HandlePixelSize { get; set; } = GizmoGeometry.DefaultPixelSize;

    /// <summary>How close in pixels the cursor must come to a line-shaped handle to pick it.</summary>
    public float PickTolerancePixels { get; set; } = GizmoHitTesting.DefaultTolerancePixels;

    /// <summary>The button that grabs a handle and, on release, commits the drag.</summary>
    public PointerButtons DragButton { get; set; } = PointerButtons.Left;

    /// <summary>
    /// The button that cancels an in-progress drag. Right-click is the
    /// universal "never mind" during a manipulation; Escape does the same
    /// through <see cref="Update"/>'s cancel flag.
    /// </summary>
    public PointerButtons CancelButton { get; set; } = PointerButtons.Right;

    /// <summary>The label a committed drag carries into the undo menu.</summary>
    public string TransactionName { get; set; }

    /// <summary>Where the gizmo is in its interaction cycle.</summary>
    public GizmoInteractionState State => _state;

    /// <summary>The handle under the cursor, or <see cref="GizmoHandle.None"/>.</summary>
    public GizmoHandle HoveredHandle => _hovered;

    /// <summary>
    /// The handle being dragged, or <see cref="GizmoHandle.None"/> when no drag
    /// is in progress.
    /// </summary>
    public GizmoHandle ActiveHandle => _active;

    /// <summary>
    /// True when the last <see cref="Update"/> produced drawable geometry — a
    /// non-empty selection in a sized viewport.
    /// </summary>
    public bool IsVisible => _hasGeometry;

    /// <summary>
    /// The pivot the last <see cref="Update"/> placed the gizmo at: the
    /// selection's average world position while idle, and — for a tool that
    /// moves its own pivot — where the drag has taken it.
    /// <see cref="Vector3.Zero"/> before the first <see cref="Update"/>: it
    /// reports where the gizmo <em>is</em>, not where a selection change has just
    /// decided it should go.
    /// </summary>
    public Vector3 Pivot => _livePivot;

    /// <summary>
    /// The last frame's geometry — the same struct picking used, exposed for a
    /// host that wants to draw the gizmo itself. Meaningless when
    /// <see cref="IsVisible"/> is false.
    /// </summary>
    public GizmoGeometry Geometry => _geometry;

    /// <summary>How many nodes the current drag is manipulating; zero when idle.</summary>
    public int DragTargetCount => _targets.Count;

    /// <summary>The nodes the current drag is manipulating, in capture order.</summary>
    protected IReadOnlyList<GizmoDragTarget> Targets => _targets;

    /// <summary>The pivot the current drag was grabbed at.</summary>
    protected Vector3 GrabPivot => _grabPivot;

    /// <summary>The frame rotation the current drag was grabbed in.</summary>
    /// <remarks>
    /// Frozen at the grab rather than tracked live: a selection that rotates
    /// mid-drag (which is exactly what the rotate tool does to it) would
    /// otherwise swing the constraint out from under the cursor and chase itself.
    /// </remarks>
    protected Quaternion GrabFrame => _grabFrame;

    /// <summary>
    /// Where the gizmo is drawn during the drag. Defaults to the grab pivot;
    /// <see cref="TranslateGizmo"/> overrides it so the gizmo travels with what
    /// it is moving.
    /// </summary>
    protected virtual Vector3 LivePivot => _grabPivot;

    /// <summary>
    /// Whether the current drag has actually changed anything. A gesture that
    /// ends with this false cancels instead of committing.
    /// </summary>
    protected abstract bool HasEdit { get; }

    /// <summary>
    /// Advances the gizmo by one frame: hit-tests, starts a drag on the grab
    /// edge, manipulates the selection while dragging, and commits or cancels on
    /// the release or cancel edge.
    /// </summary>
    /// <param name="frame">This frame's input snapshot.</param>
    /// <param name="cancelRequested">
    /// True on the frame the user asked to abort — the Escape key, or a viewport
    /// that lost focus. It arrives as a parameter rather than inside
    /// <c>EditorInputFrame</c> because the frame deliberately carries no keyboard
    /// vocabulary: the host owns the keymap and passes the verdict down, which
    /// keeps the backend-neutral input seam intact.
    /// </param>
    /// <param name="pointerAvailable">
    /// False when something else has already claimed this frame's press — the
    /// viewport camera's own navigation buttons, in practice. The hover still
    /// updates; only the grab is refused, because a press that belongs to
    /// navigation must not also start a manipulation. A drag already in progress
    /// is unaffected: it owns the pointer and nothing may take it away.
    /// </param>
    /// <returns>What this call did.</returns>
    public GizmoUpdateResult Update(in EditorInputFrame frame, bool cancelRequested = false, bool pointerAvailable = true)
    {
        if (_state == GizmoInteractionState.Dragging)
            return UpdateDrag(in frame, cancelRequested);

        return UpdateHover(in frame, pointerAvailable);
    }

    /// <summary>
    /// Hit-tests the gizmo at this frame's cursor <b>without touching any of
    /// the tool's state</b> — no cached geometry, no hover, no drag. The
    /// question "would a press here grab a handle?", asked by the viewport's
    /// drag arbitration before it has committed to an interpretation.
    /// </summary>
    /// <remarks>
    /// It rebuilds the geometry locally rather than reading
    /// <see cref="Geometry"/>, so the answer is correct even on a frame where
    /// <see cref="Update"/> has not run yet — which is what makes the
    /// arbitration testable on its own, instead of only as a side effect of
    /// driving the whole tool.
    /// </remarks>
    public GizmoPick PickAt(in EditorInputFrame frame)
    {
        if (Scene.Selection.Count == 0 ||
            frame.ViewportSize.X <= 0f || frame.ViewportSize.Y <= 0f ||
            !frame.IsPointerUsable)
        {
            return GizmoPick.Miss;
        }

        GizmoGeometry geometry = GizmoGeometry.Build(
            Scene.Camera, SelectionPivot(), FrameRotation(), frame.ViewportSize, HandlePixelSize);
        Ray3 ray = Scene.Camera.ScreenPointToRay(frame.CursorPosition, frame.ViewportSize);
        return HitTest(in geometry, in ray, PickTolerancePixels);
    }

    /// <summary>
    /// The handle a drag that did not start on the gizmo should be routed to —
    /// the one that means "just move this where the cursor goes".
    /// <see cref="GizmoHandle.None"/> for a tool that has no such handle, which
    /// refuses the gesture rather than inventing one.
    /// </summary>
    /// <remarks>
    /// This is what makes "press on an object and drag it" possible without a
    /// second manipulation path: the viewport picks the object, selects it, and
    /// hands the gesture to whatever this tool calls free movement — the centre
    /// disc for <see cref="TranslateGizmo"/>. Rotate and resize deliberately
    /// have none: dragging an unselected object must not silently spin or
    /// stretch it, so the press stays a plain click-select.
    /// </remarks>
    public virtual GizmoHandle FreeMoveHandle => GizmoHandle.None;

    /// <summary>
    /// Starts a drag on <paramref name="handle"/> as if the user had grabbed it
    /// this frame, without requiring the cursor to be over it. Returns false —
    /// changing nothing and opening no transaction — when a drag is already in
    /// progress, the handle is <see cref="GizmoHandle.None"/>, there is nothing
    /// selected, or the tool refuses the constraint (see
    /// <see cref="TryPrepareDrag"/>).
    /// </summary>
    /// <remarks>
    /// The one caller is the viewport's drag arbitration: a press that landed
    /// on an object rather than on a handle changes the selection first and
    /// then routes the very same gesture into the ordinary drag machine, so it
    /// commits one undo entry and behaves identically to a handle drag from the
    /// second frame onward.
    /// </remarks>
    public bool TryBeginDrag(in EditorInputFrame frame, GizmoHandle handle)
    {
        if (_state == GizmoInteractionState.Dragging || handle == GizmoHandle.None)
            return false;

        // Geometry is normally built by the hover pass; a synthesized grab has
        // to build it itself, and at the pivot of the selection as it stands
        // NOW (the caller has just changed it).
        if (!TryBuildGeometry(in frame, SelectionPivot(), FrameRotation()))
            return false;

        _hovered = handle;
        return BeginDrag(in frame) == GizmoUpdateResult.DragBegan;
    }

    /// <summary>
    /// Pushes the gizmo into <paramref name="output"/>, highlighting the active
    /// handle while dragging and the hovered one otherwise. Draws nothing when
    /// there is no selection.
    /// </summary>
    public void Draw(DebugDraw output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!_hasGeometry || _geometry.IsBehindCamera || _geometry.AxisLength <= 0f)
            return;

        GizmoHandle highlighted = _state == GizmoInteractionState.Dragging ? _active : _hovered;
        DrawHandles(output, in _geometry, highlighted);
    }

    /// <summary>
    /// Aborts an in-progress drag, restoring every node to the state it had at
    /// the grab and landing nothing in the history. Returns false when no drag
    /// was in progress. For a host that must cancel outside the input frame — a
    /// lost window focus, a scene reload.
    /// </summary>
    public bool CancelDrag()
    {
        if (_state != GizmoInteractionState.Dragging)
            return false;

        Undo.CancelTransaction();
        EndDrag();
        return true;
    }

    /// <summary>
    /// Returns the tool to a cold state: cancels any drag (restoring the
    /// selection), forgets the hover, and drops the cached geometry.
    /// </summary>
    /// <remarks>
    /// <b>This is what a mode switch calls.</b> A tool that was left mid-drag
    /// would keep an undo transaction open — the next tool's
    /// <c>BeginTransaction</c> would throw, since transactions do not nest — and
    /// would report a stale <see cref="ActiveHandle"/> forever. Switching away
    /// from a half-finished gesture means abandoning it, which is also what the
    /// user expects: the drag never completed.
    /// </remarks>
    public void Reset()
    {
        CancelDrag();
        _hovered = GizmoHandle.None;
        _active = GizmoHandle.None;
        _hasGeometry = false;
        _state = GizmoInteractionState.Idle;
    }

    // --- Hooks ---------------------------------------------------------------

    /// <summary>Picks the handle this tool's shape puts under <paramref name="ray"/>.</summary>
    protected abstract GizmoPick HitTest(in GizmoGeometry geometry, in Ray3 ray, float tolerancePixels);

    /// <summary>
    /// Sets up the constraint for a grab on <see cref="ActiveHandle"/> and
    /// records everything the drag will recompute from. Returning false refuses
    /// the gesture — a view edge-on to the constraint, or a selection this tool
    /// cannot act on — and no transaction is opened.
    /// </summary>
    /// <remarks>
    /// <see cref="Targets"/> is already populated and non-empty when this runs.
    /// </remarks>
    protected abstract bool TryPrepareDrag(in EditorInputFrame frame, in Ray3 ray);

    /// <summary>
    /// Allocates and records this tool's per-node commands into the transaction
    /// the base has just opened. Called once per gesture, immediately after
    /// <see cref="TryPrepareDrag"/> succeeded.
    /// </summary>
    /// <remarks>
    /// Record, do not execute: the commands' after-state must already match the
    /// scene (nothing has moved yet), and the drag applies every later value
    /// through the same command objects.
    /// </remarks>
    protected abstract void RecordCommands();

    /// <summary>
    /// Applies one frame of the drag, recomputed from the grab capture. A frame
    /// whose cursor ray cannot be projected onto the constraint must leave the
    /// last applied value alone rather than substituting a failed result.
    /// </summary>
    protected abstract void ApplyDrag(in EditorInputFrame frame, in Ray3 ray);

    /// <summary>Clears whatever per-gesture state the tool holds beyond <see cref="Targets"/>.</summary>
    protected abstract void ClearDragState();

    /// <summary>Draws this tool's handles.</summary>
    protected abstract void DrawHandles(DebugDraw output, in GizmoGeometry geometry, GizmoHandle highlighted);

    // --- Shared helpers for subclasses ---------------------------------------

    /// <summary>
    /// The node whose orientation a local-frame gizmo aligns to: the most
    /// recently selected one, which is the "active object" every editor with a
    /// local mode uses. Null for an empty selection.
    /// </summary>
    public SceneNode? ReferenceNode
    {
        get
        {
            IReadOnlyList<SceneNode> items = Scene.Selection.Items;
            return items.Count == 0 ? null : items[items.Count - 1];
        }
    }

    /// <summary>
    /// The rotation the gizmo's handles are laid out in this frame: identity in
    /// <see cref="GizmoOrientation.World"/>, the reference node's world rotation
    /// in <see cref="GizmoOrientation.Local"/>.
    /// </summary>
    protected virtual Quaternion FrameRotation()
    {
        if (Orientation == GizmoOrientation.World || ReferenceNode is not { } node)
            return Quaternion.Identity;

        return WorldRotationOf(node);
    }

    /// <summary>
    /// A node's world rotation, decomposed from its world matrix. Falls back to
    /// identity for a matrix that cannot be decomposed (a zero scale somewhere in
    /// the chain), which is the only finite answer available.
    /// </summary>
    protected static Quaternion WorldRotationOf(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Matrix4x4.Decompose(node.WorldMatrix, out _, out Quaternion rotation, out _)
            ? rotation
            : Quaternion.Identity;
    }

    // --- Hover ---------------------------------------------------------------

    private GizmoUpdateResult UpdateHover(in EditorInputFrame frame, bool pointerAvailable)
    {
        _hovered = GizmoHandle.None;
        _active = GizmoHandle.None;

        if (!TryBuildGeometry(in frame, SelectionPivot(), FrameRotation()))
        {
            _state = GizmoInteractionState.Idle;
            return GizmoUpdateResult.None;
        }

        // A cursor outside the viewport belongs to whatever panel it is over,
        // not to this gizmo — and a LOCKED cursor has no position at all, so
        // hit-testing through it would highlight a handle under a pointer the
        // user cannot see or aim.
        if (frame.IsPointerUsable)
        {
            Ray3 ray = Scene.Camera.ScreenPointToRay(frame.CursorPosition, frame.ViewportSize);
            _hovered = HitTest(in _geometry, in ray, PickTolerancePixels).Handle;
        }

        if (pointerAvailable && _hovered != GizmoHandle.None && frame.WasPressed(DragButton))
            return BeginDrag(in frame);

        _state = _hovered != GizmoHandle.None ? GizmoInteractionState.Hovering : GizmoInteractionState.Idle;
        return _state == GizmoInteractionState.Hovering ? GizmoUpdateResult.Hovering : GizmoUpdateResult.None;
    }

    private bool TryBuildGeometry(in EditorInputFrame frame, Vector3 pivot, Quaternion frameRotation)
    {
        // Nothing selected, or a panel that has not been laid out yet: there is
        // no gizmo to draw and nothing to pick.
        if (Scene.Selection.Count == 0 || frame.ViewportSize.X <= 0f || frame.ViewportSize.Y <= 0f)
        {
            _hasGeometry = false;
            return false;
        }

        _geometry = GizmoGeometry.Build(
            Scene.Camera, pivot, frameRotation, frame.ViewportSize, HandlePixelSize);
        _livePivot = pivot;
        _hasGeometry = true;
        return true;
    }

    /// <summary>
    /// The selection's pivot: the average of every selected node's world
    /// position, which for a single selection is simply that node's position.
    /// </summary>
    /// <remarks>
    /// The average is over the whole selection, including nodes that will not be
    /// manipulated directly because an ancestor of theirs is also selected — the
    /// pivot is where the user sees their selection sitting, and those nodes are
    /// part of what they see. It moves rigidly with everything else either way,
    /// since a parent's edit carries its children.
    /// </remarks>
    protected Vector3 SelectionPivot()
    {
        IReadOnlyList<SceneNode> items = Scene.Selection.Items;
        if (items.Count == 0)
            return Vector3.Zero;

        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < items.Count; i++)
            sum += items[i].WorldPosition;

        return sum / items.Count;
    }

    // --- Grab ----------------------------------------------------------------

    private GizmoUpdateResult BeginDrag(in EditorInputFrame frame)
    {
        _active = _hovered;
        _grabPivot = _geometry.Pivot;
        _grabFrame = FrameRotation();

        CaptureTargets();

        Ray3 ray = Scene.Camera.ScreenPointToRay(frame.CursorPosition, frame.ViewportSize);
        if (_targets.Count == 0 || !TryPrepareDrag(in frame, in ray))
        {
            // Either nothing in the selection is manipulable by this tool, or
            // the view is edge-on to the constraint at the instant of the grab
            // and there is no cursor position to anchor the drag to. Refuse the
            // gesture rather than open a transaction that can never do anything.
            _targets.Clear();
            _active = GizmoHandle.None;
            _state = GizmoInteractionState.Hovering;
            return GizmoUpdateResult.Hovering;
        }

        Undo.BeginTransaction(TransactionName);
        RecordCommands();

        _livePivot = _grabPivot;
        _state = GizmoInteractionState.Dragging;
        return GizmoUpdateResult.DragBegan;
    }

    private void CaptureTargets()
    {
        _targets.Clear();

        IReadOnlyList<SceneNode> items = Scene.Selection.Items;
        for (int i = 0; i < items.Count; i++)
        {
            SceneNode node = items[i];

            // Skip nodes an also-selected ancestor already carries: applying the
            // edit to both would apply it twice to the descendant and tear the
            // selection apart instead of manipulating it rigidly.
            if (HasSelectedAncestor(node))
                continue;

            _targets.Add(GizmoDragTarget.Capture(node));
        }
    }

    private bool HasSelectedAncestor(SceneNode node)
    {
        for (SceneNode? parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (Scene.Selection.Contains(parent))
                return true;
        }
        return false;
    }

    // --- Drag ----------------------------------------------------------------

    private GizmoUpdateResult UpdateDrag(in EditorInputFrame frame, bool cancelRequested)
    {
        if (cancelRequested || frame.WasPressed(CancelButton))
        {
            Undo.CancelTransaction();
            EndDrag();
            return GizmoUpdateResult.DragCancelled;
        }

        if (frame.WasReleased(DragButton))
            return CommitDrag();

        Ray3 ray = Scene.Camera.ScreenPointToRay(frame.CursorPosition, frame.ViewportSize);
        ApplyDrag(in frame, in ray);

        // Rebuild at the live pivot, in the frozen grab frame, so the gizmo
        // keeps its constant screen size as the selection travels toward or away
        // from the camera without the handles swinging around mid-gesture.
        _livePivot = LivePivot;
        _geometry = GizmoGeometry.Build(
            Scene.Camera, _livePivot, _grabFrame, frame.ViewportSize, HandlePixelSize);
        _hasGeometry = true;

        return GizmoUpdateResult.DragUpdated;
    }

    private GizmoUpdateResult CommitDrag()
    {
        // A grab that never changed anything is a click, not an edit. Cancelling
        // restores the (identical) captured state and leaves the history clean,
        // instead of littering it with no-op entries the user has to undo past.
        bool edited = HasEdit;
        if (edited)
            Undo.CommitTransaction();
        else
            Undo.CancelTransaction();

        EndDrag();
        return edited ? GizmoUpdateResult.DragCommitted : GizmoUpdateResult.DragCancelled;
    }

    private void EndDrag()
    {
        ClearDragState();
        _targets.Clear();
        _active = GizmoHandle.None;
        // Back to Idle rather than Hovering: the next frame's hit test decides
        // whether the cursor is still over a handle, at the gizmo's new home.
        _state = GizmoInteractionState.Idle;
    }
}
