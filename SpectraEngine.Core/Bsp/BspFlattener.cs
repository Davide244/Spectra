using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Turns a live <see cref="BspTree"/> into the flat node array the compiled map
/// format bakes.
/// </summary>
public static class BspFlattener
{
    // Sentinel for the root's "parent" slot. Deliberately not spelled
    // FlatBspNode.EmptyLeaf even though both are -1: one addresses the node
    // array, the other is a child code, and conflating them is how a future
    // edit patches the wrong thing.
    private const int NoParent = -1;

    /// <summary>
    /// Flattens <paramref name="tree"/> pre-order depth first with the FRONT
    /// child emitted first, and reports the root's child code in
    /// <paramref name="rootIndex"/> (0 for any tree with a split at its root,
    /// a leaf code for a tree that is one bare leaf).
    /// </summary>
    /// <remarks>
    /// The order is a pure function of the tree, so two flattens of one tree
    /// produce element-identical arrays. Emitting a node before its subtrees
    /// also makes every child index strictly greater than its parent's, which
    /// is what makes a cycle unrepresentable in a well-formed array.
    /// </remarks>
    public static FlatBspNode[] Flatten(BspTree tree, out int rootIndex)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var nodes = new List<FlatBspNode>();

        // An explicit stack rather than recursion: the live builder recurses
        // and so bounds its own depth, but this walk will also run over trees
        // read back from a file this process did not build.
        var pending = new Stack<PendingChild>();
        pending.Push(new PendingChild(tree.Root, NoParent, IsFront: false));

        rootIndex = FlatBspNode.EmptyLeaf;

        while (pending.Count > 0)
        {
            PendingChild slot = pending.Pop();
            BspNode live = slot.Node;

            int child;
            if (live.IsLeaf)
            {
                child = live.IsSolid ? FlatBspNode.SolidLeaf : FlatBspNode.EmptyLeaf;
            }
            else
            {
                // The slot is claimed BEFORE descending, which is what makes
                // the emission pre-order; the children patch it on their way
                // out.
                child = nodes.Count;
                nodes.Add(new FlatBspNode(live.Plane, FlatBspNode.EmptyLeaf, FlatBspNode.EmptyLeaf));

                // Back pushed first so Front pops first: the whole front
                // subtree is emitted before the back one.
                pending.Push(new PendingChild(live.Back!, child, IsFront: false));
                pending.Push(new PendingChild(live.Front!, child, IsFront: true));
            }

            if (slot.Parent == NoParent)
            {
                rootIndex = child;
                continue;
            }

            FlatBspNode parent = nodes[slot.Parent];
            nodes[slot.Parent] = slot.IsFront
                ? new FlatBspNode(parent.Plane, child, parent.Back)
                : new FlatBspNode(parent.Plane, parent.Front, child);
        }

        return [.. nodes];
    }

    private readonly record struct PendingChild(BspNode Node, int Parent, bool IsFront);
}
