using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// Finding a node the user picked in the viewport.
/// </summary>
/// <remarks>
/// <b>The scroll is the visible half and the expansion is the load-bearing
/// one.</b> A row under a collapsed parent has no container to scroll to, so
/// revealing has to be something the DATA can express before any of it is
/// realised; that is why expansion lives on the node rather than on the
/// control, and why this suite is about which flags get set rather than about
/// pixels.
/// <para>
/// The parent map that walks the chain is also what detaching a node uses, so
/// these tests double as its coverage: an entry left behind by a delete would
/// surface here as a reveal that expands a subtree which no longer exists.
/// </para>
/// </remarks>
public sealed class SceneTreeRevealTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid Leaf = Guid.NewGuid();
    private static readonly Guid Sibling = Guid.NewGuid();

    private static SceneTreeModel NewTree() => new(new EngineHost(NullLogger.Instance), NullLogger.Instance);

    private static SceneChange Added(Guid id, Guid parent, string name, int index) =>
        new(SceneChangeKind.Added, id, parent, name, index, SceneNodeKind.Empty);

    private static SceneTreeModel Nested()
    {
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(new FrameSnapshot
        {
            FrameNumber = 1,
            Changes = new[]
            {
                Added(Root, Guid.Empty, "Root", -1),
                Added(Branch, Root, "Branch", 0),
                Added(Leaf, Branch, "Leaf", 0),
                Added(Sibling, Root, "Sibling", 1),
            },
        });

        // Closed deliberately: a top-level row now opens by default (so a
        // freshly opened project shows its scene), and what these tests are
        // about is what the REVEAL opens, which needs a known closed start.
        tree.ToggleExpanded(tree.Roots[0]);
        return tree;
    }

    private static SceneTreeNode Find(SceneTreeModel tree, string name) =>
        Walk(tree.Roots).First(n => n.Name == name);

    private static IEnumerable<SceneTreeNode> Walk(IEnumerable<SceneTreeNode> nodes)
    {
        foreach (SceneTreeNode node in nodes)
        {
            yield return node;
            foreach (SceneTreeNode child in Walk(node.Children))
                yield return child;
        }
    }

    [Fact]
    public void Revealing_a_node_expands_every_parent_above_it()
    {
        SceneTreeModel tree = Nested();

        tree.TryReveal(Leaf, out SceneTreeNode node).ShouldBeTrue();

        node.Name.ShouldBe("Leaf");
        Find(tree, "Root").IsExpanded.ShouldBeTrue();
        Find(tree, "Branch").IsExpanded.ShouldBeTrue();
    }

    [Fact]
    public void Revealing_a_node_does_not_expand_the_node_itself()
    {
        // Picking a group in the viewport means "show me this", not "show me
        // everything inside it"; a group with two hundred children would push
        // its own row off the panel it was just scrolled onto.
        SceneTreeModel tree = Nested();

        tree.TryReveal(Branch, out _).ShouldBeTrue();

        Find(tree, "Branch").IsExpanded.ShouldBeFalse();
        Find(tree, "Root").IsExpanded.ShouldBeTrue();
    }

    [Fact]
    public void Revealing_never_collapses_anything()
    {
        // The expansion set is the user's. A reveal that tidied up on its way
        // past would undo their work every time they clicked in the viewport.
        SceneTreeModel tree = Nested();
        Find(tree, "Sibling").IsExpanded = true;

        tree.TryReveal(Leaf, out _).ShouldBeTrue();

        Find(tree, "Sibling").IsExpanded.ShouldBeTrue();
    }

    [Fact]
    public void A_root_node_reveals_with_nothing_to_expand()
    {
        SceneTreeModel tree = Nested();

        tree.TryReveal(Root, out SceneTreeNode node).ShouldBeTrue();

        node.Name.ShouldBe("Root");
        node.IsExpanded.ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_id_reveals_nothing_rather_than_throwing()
    {
        // A viewport selection can name a node whose Added change has not been
        // drained yet. That is a frame of ordinary lag, not a fault.
        SceneTreeModel tree = Nested();

        tree.TryReveal(Guid.NewGuid(), out _).ShouldBeFalse();
        Find(tree, "Root").IsExpanded.ShouldBeFalse();
    }

    // --- The parent map, through the reveal ----------------------------------

    [Fact]
    public void A_reparented_node_reveals_through_its_new_chain()
    {
        SceneTreeModel tree = Nested();

        tree.ApplyChanges(new FrameSnapshot
        {
            FrameNumber = 2,
            Changes = new[]
            {
                new SceneChange(SceneChangeKind.Reparented, Leaf, Sibling, "Leaf", 0, SceneNodeKind.Empty),
            },
        });

        tree.TryReveal(Leaf, out _).ShouldBeTrue();

        Find(tree, "Sibling").IsExpanded.ShouldBeTrue();
        Find(tree, "Branch").IsExpanded.ShouldBeFalse("the old parent is no longer on the chain");
    }

    [Fact]
    public void A_removed_subtree_leaves_no_parentage_behind()
    {
        // An entry surviving a delete would make a later reveal expand a branch
        // that is no longer in the tree, which is invisible until the day two
        // nodes reuse the same object.
        SceneTreeModel tree = Nested();

        tree.ApplyChanges(new FrameSnapshot
        {
            FrameNumber = 2,
            Changes = new[]
            {
                new SceneChange(SceneChangeKind.Removed, Branch, Guid.Empty, "Branch", -1, SceneNodeKind.Empty),
            },
        });

        tree.TryReveal(Leaf, out _).ShouldBeFalse();
        tree.TryReveal(Branch, out _).ShouldBeFalse();
        tree.Count.ShouldBe(2);
    }

    [Fact]
    public void A_node_moved_to_the_top_level_has_no_chain_to_expand()
    {
        SceneTreeModel tree = Nested();

        tree.ApplyChanges(new FrameSnapshot
        {
            FrameNumber = 2,
            Changes = new[]
            {
                new SceneChange(SceneChangeKind.Reparented, Leaf, Guid.Empty, "Leaf", 1, SceneNodeKind.Empty),
            },
        });

        tree.TryReveal(Leaf, out SceneTreeNode node).ShouldBeTrue();
        tree.Roots.ShouldContain(node);
        Find(tree, "Branch").IsExpanded.ShouldBeFalse();
    }
}
