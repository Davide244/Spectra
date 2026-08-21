using SpectraEngine.Core.Bsp;
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
/// The resize tool: three cube-capped axis handles and a uniform centre cube,
/// turning a drag into a per-axis scale factor — applied to a mesh node's
/// transform scale, and to a brush node's <em>local plane extents</em>.
/// </summary>
/// <remarks>
/// <b>Brush nodes never receive node scale, and that is the load-bearing rule of
/// this whole tool.</b> The CSG epsilon scheme assumes rigid brush placements and
/// unit-length plane normals; a scale in a brush node's transform makes
/// <c>Plane.Transform</c> emit non-normalized planes and silently changes the
/// meaning of every distance tolerance downstream, which is why
/// <c>Scene</c>'s snapshot rejects a non-rigid brush node outright rather than
/// carving it. So the tool routes by payload:
/// <list type="bullet">
///   <item><description>
///     A node with a <see cref="Brush"/> is resized by rebuilding the brush with
///     scaled plane offsets (<see cref="Brush.WithScaledExtents"/>) and swapping
///     the successor onto the node through <see cref="SetBrushCommand"/>. The
///     node's transform is not touched at all.
///   </description></item>
///   <item><description>
///     A node with <em>no brush anywhere in its subtree</em> is resized by
///     writing <c>LocalScale</c> through
///     <see cref="SetLocalTransformCommand"/>, which is what scale means for a
///     mesh.
///   </description></item>
///   <item><description>
///     A node that carries no brush of its own but has brush
///     <em>descendants</em> is refused: it is skipped, and a gesture in which
///     no selected node is resizable never starts at all.
///   </description></item>
/// </list>
/// <para>
/// <b>That third case is why the routing asks
/// <see cref="SceneNode.SubtreeBrushCount"/> and not <c>node.Brush</c>.</b> A
/// brush's placement is the world matrix of the node it hangs under, so a scale
/// written on any ancestor makes it non-rigid — and the compile rejects the
/// whole placement snapshot when one entry is non-rigid, which wedges the
/// static world <em>scene-wide</em> until the scale is undone, not merely for
/// the scaled subtree. A group node with brush children is a supported shape
/// (that is what the subtree count is maintained for), so this tool has to
/// decline it rather than assume it away.
/// </para>
/// <para>
/// <b>This gizmo has no world/local toggle</b> (<see cref="SupportsOrientation"/>
/// is false) and is always laid out in the reference node's own frame. A
/// non-uniform scale is only defined relative to a set of axes: applying
/// world-axis factors to a rotated object is a shear, not a resize, and there is
/// nothing in either a <c>Transform.Scale</c> or a brush's plane set that can
/// represent the result. Roblox Studio's and Unity's resize handles are
/// local-only for exactly this reason.
/// </para>
/// <para>
/// <b>Each node resizes about its own origin</b>, and no node's position moves.
/// A multi-selection resize therefore makes each object bigger where it stands
/// rather than also pushing them apart — the behaviour Unity has in Pivot mode,
/// and the only one that stays exact when the selected nodes have different
/// rotations. Nodes whose rotation differs from the gizmo's frame receive the
/// factors along their <em>own</em> axes.
/// </para>
/// <para>
/// <b>Cost.</b> Rebuilding a brush re-runs its plane validation and face clipping
/// and therefore allocates — a resize drag of a brush node is the one gesture in
/// the editing layer that is not per-frame allocation-free, and it cannot be:
/// brushes are immutable by design. The tool only rebuilds when the (snapped)
/// factor actually changed since the previous frame, so a snapped drag rebuilds
/// once per grid step rather than once per frame, and a drag that has not left
/// its starting size rebuilds nothing at all.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only. A grab allocates one command per node.
/// </para>
/// </remarks>
public sealed class ScaleGizmo : GizmoTool
{
    /// <summary>
    /// The smallest factor a drag will apply. Zero would collapse a mesh to a
    /// plane and is rejected outright by <see cref="Brush.WithScaledExtents"/>;
    /// negative would mirror the object, turning a brush inside out.
    /// </summary>
    public const float MinimumFactor = 0.01f;

