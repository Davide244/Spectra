using SpectraEngine.Core.Scene;
using System;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Sets a node's whole local <see cref="Transform"/> — position, rotation
/// <em>and</em> scale — to an absolute value, remembering the absolute
/// transform it had before. The full-transform sibling of
/// <see cref="SetTransformCommand"/>, for edits that really do own all three
/// components: a property-panel commit, a paste, or a scale gizmo.
/// </summary>
/// <remarks>
/// Writing scale through this command on a <em>brush</em> node will break the
/// rigidity the static-world compile requires; that check lives in the compile,
/// not here, but tools should not offer scale on brush nodes.
/// </remarks>
public sealed class SetLocalTransformCommand : ICoalescingCommand
{
    // The node this command last wrote to, weakly — see the identical field on
    // <see cref="SetTransformCommand"/> for why it exists and why it is weak.
    private WeakReference<SceneNode>? _lastApplied;

    /// <summary>Creates a command from explicit before/after transforms.</summary>
    public SetLocalTransformCommand(Guid nodeId, Transform before, Transform after)
    {
        NodeId = nodeId;
        Before = before;
        After = after;
    }

    /// <summary>
    /// Captures <paramref name="node"/>'s current local transform as the
    /// before-state and records <paramref name="after"/> as the after-state.
    /// Call this <em>before</em> applying the edit to the node.
    /// </summary>
    public static SetLocalTransformCommand Capture(SceneNode node, Transform after)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new SetLocalTransformCommand(node.Id, node.LocalTransform, after);
    }

    /// <summary>The id of the node this command edits.</summary>
    public Guid NodeId { get; }

    /// <summary>The node's local transform before the edit.</summary>
    public Transform Before { get; }

    /// <summary>
    /// The node's local transform after the edit. Mutable through
    /// <see cref="SetAfter"/> and <see cref="TryAbsorb"/> while the command is
    /// still inside an open transaction.
    /// </summary>
    public Transform After { get; private set; }

    /// <inheritdoc/>
    public string Name { get; init; } = "Transform";

    /// <summary>
    /// Retargets the after-state, keeping the captured before-state — the
    /// zero-allocation drag path, as on <see cref="SetTransformCommand"/>.
    /// </summary>
    public void SetAfter(Transform after) => After = after;

    /// <inheritdoc/>
    public void Do(Scene scene) => Apply(scene, After);

    /// <inheritdoc/>
    public void Undo(Scene scene) => Apply(scene, Before);

    /// <inheritdoc/>
    public void RollBack(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (scene.TryFindById(NodeId, out SceneNode? node))
        {
            node.LocalTransform = Before;
            return;
        }

        // A cancel discards its commands, so a node that left the scene
        // mid-gesture has to be restored through the reference this command
        // actually wrote to — see IEditorCommand.RollBack.
        if (_lastApplied is not null && _lastApplied.TryGetTarget(out SceneNode? detached))
            detached.LocalTransform = Before;
    }

    /// <inheritdoc/>
    public bool TryAbsorb(IEditorCommand newer)
    {
        if (newer is not SetLocalTransformCommand next || next.NodeId != NodeId)
            return false;

        SetAfter(next.After);
        return true;
    }

    private void Apply(Scene scene, Transform transform)
    {
        ArgumentNullException.ThrowIfNull(scene);
        // Missing target = no-op, per the IEditorCommand contract.
        if (!scene.TryFindById(NodeId, out SceneNode? node))
            return;

        if (_lastApplied is null)
            _lastApplied = new WeakReference<SceneNode>(node);
        else
            _lastApplied.SetTarget(node);

        // One absolute write; LocalTransform's field-wise equality check makes
        // a replay of the current value free.
        node.LocalTransform = transform;
    }
}
