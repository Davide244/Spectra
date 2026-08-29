using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The shell's mirror of the scene graph, replaying the engine's change log.
/// </summary>
/// <remarks>
/// <b>Every claim here is about a view staying equal to a graph it cannot
/// see.</b> The tree is fed ids and names across a thread boundary and never
/// touches a node, so the only thing keeping it correct is that the replay is
/// faithful. A tree that drifts looks entirely plausible, which is why this is
/// pinned rather than eyeballed.
/// </remarks>
public sealed class SceneTreeModelTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid ChildA = Guid.NewGuid();
    private static readonly Guid ChildB = Guid.NewGuid();
    private static readonly Guid GrandChild = Guid.NewGuid();

    private static SceneTreeModel NewTree() => new(new EngineHost(NullLogger.Instance), NullLogger.Instance);

    private static FrameSnapshot Batch(long frame, params SceneChange[] changes) => new()
    {
        FrameNumber = frame,
        Changes = changes,
    };

    private static SceneChange Added(Guid id, Guid parent, string name, int index) =>
        new(SceneChangeKind.Added, id, parent, name, index);

    // --- Structure -----------------------------------------------------------

    [Fact]
    public void A_batch_of_adds_builds_the_tree_it_describes()
    {
        SceneTreeModel tree = NewTree();

        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 0),
            Added(ChildB, Root, "B", 1),
            Added(GrandChild, ChildA, "Deep", 0)));

        tree.Roots.Count.ShouldBe(1);
        tree.Roots[0].Name.ShouldBe("Root");
        tree.Roots[0].Children.Count.ShouldBe(2);
        tree.Roots[0].Children[0].Name.ShouldBe("A");
        tree.Roots[0].Children[1].Name.ShouldBe("B");
        tree.Roots[0].Children[0].Children[0].Name.ShouldBe("Deep");
        tree.Count.ShouldBe(4);
    }

    [Fact]
    public void Sibling_order_follows_the_reported_index_rather_than_arrival()
    {
        // Sibling index IS traversal order IS the static world's placement-slot
        // order, so a tree that shows a different order shows a different level
        // from the one that will compile.
        SceneTreeModel tree = NewTree();

        tree.ApplyChanges(Batch(1, Added(Root, Guid.Empty, "Root", -1)));
        tree.ApplyChanges(Batch(2, Added(ChildA, Root, "second", 0)));
        tree.ApplyChanges(Batch(3, Added(ChildB, Root, "first", 0)));

        tree.Roots[0].Children[0].Name.ShouldBe("first");
        tree.Roots[0].Children[1].Name.ShouldBe("second");
    }

    [Fact]
    public void A_child_whose_index_is_past_the_end_is_appended_rather_than_dropped()
    {
        // A parent's add and a child's can arrive in one batch, and a child
        // reported at index 3 of a list the tree has only two of is a batch
        // mid-replay, not corruption.
        SceneTreeModel tree = NewTree();

        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 7)));

        tree.Roots[0].Children.Count.ShouldBe(1);
        tree.Roots[0].Children[0].Name.ShouldBe("A");
    }

    [Fact]
    public void A_node_whose_parent_is_unknown_lands_at_the_top_rather_than_vanishing()
    {
        // The engine only reports what changed, and the shell attaches partway
        // through a scene's life: a node under a parent the tree never heard of
        // must still be visible, because the alternative is a silently missing
        // subtree.
        SceneTreeModel tree = NewTree();

        tree.ApplyChanges(Batch(1, Added(ChildA, Root, "Orphan", 0)));

        tree.Roots.Count.ShouldBe(1);
        tree.Roots[0].Name.ShouldBe("Orphan");
    }

    [Fact]
    public void A_top_level_row_opens_by_default_so_the_panel_shows_the_scene()
    {
        // The engine reports one top-level node - the scene root - so without
        // this a freshly opened project presents a whole level as a single
        // collapsed row reading "Root". One level only: expanding everything
        // would put thousands of rows into a panel that shows thirty-five.
        SceneTreeModel tree = NewTree();

        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 0),
            Added(GrandChild, ChildA, "Deep", 0),
            Added(ChildB, Root, "B", 1)));

        tree.Rows.Select(r => r.Name).ShouldBe(["Root", "A", "B"]);
        tree.Roots[0].IsExpanded.ShouldBeTrue();
        tree.Roots[0].Children[0].IsExpanded.ShouldBeFalse("only the top level opens");
    }

    // --- Row patching --------------------------------------------------------

    [Fact]
    public void Rows_report_that_they_are_being_patched_while_they_change()
    {
        // The flag a view reads to tell the framework's own reaction to rows
        // leaving the list from a user gesture. A list control drops a removed
        // item from its selection and reports that exactly as it reports a
        // click, so without this a collapsed group reads as "the user
        // deselected everything inside it" - and the shell dutifully told the
        // engine so, losing a selection that could not be got back.
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 0),
            Added(GrandChild, ChildA, "Deep", 0)));
        tree.TryReveal(GrandChild, out _).ShouldBeTrue();

        var seen = new List<bool>();
        tree.Rows.CollectionChanged += (_, _) => seen.Add(tree.IsPatchingRows);

        tree.ToggleExpanded(tree.Roots[0].Children[0]);

        seen.ShouldNotBeEmpty("collapsing hides a row, which changes the projection");
        seen.ShouldAllBe(patching => patching);

        // ...and the flag is down again once the patch is finished, or the
        // panel would ignore the user's next real click.
        tree.IsPatchingRows.ShouldBeFalse();
    }

    // --- Rename --------------------------------------------------------------

    [Fact]
    public void A_rename_updates_the_row_in_place_without_moving_it()
    {
        // Same node object, same position, new name: an expanded subtree and
        // the row's identity survive a rename, which is what makes the tree's
        // patched projection cheap and the selection stable through one.
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "Before", 0),
            Added(ChildB, Root, "B", 1)));
        SceneTreeNode row = tree.Roots[0].Children[0];

        tree.ApplyChanges(Batch(2,
            new SceneChange(SceneChangeKind.Renamed, ChildA, Root, "After", 0)));

        tree.Roots[0].Children[0].ShouldBeSameAs(row);
        row.Name.ShouldBe("After");
        tree.Count.ShouldBe(3);
    }

    [Fact]
    public void A_rename_for_an_id_the_tree_never_heard_of_is_ignored()
    {
        // Ordinary lag, not corruption: the node's Added change may ride a
        // later snapshot than the rename that followed it on the render thread.
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1, Added(Root, Guid.Empty, "Root", -1)));

        Should.NotThrow(() => tree.ApplyChanges(Batch(2,
            new SceneChange(SceneChangeKind.Renamed, Guid.NewGuid(), Root, "Ghost", 0))));

        tree.Count.ShouldBe(1);
    }

    // --- Removal -------------------------------------------------------------

    [Fact]
    public void Removing_a_node_takes_its_whole_subtree_with_it()
    {
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 0),
            Added(GrandChild, ChildA, "Deep", 0)));

        tree.ApplyChanges(Batch(2, new SceneChange(SceneChangeKind.Removed, ChildA, Guid.Empty, "A", -1)));

        tree.Roots[0].Children.ShouldBeEmpty();
        tree.Count.ShouldBe(1, "the child and its descendant are both forgotten");
    }

    [Fact]
    public void An_undone_delete_puts_the_node_back_under_the_same_id()
    {
        // Structural commands address nodes by id precisely so that undo can
        // recreate one; the tree has to accept that re-add rather than treat it
        // as a duplicate.
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 0),
            Added(ChildB, Root, "B", 1)));

        tree.ApplyChanges(Batch(2, new SceneChange(SceneChangeKind.Removed, ChildA, Guid.Empty, "A", -1)));
        tree.ApplyChanges(Batch(3, Added(ChildA, Root, "A", 0)));

        tree.Roots[0].Children.Count.ShouldBe(2);
        tree.Roots[0].Children[0].Name.ShouldBe("A", "and back at its original index, not appended");
        tree.Roots[0].Children[1].Name.ShouldBe("B");
    }

    // --- Reparenting ---------------------------------------------------------

    [Fact]
    public void A_reparent_moves_the_node_without_recreating_it()
    {
        // A reparent raises neither membership event in the engine, which is
        // why Scene.NodeReparented exists at all. The tree must move the same
        // object, or a drag collapses every expanded subtree under it.
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 0),
            Added(ChildB, Root, "B", 1),
            Added(GrandChild, ChildA, "Deep", 0)));

        SceneTreeNode moved = tree.Roots[0].Children[0];

        tree.ApplyChanges(Batch(2, new SceneChange(SceneChangeKind.Reparented, ChildA, ChildB, "A", 0)));

        tree.Roots[0].Children.Count.ShouldBe(1);
        tree.Roots[0].Children[0].Name.ShouldBe("B");
        tree.Roots[0].Children[0].Children[0].ShouldBeSameAs(moved);
        moved.Children[0].Name.ShouldBe("Deep", "the subtree travels with it");
        tree.Count.ShouldBe(4);
    }

    [Fact]
    public void A_reorder_under_one_parent_is_reported_and_applied()
    {
        // Same parent, different index: nothing entered or left the graph, and
        // the order that changed is the one the static world compiles in.
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 0),
            Added(ChildB, Root, "B", 1)));

        tree.ApplyChanges(Batch(2, new SceneChange(SceneChangeKind.Reparented, ChildA, Root, "A", 1)));

        tree.Roots[0].Children[0].Name.ShouldBe("B");
        tree.Roots[0].Children[1].Name.ShouldBe("A");
    }

    // --- Snapshot discipline -------------------------------------------------

    [Fact]
    public void The_same_snapshot_applied_twice_changes_nothing_the_second_time()
    {
        // A shell pumps faster than the engine publishes, so seeing one twice is
        // ordinary. Replaying its adds would re-insert each node at the index it
        // was reported at, which is the wrong index once its siblings moved.
        SceneTreeModel tree = NewTree();
        FrameSnapshot batch = Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 0),
            Added(ChildB, Root, "B", 1));

        tree.ApplyChanges(batch);
        tree.ApplyChanges(batch);

        tree.Count.ShouldBe(3);
        tree.Roots[0].Children.Count.ShouldBe(2);
        tree.Roots[0].Children[0].Name.ShouldBe("A");
    }

    [Fact]
    public void An_overflowed_snapshot_asks_the_engine_for_the_whole_graph()
    {
        // The engine says "you have fallen behind, or the scene was swapped".
        // The only correct answer is to re-read the graph on the thread that
        // owns it, which is a queued command like every other read of live
        // state.
        var host = new EngineHost(NullLogger.Instance);
        var tree = new SceneTreeModel(host, NullLogger.Instance);

        tree.ApplyChanges(new FrameSnapshot { FrameNumber = 1, ChangesOverflowed = true });

        host.PendingCommandCount.ShouldBe(1);
    }

    [Fact]
    public void A_run_of_overflowed_snapshots_asks_once()
    {
        // Otherwise every frame the shell is behind queues another full walk of
        // the graph onto the render thread, which is the opposite of catching
        // up.
        var host = new EngineHost(NullLogger.Instance);
        var tree = new SceneTreeModel(host, NullLogger.Instance);

        tree.ApplyChanges(new FrameSnapshot { FrameNumber = 1, ChangesOverflowed = true });
        tree.ApplyChanges(new FrameSnapshot { FrameNumber = 2, ChangesOverflowed = true });
        tree.ApplyChanges(new FrameSnapshot { FrameNumber = 3, ChangesOverflowed = true });

        host.PendingCommandCount.ShouldBe(1);
    }

    [Fact]
    public void The_rebuild_walks_the_live_graph_in_the_order_the_log_would_have_reported_it()
    {
        // The reply is a pre-order list of Added changes, deliberately the same
        // shape the change log emits, so the tree has one apply path rather
        // than two. Pre-order is not incidental: a child inserted before its
        // parent exists lands at the top of the tree instead of under it.
        var scene = new Scene("Rebuild");
        SceneNode a = scene.Root.CreateChild("A");
        SceneNode deep = a.CreateChild("Deep");
        SceneNode b = scene.Root.CreateChild("B");

        var flattened = new List<SceneChange>();
        SceneTreeModel.Flatten(scene.Root, flattened);

        flattened.Select(c => c.Name).ShouldBe(["Root", "A", "Deep", "B"]);
        flattened.ShouldAllBe(c => c.Kind == SceneChangeKind.Added);
        flattened[1].ParentId.ShouldBe(scene.Root.Id);
        flattened[2].ParentId.ShouldBe(a.Id);
        flattened[3].SiblingIndex.ShouldBe(1);

        // And replaying it produces the graph it came from.
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1, [.. flattened]));

        tree.Count.ShouldBe(4);
        tree.Roots[0].Children[0].Children[0].Id.ShouldBe(deep.Id);
        tree.Roots[0].Children[1].Id.ShouldBe(b.Id);
    }

    // --- Selection -----------------------------------------------------------

    [Fact]
    public void Selection_marks_exactly_the_reported_nodes()
    {
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1),
            Added(ChildA, Root, "A", 0),
            Added(ChildB, Root, "B", 1)));

        tree.ApplySelection(new List<Guid> { ChildB });

        tree.Roots[0].Children[0].IsSelected.ShouldBeFalse();
        tree.Roots[0].Children[1].IsSelected.ShouldBeTrue();

        tree.ApplySelection(Array.Empty<Guid>());
        tree.Roots[0].Children[1].IsSelected.ShouldBeFalse();
    }

    [Fact]
    public void A_selected_id_the_tree_has_never_heard_of_is_ignored()
    {
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1, Added(Root, Guid.Empty, "Root", -1)));

        Should.NotThrow(() => tree.ApplySelection(new List<Guid> { Guid.NewGuid() }));
        tree.Roots[0].IsSelected.ShouldBeFalse();
    }
}
