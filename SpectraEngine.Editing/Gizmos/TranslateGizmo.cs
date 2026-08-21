using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The move tool: draws a translate gizmo at the selection's pivot, decides
/// which handle the cursor is over, and runs the grab → drag → commit/cancel
/// gesture that moves every selected node.
/// </summary>
/// <remarks>
/// <b>Feed it one <see cref="EditorInputFrame"/> per frame and call
/// <c>Draw</c>; <see cref="GizmoTool"/> owns everything else</b> — the state
/// machine, the transaction, the constant-screen-size geometry, the target
/// capture. What lives here is only what makes a move a move: the line/plane
/// constraint, the grid snap, and the arrows.
/// <para>
/// <b>Brush nodes move through the ordinary transform setters</b>, via
/// <see cref="SetTransformCommand"/>, so a gizmo drag dirties exactly the chunk
/// cells a scripted move of the same node would and drives the async chunked
/// recompile through its normal incremental path. The gizmo knows nothing about
/// brushes or the static world, and that is the point.
/// </para>
/// <para>
/// <b>Snapping depends on the orientation.</b> In
/// <see cref="GizmoOrientation.World"/> the RESULT is snapped, so a snapped drag
/// lands on absolute grid coordinates instead of preserving whatever sub-grid
/// offset the selection started with — and only on the axes the grabbed handle
/// actually frees. In <see cref="GizmoOrientation.Local"/> there is no absolute
/// grid to land on (the frame is the object's, and it is not aligned with the
/// world), so the DISPLACEMENT along each free frame axis is snapped instead:
/// "move exactly two units along the object's own x".
/// </para>
/// <para>
/// <b>Snapping never manufactures movement.</b> A frame whose cursor sits
/// exactly where the drag was grabbed applies a zero delta unsnapped, so a
/// click that happens to be held for a frame or two — which every click is —
/// leaves an off-grid selection exactly where it was instead of quantising it.
/// The grid takes effect from the first frame the cursor genuinely asks for
/// movement.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only. Steady-state hovering and dragging
/// allocate nothing; a grab allocates one command per moved node.
/// </para>
/// </remarks>
public sealed class TranslateGizmo : GizmoTool
{
    // Index-aligned with Targets, allocated on the first gesture of a given
    // width and reused after that.
    private readonly List<SetTransformCommand> _commands = [];

    private Vector3 _grabPoint;
    private Vector3 _constraintAxis;
    private Vector3 _constraintNormal;
    private Vector3 _freeAxisMask;
    private Vector3 _appliedDelta;

    /// <summary>
    /// Creates a move tool over a scene and the history its edits land in.
    /// </summary>
    /// <param name="scene">The scene whose selection this gizmo moves.</param>
    /// <param name="undo">The history to open a transaction in per drag.</param>
    public TranslateGizmo(Scene scene, UndoStack undo)
        : base(scene, undo, "Move")
    {
    }

    /// <inheritdoc/>
    public override GizmoMode Mode => GizmoMode.Translate;

    /// <summary>
    /// The centre disc: a drag that started on an object rather than on a
    /// handle follows the cursor in the camera-facing plane, which is what
    /// "pick it up and move it" means.
    /// </summary>
    public override GizmoHandle FreeMoveHandle => GizmoHandle.Screen;

    /// <summary>Grid-snapping configuration for drags. See <see cref="GridSnapSettings"/>.</summary>
    public GridSnapSettings Snap { get; } = new();

    /// <summary>
    /// The world-space movement applied so far in the current drag, snapping
    /// included; <see cref="Vector3.Zero"/> when no drag is in progress.
    /// </summary>
    public Vector3 DragDelta => _appliedDelta;

    /// <inheritdoc/>
    protected override bool HasEdit => _appliedDelta != Vector3.Zero;

    /// <summary>
    /// The gizmo travels with what it is moving, so it keeps its constant screen
    /// size as the selection goes toward or away from the camera.
    /// </summary>
    protected override Vector3 LivePivot => GrabPivot + _appliedDelta;

    /// <inheritdoc/>
    protected override GizmoPick HitTest(in GizmoGeometry geometry, in Ray3 ray, float tolerancePixels) =>
        TranslateGizmoHitTester.Pick(in geometry, in ray, tolerancePixels);

    /// <inheritdoc/>
    protected override bool TryPrepareDrag(in EditorInputFrame frame, in Ray3 ray)
    {
        SetUpConstraint(ActiveHandle);

        if (!TryProjectOntoConstraint(in ray, out Vector3 grabPoint))
            return false;

        _grabPoint = grabPoint;
        _appliedDelta = Vector3.Zero;
        return true;
    }

    /// <inheritdoc/>
    protected override void RecordCommands()
    {
        _commands.Clear();

        IReadOnlyList<GizmoDragTarget> targets = Targets;
        for (int i = 0; i < targets.Count; i++)
        {
            SceneNode node = targets[i].Node;
            Vector3 startLocal = targets[i].StartLocal.Position;
            var command = new SetTransformCommand(
                node.Id, startLocal, node.LocalRotation, startLocal, node.LocalRotation)
            {
                Name = TransactionName,
            };

            _commands.Add(command);
            Undo.Record(command);
        }
    }

