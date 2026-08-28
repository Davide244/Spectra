using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The box a selection occupies, measured in the gizmo's own frame: where a
/// Studio-style gizmo puts its pivot, and how far out each of its handles has to
/// stand to be sitting on a face.
/// </summary>
/// <remarks>
/// <b>Measured in the gizmo frame, not in the world.</b> The handles lie along
/// the frame's axes, so the only extent that means anything to them is the
/// extent along those axes. In world orientation the frame is the world and this
/// is an ordinary world AABB; in local orientation it is the selection's own
/// box, which is exactly the box a user sees around a rotated part.
/// <para>
/// <b>Each node contributes an exact frame-space extent, not eight transformed
/// corners.</b> A node's geometry is an axis-aligned box in its own local space
/// under an affine world matrix, and the half-extent of that image along a unit
/// direction is the sum of the local half-extents weighted by the absolute dot
/// products of that direction with the matrix's three basis rows. That is one
/// transform and nine dot products per node instead of eight transforms and
/// twenty-four, it is exact rather than a bound, and it is the standard
/// oriented-box projection.
/// </para>
/// <para>
/// <b>A node with no geometry still votes, as a point at its own origin.</b> An
/// empty group in a selection has no size, but it has a place, and dropping it
/// from the box would let the gizmo drift away from part of what the user can
/// see is selected.
/// </para>
/// <para>
/// <b>Cost.</b> Linear in the selection, evaluated once per frame per gizmo
/// update, and only for a style that asks (<see cref="GizmoStyle.PivotMode"/> or
/// <see cref="GizmoStyle.HandlesStandOffBounds"/>). It is the same order as the
/// pivot average it replaces, with a larger constant; a selection of many
/// thousands would want this cached against the scene's transform version rather
/// than recomputed, and nothing here prevents that later.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the scene it reads.
/// </para>
/// </remarks>
public static class GizmoSelectionBounds
{
    /// <summary>
    /// Measures the box enclosing <paramref name="nodes"/> in the frame spanned
    /// by the three unit axes, reporting it as frame-space coordinates (each
    /// component is a distance along the matching axis from the world origin).
    /// Returns false, with both corners zeroed, for an empty list.
    /// </summary>
    /// <param name="nodes">The nodes to enclose, usually the selection.</param>
    /// <param name="axisX">The frame's first unit axis.</param>
    /// <param name="axisY">The frame's second unit axis.</param>
    /// <param name="axisZ">The frame's third unit axis.</param>
    /// <param name="min">The low corner, in frame coordinates.</param>
    /// <param name="max">The high corner, in frame coordinates.</param>
    public static bool TryMeasure(
        IReadOnlyList<SceneNode> nodes,
        Vector3 axisX,
        Vector3 axisY,
        Vector3 axisZ,
        out Vector3 min,
        out Vector3 max)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        min = Vector3.Zero;
        max = Vector3.Zero;
        if (nodes.Count == 0)
            return false;

        bool any = false;
        for (int i = 0; i < nodes.Count; i++)
        {
            SceneNode node = nodes[i];
            Vector3 centre;
            Vector3 half;

            if (TryGetLocalBounds(node, out Aabb local))
            {
                Matrix4x4 world = node.WorldMatrix;
                Vector3 localHalf = local.Size * 0.5f;
                Vector3 row0 = new(world.M11, world.M12, world.M13);
                Vector3 row1 = new(world.M21, world.M22, world.M23);
                Vector3 row2 = new(world.M31, world.M32, world.M33);

                centre = Vector3.Transform((local.Min + local.Max) * 0.5f, world);
                half = new Vector3(
                    ExtentAlong(axisX, row0, row1, row2, localHalf),
                    ExtentAlong(axisY, row0, row1, row2, localHalf),
                    ExtentAlong(axisZ, row0, row1, row2, localHalf));
            }
            else
            {
                centre = node.WorldPosition;
                half = Vector3.Zero;
            }

            var frameCentre = new Vector3(
                Vector3.Dot(centre, axisX),
                Vector3.Dot(centre, axisY),
                Vector3.Dot(centre, axisZ));

            Vector3 nodeMin = frameCentre - half;
            Vector3 nodeMax = frameCentre + half;

            if (any)
            {
                min = Vector3.Min(min, nodeMin);
                max = Vector3.Max(max, nodeMax);
            }
            else
            {
                min = nodeMin;
                max = nodeMax;
                any = true;
            }
        }

        return any;
    }

    /// <summary>
    /// The world point a set of frame coordinates stands for. The frame's axes
    /// are orthonormal and its origin is the world's, so the reconstruction is
    /// exact.
    /// </summary>
    public static Vector3 ToWorld(Vector3 frameCoordinates, Vector3 axisX, Vector3 axisY, Vector3 axisZ) =>
        axisX * frameCoordinates.X + axisY * frameCoordinates.Y + axisZ * frameCoordinates.Z;

    /// <summary>
    /// The local-space box a node's own geometry occupies: a brush's plane
    /// bounds, or a mesh's bounds. Returns false for a node with neither.
    /// </summary>
    /// <remarks>
    /// <b>The one definition of "this node has a measurable shape"</b>, shared
    /// with <see cref="ResizeMath.TryMeasure"/> so a node cannot be big enough to
    /// put a handle on and too small to resize, or the other way round.
    /// <para>
    /// A brush wins over a mesh on the same node: the brush is the authoring
    /// primitive, and a brush node's mesh (if it somehow has one) is derived
    /// decoration. A mesh is trusted when it says its bounds are real
    /// (<c>Mesh.HasLocalBounds</c>), which is computed off the upload stream for
    /// every mesh whether or not it kept a CPU copy of its vertices.
    /// </para>
    /// </remarks>
    public static bool TryGetLocalBounds(SceneNode node, out Aabb bounds)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Brush is { } brush)
        {
            bounds = brush.LocalBounds;
            return true;
        }

        if (node.MeshRenderer is { } renderer && renderer.Mesh.HasLocalBounds)
        {
            bounds = renderer.Mesh.LocalBounds;
            return true;
        }

        bounds = default;
        return false;
    }

    // The half-extent, along one unit direction, of a local box with the given
    // half-extents under a world matrix whose basis rows are given.
    private static float ExtentAlong(
        Vector3 direction, Vector3 row0, Vector3 row1, Vector3 row2, Vector3 localHalf) =>
        MathF.Abs(Vector3.Dot(direction, row0)) * localHalf.X +
        MathF.Abs(Vector3.Dot(direction, row1)) * localHalf.Y +
        MathF.Abs(Vector3.Dot(direction, row2)) * localHalf.Z;
}
