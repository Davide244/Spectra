using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The flat projection a virtualizing panel binds to.
/// </summary>
/// <remarks>
/// <b>What this pins is that the panel is handed only what can be seen.</b> A
/// tree control realises a container per node whether or not it is visible,
/// which at the engine's own ceiling of 25,000 nodes means building
/// twenty-five thousand rows to show thirty-five. The projection is the fix,
/// and it fails silently in both directions: too many rows is just a slow
/// panel, too few is a node the user cannot find.
/// <para>
/// The patching matters as much as the contents. Replacing the collection
/// would reset the scroll position and the selection, so expanding one group
/// would throw the user back to the top of the scene.
/// </para>
/// </remarks>
public sealed class SceneTreeRowTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid Leaf = Guid.NewGuid();
    private static readonly Guid Sibling = Guid.NewGuid();

    private static SceneTreeModel NewTree() => new(new EngineHost(NullLogger.Instance), NullLogger.Instance);

    private static SceneChange Added(Guid id, Guid parent, string name, int index) =>
        new(SceneChangeKind.Added, id, parent, name, index, SceneNodeKind.Empty);

    private static FrameSnapshot Batch(long frame, params SceneChange[] changes) =>
        new() { FrameNumber = frame, Changes = changes };

    private static SceneTreeModel Nested()
    {
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(Branch, Root, "Branch", 0),
            Added(Leaf, Branch, "Leaf", 0),
            Added(Sibling, Root, "Sibling", 1)));
        return tree;
    }

    private static SceneTreeNode Node(SceneTreeModel tree, Guid id)
    {
        tree.TryReveal(id, out SceneTreeNode found).ShouldBeTrue();
        return found;
    }

    private static string[] Names(SceneTreeModel tree) => [.. tree.Rows.Select(r => r.Name)];

    // --- What is visible -----------------------------------------------------

    [Fact]
    public void A_collapsed_tree_projects_only_its_roots()
    {
        // Four nodes in the scene, one row on screen. This is the whole point.
        SceneTreeModel tree = Nested();

        Names(tree).ShouldBe(["Root"]);
        tree.Count.ShouldBe(4);
    }

    [Fact]
    public void Expanding_projects_the_children_in_order()
    {
        SceneTreeModel tree = Nested();

        tree.ToggleExpanded(tree.Rows[0]);

        Names(tree).ShouldBe(["Root", "Branch", "Sibling"]);
    }

    [Fact]
    public void A_grandchild_appears_only_when_its_own_parent_opens()
    {
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);

        Names(tree).ShouldNotContain("Leaf");

        tree.ToggleExpanded(tree.Rows[1]);
        Names(tree).ShouldBe(["Root", "Branch", "Leaf", "Sibling"]);
    }

    [Fact]
    public void Collapsing_takes_the_whole_subtree_out_of_the_projection()
    {
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);
        tree.ToggleExpanded(tree.Rows[1]);
        tree.Rows.Count.ShouldBe(4);

        tree.ToggleExpanded(tree.Rows[0]);

        Names(tree).ShouldBe(["Root"]);
        Node(tree, Branch).IsExpanded.ShouldBeTrue("collapsing a parent does not close what is under it");
    }

    // --- What each row carries ----------------------------------------------

    [Fact]
    public void Each_row_carries_its_own_depth()
    {
        // A flat list has no nesting left to indent by, so the depth travels
        // with the row or the hierarchy is invisible.
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);
        tree.ToggleExpanded(tree.Rows[1]);

        tree.Rows.Select(r => r.Depth).ShouldBe([0, 1, 2, 1]);
    }

    [Fact]
    public void A_row_knows_whether_it_has_an_expander()
    {
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);

        Node(tree, Root).HasChildren.ShouldBeTrue();
        Node(tree, Branch).HasChildren.ShouldBeTrue();
        Node(tree, Sibling).HasChildren.ShouldBeFalse();
    }

    [Fact]
    public void Emptying_a_group_takes_its_expander_away()
    {
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);
        Node(tree, Branch).HasChildren.ShouldBeTrue();

        tree.ApplyChanges(Batch(2,
            new SceneChange(SceneChangeKind.Removed, Leaf, Guid.Empty, "Leaf", -1, SceneNodeKind.Empty)));

        Node(tree, Branch).HasChildren.ShouldBeFalse();
    }

    [Fact]
    public void Reparenting_updates_the_depth_it_is_drawn_at()
    {
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);
        tree.ToggleExpanded(tree.Rows[1]);
        Node(tree, Leaf).Depth.ShouldBe(2);

        tree.ApplyChanges(Batch(2,
            new SceneChange(SceneChangeKind.Reparented, Leaf, Root, "Leaf", 2, SceneNodeKind.Empty)));

        Node(tree, Leaf).Depth.ShouldBe(1);
    }

    // --- Structural changes through the projection ---------------------------

    [Fact]
    public void A_node_added_under_a_collapsed_parent_adds_no_row()
    {
        SceneTreeModel tree = Nested();

        tree.ApplyChanges(Batch(2, Added(Guid.NewGuid(), Root, "Late", 2)));

        Names(tree).ShouldBe(["Root"]);
        tree.Count.ShouldBe(5, "it is in the scene, just not on screen");
    }

    [Fact]
    public void A_node_added_under_an_open_parent_appears_at_its_index()
    {
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);

        tree.ApplyChanges(Batch(2, Added(Guid.NewGuid(), Root, "Middle", 1)));

        Names(tree).ShouldBe(["Root", "Branch", "Middle", "Sibling"]);
    }

    [Fact]
    public void A_removed_node_leaves_the_projection()
    {
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);

        tree.ApplyChanges(Batch(2,
            new SceneChange(SceneChangeKind.Removed, Sibling, Guid.Empty, "Sibling", -1, SceneNodeKind.Empty)));

        Names(tree).ShouldBe(["Root", "Branch"]);
    }

    [Fact]
    public void Revealing_a_hidden_node_brings_its_row_into_the_projection()
    {
        SceneTreeModel tree = Nested();
        Names(tree).ShouldBe(["Root"]);

        tree.TryReveal(Leaf, out SceneTreeNode leaf).ShouldBeTrue();

        tree.Rows.ShouldContain(leaf);
        Names(tree).ShouldBe(["Root", "Branch", "Leaf", "Sibling"]);
    }

    // --- How it is applied ---------------------------------------------------

    [Fact]
    public void Expanding_inserts_rather_than_resetting_the_collection()
    {
        // A Reset drops the scroll position and the selection, so expanding one
        // group would throw the user back to the top of the scene.
        SceneTreeModel tree = Nested();
        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)tree.Rows).CollectionChanged += (_, e) => actions.Add(e.Action);

        tree.ToggleExpanded(tree.Rows[0]);

        actions.ShouldNotBeEmpty();
        actions.ShouldAllBe(a => a == NotifyCollectionChangedAction.Add);
    }

    [Fact]
    public void Collapsing_removes_rather_than_resetting_the_collection()
    {
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);

        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)tree.Rows).CollectionChanged += (_, e) => actions.Add(e.Action);

        tree.ToggleExpanded(tree.Rows[0]);

        actions.ShouldNotBeEmpty();
        actions.ShouldAllBe(a => a == NotifyCollectionChangedAction.Remove);
    }

    [Fact]
    public void An_unchanged_tree_notifies_nothing()
    {
        // Structural batches arrive whenever anything moves in the graph, and a
        // projection that churned on each one would rebuild the panel's rows
        // for nothing.
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);

        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)tree.Rows).CollectionChanged += (_, e) => actions.Add(e.Action);

        tree.ApplyChanges(Batch(2,
            new SceneChange(SceneChangeKind.Reparented, Sibling, Root, "Sibling", 1, SceneNodeKind.Empty)));

        actions.ShouldBeEmpty();
    }

    [Fact]
    public void Toggling_a_leaf_does_nothing()
    {
        // Its chevron is hidden, but a keyboard Right arrow reaches the same
        // call and must not put the node into a state its row cannot show.
        SceneTreeModel tree = Nested();
        tree.ToggleExpanded(tree.Rows[0]);
        SceneTreeNode sibling = Node(tree, Sibling);

        tree.ToggleExpanded(sibling);

        sibling.IsExpanded.ShouldBeFalse();
        tree.Rows.Count.ShouldBe(3);
    }
}
