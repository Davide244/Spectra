using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Undo;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// The four structural verbs an editor offers over a selection: duplicate,
/// delete, group and ungroup. Each lands as exactly one undo entry, and each
/// works on the selection's <em>roots</em> rather than on every selected node.
/// </summary>
/// <remarks>
/// <b>Every one of these is composed out of the three structural commands</b>
/// (<see cref="AddNodesCommand"/>, <see cref="RemoveNodesCommand"/>,
/// <see cref="ReparentNodesCommand"/>) plus the transform command that already
/// existed, rather than being four more command types. A group is a node added
/// and a set of nodes reparented; an ungroup is the same run backwards. Keeping
/// them compositions means the sibling-index restoration that makes structural
/// undo correct is written once.
/// <para>
/// <b>The selection is filtered to its roots first, and that is not a tidy-up.</b>
/// Duplicating a node whose parent is also selected would copy that subtree
/// twice, once as itself and once inside its parent's copy; deleting one would
/// remove a node that is about to be removed anyway, and record a placement into
/// a parent that will not exist. This is the same effective-selection rule the
/// gizmos apply before manipulating.
/// </para>
/// <para>
/// <b>Selection changes are NOT part of the undo entry.</b> Undoing a delete
/// restores the geometry, not the fact that it was selected, which is what every
/// editor this engine's audiences use already does. The selection is still
/// updated as a side effect, because a duplicate you cannot immediately drag is
/// not a duplicate.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the scene and the history.
/// </para>
/// </remarks>
public static class StructuralEditor
{
    /// <summary>The name a duplicated node keeps: its original's.</summary>
    /// <remarks>
    /// Studio's behaviour, and the right default for the engine's default gizmo
    /// style. Blender's ".001" suffix is the alternative, and a scene tree that
    /// wants unique display names should derive them rather than mangle the
    /// authored name at duplicate time.
    /// </remarks>
    public const string DuplicateTransactionName = "Duplicate";

    /// <summary>
    /// Copies each root of <paramref name="selection"/> and attaches the copies
    /// beside their originals, then selects the copies. Returns false, changing
    /// nothing, when there is nothing duplicable.
    /// </summary>
    /// <remarks>
    /// Copies land at the END of their original's parent, which is where Studio
    /// puts them and what keeps the index arithmetic honest when several
    /// siblings are duplicated at once.
    /// </remarks>
    public static bool TryDuplicate(Scene scene, UndoStack undo, IReadOnlyList<SceneNode> selection)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(undo);

        List<SceneNode> roots = SelectionRoots(scene, selection);
        if (roots.Count == 0)
            return false;

        var placements = new List<NodePlacement>(roots.Count);
        var clones = new List<SceneNode>(roots.Count);

        // Consecutive indices per parent, counted from that parent's current
        // child count, so several siblings duplicated together keep their
        // relative order instead of all naming the same slot.
        var nextIndex = new Dictionary<Guid, int>();
        foreach (SceneNode root in roots)
        {
            SceneNode parent = root.Parent!;
            if (!nextIndex.TryGetValue(parent.Id, out int index))
                index = parent.Children.Count;

            SceneNode clone = root.Clone();
            clones.Add(clone);
            placements.Add(new NodePlacement(clone, parent.Id, index));
            nextIndex[parent.Id] = index + 1;
        }

        undo.BeginTransaction(DuplicateTransactionName);
        Run(scene, undo, new AddNodesCommand(placements) { Name = DuplicateTransactionName });
        undo.CommitTransaction();

