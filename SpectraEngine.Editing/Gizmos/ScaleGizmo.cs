using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The resize tool: a cube-capped handle per axis direction the style offers,
/// plus a uniform centre cube, turning a drag into a <b>world-unit change of the
/// object's size</b> — applied to a mesh node's transform scale, and to a brush
/// node's <em>local plane extents</em>.
/// </summary>
/// <remarks>
/// <b>The drag quantity is a size in world units, not a multiplier, and that is
/// the difference the whole tool is built around.</b> A factor drag makes the
/// world-space change per snap notch proportional to whatever the object already
/// measures — at a 0.25 factor step a 10-unit brush jumps 2.5 units per notch
/// and a 0.4-unit brush jumps 0.1 — so the increment a user sets means a
/// different thing for every object they click. Here the cursor's travel along
/// the constraint IS the size change (1:1 in world units), the snap increment is
/// world units (<see cref="ResizeSnapSettings"/>), and the per-node scale factor
/// is <em>derived</em> from each node's own measured size
/// (<see cref="ResizeMath.FactorForSizeChange"/>). One notch is one increment on
/// every object in the selection, at any size.
/// <para>
/// <b>What a resize holds still is the style's decision, and the handle roster
/// travels with it.</b> In <see cref="GizmoStyle.Studio"/> a resize is
/// face-anchored: growing along +x moves the +x face out by exactly the
/// increment and leaves the −x face planted, so the node's position shifts by
/// half the increment. That only works because the style offers a handle on
/// <em>every</em> face; with handles on the positive ends alone, an anchored
/// resize can only ever move three of an object's six faces, and "grow this
/// leftwards" has no gesture at all. In <see cref="GizmoStyle.Classic"/> the
/// resize is symmetric about the pivot instead, both faces moving by half the
/// increment each, which is why three handles are enough there.
/// <see cref="SymmetricModifier"/> asks for the other one for the duration of a
/// gesture, whichever way round the style has it. The uniform centre cube is
/// always symmetric — it drags no single face — and grows the object's largest
/// dimension by the increment, scaling the other two in proportion.
/// </para>
/// <para>
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
///     the successor onto the node through <see cref="SetBrushCommand"/>. Its
///     transform receives the face-anchoring <em>translation</em> and nothing
///     else — a translation keeps the placement rigid, a scale would not.
///   </description></item>
///   <item><description>
///     A node with <em>no brush anywhere in its subtree</em> is resized by
///     writing <c>LocalScale</c> (and the same anchoring translation) through
///     <see cref="SetLocalTransformCommand"/>, which is what scale means for a
///     mesh. The factor comes from the mesh's own bounds, so the requested world
///     size is what the object ends up measuring.
///   </description></item>
///   <item><description>
///     A node that carries no brush of its own but has brush
///     <em>descendants</em> is refused: it is skipped, and a gesture in which
///     no selected node is resizable never starts at all.
///   </description></item>
/// </list>
/// </para>
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
/// <b>A node with no measurable size is the one proportional case left.</b> An
/// empty group, or a mesh that reports no bounds of its own, has no world
/// size to add an increment to; those targets fall back to the old proportional
/// mapping (one gizmo length of travel doubles them, snapped on a
/// <see cref="ProportionalFactorIncrement"/> factor ladder). It is reported
/// through <see cref="ProportionalFallbackCount"/> and logged once per gesture
/// through <see cref="Logger"/> rather than silently looking like a fixed
/// increment that is not one.
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
/// <b>Each node resizes along its OWN axes and lands on its OWN increment.</b>
/// A multi-selection never shares one factor — that is precisely what made the
/// world-space step size-dependent — so a 0.4-unit brush and a 10-unit brush
/// dragged together both grow by exactly one increment. Nodes whose rotation
/// differs from the gizmo's frame receive the change along their own axes.
/// </para>
/// <para>
/// <b>Cost.</b> Rebuilding a brush re-runs its plane validation and face clipping
/// and therefore allocates — a resize drag of a brush node is the one gesture in
/// the editing layer that is not per-frame allocation-free, and it cannot be:
/// brushes are immutable by design. The tool only rebuilds when the (snapped)
/// size actually changed since the previous frame, so a snapped drag rebuilds
/// once per increment rather than once per frame, and a drag that has not left
/// its starting size rebuilds nothing at all. That holds because the only other
/// quantity the pass keys on — the proportional fallback factor, which does move
/// continuously with the cursor — is computed <em>only</em> when the gesture
/// actually contains an unmeasurable target
/// (<see cref="ProportionalFallbackCount"/>). A selection that mixes one in pays
/// the fallback ladder's rebuild rate for the whole selection; that is the cost
/// of the fallback, not of the ordinary case.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only. A grab allocates one or two commands per
/// node.
/// </para>
/// </remarks>
public sealed class ScaleGizmo : GizmoTool
{
    /// <summary>
    /// The smallest world size a resize will produce along an axis. Zero would
    /// collapse a mesh to a plane and is rejected outright by
    /// <see cref="Brush.WithScaledExtents"/>; negative would mirror the object,
    /// turning a brush inside out.
    /// </summary>
    public const float MinimumSize = 0.01f;

