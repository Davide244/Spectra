using SpectraEngine.Core.Scene;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// One node a drag is manipulating, captured at the grab: the node itself, the
/// local transform it started with, where and how it sat in world space, and the
/// matrices that take a world-space answer back into its parent's frame.
/// </summary>
/// <remarks>
/// <b>Every frame of every drag recomputes its result from this, never from the
/// node's current state.</b> Accumulating per-frame deltas would let rounding,
/// snapping, and any frame whose cursor ray could not be projected (a grazing
/// view angle) each leave a permanent residue; recomputing makes the drag a pure
/// function of the current cursor position, so it is exactly reversible and
/// drifts nowhere.
/// <para>
/// The parent matrices are captured once because no recorded node's parent chain
/// moves during a drag: a node whose ancestor was also selected is skipped at
/// capture time (an ancestor's edit already carries it), so every target here is
/// a root of the manipulated set.
/// </para>
/// <para>
/// This holds a live <see cref="SceneNode"/> reference, which is safe precisely
/// because it lives only for the duration of a gesture — the <em>recorded
/// history</em> addresses nodes by <see cref="SceneNode.Id"/>, so it survives a
/// later delete/undo cycle that replaces the instance.
/// </para>
/// </remarks>
/// <param name="Node">The node being manipulated.</param>
/// <param name="StartLocal">Its local transform at the grab.</param>
/// <param name="StartWorldPosition">Its world position at the grab.</param>
/// <param name="StartWorldRotation">Its world rotation at the grab.</param>
/// <param name="ParentWorldInverse">
/// The inverse of the parent's world matrix — turns a world position into a
/// local one. Identity for a root node, and also for the degenerate case of a
/// parent whose world matrix cannot be inverted (a zero scale somewhere up the
/// chain), where treating the parent as world space is the only finite answer.
/// </param>
/// <param name="ParentWorldRotationInverse">
/// The inverse of the parent's world rotation — turns a world rotation into a
/// local one.
/// </param>
public readonly record struct GizmoDragTarget(
    SceneNode Node,
    Transform StartLocal,
    Vector3 StartWorldPosition,
    Quaternion StartWorldRotation,
    Matrix4x4 ParentWorldInverse,
    Quaternion ParentWorldRotationInverse)
{
    /// <summary>
    /// Captures <paramref name="node"/>'s starting state, decomposing its
    /// parent's world matrix into the two inverses a drag needs.
    /// </summary>
    public static GizmoDragTarget Capture(SceneNode node)
    {
        Matrix4x4 parentWorldInverse = Matrix4x4.Identity;
        Quaternion parentRotationInverse = Quaternion.Identity;

        if (node.Parent is { } parent)
        {
            Matrix4x4 parentWorld = parent.WorldMatrix;
            if (!Matrix4x4.Invert(parentWorld, out parentWorldInverse))
                parentWorldInverse = Matrix4x4.Identity; // degenerate parent scale; treat as world space

            // Decompose rather than walking the chain of LocalRotations: an
            // ancestor with non-uniform scale makes the two disagree, and the
            // world matrix is what actually places the node.
            if (Matrix4x4.Decompose(parentWorld, out _, out Quaternion parentRotation, out _))
                parentRotationInverse = Quaternion.Conjugate(parentRotation);
        }

        Matrix4x4 world = node.WorldMatrix;
        Quaternion worldRotation = Matrix4x4.Decompose(world, out _, out Quaternion rotation, out _)
            ? rotation
            : Quaternion.Identity;

        return new GizmoDragTarget(
            node,
            node.LocalTransform,
            world.Translation,
            worldRotation,
            parentWorldInverse,
            parentRotationInverse);
    }
}