    /// <summary>
    /// The largest factor a single drag will apply. Not a safety limit so much
    /// as a sanity one: past this the cursor is a handful of pixels from the
    /// pivot and every further pixel multiplies the size again.
    /// </summary>
    public const float MaximumFactor = 1000f;

    // Below this the grab offset along the constraint is dominated by rounding,
    // and the factor (a ratio against it) would explode. A fraction of the
    // gizmo's own length, so it scales with the gizmo.
    private const float MinimumGrabOffsetFactor = 0.05f;

    // What one node needs for the whole gesture. At most one of the two
    // commands is non-null, decided by the routing in the type remarks; BOTH
    // are null for a node this tool declines to resize, whose slot exists only
    // to keep the list index-aligned with Targets.
    private readonly record struct ScaleTarget(
        SetLocalTransformCommand? Transform,
        SetBrushCommand? Brush,
        Brush? StartBrush);

    private readonly List<ScaleTarget> _scaleTargets = [];

    private Vector3 _constraintAxis;
    private Vector3 _constraintNormal;
    private Vector3 _uniformDirection;
    private Vector3 _grabPoint;
    private float _grabOffset;
    private float _grabAxisLength;
    private Vector3 _appliedFactor = Vector3.One;

    /// <summary>
    /// Creates a resize tool over a scene and the history its edits land in.
    /// </summary>
    /// <param name="scene">The scene whose selection this gizmo resizes.</param>
    /// <param name="undo">The history to open a transaction in per drag.</param>
    public ScaleGizmo(Scene scene, UndoStack undo)
        : base(scene, undo, "Resize")
    {
    }

    /// <inheritdoc/>
    public override GizmoMode Mode => GizmoMode.Scale;

    /// <summary>
    /// Always false: a resize is only meaningful in the resized object's own
    /// axes. See the type remarks.
    /// </summary>
    public override bool SupportsOrientation => false;

    /// <summary>Factor-snapping configuration for drags. See <see cref="ScaleSnapSettings"/>.</summary>
    public ScaleSnapSettings Snap { get; } = new();

    /// <summary>
    /// The per-axis factor applied so far in the current drag, in the gizmo's
    /// frame; <see cref="Vector3.One"/> when no drag is in progress.
    /// </summary>
    public Vector3 DragFactor => _appliedFactor;

    /// <inheritdoc/>
    protected override bool HasEdit => _appliedFactor != Vector3.One;

    /// <summary>
    /// Always the reference node's world rotation, whatever
    /// <see cref="GizmoTool.Orientation"/> says — see the type remarks for why
    /// there is no choice to make here.
    /// </summary>
    protected override Quaternion FrameRotation() =>
        ReferenceNode is { } node ? WorldRotationOf(node) : Quaternion.Identity;

    /// <inheritdoc/>
    protected override GizmoPick HitTest(in GizmoGeometry geometry, in Ray3 ray, float tolerancePixels) =>
        ScaleGizmoHitTester.Pick(in geometry, in ray, tolerancePixels);

    /// <inheritdoc/>
    protected override bool TryPrepareDrag(in EditorInputFrame frame, in Ray3 ray)
    {
        // Refuse the whole gesture when nothing in the selection can be resized
        // (see the type remarks). Doing it here rather than in RecordCommands is
        // what keeps a hopeless grab from opening a transaction and reporting a
        // commit for an edit that never had anywhere to land.
        if (!AnyResizableTarget())
            return false;

        GizmoGeometry geometry = Geometry;
        _constraintAxis = geometry.Axis(ActiveHandle);
        _constraintNormal = geometry.ViewNormal;
        _grabAxisLength = geometry.AxisLength;

        // "Up and to the right makes it bigger" — the direction every uniform
        // resize handle in every editor grows along. Frozen at the grab so a
        // camera that turns mid-drag does not re-point it.
        _uniformDirection = Vector3.Normalize(geometry.ViewRight + geometry.ViewUp);

        if (GizmoHandles.IsAxis(ActiveHandle))
        {
            if (!TryProjectOntoAxis(in ray, out float offset))
                return false;

            // The factor is a RATIO against this, so a grab at the pivot would
            // make every later frame divide by (almost) nothing.
            if (MathF.Abs(offset) < geometry.AxisLength * MinimumGrabOffsetFactor)
                return false;

            _grabOffset = offset;
        }
        else
        {
            if (!TryProjectOntoViewPlane(in ray, out Vector3 grabPoint))
                return false;

            _grabPoint = grabPoint;
        }

        _appliedFactor = Vector3.One;
        return true;
    }