    /// <summary>
    /// The smallest factor a drag will apply to a node. A clamp on the
    /// <em>derived</em> factor, so a size change that would invert or collapse a
    /// tiny object stops here as well as at <see cref="MinimumSize"/>.
    /// </summary>
    public const float MinimumFactor = 0.01f;

    /// <summary>
    /// The largest factor a single drag will apply. Not a safety limit so much
    /// as a sanity one: past this the object is a thousand times its original
    /// size and every further pixel multiplies it again.
    /// </summary>
    public const float MaximumFactor = 1000f;

    /// <summary>
    /// The snap step used for the proportional fallback — targets with no
    /// measurable world size, where there is no absolute quantity to quantise.
    /// A factor ladder, in the same spirit as the increment this tool used to
    /// snap for everything.
    /// </summary>
    public const float ProportionalFactorIncrement = 0.25f;

    // What one node needs for the whole gesture. Transform is non-null for every
    // node this tool accepts (a brush node gets one too — for the face-anchoring
    // TRANSLATION, never a scale); Brush is non-null only for a brush node. BOTH
    // are null for a node this tool declines to resize, whose slot exists only to
    // keep the list index-aligned with Targets.
    //
    // LocalAnchor is the corner a face-anchored resize plants, resolved ONCE at
    // the grab: which of the two corners it is depends both on which end of the
    // axis was grabbed and on whether this node's own axis points the same way
    // the handle does. LocalCentre is what a symmetric resize holds instead.
    private readonly record struct ResizeTarget(
        SetLocalTransformCommand? Transform,
        SetBrushCommand? Brush,
        Brush? StartBrush,
        Vector3 StartSize,
        Vector3 LocalAnchor,
        Vector3 LocalCentre);

    private readonly List<ResizeTarget> _resizeTargets = [];

    private Vector3 _constraintAxis;
    private Vector3 _constraintNormal;
    private Vector3 _uniformDirection;
    private Vector3 _grabPoint;
    private Vector3 _axisMask;
    private float _grabOffset;
    private float _grabAxisLength;

    // The state the last ApplyDrag wrote, so a frame that asks for the same
    // thing can skip the whole pass (a brush rebuild is expensive). The
    // proportional term only ever leaves 1 when the gesture actually has an
    // unmeasurable target — otherwise it is a constant and cannot spuriously
    // defeat the skip. See ApplyDrag.
    private float _appliedSizeChange;
    private float _appliedProportional = 1f;
    private bool _appliedSymmetric;
    private bool _appliedEdit;

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

    /// <summary>Size-snapping configuration for drags. See <see cref="ResizeSnapSettings"/>.</summary>
    public ResizeSnapSettings Snap { get; } = new();

    /// <summary>
    /// The modifier that asks for the anchoring the style is not doing: symmetric
    /// where the style is face-anchored (both faces move and the node stays put),
    /// face-anchored where the style is symmetric (the grabbed face moves and the
    /// opposite one is planted). The snapped increment is the total size change
    /// either way. <see cref="KeyModifiers.None"/> removes the option.
    /// </summary>
    /// <remarks>
    /// Shift, because Alt already inverts snapping for all three tools and
    /// Shift/Control only mean add-to-selection and toggle-selection at the
    /// instant of a press — by the time a drag is running they are free.
    /// </remarks>
    public KeyModifiers SymmetricModifier { get; set; } = KeyModifiers.Shift;

