using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The arithmetic behind a fixed-increment resize: how big a node currently is
/// in world units, what scale factor turns that into a requested world size, and
/// how far the node has to move so the face opposite the dragged one stays
/// planted.
/// </summary>
/// <remarks>
/// <b>Separate from <see cref="ScaleGizmo"/> because it is the part with a
/// right answer.</b> The tool owns the gesture — constraints, capture, undo —
/// while everything here is a pure function of numbers a test can supply
/// directly, which is what makes "one notch is one world unit at any size"
/// checkable without a viewport.
/// <para>
/// <b>Size is measured, not assumed.</b> A brush's size is its local plane
/// bounds; a mesh node's is its mesh bounds times the node's world scale. Both
/// are world units, so the same increment means the same thing for both — the
/// claim the whole fixed-increment design rests on. A node with neither (an
/// empty group, a mesh whose CPU positions were never kept) has no size to
/// resize, and <see cref="TryMeasure"/> says so rather than inventing one; the
/// tool falls back to a proportional drag there and says so out loud.
/// </para>
/// </remarks>
public static class ResizeMath
{
    /// <summary>
    /// The smallest world size that still counts as measurable. Below this the
    /// factor needed for a given size change explodes, and the object is
    /// visually a point or a sheet anyway.
    /// </summary>
    public const float MinimumMeasurableSize = 1e-4f;

    /// <summary>
    /// Measures <paramref name="node"/>'s world-space size along its own three
    /// local axes, and reports the local bounds a resize is anchored against.
    /// Returns false — with <paramref name="worldSize"/> zeroed — for a node with
    /// no geometry at all.
    /// </summary>
    /// <param name="node">The node to measure.</param>
    /// <param name="worldSize">
    /// Size in world units along the node's local x/y/z. A component can still be
    /// zero for a flat object (a quad mesh); callers check per axis.
    /// </param>
    /// <param name="localBounds">
    /// The node's local bounds. Its two corners are the coordinates of the faces
    /// a resize plants: the minimum for a grow along +axis, the maximum for one
    /// along −axis. Default when there is nothing to measure.
    /// </param>
    /// <remarks>
    /// <b>Both corners, because a resize can be grabbed by either face.</b> A
    /// face-anchored grow along +x plants the −x face and a grow along −x plants
    /// the +x one, so a caller that only ever received the minimum could only
    /// ever anchor one way, which is the same restriction that made half of every
    /// object unreachable before the negative handles existed.
    /// <para>
    /// What counts as measurable geometry is
    /// <see cref="GizmoSelectionBounds.TryGetLocalBounds"/>'s single definition,
    /// shared with the box the handles stand on.
    /// </para>
    /// </remarks>
    public static bool TryMeasure(SceneNode node, out Vector3 worldSize, out Aabb localBounds)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!GizmoSelectionBounds.TryGetLocalBounds(node, out localBounds))
        {
            worldSize = Vector3.Zero;
            return false;
        }

        worldSize = localBounds.Size * WorldScaleOf(node);
        return true;
    }

    /// <summary>
    /// The node's world scale — the length of each row of its world matrix's
    /// basis, which is the factor taking one local unit along that axis to world
    /// units.
    /// </summary>
    /// <remarks>
    /// Row lengths rather than <see cref="Matrix4x4.Decompose"/>: decomposition
    /// can fail outright (a zero scale somewhere in the chain) and would then
    /// have to guess, while a row length is always defined and is exactly the
    /// quantity wanted here.
    /// </remarks>
    public static Vector3 WorldScaleOf(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Matrix4x4 m = node.WorldMatrix;
        return new Vector3(
            new Vector3(m.M11, m.M12, m.M13).Length(),
            new Vector3(m.M21, m.M22, m.M23).Length(),
            new Vector3(m.M31, m.M32, m.M33).Length());
    }

    /// <summary>
    /// The multiplier that turns a current world size into
    /// <c>startWorldSize + sizeChange</c>, clamped so the result can neither
    /// collapse nor invert.
    /// </summary>
    /// <param name="startWorldSize">The size the drag was grabbed at, in world units.</param>
    /// <param name="sizeChange">The requested change, in world units — the snapped increment.</param>
    /// <param name="minimumSize">The smallest world size a resize may produce.</param>
    /// <param name="minimumFactor">Lower clamp on the returned factor.</param>
    /// <param name="maximumFactor">Upper clamp on the returned factor.</param>
    /// <remarks>
    /// This is the whole fix in one line: the factor is <em>derived</em> from an
    /// absolute size, so a one-unit change stays a one-unit change whatever the
    /// object measures, instead of the size change being derived from a factor
    /// and therefore scaling with the object.
    /// <para>
    /// <b>Why a factor and not an absolute-extents API on <c>Brush</c>.</b> An
    /// exact world size is what the caller asks for, so a
    /// <c>Brush.WithExtents(size)</c> looks tempting — but a brush is a set of
    /// half-spaces, not a box: "extents" are only defined through its derived
    /// bounds, and scaling happens about the brush's local origin, which for
    /// non-centred bounds is not the box centre. Such an API would have to invent
    /// a centring convention that the brush deliberately does not have. Deriving
    /// the factor here and handing it to <c>Brush.WithScaledExtents</c> — the
    /// exact half-space image of the solid under a diagonal map, wedges included —
    /// keeps the one meaning the geometry already has, and one division is the
    /// entire difference.
    /// </para>
    /// </remarks>
    public static float FactorForSizeChange(
        float startWorldSize,
        float sizeChange,
        float minimumSize,
        float minimumFactor,
        float maximumFactor)
    {
        if (startWorldSize <= MinimumMeasurableSize)
            return 1f;

        float wanted = MathF.Max(startWorldSize + sizeChange, minimumSize);
        return Math.Clamp(wanted / startWorldSize, minimumFactor, maximumFactor);
    }

    /// <summary>
    /// How far the node must move, along one of its own local axes and in its
    /// <em>parent's</em> units, for the face at <paramref name="localAnchor"/> to
    /// stay exactly where it was while the object is scaled by
    /// <paramref name="factor"/> about the node's origin.
    /// </summary>
    /// <param name="localAnchor">
    /// The anchored face's coordinate in the node's local frame: the local
    /// bounds' minimum when the handle being dragged is on the positive side of
    /// the axis, and its maximum when the handle is on the negative side. The
    /// anchored face is always the one opposite the handle.
    /// </param>
    /// <param name="localScale">The node's own local scale on that axis (parent units per local unit).</param>
    /// <param name="factor">The factor the axis is being scaled by.</param>
    /// <remarks>
    /// The face sits at <c>localScale · localAnchor</c> in parent units and moves
    /// to <c>localScale · localAnchor · factor</c>, so putting it back costs
    /// <c>localScale · localAnchor · (1 − factor)</c>. For the centred bounds
    /// every box brush and most props have, that works out to exactly half the
    /// size change — which is why a face-anchored notch moves the node by half an
    /// increment and the far face not at all.
    /// </remarks>
    public static float AnchorShift(float localAnchor, float localScale, float factor) =>
        localScale * localAnchor * (1f - factor);
}
