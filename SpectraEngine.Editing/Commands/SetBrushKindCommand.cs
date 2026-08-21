using SpectraEngine.Core.Scene;
using System;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Converts a brush between world geometry and a standalone part — the one
/// edit that changes whether a brush is admitted to the fused static world.
/// </summary>
/// <remarks>
/// <para>
/// <b>The conversion is a property write, not a rebuild.</b> The node keeps its
/// <see cref="SceneNode.Id"/>, its brush instance, its per-face materials and
/// its texture axes, so every <c>NodeRef</c>, every entity target name and every
/// undo entry pointing at it stays valid across the round trip. That is the
/// whole reason the kind is a bit on the node rather than a different node type:
/// the alternative — delete the brush node, create a mesh node — invalidates
/// history and identity to express what is conceptually one checkbox.
/// </para>
/// <para>
/// <b>It is an absolute before/after pair, like every other command here.</b>
/// Replaying a kind the node already has is free (the setter early-outs on an
/// equal write), which is what makes undo and redo idempotent rather than
/// merely reversible.
/// </para>
/// <para>
/// <b>What the user should be told, and what this command deliberately does
/// not do.</b> Converting to <see cref="BrushKind.Part"/> takes the brush out
/// of the carve: it stops merging with the geometry around it, so a face left
/// coplanar with a world face will z-fight and a seam that used to be welded
/// becomes two independent surfaces. Converting back re-admits it and costs one
/// static-world recompile. Neither is reversible information loss, so this
/// command performs no baking and issues no warning — the dialog that explains
/// the trade is a UI concern, and it belongs where the user can see it.
/// </para>
/// </remarks>
public sealed class SetBrushKindCommand : IEditorCommand
{
    private WeakReference<SceneNode>? _lastApplied;

    /// <summary>Creates a command from explicit before/after kinds.</summary>
    public SetBrushKindCommand(Guid nodeId, BrushKind before, BrushKind after)
    {
        NodeId = nodeId;
        Before = before;
        After = after;
    }

    /// <summary>
    /// Captures <paramref name="node"/>'s current kind as the before-state.
    /// Call this <em>before</em> applying the edit to the node.
    /// </summary>
    public static SetBrushKindCommand Capture(SceneNode node, BrushKind after)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new SetBrushKindCommand(node.Id, node.BrushKind, after);
    }

    /// <summary>The id of the node this command edits.</summary>
    public Guid NodeId { get; }

    /// <summary>The kind the node carried before the edit.</summary>
    public BrushKind Before { get; }

    /// <summary>The kind the node carries after the edit.</summary>
    public BrushKind After { get; }

    /// <inheritdoc/>
    public string Name { get; init; } = "Convert Brush";

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
            node.BrushKind = Before;
            return;
        }

        if (_lastApplied is not null && _lastApplied.TryGetTarget(out SceneNode? detached))
            detached.BrushKind = Before;
    }

    private void Apply(Scene scene, BrushKind kind)
    {
        ArgumentNullException.ThrowIfNull(scene);
        // Missing target = no-op, per the IEditorCommand contract.
        if (!scene.TryFindById(NodeId, out SceneNode? node))
            return;

        if (_lastApplied is null)
            _lastApplied = new WeakReference<SceneNode>(node);
        else
            _lastApplied.SetTarget(node);

        node.BrushKind = kind;
    }
}