    /// <summary>
    /// Optional sink for the one thing this tool has to say out loud: that a
    /// target had no measurable size and is being resized proportionally rather
    /// than by the fixed increment. Null (the default) simply keeps quiet — the
    /// count is still available on <see cref="ProportionalFallbackCount"/>.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// How many of the last gesture's targets fall back to a proportional
    /// factor because they have no measurable world size along the dragged axes.
    /// Zero for the ordinary brush-and-mesh case.
    /// </summary>
    public int ProportionalFallbackCount { get; private set; }

    /// <summary>
    /// The world-unit size change the current drag is asking for — of the dragged
    /// axis, or of the largest dimension for the uniform handle. Zero when no
    /// drag is in progress, and after a snapped drag it is always a whole
    /// multiple of <see cref="Snap"/>'s increment.
    /// </summary>
    public float DragSizeChange => _appliedSizeChange;

    /// <inheritdoc/>
    protected override bool HasEdit => _appliedEdit;

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

        // The proportional fallback measures travel against this, so a degenerate
        // gizmo (a viewport with no height) would divide by zero.
        if (geometry.AxisLength <= 0f)
            return false;

        _constraintAxis = geometry.Axis(ActiveHandle);
        _constraintNormal = geometry.ViewNormal;
        _grabAxisLength = geometry.AxisLength;
        _axisMask = AxisMaskOf(ActiveHandle);

        // "Up and to the right makes it bigger" — the direction every uniform
        // resize handle in every editor grows along. Frozen at the grab so a
        // camera that turns mid-drag does not re-point it.
        _uniformDirection = Vector3.Normalize(geometry.ViewRight + geometry.ViewUp);

        if (GizmoHandles.IsAxis(ActiveHandle))
        {
            if (!TryProjectOntoAxis(in ray, out float offset))
                return false;

            _grabOffset = offset;
        }
        else
        {
            if (!TryProjectOntoViewPlane(in ray, out Vector3 grabPoint))
                return false;

            _grabPoint = grabPoint;
        }

