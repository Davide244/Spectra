using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Where a node sits in the graph: which parent it hangs under, and at which
/// position among that parent's children.
/// </summary>
/// <remarks>
/// <b>The index is not decoration.</b> Traversal order is child-list order,
/// traversal order is the static world's placement-slot order, and placement
/// order breaks ties in the carve's overlap ordering. A node restored at a
/// different index therefore produces geometry that is valid, different, and
/// bit-unequal to what was there before, which is precisely what the engine's
/// determinism oracles exist to refuse.
/// <para>
/// The parent is named by <see cref="Guid"/> like every other command target,
/// but the node is held by <em>reference</em>, because a node waiting to be
/// added is not in the scene and so cannot be looked up in it. That reference is
/// what keeps a deleted subtree alive for as long as its undo entry exists.
/// </para>
/// </remarks>
/// <param name="Node">The node to attach. Detached while the command is undone.</param>
/// <param name="ParentId">The id of the node it hangs under.</param>
/// <param name="Index">Its position among that parent's children.</param>
public readonly record struct NodePlacement(SceneNode Node, Guid ParentId, int Index);

/// <summary>
/// Attaches one or more detached nodes to the scene at recorded positions, and
/// detaches them again on undo. The create half of every structural edit:
/// duplicate, paste, and the group node a group operation introduces.
/// </summary>
/// <remarks>
/// <b>This command owns its nodes while it is undone</b>, which is the one place
/// the editing layer holds scene state outside the scene. It has to: a node that
/// is not in the graph cannot be resolved from it, so if the command let go, an
/// undone duplicate could never be redone. The consequence to know about is that
/// history keeps whole subtrees alive, including their shared GPU meshes, until
/// the entry falls off the end of the bounded ring.
/// <para>
/// <b>Inserts run in ascending index order</b>, so a set of siblings restored
/// together lands where it was: inserting the higher index first would find a
/// list too short to hold it and clamp, silently reordering the very thing this
/// command exists to preserve.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the scene it mutates.
/// </para>
/// </remarks>
public sealed class AddNodesCommand : IEditorCommand
{
    private readonly NodePlacement[] _placements;

    /// <summary>
    /// Creates a command that will attach each node at its recorded placement.
    /// </summary>
    /// <param name="placements">
    /// The nodes and where they go. Copied and sorted by index, so the caller's
    /// collection is not retained and its order does not matter.
    /// </param>
    public AddNodesCommand(IReadOnlyList<NodePlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        _placements = [.. placements.OrderBy(p => p.Index)];
    }

    /// <inheritdoc/>
    public string Name { get; init; } = "Add";

    /// <summary>The placements this command applies, in the order it applies them.</summary>
    public IReadOnlyList<NodePlacement> Placements => _placements;

    /// <inheritdoc/>
    public void Do(Scene scene) => StructuralEdit.Attach(scene, _placements);

    /// <inheritdoc/>
    public void Undo(Scene scene) => StructuralEdit.Detach(scene, _placements);
}