    /// <inheritdoc/>
    protected override void RecordCommands()
    {
        _scaleTargets.Clear();

        IReadOnlyList<GizmoDragTarget> targets = Targets;
        for (int i = 0; i < targets.Count; i++)
        {
            SceneNode node = targets[i].Node;

            // The routing decision, made once per gesture: a brush node's size
            // lives in its planes, a brush-free node's lives in its transform,
            // and a node with brush DESCENDANTS has nowhere to put it at all.
            if (node.Brush is { } brush)
            {
                var command = new SetBrushCommand(node.Id, brush, brush) { Name = TransactionName };
                _scaleTargets.Add(new ScaleTarget(null, command, brush));
                Undo.Record(command);
            }
            else if (node.SubtreeBrushCount > 0)
            {
                // Declined: writing LocalScale here would make every brush below
                // this node non-rigid and stall the static-world compile for the
                // whole scene. Hold the slot so the list stays index-aligned.
                _scaleTargets.Add(new ScaleTarget(null, null, null));
            }
            else
            {
                var command = new SetLocalTransformCommand(node.Id, targets[i].StartLocal, targets[i].StartLocal)
                {
                    Name = TransactionName,
                };
                _scaleTargets.Add(new ScaleTarget(command, null, null));
                Undo.Record(command);
            }
        }
    }

    /// <inheritdoc/>
    protected override void ApplyDrag(in EditorInputFrame frame, in Ray3 ray)
    {
        // A frame whose ray cannot be projected onto the constraint holds the
        // last factor: the result is recomputed from the grab every frame, so a
        // skipped one leaves no residue.
        if (!TryComputeRatio(in ray, out float ratio))
            return;

        if (Snap.IsActiveWith(frame.Modifiers))
            ratio = Snap.SnapScalar(ratio);

        ratio = Math.Clamp(ratio, MinimumFactor, MaximumFactor);

        // An axis handle scales one frame axis; the centre cube scales all three.
        Vector3 factor = ActiveHandle == GizmoHandle.Screen
            ? new Vector3(ratio)
            : new Vector3(
                ActiveHandle == GizmoHandle.AxisX ? ratio : 1f,
                ActiveHandle == GizmoHandle.AxisY ? ratio : 1f,
                ActiveHandle == GizmoHandle.AxisZ ? ratio : 1f);

        ApplyFactor(factor);
    }

    /// <inheritdoc/>
    protected override void ClearDragState()
    {
        _scaleTargets.Clear();
        _constraintAxis = Vector3.Zero;
        _constraintNormal = Vector3.Zero;
        _uniformDirection = Vector3.Zero;
        _grabPoint = Vector3.Zero;
        _grabOffset = 0f;
        _grabAxisLength = 0f;
        _appliedFactor = Vector3.One;
    }

    /// <inheritdoc/>
    protected override void DrawHandles(DebugDraw output, in GizmoGeometry geometry, GizmoHandle highlighted) =>
        ScaleGizmoRenderer.Draw(output, in geometry, highlighted);