        scene.Selection.SetRange(clones);
        return true;
    }

    /// <summary>
    /// Removes each root of <paramref name="selection"/> from the scene and
    /// clears the selection. Returns false, changing nothing, when there is
    /// nothing deletable.
    /// </summary>
    /// <remarks>
    /// The removed subtrees stay alive inside the history entry, which is what
    /// makes the undo possible and what keeps their GPU meshes resident until
    /// the entry ages out of the bounded ring.
    /// </remarks>
    public static bool TryDelete(Scene scene, UndoStack undo, IReadOnlyList<SceneNode> selection)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(undo);

        List<SceneNode> roots = SelectionRoots(scene, selection);
        if (roots.Count == 0)
            return false;

        undo.BeginTransaction("Delete");
        Run(scene, undo, RemoveNodesCommand.Capture(roots));
        undo.CommitTransaction();

        scene.Selection.Clear();
        return true;
    }

    /// <summary>
    /// Puts the roots of <paramref name="selection"/> under one new node,
    /// pivoted at the centre of what it contains, and selects it. Every child
    /// keeps its exact world transform. Returns false, changing nothing, when
    /// there is nothing to group or when a transform cannot be preserved.
    /// </summary>
    /// <remarks>
    /// <b>The group's pivot is the selection's bounds centre, not the parent's
    /// origin</b>, because the pivot is what every later manipulation of the
    /// group turns and scales about. A group node dumped at the origin makes
    /// rotating the thing you just grouped rotate it around somewhere else.
    /// <para>
    /// The group is given a translation only, never a rotation or a scale, which
    /// is what keeps every brush placement under it rigid. Children are then
    /// re-expressed under it from their world matrices, so mixed parents work.
    /// A subtree whose matrix will not decompose (a zero scale somewhere in the
    /// chain) makes the whole operation refuse rather than silently shear.
    /// </para>
    /// </remarks>
    public static bool TryGroup(
        Scene scene, UndoStack undo, IReadOnlyList<SceneNode> selection, string groupName = "Group")
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(undo);

        List<SceneNode> roots = SelectionRoots(scene, selection);
        if (roots.Count == 0)
            return false;

        SceneNode parent = roots[0].Parent!;
        if (!Matrix4x4.Invert(parent.WorldMatrix, out Matrix4x4 parentInverse))
            return false;

        if (!GizmoSelectionBounds.TryMeasure(
                roots, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, out Vector3 min, out Vector3 max))
        {
            return false;
        }

        var group = new SceneNode(groupName)
        {
            LocalPosition = Vector3.Transform((min + max) * 0.5f, parentInverse),
        };

        // Where the selection was, so the group takes its place in the tree
        // rather than appearing at the bottom of it.
        int groupIndex = parent.Children.Count;
        foreach (SceneNode root in roots)
        {
            if (ReferenceEquals(root.Parent, parent))
                groupIndex = Math.Min(groupIndex, root.IndexInParent);
        }

        undo.BeginTransaction("Group");
        Run(scene, undo, new AddNodesCommand([new NodePlacement(group, parent.Id, groupIndex)]) { Name = "Group" });

        // The group is in the scene now, so it has a world matrix to re-express
        // the children against.
        if (!TryReparentPreservingWorld(scene, undo, roots, group, 0, "Group"))
        {
            undo.CancelTransaction();
            return false;
        }

        undo.CommitTransaction();
        scene.Selection.Select(group);
        return true;
    }

    /// <summary>
    /// Dissolves each selected node that has children: the children move up to
    /// the node's own parent, keeping their world transforms and their order,
    /// and the emptied node is removed. Returns false, changing nothing, when
    /// nothing in the selection is a group.
    /// </summary>
    public static bool TryUngroup(Scene scene, UndoStack undo, IReadOnlyList<SceneNode> selection)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(undo);

        List<SceneNode> roots = SelectionRoots(scene, selection);
        var groups = new List<SceneNode>(roots.Count);
        foreach (SceneNode root in roots)
        {
            if (root.Children.Count > 0)
                groups.Add(root);
        }

        if (groups.Count == 0)
            return false;

        var freed = new List<SceneNode>();
        undo.BeginTransaction("Ungroup");

        foreach (SceneNode group in groups)
        {
            SceneNode parent = group.Parent!;
            var children = new List<SceneNode>(group.Children);

            // Children take the group's slot, and the emptied group is removed
            // afterwards, so the run ends up exactly where the group was.
            if (!TryReparentPreservingWorld(scene, undo, children, parent, group.IndexInParent, "Ungroup"))
            {
                undo.CancelTransaction();
                return false;
            }

            Run(scene, undo, RemoveNodesCommand.Capture([group], "Ungroup"));
            freed.AddRange(children);
        }

        undo.CommitTransaction();
        scene.Selection.SetRange(freed);
        return true;
    }

    /// <summary>
    /// Moves the roots of <paramref name="selection"/> under
    /// <paramref name="newParent"/> at <paramref name="insertIndex"/>
    /// (<c>-1</c> appends), keeping every world transform, as one history
    /// entry. The verb a scene-tree drag lands on. Returns false, changing
    /// nothing, when nothing can legally move.
    /// </summary>
    /// <remarks>
    /// <b>A drop that would make a cycle is filtered out, never attempted.</b>
    /// Dragging a group onto its own child is an ordinary slip, and
    /// <c>SceneNode.InsertChild</c> answers it with a throw, which from inside
    /// an open transaction would leave the history open and the scene
    /// half-moved. Offending roots are dropped from the move (the rest of a
    /// multi-drag still lands); when everything offends, the verb refuses.
    /// <para>
    /// <b>The insert index is adjusted for same-parent moves.</b> A node
    /// dropped later under its own parent leaves its old slot first, which
    /// shifts every later sibling down by one; naming the pre-removal index
    /// would land it one row past where the drop indicator pointed.
    /// </para>
    /// </remarks>
    public static bool TryReparent(
        Scene scene, UndoStack undo, IReadOnlyList<SceneNode> selection, SceneNode newParent, int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(newParent);

        // The target must be this scene's; a stale reference (the parent was
        // deleted between gesture and apply) refuses cleanly.
        if (!scene.TryFindById(newParent.Id, out SceneNode? liveParent) ||
            !ReferenceEquals(liveParent, newParent))
        {
            return false;
        }

        List<SceneNode> roots = SelectionRoots(scene, selection);

        // Every node on the target's own ancestor chain (itself included) is a
        // cycle waiting to happen; one walk up collects them all.
        for (SceneNode? ancestor = newParent; ancestor is not null; ancestor = ancestor.Parent)
        {
            for (int i = roots.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(roots[i], ancestor))
                    roots.RemoveAt(i);
            }
        }

        if (roots.Count == 0)
            return false;

        int firstIndex = insertIndex < 0 ? newParent.Children.Count : insertIndex;

        // Same-parent moves vacate a slot before the insert happens.
        foreach (SceneNode root in roots)
        {
            if (ReferenceEquals(root.Parent, newParent) && root.IndexInParent < firstIndex)
                firstIndex--;
        }

        if (firstIndex < 0)
            firstIndex = 0;

        undo.BeginTransaction("Reparent");
        if (!TryReparentPreservingWorld(scene, undo, roots, newParent, firstIndex, "Reparent"))
        {
            undo.CancelTransaction();
            return false;
        }

        undo.CommitTransaction();
        scene.Selection.SetRange(roots);
        return true;
    }

    /// <summary>
    /// The nodes in <paramref name="selection"/> that no other selected node
    /// carries: the set a structural edit actually operates on. Excludes the
    /// scene root, which has no placement and cannot be removed.
    /// </summary>
    public static List<SceneNode> SelectionRoots(Scene scene, IReadOnlyList<SceneNode> selection)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(selection);

        var roots = new List<SceneNode>(selection.Count);
        for (int i = 0; i < selection.Count; i++)
        {
            SceneNode node = selection[i];

            // In THIS scene and not its root. The id index is how an outside
            // assembly asks that question: SceneNode.Owner is internal to Core,
            // and a selection can outlive the graph it was taken from.
            if (node.Parent is null ||
                !scene.TryFindById(node.Id, out SceneNode? live) ||
                !ReferenceEquals(live, node))
            {
                continue;
            }

            bool carried = false;
            for (SceneNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (Contains(selection, ancestor))
                {
                    carried = true;
                    break;
                }
            }

            if (!carried)
                roots.Add(node);
        }

        return roots;
    }

    // Moves nodes under a new parent while rewriting their local transforms so
    // nothing appears to move. Both halves are recorded, so the undo restores
    // the placement AND the transform.
    private static bool TryReparentPreservingWorld(
        Scene scene, UndoStack undo, IReadOnlyList<SceneNode> nodes, SceneNode newParent, int firstIndex, string name)
    {
        if (!Matrix4x4.Invert(newParent.WorldMatrix, out Matrix4x4 inverse))
            return false;

        // Solved BEFORE anything moves: a node's world matrix is only the one to
        // preserve while it still hangs where it did.
        var locals = new Transform[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!TryLocalUnder(nodes[i], inverse, out locals[i]))
                return false;
        }

        var befores = new Transform[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
            befores[i] = nodes[i].LocalTransform;

        Run(scene, undo, ReparentNodesCommand.Capture(nodes, newParent, firstIndex, name));

        for (int i = 0; i < nodes.Count; i++)
            Run(scene, undo, new SetLocalTransformCommand(nodes[i].Id, befores[i], locals[i]) { Name = name });

        return true;
    }

    // world = local * parent.World, so local = world * inverse(parent.World).
    private static bool TryLocalUnder(SceneNode node, Matrix4x4 newParentInverse, out Transform local)
    {
        local = Transform.Identity;

        if (!Matrix4x4.Decompose(
                node.WorldMatrix * newParentInverse,
                out Vector3 scale,
                out Quaternion rotation,
                out Vector3 position))
        {
            return false;
        }

        local.Position = position;
        local.Rotation = rotation;
        local.Scale = scale;
        return true;
    }

    private static void Run(Scene scene, UndoStack undo, IEditorCommand command)
    {
        undo.Record(command);
        command.Do(scene);
    }

    private static bool Contains(IReadOnlyList<SceneNode> nodes, SceneNode node)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (ReferenceEquals(nodes[i], node))
                return true;
        }

        return false;
    }
}
