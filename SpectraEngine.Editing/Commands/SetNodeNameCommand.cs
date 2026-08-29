using SpectraEngine.Core.Scene;
using System;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Renames a node.
/// </summary>
/// <remarks>
/// <para>
/// <b>A name is not an identity, which is why this is a plain property
/// write.</b> Every command in this history addresses its target by
/// <see cref="SceneNode.Id"/>, so renaming breaks nothing: no other command
/// goes stale, no reference is invalidated, and the tree row updates because
/// the node raises change notification. If names were the addressing scheme
/// this would have to be a structural edit instead.
/// </para>
/// <para>
/// <b>Absolute before/after, like every other command here.</b> Replaying a
/// name the node already has is free, which is what makes undo and redo
/// idempotent rather than merely reversible.
/// </para>
/// </remarks>
public sealed class SetNodeNameCommand : IEditorCommand
{
    private WeakReference<SceneNode>? _lastApplied;

    /// <summary>Creates a command from explicit before/after names.</summary>
    public SetNodeNameCommand(Guid nodeId, string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        NodeId = nodeId;
        Before = before;
        After = after;
    }

    /// <summary>
    /// Captures <paramref name="node"/>'s current name as the before-state.
    /// Call this <em>before</em> applying the edit.
    /// </summary>
    public static SetNodeNameCommand Capture(SceneNode node, string after)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new SetNodeNameCommand(node.Id, node.Name, after);
    }

    /// <summary>The id of the node this command edits.</summary>
    public Guid NodeId { get; }

    /// <summary>The name the node carried before the edit.</summary>
    public string Before { get; }

    /// <summary>The name the node carries after the edit.</summary>
    public string After { get; }

    /// <inheritdoc/>
    public string Name { get; init; } = "Rename";

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
            node.Name = Before;
            return;
        }

        // A node that left the scene mid-gesture still has to be restored, or
        // it is stranded at a half-applied value with nothing left to fix it.
        if (_lastApplied is not null && _lastApplied.TryGetTarget(out SceneNode? detached))
            detached.Name = Before;
    }

    private void Apply(Scene scene, string name)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // Missing target = no-op, per the IEditorCommand contract: replaying
        // history behind an undone delete must not fail.
        if (!scene.TryFindById(NodeId, out SceneNode? node))
            return;

        _lastApplied ??= new WeakReference<SceneNode>(node);
        node.Name = name;
    }
}
