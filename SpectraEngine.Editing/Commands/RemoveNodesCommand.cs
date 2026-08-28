using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Detaches one or more nodes from the scene, and puts them back at exactly the
/// parent and sibling index they came from on undo. The delete half of every
/// structural edit.
/// </summary>
/// <remarks>
/// <b>The command holds the deleted subtrees, and that is what makes undo
/// possible at all.</b> Nothing else in the editing layer keeps scene state
/// outside the scene; a detached node cannot be looked up in a graph it is not
/// in, so if this let go, a delete could never be undone. The consequence worth
/// knowing: a deleted subtree, and every GPU mesh its nodes share, stays alive
/// until this entry falls off the end of the undo ring or the history is
/// cleared.
/// <para>
/// <b>Restoring the sibling index is the entire difference between this and
/// "re-add it somewhere".</b> Child-list order is traversal order is
/// placement-slot order, and placement order breaks ties in the carve, so a node
/// that comes back at the end of the list rebuilds a level that is valid,
/// different, and bit-unequal to the one that was there.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only.
/// </para>
/// </remarks>
public sealed class RemoveNodesCommand : IEditorCommand
{
    private readonly NodePlacement[] _placements;

    private RemoveNodesCommand(NodePlacement[] placements) => _placements = placements;

    /// <summary>
    /// Captures where <paramref name="nodes"/> currently sit, so they can be put
    /// back there. Call this <em>before</em> removing them.
    /// </summary>
    /// <remarks>
    /// Nodes with no parent are dropped: a scene root has no placement to
    /// restore, and removing it is not an operation this engine offers.
    /// </remarks>
    /// <param name="nodes">The nodes to remove.</param>
    /// <param name="name">The history label, when the caller is a larger operation than a plain delete.</param>
    public static RemoveNodesCommand Capture(IReadOnlyList<SceneNode> nodes, string name = "Delete")
    {
        NodePlacement[] captured = StructuralEdit.CapturePlacements(nodes);
        return new RemoveNodesCommand([.. captured.OrderBy(p => p.Index)]) { Name = name };
    }

    /// <inheritdoc/>
    public string Name { get; init; } = "Delete";

    /// <summary>The nodes this command removes, and where they came from.</summary>
    public IReadOnlyList<NodePlacement> Placements => _placements;

    /// <inheritdoc/>
    public void Do(Scene scene) => StructuralEdit.Detach(scene, _placements);

    /// <inheritdoc/>
    public void Undo(Scene scene) => StructuralEdit.Attach(scene, _placements);
}
