using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Sets a node's local position and rotation to absolute values, remembering
/// the absolute values they had before. This is the workhorse behind the move
/// and rotate gizmos.
/// </summary>
/// <remarks>
/// Scale is deliberately untouched: brush nodes must keep rigid transforms (the
/// static-world compile validates that), and a move gizmo has no business
/// writing scale even on a mesh node. Use
/// <see cref="SetLocalTransformCommand"/> when the whole
/// <see cref="Transform"/> — scale included — is the thing being edited.
/// <para>
/// Both components are always written, so a move-only edit passes the node's
/// current rotation as both before and after; the setter's exact-equality
/// early-out makes that free. Position-only and rotation-only edits have
/// factories (<see cref="Move"/>, <see cref="Rotate"/>) that do exactly this.
/// </para>
/// </remarks>
public sealed class SetTransformCommand : ICoalescingCommand
{
    // The node this command last wrote to. Weak on purpose: it exists only so
    // RollBack can reach a node that left the scene mid-gesture (see
    // IEditorCommand.RollBack), and a command sitting in history must never be
    // the thing that keeps a removed subtree alive. A node nobody else holds a
    // reference to is one whose state nobody can observe, so failing to restore
    // a collected target is unobservable by construction. Allocated once, on the
    // command's first apply, and retargeted in place after that — the per-frame
    // drag path stays allocation-free.
    private WeakReference<SceneNode>? _lastApplied;

    /// <summary>
    /// Creates a command from explicit before/after values. Prefer the
    /// <see cref="Capture"/>/<see cref="Move"/>/<see cref="Rotate"/> factories,
    /// which read the before-state off the node.
    /// </summary>
    public SetTransformCommand(
        Guid nodeId,
        Vector3 beforePosition,
        Quaternion beforeRotation,
        Vector3 afterPosition,
        Quaternion afterRotation)
    {
        NodeId = nodeId;
        BeforePosition = beforePosition;
        BeforeRotation = beforeRotation;
        AfterPosition = afterPosition;
        AfterRotation = afterRotation;
    }

    /// <summary>
    /// Captures <paramref name="node"/>'s current local position and rotation
    /// as the before-state and records the given target values as the
    /// after-state. Call this <em>before</em> applying the edit to the node.
    /// </summary>
    public static SetTransformCommand Capture(SceneNode node, Vector3 afterPosition, Quaternion afterRotation)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new SetTransformCommand(
            node.Id, node.LocalPosition, node.LocalRotation, afterPosition, afterRotation);
    }

    /// <summary>
    /// A position-only edit: rotation is recorded unchanged on both sides.
    /// </summary>
    public static SetTransformCommand Move(SceneNode node, Vector3 afterPosition)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Capture(node, afterPosition, node.LocalRotation);
    }

    /// <summary>
    /// A rotation-only edit: position is recorded unchanged on both sides.
    /// </summary>
    public static SetTransformCommand Rotate(SceneNode node, Quaternion afterRotation)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Capture(node, node.LocalPosition, afterRotation);
    }

    /// <summary>The id of the node this command edits.</summary>
    public Guid NodeId { get; }

    /// <summary>The node's local position before the edit.</summary>
    public Vector3 BeforePosition { get; }

    /// <summary>The node's local rotation before the edit.</summary>
    public Quaternion BeforeRotation { get; }

    /// <summary>
    /// The node's local position after the edit. Mutable through
    /// <see cref="SetAfter"/> and <see cref="TryAbsorb"/> while the command is
    /// still inside an open transaction.
    /// </summary>
    public Vector3 AfterPosition { get; private set; }

    /// <summary>The node's local rotation after the edit.</summary>
    public Quaternion AfterRotation { get; private set; }

    /// <inheritdoc/>
    public string Name { get; init; } = "Transform";

    /// <summary>
    /// Retargets the after-state, keeping the captured before-state. A gizmo
    /// that holds on to its command for the duration of a drag calls this each
    /// frame instead of allocating a fresh command — the zero-allocation
    /// alternative to push-and-absorb.
    /// </summary>
    public void SetAfter(Vector3 position, Quaternion rotation)
    {
        AfterPosition = position;
        AfterRotation = rotation;
    }

    /// <inheritdoc/>
    public void Do(Scene scene) => Apply(scene, AfterPosition, AfterRotation);

    /// <inheritdoc/>
    public void Undo(Scene scene) => Apply(scene, BeforePosition, BeforeRotation);

    /// <inheritdoc/>
    public void RollBack(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (scene.TryFindById(NodeId, out SceneNode? node))
        {
            Write(node, BeforePosition, BeforeRotation);
            return;
        }

        // The node left the scene while the gesture was open. This is the last
        // chance to put it back, so write through the reference this command
        // actually moved rather than shrugging the way Undo does.
        if (_lastApplied is not null && _lastApplied.TryGetTarget(out SceneNode? detached))
            Write(detached, BeforePosition, BeforeRotation);
    }

    /// <inheritdoc/>
    public bool TryAbsorb(IEditorCommand newer)
    {
        if (newer is not SetTransformCommand next || next.NodeId != NodeId)
            return false;

        // Keep this command's before-state (the start of the gesture) and adopt
        // the newest after-state: one entry now spans the whole drag.
        SetAfter(next.AfterPosition, next.AfterRotation);
        return true;
    }

    private void Apply(Scene scene, Vector3 position, Quaternion rotation)
    {
        ArgumentNullException.ThrowIfNull(scene);
        // A missing node is not an error: history behind a still-undone delete
        // legitimately names nodes that are not in the scene right now.
        if (!scene.TryFindById(NodeId, out SceneNode? node))
            return;

        Remember(node);
        Write(node, position, rotation);
    }

    // Absolute writes, and the setters early-out on exact equality — so
    // replaying a value the node already holds dirties no static world and
    // raises no NodeTransformChanged.
    private static void Write(SceneNode node, Vector3 position, Quaternion rotation)
    {
        node.LocalPosition = position;
        node.LocalRotation = rotation;
    }

    private void Remember(SceneNode node)
    {
        if (_lastApplied is null)
            _lastApplied = new WeakReference<SceneNode>(node);
        else
            _lastApplied.SetTarget(node);
    }
}
