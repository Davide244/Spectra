using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// One node's move: which parent and index it came from, and which it goes to.
/// </summary>
/// <param name="NodeId">The node being moved, addressed by id like every command target.</param>
/// <param name="FromParentId">The parent it hung under before.</param>
/// <param name="FromIndex">Its sibling index before.</param>
/// <param name="ToParentId">The parent it hangs under after.</param>
/// <param name="ToIndex">Its sibling index after.</param>
public readonly record struct NodeReparent(
    Guid NodeId, Guid FromParentId, int FromIndex, Guid ToParentId, int ToIndex);

/// <summary>
/// Moves nodes to a different parent, or to a different position under the same
/// parent, and puts them back on undo. The third structural verb, and the one a
/// group, an ungroup, and a drag in a scene tree all reduce to.
/// </summary>
/// <remarks>
/// <b>Unlike its two siblings this command holds no node references</b>, because
/// a reparented node never leaves the scene: both ends of the move are resolvable
/// by id, so it addresses everything the way the rest of the editing layer does.
/// <para>
/// <b>A reparent does not preserve a node's world transform, and this command
/// deliberately does not try to.</b> Local transforms are what the graph stores,
/// so moving a node under a different parent moves it in the world unless
/// something also rewrites its local transform. Whether that is wanted is the
/// caller's decision, not this command's: a scene-tree drag usually wants the
/// object to stay where it looks, and a group operation composes this with the
/// <see cref="SetLocalTransformCommand"/>s that keep it there.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only.
/// </para>
/// </remarks>
public sealed class ReparentNodesCommand : IEditorCommand
{
    private readonly NodeReparent[] _forward;
    private readonly NodeReparent[] _backward;

    /// <summary>Creates a command from explicit before/after placements.</summary>
    /// <param name="moves">
    /// The moves to apply. Copied; the caller's order does not matter, because
    /// each direction is sorted by its own destination index.
    /// </param>
    public ReparentNodesCommand(IReadOnlyList<NodeReparent> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);

        // Each direction gets its own ordering, ascending by the index it is
        // inserting AT. Sorting once here keeps both Do and Undo allocation-free
        // and keeps the two directions from having to reason about each other.
        _forward = [.. moves.OrderBy(m => m.ToIndex)];
        _backward = [.. moves.OrderBy(m => m.FromIndex)];
    }

    /// <summary>
    /// Captures each node's current placement as the before-state and records
    /// where it is going. Call this <em>before</em> moving anything.
    /// </summary>
    /// <param name="nodes">The nodes to move; any with no parent are skipped.</param>
    /// <param name="newParent">The parent they all move under.</param>
    /// <param name="firstIndex">
    /// The index the first node lands at; each subsequent node follows it, so a
    /// selection keeps its relative order.
    /// </param>
    /// <param name="name">The history label, when the caller is a larger operation than a plain reparent.</param>
    public static ReparentNodesCommand Capture(
        IReadOnlyList<SceneNode> nodes, SceneNode newParent, int firstIndex, string name = "Reparent")
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(newParent);

        var moves = new List<NodeReparent>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            SceneNode node = nodes[i];
            if (node.Parent is { } parent)
                moves.Add(new NodeReparent(node.Id, parent.Id, node.IndexInParent, newParent.Id, firstIndex + moves.Count));
        }

        return new ReparentNodesCommand(moves) { Name = name };
    }

    /// <inheritdoc/>
    public string Name { get; init; } = "Reparent";

    /// <summary>The moves this command applies, in the order it applies them.</summary>
    public IReadOnlyList<NodeReparent> Moves => _forward;

    /// <inheritdoc/>
    public void Do(Scene scene) => Apply(scene, _forward, forward: true);

    /// <inheritdoc/>
    public void Undo(Scene scene) => Apply(scene, _backward, forward: false);

    private static void Apply(Scene scene, NodeReparent[] moves, bool forward)
    {
        ArgumentNullException.ThrowIfNull(scene);

        foreach (NodeReparent move in moves)
        {
            Guid parentId = forward ? move.ToParentId : move.FromParentId;
            int index = forward ? move.ToIndex : move.FromIndex;

            // Missing target on either end is a no-op, per the IEditorCommand
            // contract: history behind a still-undone delete legitimately names
            // a node or a parent that is not in the scene right now.
            if (!scene.TryFindById(move.NodeId, out SceneNode? node) ||
                !scene.TryFindById(parentId, out SceneNode? parent))
            {
                continue;
            }

            if (ReferenceEquals(node.Parent, parent) && node.IndexInParent == index)
                continue;

            parent.InsertChild(index, node);
        }
    }
}
