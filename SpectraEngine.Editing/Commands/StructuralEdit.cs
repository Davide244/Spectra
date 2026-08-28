using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// The attach and detach halves that <see cref="AddNodesCommand"/> and
/// <see cref="RemoveNodesCommand"/> are the two orientations of.
/// </summary>
/// <remarks>
/// <b>One implementation, two commands, because they are exact mirrors.</b> An
/// add undone is a remove and a remove undone is an add, so writing the pair
/// twice would be two chances for the two directions to disagree about index
/// restoration, which is the only thing either of them is really for.
/// <para>
/// <b>Threading:</b> render thread only.
/// </para>
/// </remarks>
internal static class StructuralEdit
{
    /// <summary>
    /// Attaches every placement's node under its recorded parent at its recorded
    /// index. Placements whose parent is not in the scene are skipped, per the
    /// <see cref="IEditorCommand"/> missing-target rule.
    /// </summary>
    /// <remarks>
    /// Expects <paramref name="placements"/> already in ascending index order;
    /// both commands sort at construction so this runs allocation-free.
    /// </remarks>
    public static void Attach(Scene scene, IReadOnlyList<NodePlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(scene);

        for (int i = 0; i < placements.Count; i++)
        {
            NodePlacement placement = placements[i];
            if (!scene.TryFindById(placement.ParentId, out SceneNode? parent))
                continue;

            // Idempotent, and not merely defensively so: re-attaching a node that
            // is already exactly where it belongs would otherwise detach and
            // re-insert it, bumping the graph structure version and forcing the
            // next compile down the full validated walk for no change at all.
            if (ReferenceEquals(placement.Node.Parent, parent) &&
                placement.Node.IndexInParent == placement.Index)
            {
                continue;
            }

            parent.InsertChild(placement.Index, placement.Node);
        }
    }

    /// <summary>
    /// Detaches every placement's node from the graph, leaving the command
    /// holding the only reference to it. Nodes that are not currently attached
    /// are skipped.
    /// </summary>
    public static void Detach(Scene scene, IReadOnlyList<NodePlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // Descending, the mirror of Attach's ascending order: it is not required
        // for correctness (a removal by reference cannot be shifted out of range
        // the way an insert can) but it keeps the two halves visibly inverse.
        for (int i = placements.Count - 1; i >= 0; i--)
        {
            SceneNode node = placements[i].Node;
            node.Parent?.RemoveChild(node);
        }
    }

    /// <summary>
    /// Captures where each node currently sits, for a command that is about to
    /// move or remove it. Nodes with no parent (a scene root, or something
    /// already detached) are skipped: there is no placement to restore.
    /// </summary>
    public static NodePlacement[] CapturePlacements(IReadOnlyList<SceneNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var captured = new List<NodePlacement>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            SceneNode node = nodes[i];
            if (node.Parent is { } parent)
                captured.Add(new NodePlacement(node, parent.Id, node.IndexInParent));
        }

        return [.. captured];
    }
}