        _appliedSizeChange = 0f;
        _appliedProportional = 1f;
        _appliedSymmetric = false;
        _appliedEdit = false;
        return true;
    }

    /// <inheritdoc/>
    protected override void RecordCommands()
    {
        _resizeTargets.Clear();
        ProportionalFallbackCount = 0;

        IReadOnlyList<GizmoDragTarget> targets = Targets;
        for (int i = 0; i < targets.Count; i++)
        {
            SceneNode node = targets[i].Node;

            // The routing decision, made once per gesture: a brush node's size
            // lives in its planes, a brush-free node's lives in its transform,
            // and a node with brush DESCENDANTS has nowhere to put it at all.
            if (node.Brush is null && node.SubtreeBrushCount > 0)
            {
                // Declined: writing LocalScale here would make every brush below
                // this node non-rigid and stall the static-world compile for the
                // whole scene. Hold the slot so the list stays index-aligned.
                _resizeTargets.Add(default);
                continue;
            }

            if (!ResizeMath.TryMeasure(node, out Vector3 size, out Aabb bounds) || !IsMeasurable(size))
            {
                size = Vector3.Zero;
                bounds = default;
                ProportionalFallbackCount++;
                Logger?.LogWarning(
                    "Resize: node '{Node}' has no measurable size along the dragged axes; " +
                    "falling back to a proportional ×{Increment} factor step instead of the " +
                    "{SizeIncrement}-unit resize increment",
                    node.Name, ProportionalFactorIncrement, Snap.Increment);
            }

            // Every accepted node gets a transform command. For a mesh it carries
            // the scale AND the anchoring shift; for a brush it carries the shift
            // alone — scale on a brush node is exactly what must never happen.
            var transform = new SetLocalTransformCommand(node.Id, targets[i].StartLocal, targets[i].StartLocal)
            {
                Name = TransactionName,
            };
            Undo.Record(transform);

            SetBrushCommand? brushCommand = null;
            if (node.Brush is { } brush)
            {
                brushCommand = new SetBrushCommand(node.Id, brush, brush) { Name = TransactionName };
                Undo.Record(brushCommand);
            }

            _resizeTargets.Add(new ResizeTarget(
                transform, brushCommand, node.Brush, size,
                ResolveAnchor(node, in bounds), (bounds.Min + bounds.Max) * 0.5f));
        }
    }

    /// <inheritdoc/>
    protected override void ApplyDrag(in EditorInputFrame frame, in Ray3 ray)
    {
        // A frame whose ray cannot be projected onto the constraint holds the
        // last size: the result is recomputed from the grab every frame, so a
        // skipped one leaves no residue.
        if (!TryComputeTravel(in ray, out float travel))
            return;

        // The style decides which way round the default is, and the modifier asks
        // for the other one. The uniform handle is excluded because it drags no
        // single face: its travel is already the whole size change, and doubling
        // it would make one uniform notch two.
        bool inverted = SymmetricModifier != KeyModifiers.None &&
            (frame.Modifiers & SymmetricModifier) == SymmetricModifier;
        bool symmetric = ActiveHandle != GizmoHandle.Screen &&
            (Style.FaceAnchoredResize ? inverted : !inverted);

        // Symmetric moves BOTH faces, so the cursor's travel is half the size
        // change. Doubling before the snap is what keeps the dragged face under
        // the cursor while the snapped quantity stays the size.
        float requested = symmetric ? travel * 2f : travel;

        bool snapping = Snap.IsActiveWith(frame.Modifiers);
        float sizeChange = snapping ? Snap.SnapScalar(requested) : requested;

        // The fallback for targets with no measurable size: one gizmo length of
        // travel doubles them, on a factor ladder of its own.
        //
        // Computed ONLY when some target in this gesture actually reads it. It is
        // a function of the RAW cursor travel on a ladder of its own, so it moves
        // several times within a single resize notch; feeding it to Apply's
        // change-detection when nothing consults it turned "rebuild once per
        // increment" into "rebuild whenever the fallback ladder happens to tick",
        // which for a brush node means a full immutable rebuild, a carve-cache
        // invalidation and another async CSG recompile per tick — all producing
        // geometry bit-identical to what the node already holds. The count is
        // fixed for the gesture (RecordCommands runs once, at the grab), and a
        // target consults the fallback exactly when it was counted into it, so
        // this is the precise gate and not an approximation of one.
        float proportional = 1f;
        if (ProportionalFallbackCount > 0)
        {
            proportional = 1f + travel / _grabAxisLength;
            if (snapping)
            {
                proportional = MathF.Round(
                    proportional / ProportionalFactorIncrement, MidpointRounding.AwayFromZero) *
                    ProportionalFactorIncrement;
            }
            proportional = Math.Clamp(proportional, MinimumFactor, MaximumFactor);
        }

        Apply(sizeChange, proportional, symmetric);
    }

    /// <inheritdoc/>
    protected override void ClearDragState()
    {
        _resizeTargets.Clear();
        _constraintAxis = Vector3.Zero;
        _constraintNormal = Vector3.Zero;
        _uniformDirection = Vector3.Zero;
        _grabPoint = Vector3.Zero;
        _axisMask = Vector3.Zero;
        _grabOffset = 0f;
        _grabAxisLength = 0f;
        _appliedSizeChange = 0f;
        _appliedProportional = 1f;
        _appliedSymmetric = false;
        _appliedEdit = false;
    }

    /// <inheritdoc/>
    protected override void DrawHandles(DebugDraw output, in GizmoGeometry geometry, GizmoHandle highlighted) =>
        ScaleGizmoRenderer.Draw(output, in geometry, highlighted);

    /// <summary>
    /// How far the cursor has travelled along the constraint since the grab, in
    /// <b>world units</b> — which is the size change the drag is asking for.
    /// Both handle families map to it as a pure function of the cursor that
    /// returns exactly zero at the grab point, so a drag that comes back where it
    /// started leaves the object at exactly its original size.
    /// </summary>
    /// <remarks>
    /// <b>An axis handle measures along its own DIRECTION</b>, negative handles
    /// included, so the dragged face tracks the cursor one-for-one in world space
    /// — the mapping that makes the handle feel attached to the face it is
    /// moving, and the reason the drag has no ratio in it any more. Because the
    /// direction carries the sign, pulling a −x handle outward (which is toward
    /// −x) reads as positive travel and therefore as growth, exactly as pulling
    /// the +x handle outward does.
    /// <para>
    /// <b>The uniform handle measures along a frozen up-and-right diagonal</b>,
    /// because its cube sits ON the pivot and has no axis of its own; the travel
    /// there is the growth of the object's largest dimension.
    /// </para>
    /// </remarks>
    private bool TryComputeTravel(in Ray3 ray, out float travel)
    {
        if (GizmoHandles.IsAxis(ActiveHandle))
        {
            if (!TryProjectOntoAxis(in ray, out float offset))
            {
                travel = 0f;
                return false;
            }

            travel = offset - _grabOffset;
            return true;
        }

        if (!TryProjectOntoViewPlane(in ray, out Vector3 point))
        {
            travel = 0f;
            return false;
        }

        travel = Vector3.Dot(point - _grabPoint, _uniformDirection);
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

    /// <summary>
    /// Writes one frame of the drag: per target, the factor its own measured size
    /// needs to grow by <paramref name="sizeChange"/> world units, plus the
    /// translation that keeps the opposite face planted.
    /// </summary>
    private void Apply(float sizeChange, float proportionalFactor, bool symmetric)
    {
        // Rebuilding a brush is expensive (see the type remarks), and a snapped
        // drag spends most of its frames asking for the same size. Skipping the
        // whole pass when nothing changed also keeps the scene from being
        // dirtied for a value it already holds.
        if (sizeChange == _appliedSizeChange &&
            proportionalFactor == _appliedProportional &&
            symmetric == _appliedSymmetric)
        {
            return;
        }

        _appliedSizeChange = sizeChange;
        _appliedProportional = proportionalFactor;
        _appliedSymmetric = symmetric;

        bool edited = false;
        IReadOnlyList<GizmoDragTarget> targets = Targets;
        for (int i = 0; i < targets.Count; i++)
        {
            ResizeTarget target = _resizeTargets[i];
            if (target.Transform is null)
                continue; // declined at record time; see RecordCommands

            Transform start = targets[i].StartLocal;
            Vector3 factor = SolveFactor(in target, sizeChange, proportionalFactor);

            // The uniform handle drags no single face, so it is symmetric by
            // construction whatever the style's default is for the axis handles.
            Vector3 shift = SolveAnchorShift(
                in target, start.Scale, factor, symmetric || ActiveHandle == GizmoHandle.Screen);

            if (target.Brush is { } brushCommand)
            {
                if (!TryScaleExtents(target.StartBrush!, factor, out Brush? resized))
                    continue; // degenerate result; hold the last good extents

                brushCommand.SetAfter(resized);
                brushCommand.Do(Scene);
            }
            else
            {
                // Componentwise, in the node's OWN local axes — never a matrix, so
                // a node's transform can never pick up a shear from this tool.
                start.Scale *= factor;
            }

            // The anchoring shift is expressed along the node's local axes, so it
            // rotates into the parent's frame by the node's own rotation.
            if (shift != Vector3.Zero)
                start.Position += Vector3.Transform(shift, start.Rotation);

            target.Transform.SetAfter(start);
            target.Transform.Do(Scene);

            edited |= factor != Vector3.One || shift != Vector3.Zero;
        }

        _appliedEdit = edited;
    }

    /// <summary>
    /// The per-axis factor one target needs: derived from its own measured size
    /// wherever there is one, and from the proportional fallback where there is
    /// not.
    /// </summary>
    private Vector3 SolveFactor(in ResizeTarget target, float sizeChange, float proportionalFactor)
    {
        if (ActiveHandle == GizmoHandle.Screen)
        {
            // Uniform: the largest dimension grows by the increment and the other
            // two follow it, which is the only reading of "one uniform notch"
            // that is a single world-unit quantity.
            float reference = MathF.Max(target.StartSize.X, MathF.Max(target.StartSize.Y, target.StartSize.Z));
            float uniform = reference > ResizeMath.MinimumMeasurableSize
                ? ResizeMath.FactorForSizeChange(reference, sizeChange, MinimumSize, MinimumFactor, MaximumFactor)
                : proportionalFactor;
            return new Vector3(uniform);
        }

        return new Vector3(
            AxisFactor(_axisMask.X, target.StartSize.X, sizeChange, proportionalFactor),
            AxisFactor(_axisMask.Y, target.StartSize.Y, sizeChange, proportionalFactor),
            AxisFactor(_axisMask.Z, target.StartSize.Z, sizeChange, proportionalFactor));
    }

    private static float AxisFactor(float mask, float startSize, float sizeChange, float proportionalFactor)
    {
        if (mask == 0f)
            return 1f;

        return startSize > ResizeMath.MinimumMeasurableSize
            ? ResizeMath.FactorForSizeChange(startSize, sizeChange, MinimumSize, MinimumFactor, MaximumFactor)
            : proportionalFactor;
    }

    /// <summary>
    /// The translation that keeps the face opposite the dragged handle exactly
    /// where it was. Zero for the uniform handle, which drags no single face, and
    /// zero on every axis the handle does not drive.
    /// </summary>
    private static Vector3 SolveAnchorShift(
        in ResizeTarget target, Vector3 localScale, Vector3 factor, bool symmetric)
    {
        // A symmetric resize holds the object's own CENTRE, not its origin. The
        // two coincide for the centred bounds a box brush has, and for anything
        // else scaling about the origin moves the two faces by different amounts:
        // the size still lands on the increment, but the face under the cursor
        // does not move by half of it, so the handle drifts away from the
        // pointer. Holding the centre is also what makes the drag's doubled
        // travel exactly right rather than right-for-centred-geometry.
        Vector3 anchor = symmetric ? target.LocalCentre : target.LocalAnchor;

        return new Vector3(
            ResizeMath.AnchorShift(anchor.X, localScale.X, factor.X),
            ResizeMath.AnchorShift(anchor.Y, localScale.Y, factor.Y),
            ResizeMath.AnchorShift(anchor.Z, localScale.Z, factor.Z));
    }

    /// <summary>
    /// The local corner a face-anchored drag plants for one node: the face
    /// opposite the handle, expressed in that node's own frame.
    /// </summary>
    /// <remarks>
    /// <b>The handle's sign is a fact about the GIZMO's frame and the anchor is a
    /// coordinate in the NODE's</b>, so the two have to be reconciled per node
    /// rather than assumed equal. Each node is resized along its own axes (see
    /// the type remarks), and a selection member turned more than a right angle
    /// away from the gizmo's frame has its local +x pointing the way the handle
    /// does not. Reading the handle's sign straight would then plant the face on
    /// the side the user is dragging TOWARD, so one member of the selection would
    /// grow left while the rest grew right, from one drag of one handle.
    /// <para>
    /// Resolved once per gesture, at the grab: the node's rotation relative to
    /// the frozen constraint cannot change during a resize, and doing it per
    /// frame would be three dot products per node per frame for an answer that
    /// never moves.
    /// </para>
    /// </remarks>
    private Vector3 ResolveAnchor(SceneNode node, in Aabb bounds)
    {
        Matrix4x4 world = node.WorldMatrix;
        return new Vector3(
            AnchorOn(new Vector3(world.M11, world.M12, world.M13), bounds.Min.X, bounds.Max.X),
            AnchorOn(new Vector3(world.M21, world.M22, world.M23), bounds.Min.Y, bounds.Max.Y),
            AnchorOn(new Vector3(world.M31, world.M32, world.M33), bounds.Min.Z, bounds.Max.Z));

        // The constraint already carries the handle's direction, so this one test
        // answers both questions at once: which end was grabbed, and which way
        // this node's own axis points.
        float AnchorOn(Vector3 nodeAxis, float min, float max) =>
            Vector3.Dot(nodeAxis, _constraintAxis) >= 0f ? min : max;
    }

    /// <summary>
    /// Whether a measured size is usable on the axes this gesture drives — all
    /// three for the uniform handle (where the largest one is what counts), the
    /// dragged one otherwise.
    /// </summary>
    private bool IsMeasurable(Vector3 size)
    {
        if (ActiveHandle == GizmoHandle.Screen)
        {
            return MathF.Max(size.X, MathF.Max(size.Y, size.Z)) > ResizeMath.MinimumMeasurableSize;
        }

        // The mask is a unit axis, so the dot product IS the dragged component.
        return Vector3.Dot(size, _axisMask) > ResizeMath.MinimumMeasurableSize;
    }

    // Unsigned, and taken from the handle's AXIS rather than its direction: a −x
    // handle resizes x, exactly as a +x one does, and the direction lives in the
    // travel instead. Reading the raw handle here (and so letting the negative
    // values fall into the uniform default) would turn every negative-face drag
    // into a silent three-axis resize.
    private static Vector3 AxisMaskOf(GizmoHandle handle) => GizmoHandles.PositiveAxis(handle) switch
    {
        GizmoHandle.AxisX => Vector3.UnitX,
        GizmoHandle.AxisY => Vector3.UnitY,
        GizmoHandle.AxisZ => Vector3.UnitZ,
        _ => Vector3.One,
    };

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