    /// <summary>
    /// The factor the cursor is currently asking for. The two handle families
    /// map it differently, and both maps are pure functions of the cursor that
    /// return exactly 1 at the grab point — which is what makes a drag that comes
    /// back where it started leave the object at exactly its original size.
    /// </summary>
    /// <remarks>
    /// <b>An axis handle is a ratio</b> of the cursor's distance from the pivot
    /// along that axis to the distance it was grabbed at: pull the cube twice as
    /// far out as it started and the object is twice as big, which is the
    /// mapping that makes the handle feel attached to the object's edge.
    /// <para>
    /// <b>The uniform handle is linear instead</b>, because its cube sits ON the
    /// pivot: a ratio against a grab offset of nearly zero would divide by noise
    /// and send the size to infinity on the first pixel of movement. So it reads
    /// the cursor's travel along a frozen up-and-right diagonal, measured in
    /// units of the gizmo's own on-screen length — one gizmo-length up and right
    /// doubles it, half a gizmo-length down and left halves it, and a full one
    /// down and left runs into <see cref="MinimumFactor"/>.
    /// </para>
    /// </remarks>
    private bool TryComputeRatio(in Ray3 ray, out float ratio)
    {
        if (GizmoHandles.IsAxis(ActiveHandle))
        {
            if (!TryProjectOntoAxis(in ray, out float offset))
            {
                ratio = 1f;
                return false;
            }

            ratio = offset / _grabOffset;
            return true;
        }

        if (!TryProjectOntoViewPlane(in ray, out Vector3 point))
        {
            ratio = 1f;
            return false;
        }

        ratio = 1f + Vector3.Dot(point - _grabPoint, _uniformDirection) / _grabAxisLength;
        return true;
    }

    private bool TryProjectOntoAxis(in Ray3 ray, out float offset)
    {
        if (!GizmoMath.TryClosestPointOnLine(in ray, GrabPivot, _constraintAxis, out Vector3 onAxis))
        {
            offset = 0f;
            return false;
        }

        offset = Vector3.Dot(onAxis - GrabPivot, _constraintAxis);
        return true;
    }

    private bool TryProjectOntoViewPlane(in Ray3 ray, out Vector3 point)
    {
        if (!GizmoMath.TryRayPlane(in ray, GrabPivot, _constraintNormal, out float distance))
        {
            point = Vector3.Zero;
            return false;
        }

        point = ray.PointAt(distance);
        return true;
    }

    private void ApplyFactor(Vector3 factor)
    {
        // Rebuilding a brush is expensive (see the type remarks), and a drag
        // spends most of its frames at the same snapped factor. Skipping the
        // whole pass when nothing changed also keeps the scene from being
        // dirtied for a value it already holds.
        if (factor == _appliedFactor)
            return;

        _appliedFactor = factor;

        IReadOnlyList<GizmoDragTarget> targets = Targets;
        for (int i = 0; i < targets.Count; i++)
        {
            ScaleTarget target = _scaleTargets[i];

            if (target.Brush is { } brushCommand)
            {
                if (!TryScaleExtents(target.StartBrush!, factor, out Brush? resized))
                    continue; // degenerate result; hold the last good extents

                brushCommand.SetAfter(resized);
                brushCommand.Do(Scene);
                continue;
            }

            if (target.Transform is null)
                continue; // declined at record time; see RecordCommands

            SetLocalTransformCommand transformCommand = target.Transform!;
            Transform after = targets[i].StartLocal;
            // Componentwise, in the node's OWN local axes — never a matrix, so a
            // node's transform can never pick up a shear from this tool.
            after.Scale *= factor;
            transformCommand.SetAfter(after);
            transformCommand.Do(Scene);
        }
    }

    /// <summary>
    /// Whether this node can receive a resize at all: a node carrying a brush
    /// resizes its planes, and a node with no brush anywhere below it resizes
    /// its transform. A node whose subtree holds brushes it does not own itself
    /// has neither door — see the type remarks.
    /// </summary>
    private static bool IsResizable(SceneNode node) =>
        node.Brush is not null || node.SubtreeBrushCount == 0;

    private bool AnyResizableTarget()
    {
        IReadOnlyList<GizmoDragTarget> targets = Targets;
        for (int i = 0; i < targets.Count; i++)
        {
            if (IsResizable(targets[i].Node))
                return true;
        }

        return false;
    }

    // Brush.WithScaledExtents rejects plane sets that stop bounding a volume, and
    // an extreme factor can push two nearly-parallel planes into being duplicates.
    // Mid-drag that is not an error worth tearing the editor down for: hold the
    // last size the user could actually see and let them drag back.
    private static bool TryScaleExtents(Brush start, Vector3 factor, out Brush? resized)
    {
        try
        {
            resized = start.WithScaledExtents(factor);
            return true;
        }
        catch (ArgumentException)
        {
            resized = null;
            return false;
        }
    }
}
