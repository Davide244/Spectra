using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The rotate tool: draws three axis rings and a view-aligned ring at the
/// selection's pivot, and turns a drag around one of them into a rotation of the
/// whole selection about that pivot.
/// </summary>
/// <remarks>
/// <b>A multi-selection rotates as one rigid body.</b> Every node's orientation
/// is turned by the swept angle <em>and</em> its position is carried around the
/// shared pivot, which is what makes rotating four walls of a room behave like
/// rotating the room rather than like spinning four walls in place. A single
/// selection is the degenerate case of the same arithmetic: its position is the
/// pivot, so it only spins.
/// <para>
/// <b>The angle is the sweep between two cursor positions projected onto the
/// ring's plane</b> — the grab point and the current one — measured about the
/// ring's axis. That is the mapping every editor uses, and it is what makes a
/// rotation follow the hand rather than the mouse's raw travel.
/// </para>
/// <para>
/// <b>One piece of per-frame state, and only one.</b> A cursor position defines
/// its angle only modulo a full turn, so continuing past ±180° needs the winding
/// number, which the cursor genuinely does not carry; the tool tracks it across
/// frames. Everything else is recomputed from the grab capture every frame — the
/// applied transform is always <c>rotation(total angle) · starting transform</c>,
/// never last frame's transform nudged — so no rounding or snapping residue can
/// accumulate, and a cancel restores the starting transforms exactly.
/// </para>
/// <para>
/// <b>Brush nodes rotate through the ordinary transform setters</b>, like a move:
/// rotation and translation are exactly what a brush node's transform is allowed
/// to hold, so the static-world compile sees a gizmo rotation as just another
/// edit and recompiles the cells it touched.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only. Hovering and dragging allocate nothing;
/// a grab allocates one command per rotated node.
/// </para>
/// </remarks>
public sealed class RotateGizmo : GizmoTool
{
    // Below this the grab point is so close to the pivot that its direction is
    // dominated by rounding, and the swept angle would jitter wildly. Expressed
    // as a fraction of the ring radius so it scales with the gizmo, and shared
    // with the hit tester so what highlights is what a press can grab.
    private const float MinimumGrabRadiusFactor = RotateGizmoHitTester.MinimumGrabRadiusFactor;

    // Index-aligned with Targets; reused across gestures.
    private readonly List<SetTransformCommand> _commands = [];

    private Vector3 _ringAxis;
    private Vector3 _reference;      // unit, pivot → grab point, in the ring's plane
    private Vector3 _perpendicular;  // cross(axis, reference): the +90° direction
    private float _ringRadius;

    private float _rawAngle;         // last frame's angle in (−π, π]
    private int _turns;              // winding number, the one stateful quantity
    private float _appliedAngle;

    /// <summary>
    /// Creates a rotate tool over a scene and the history its edits land in.
    /// </summary>
    /// <param name="scene">The scene whose selection this gizmo rotates.</param>
    /// <param name="undo">The history to open a transaction in per drag.</param>
    public RotateGizmo(Scene scene, UndoStack undo)
        : base(scene, undo, "Rotate")
    {
    }

    /// <inheritdoc/>
    public override GizmoMode Mode => GizmoMode.Rotate;

    /// <summary>Angle-snapping configuration for drags. See <see cref="AngleSnapSettings"/>.</summary>
    public AngleSnapSettings Snap { get; } = new();

    /// <summary>
    /// The angle applied so far in the current drag, in radians, signed about
    /// <see cref="DragAxis"/>; zero when no drag is in progress.
    /// </summary>
    public float DragAngle => _appliedAngle;

    /// <summary>
    /// The same angle in degrees — the number a status bar shows and the unit
    /// <see cref="Snap"/> is configured in.
    /// </summary>
    public float DragAngleDegrees => _appliedAngle * (180f / MathF.PI);

    /// <summary>
    /// The world-space unit axis the current drag turns about;
    /// <see cref="Vector3.Zero"/> when no drag is in progress.
    /// </summary>
    public Vector3 DragAxis => _ringAxis;

    /// <inheritdoc/>
    protected override bool HasEdit => _appliedAngle != 0f;

    /// <inheritdoc/>
    protected override GizmoPick HitTest(in GizmoGeometry geometry, in Ray3 ray, float tolerancePixels) =>
        RotateGizmoHitTester.Pick(in geometry, in ray, tolerancePixels);