    /// <inheritdoc/>
    protected override void ApplyDrag(in EditorInputFrame frame, in Ray3 ray)
    {
        // A frame whose ray cannot be projected (the view went edge-on to the
        // constraint) simply holds the last position. Because the result is
        // recomputed from the grab every frame, skipping one leaves no residue.
        if (!TryProjectOntoConstraint(in ray, out Vector3 point))
            return;

        Vector3 delta = point - _grabPoint;

        // A cursor that has not moved off the grab is not a drag, and snapping
        // must not turn it into one. In world orientation SnapDelta quantises
        // the ABSOLUTE destination, so at a zero cursor delta it still returns
        // round(pivot) − pivot — non-zero for any off-grid selection. Without
        // this the first held frame of a plain click teleports the selection
        // onto the grid, commits a "Move" nobody asked for, recompiles the
        // static world around it, and (because the gesture now reports as a
        // real edit) swallows the click-to-isolate that should have collapsed a
        // multi-selection. The projection is a pure function of the cursor
        // pixel and the frozen constraint, so an unmoved cursor gives back the
        // grab point bit-for-bit and this test is exact, not a tolerance.
        if (delta != Vector3.Zero && Snap.IsActiveWith(frame.Modifiers))
            delta = SnapDelta(delta);

        ApplyDelta(delta);
    }

    /// <inheritdoc/>
    protected override void ClearDragState()
    {
        _commands.Clear();
        _appliedDelta = Vector3.Zero;
    }

    /// <inheritdoc/>
    protected override void DrawHandles(DebugDraw output, in GizmoGeometry geometry, GizmoHandle highlighted) =>
        TranslateGizmoRenderer.Draw(output, in geometry, highlighted);

    private void SetUpConstraint(GizmoHandle handle)
    {
        GizmoGeometry geometry = Geometry;
        _freeAxisMask = GizmoHandles.FreeAxisMask(handle);
        _constraintAxis = geometry.Axis(handle);
        // Frozen at the grab rather than tracked live: a camera that moved
        // mid-drag would otherwise swing the constraint plane out from under the
        // cursor and drag the selection with it.
        _constraintNormal = geometry.PlaneNormal(handle);
    }

    // Projects the cursor ray onto this drag's constraint. Returns false when
    // the view is too close to edge-on for the projection to mean anything, in
    // which case `point` is undefined and callers must hold their last value —
    // never substitute the failed result.
    private bool TryProjectOntoConstraint(in Ray3 ray, out Vector3 point)
    {
        // An axis handle constrains to a line, everything else to a plane —
        // both through the pivot the drag was grabbed at.
        if (GizmoHandles.IsAxis(ActiveHandle))
            return GizmoMath.TryClosestPointOnLine(in ray, GrabPivot, _constraintAxis, out point);

        if (GizmoMath.TryRayPlane(in ray, GrabPivot, _constraintNormal, out float distance))
        {
            point = ray.PointAt(distance);
            return true;
        }

        point = Vector3.Zero;
        return false;
    }

    // World mode snaps the absolute destination onto the world grid; local mode
    // snaps the displacement along each free frame axis (see the type remarks).
    private Vector3 SnapDelta(Vector3 delta)
    {
        if (Orientation == GizmoOrientation.World)
            return Snap.SnapMasked(GrabPivot + delta, _freeAxisMask) - GrabPivot;

        GizmoGeometry geometry = Geometry;
        Vector3 snapped = Vector3.Zero;
        if (_freeAxisMask.X != 0f)
            snapped += geometry.AxisX * Snap.SnapScalar(Vector3.Dot(delta, geometry.AxisX));
        if (_freeAxisMask.Y != 0f)
            snapped += geometry.AxisY * Snap.SnapScalar(Vector3.Dot(delta, geometry.AxisY));
        if (_freeAxisMask.Z != 0f)
            snapped += geometry.AxisZ * Snap.SnapScalar(Vector3.Dot(delta, geometry.AxisZ));

        return snapped;
    }

    private void ApplyDelta(Vector3 worldDelta)
    {
        _appliedDelta = worldDelta;

        IReadOnlyList<GizmoDragTarget> targets = Targets;
        for (int i = 0; i < targets.Count; i++)
        {
            GizmoDragTarget target = targets[i];
            SetTransformCommand command = _commands[i];

            // From the CAPTURED start, never from where the node is now.
            Vector3 local = target.StartLocal.Position
                + Vector3.TransformNormal(worldDelta, target.ParentWorldInverse);

            command.SetAfter(local, command.AfterRotation);
            // Through the command, so the scene and the history entry can never
            // disagree about where the node ended up — and through the node's
            // ordinary transform setter, so a brush node dirties its chunk cells
            // exactly as a scripted move would.
            command.Do(Scene);
        }
    }
}
