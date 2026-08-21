using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Swaps the <see cref="Brush"/> attached to a node, remembering the one that
/// was there before. This is how a brush is <em>edited</em> at all: brushes are
/// immutable after construction, so every change — a retexture, a resize — is a
/// new instance assigned back to the node.
/// </summary>
/// <remarks>
/// <b>The new instance is the point, not an implementation detail.</b>
/// <see cref="Brush"/> reference identity is the CSG carve cache's validity key,
/// so assigning a successor is exactly what makes the old carve provably
/// unreachable instead of merely unlikely; the node's setter then dirties that
/// brush's chunk cells and the async chunked compile picks the change up through
/// its ordinary incremental path.
/// <para>
/// <b>Why this and not a scale on the node.</b> A brush node's transform must
/// stay rigid — the CSG epsilon scheme assumes unit-length plane normals, and
/// <c>Scene</c>'s snapshot rejects a scaled brush node outright — so a resize
/// edits the brush's local plane offsets
/// (<see cref="Brush.WithScaledExtents"/>) and lands here.
/// </para>
/// <para>
/// Null is a legal value on both sides: this command also expresses attaching a
/// brush to a node that had none, and detaching one.
/// </para>
/// </remarks>
public sealed class SetBrushCommand : ICoalescingCommand
{
    // The node this command last wrote to, weakly — see the identical field on
    // <see cref="SetTransformCommand"/> for why it exists and why it is weak.
    private WeakReference<SceneNode>? _lastApplied;

    /// <summary>Creates a command from explicit before/after brushes.</summary>
    public SetBrushCommand(Guid nodeId, Brush? before, Brush? after)
    {
        NodeId = nodeId;
        Before = before;
        After = after;
    }

    /// <summary>
    /// Captures <paramref name="node"/>'s current brush as the before-state and
    /// records <paramref name="after"/> as the after-state. Call this
    /// <em>before</em> applying the edit to the node.
    /// </summary>
    public static SetBrushCommand Capture(SceneNode node, Brush? after)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new SetBrushCommand(node.Id, node.Brush, after);
    }

    /// <summary>The id of the node this command edits.</summary>
    public Guid NodeId { get; }

    /// <summary>The brush the node carried before the edit, or null if it had none.</summary>
    public Brush? Before { get; }

    /// <summary>
    /// The brush the node carries after the edit. Mutable through
    /// <see cref="SetAfter"/> and <see cref="TryAbsorb"/> while the command is
    /// still inside an open transaction — the drag path, as on
    /// <see cref="SetTransformCommand"/>.
    /// </summary>
    public Brush? After { get; private set; }

    /// <inheritdoc/>
    public string Name { get; init; } = "Brush";

    /// <summary>Retargets the after-state, keeping the captured before-state.</summary>
    public void SetAfter(Brush? after) => After = after;

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
            node.Brush = Before;
            return;
        }

        // A cancel discards its commands, so a node that left the scene
        // mid-gesture has to be restored through the reference this command
        // actually wrote to — see IEditorCommand.RollBack.
        if (_lastApplied is not null && _lastApplied.TryGetTarget(out SceneNode? detached))
            detached.Brush = Before;
    }

    /// <inheritdoc/>
    public bool TryAbsorb(IEditorCommand newer)
    {
        if (newer is not SetBrushCommand next || next.NodeId != NodeId)
            return false;

        SetAfter(next.After);
        return true;
    }

    private void Apply(Scene scene, Brush? brush)
    {
        ArgumentNullException.ThrowIfNull(scene);
        // Missing target = no-op, per the IEditorCommand contract: history behind
        // a still-undone delete legitimately names nodes that are not in the
        // scene right now.
        if (!scene.TryFindById(NodeId, out SceneNode? node))
            return;

        if (_lastApplied is null)
            _lastApplied = new WeakReference<SceneNode>(node);
        else
            _lastApplied.SetTarget(node);

        // The setter early-outs on reference equality, so replaying the brush the
        // node already holds dirties no static world.
        node.Brush = brush;
    }
}