    /// <inheritdoc/>
    protected override bool TryPrepareDrag(in EditorInputFrame frame, in Ray3 ray)
    {
        GizmoGeometry geometry = Geometry;
        if (!geometry.TryGetRing(ActiveHandle, out Vector3 axis, out float radius))
            return false;

        // Frozen at the grab: the selection is about to start turning (and, for
        // the view ring, the camera may move), and a constraint plane that
        // followed either would chase the cursor it is supposed to be measuring.
        _ringAxis = axis;
        _ringRadius = radius;

        if (!TryProjectOntoRingPlane(in ray, out Vector3 grabPoint))
            return false;

        Vector3 spoke = grabPoint - GrabPivot;
        float length = spoke.Length();
        if (length < radius * MinimumGrabRadiusFactor)
            return false; // grabbed essentially at the pivot; no direction to sweep from

        _reference = spoke / length;
        _perpendicular = Vector3.Cross(_ringAxis, _reference);
        _rawAngle = 0f;
        _turns = 0;
        _appliedAngle = 0f;
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
            var command = new SetTransformCommand(
                node.Id, node.LocalPosition, node.LocalRotation, node.LocalPosition, node.LocalRotation)
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
        // The view went edge-on to the ring's plane: hold the last angle rather
        // than substituting a meaningless projection. Nothing accumulates, so a
        // skipped frame leaves no trace.
        if (!TryProjectOntoRingPlane(in ray, out Vector3 point))
            return;

        Vector3 spoke = point - GrabPivot;
        float length = spoke.Length();
        if (length < _ringRadius * MinimumGrabRadiusFactor)
            return; // cursor is at the pivot; its direction is noise

        // Signed angle from the reference spoke, in the ring's own plane.
        float raw = MathF.Atan2(Vector3.Dot(spoke, _perpendicular), Vector3.Dot(spoke, _reference));

        // Winding: a jump of more than half a turn between frames is the branch
        // cut at ±π, not a real half-turn of the wrist.
        float jump = raw - _rawAngle;
        if (jump > MathF.PI)
            _turns--;
        else if (jump < -MathF.PI)
            _turns++;
        _rawAngle = raw;

        float angle = raw + _turns * MathF.Tau;
        if (Snap.IsActiveWith(frame.Modifiers))
            angle = Snap.SnapRadians(angle);

        ApplyAngle(angle);
    }

    /// <inheritdoc/>
    protected override void ClearDragState()
    {
        _commands.Clear();
        _ringAxis = Vector3.Zero;
        _reference = Vector3.Zero;
        _perpendicular = Vector3.Zero;
        _ringRadius = 0f;
        _rawAngle = 0f;
        _turns = 0;
        _appliedAngle = 0f;
    }

    /// <inheritdoc/>
    protected override void DrawHandles(DebugDraw output, in GizmoGeometry geometry, GizmoHandle highlighted)
    {
        RotateGizmoRenderer.Draw(output, in geometry, highlighted);

        // The protractor only exists during a drag, and only once the angle is
        // non-zero — a zero sweep would draw two coincident spokes and read as a
        // stray line through the gizmo.
        if (State == GizmoInteractionState.Dragging && _appliedAngle != 0f)
            RotateGizmoRenderer.DrawSweep(output, in geometry, _ringAxis, _reference, _ringRadius, _appliedAngle);
    }

    // The ring's plane through the pivot. Unlike the translate gizmo's axis
    // constraint there is no line case: every rotate handle is a plane.
    private bool TryProjectOntoRingPlane(in Ray3 ray, out Vector3 point)
    {
        if (GizmoMath.TryRayPlane(in ray, GrabPivot, _ringAxis, out float distance))
        {
            point = ray.PointAt(distance);
            return true;
        }

        point = Vector3.Zero;
        return false;
    }

    private void ApplyAngle(float angle)
    {
        _appliedAngle = angle;

        // Exactly zero is worth its own branch: it is where a snapped drag spends
        // its first frames, and Quaternion.CreateFromAxisAngle(axis, 0) is only
        // approximately the identity in float, so going through the general path
        // would write a rotation that differs from the starting one in the last
        // bit and dirty a brush node's chunk for nothing.
        Quaternion delta = angle == 0f
            ? Quaternion.Identity
            : Quaternion.CreateFromAxisAngle(_ringAxis, angle);

        IReadOnlyList<GizmoDragTarget> targets = Targets;
        for (int i = 0; i < targets.Count; i++)
        {
            GizmoDragTarget target = targets[i];
            SetTransformCommand command = _commands[i];

            // Both from the CAPTURED start, never from where the node is now.
            // Position orbits the shared pivot; orientation is pre-multiplied,
            // because the delta is expressed in world space and the start
            // rotation is applied first.
            Vector3 worldPosition = GrabPivot + Vector3.Transform(target.StartWorldPosition - GrabPivot, delta);
            Quaternion worldRotation = delta * target.StartWorldRotation;

            command.SetAfter(
                Vector3.Transform(worldPosition, target.ParentWorldInverse),
                target.ParentWorldRotationInverse * worldRotation);
            command.Do(Scene);
        }
    }
}
