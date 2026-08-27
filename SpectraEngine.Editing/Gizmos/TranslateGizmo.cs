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
/// <b>Snapping quantises the DISPLACEMENT by default</b> — "move exactly two
/// units along x", with the selection's sub-grid offsets preserved — which is
/// what both Roblox Studio and Blender do (see <see cref="TranslateSnapMode"/>;
/// an earlier revision snapped the absolute destination and mis-cited Studio
/// for it). The screen handle snaps its displacement along its own frozen
/// constraint-plane basis rather than the three world axes, so a snapped
/// free-drag steps ruler-like across the camera plane and never leaves it.
/// <see cref="TranslateSnapMode.AbsoluteGrid"/> restores destination snapping
/// as an opt-in, world orientation only, anchored on the reference node's
/// captured start so the node the user grabbed is the one that lands on grid
/// multiples — anchoring on the multi-select pivot AVERAGE, as the old default
/// did, landed no node on the grid at all.
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

    // The screen handle's constraint-plane basis, frozen at the grab like the
    // normal: a snapped free-drag quantises its displacement along these, so
    // the result stays exactly in the plane the cursor is tracked in.
    private Vector3 _screenRight;
    private Vector3 _screenUp;

    // Where AbsoluteGrid snapping anchors: the reference node's captured world
    // start. The pivot AVERAGE is the wrong anchor for a multi-selection —
    // rounding around it lands every node off-grid (each keeps its offset from
    // a point that is itself off-grid), while anchoring on the node the user
    // grabbed lands that node on exact grid multiples and the rest keep their
    // relative offsets.
    private Vector3 _absoluteAnchor;

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
        _absoluteAnchor = ResolveAbsoluteAnchor();
        return true;
    }

    // The reference node's captured world start, for AbsoluteGrid snapping.
    // Falls back to the last captured target when the reference node itself
    // was skipped at capture (a selected ancestor carries it), and to the grab
    // pivot when nothing was captured at all — the anchor must always be
    // finite, and for a single selection all three answers coincide.
    private Vector3 ResolveAbsoluteAnchor()
    {
        IReadOnlyList<GizmoDragTarget> targets = Targets;
        if (targets.Count == 0)
            return GrabPivot;

        SceneNode? reference = ReferenceNode;
        for (int i = 0; i < targets.Count; i++)
        {
            if (ReferenceEquals(targets[i].Node, reference))
                return targets[i].StartWorldPosition;
        }

        return targets[targets.Count - 1].StartWorldPosition;
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
        // cursor and drag the selection with it. The screen basis freezes with
        // the normal for the same reason — it is the snap frame of the plane
        // the normal defines.
        _constraintNormal = geometry.PlaneNormal(handle);
        _screenRight = geometry.ViewRight;
        _screenUp = geometry.ViewUp;
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

    // Delta mode quantises the displacement (the default; see the type
    // remarks); AbsoluteGrid quantises the reference node's destination onto
    // the world grid. Local orientation always snaps the displacement along
    // each free frame axis — a local frame has no absolute grid to land on.
    private Vector3 SnapDelta(Vector3 delta)
    {
        // The screen handle's constraint plane is not axis-aligned in ANY
        // frame, so its delta snaps along the plane's own frozen basis: the
        // drag steps ruler-like across the camera plane and never leaves it.
        // Snapping the three world components instead (the old behavior) let
        // the result sit up to half a grid step off the plane the cursor was
        // tracked in — with snapping on by default, every free-drag popped in
        // x, y and z at once. AbsoluteGrid keeps the world rounding: there the
        // user asked for an absolute grid position, and the plane was only
        // ever the input mapping.
        if (ActiveHandle == GizmoHandle.Screen &&
            (Orientation == GizmoOrientation.Local || Snap.Mode == TranslateSnapMode.Delta))
        {
            return _screenRight * Snap.SnapScalar(Vector3.Dot(delta, _screenRight))
                 + _screenUp * Snap.SnapScalar(Vector3.Dot(delta, _screenUp));
        }

        if (Orientation == GizmoOrientation.World)
        {
            if (Snap.Mode == TranslateSnapMode.AbsoluteGrid)
                return Snap.SnapMasked(_absoluteAnchor + delta, _freeAxisMask) - _absoluteAnchor;

            // In world orientation the frame axes ARE the world axes, so
            // displacement snapping is the masked componentwise round.
            return Snap.SnapMasked(delta, _freeAxisMask);
        }

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
