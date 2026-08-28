using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The tree's three additions for a scene of a few hundred nodes: what a node
/// IS, what the filter does to it, and the notification that makes either of
/// them visible.
/// </summary>
/// <remarks>
/// <b>Each of these fails silently if it regresses.</b> A missing notification
/// leaves a stale name that still looks like a name; a filter that rebuilds
/// instead of flagging still shows the right rows, just slowly and with the
/// user's expansion state thrown away; a selection walk over every node still
/// produces a correct highlight. Nothing here would surface as an exception,
/// which is exactly why it is pinned.
/// </remarks>
public sealed class SceneTreeFilterTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Lamp = Guid.NewGuid();
    private static readonly Guid Wall = Guid.NewGuid();
    private static readonly Guid Nested = Guid.NewGuid();

    private static SceneTreeModel NewTree() => new(new EngineHost(NullLogger.Instance), NullLogger.Instance);

    private static FrameSnapshot Batch(long frame, params SceneChange[] changes) => new()
    {
        FrameNumber = frame,
        Changes = changes,
    };

    private static SceneChange Added(Guid id, Guid parent, string name, int index, SceneNodeKind kind) =>
        new(SceneChangeKind.Added, id, parent, name, index, kind);

    private static SceneTreeModel Populated()
    {
        SceneTreeModel tree = NewTree();
        tree.ApplyChanges(Batch(1,
            Added(Root, Guid.Empty, "Root", -1, SceneNodeKind.Group),
            Added(Lamp, Root, "LampWarm", 0, SceneNodeKind.Light),
            Added(Wall, Root, "WallNorth", 1, SceneNodeKind.BrushWorld),
            Added(Nested, Wall, "DoorwayCut", 0, SceneNodeKind.BrushSubtractive)));
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

    // --- Kind ----------------------------------------------------------------

    [Fact]
    public void A_nodes_kind_travels_with_its_change()
    {
        // Without it the panel is a list of names, and a subtractive brush --
        // which renders nothing at all and is unpickable in the viewport -- has
        // nowhere left that it can be seen.
        SceneTreeModel tree = Populated();

        Find(tree, "LampWarm").Kind.ShouldBe(SceneNodeKind.Light);
        Find(tree, "WallNorth").Kind.ShouldBe(SceneNodeKind.BrushWorld);
        Find(tree, "DoorwayCut").Kind.ShouldBe(SceneNodeKind.BrushSubtractive);
        Find(tree, "Root").Kind.ShouldBe(SceneNodeKind.Group);
    }

    [Fact]
    public void A_reparent_carries_the_kind_across_with_it()
    {
        SceneTreeModel tree = Populated();
        SceneTreeNode lamp = Find(tree, "LampWarm");

        tree.ApplyChanges(Batch(2,
            new SceneChange(SceneChangeKind.Reparented, Lamp, Wall, "LampWarm", 1, SceneNodeKind.Light)));

        lamp.Kind.ShouldBe(SceneNodeKind.Light);
    }

    // --- Notification --------------------------------------------------------

    [Fact]
    public void Renaming_a_node_raises_a_change()
    {
        // The two paths that rewrite a node in place -- a reparent, and a re-add
        // under the same id when a delete is undone -- both go through Name.
        // Before this the binding read once and the tree showed a name the node
        // no longer had.
        SceneTreeModel tree = Populated();
        SceneTreeNode wall = Find(tree, "WallNorth");

        var raised = new List<string?>();
        ((INotifyPropertyChanged)wall).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tree.ApplyChanges(Batch(2,
            new SceneChange(SceneChangeKind.Reparented, Wall, Root, "WallSouth", 1, SceneNodeKind.BrushWorld)));

        wall.Name.ShouldBe("WallSouth");
        raised.ShouldContain(nameof(SceneTreeNode.Name));
    }

    [Fact]
    public void Setting_a_property_to_the_value_it_already_has_raises_nothing()
    {
        // Selection is reapplied from a snapshot about thirty times a second.
        // Without the equality guard that is a notification storm proportional
        // to the whole scene rather than to what changed.
        SceneTreeModel tree = Populated();
        SceneTreeNode lamp = Find(tree, "LampWarm");

        var raised = new List<string?>();
        ((INotifyPropertyChanged)lamp).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        lamp.Name = "LampWarm";
        lamp.IsSelected = false;
        lamp.Kind = SceneNodeKind.Light;

        raised.ShouldBeEmpty();
    }

    [Fact]
    public void Reapplying_an_identical_selection_touches_nothing()
    {
        // The delta set, from the other side: an unchanged selection must cost
        // nothing at all, because it is the overwhelmingly common case.
        SceneTreeModel tree = Populated();
        SceneTreeNode lamp = Find(tree, "LampWarm");
        tree.ApplySelection([Lamp]);

        var raised = new List<string?>();
        ((INotifyPropertyChanged)lamp).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tree.ApplySelection([Lamp]);
        tree.ApplySelection([Lamp]);

        raised.ShouldBeEmpty();
        lamp.IsSelected.ShouldBeTrue();
    }

    [Fact]
    public void A_changed_selection_clears_only_what_left_and_sets_only_what_arrived()
    {
        SceneTreeModel tree = Populated();
        tree.ApplySelection([Lamp]);

        tree.ApplySelection([Wall]);

        Find(tree, "LampWarm").IsSelected.ShouldBeFalse();
        Find(tree, "WallNorth").IsSelected.ShouldBeTrue();
    }

    // --- Filter --------------------------------------------------------------

    [Fact]
    public void A_name_filter_marks_matches_and_dims_the_rest()
    {
        SceneTreeModel tree = Populated();

        tree.ApplyFilter("wall");

        Find(tree, "WallNorth").Match.ShouldBe(SceneTreeMatch.Match);
        Find(tree, "LampWarm").Match.ShouldBe(SceneTreeMatch.None);
        tree.MatchCount.ShouldBe(1);
    }

    [Fact]
    public void A_type_filter_selects_by_kind()
    {
        SceneTreeModel tree = Populated();

        tree.ApplyFilter("t:light");

        Find(tree, "LampWarm").Match.ShouldBe(SceneTreeMatch.Match);
        Find(tree, "WallNorth").Match.ShouldBe(SceneTreeMatch.None);
        tree.MatchCount.ShouldBe(1);
    }

    [Fact]
    public void A_plural_type_filter_still_works()
    {
        // Returning nothing because somebody typed the plural is worse than
        // having no filter at all: it reads as "there are none".
        SceneTreeModel tree = Populated();

        tree.ApplyFilter("t:lights");

        tree.MatchCount.ShouldBe(1);
    }

    [Fact]
    public void The_ancestors_of_a_match_are_context_rather_than_dimmed()
    {
        // A match three levels down is invisible if the chain above it is
        // dimmed to nothing.
        SceneTreeModel tree = Populated();

        tree.ApplyFilter("doorway");

        Find(tree, "DoorwayCut").Match.ShouldBe(SceneTreeMatch.Match);
        Find(tree, "WallNorth").Match.ShouldBe(SceneTreeMatch.Ancestor);
        Find(tree, "Root").Match.ShouldBe(SceneTreeMatch.Ancestor);
        Find(tree, "LampWarm").Match.ShouldBe(SceneTreeMatch.None);

        // Context is not a match: the header's count must not include it.
        tree.MatchCount.ShouldBe(1);
    }

    [Fact]
    public void Filtering_never_removes_a_row()
    {
        // Hiding rows collapses the hierarchy around every match, which
        // destroys the user's spatial memory of the scene on the first
        // keystroke and rebuilds it differently on the second.
        SceneTreeModel tree = Populated();
        int before = tree.Count;

        tree.ApplyFilter("nothing matches this");

        tree.Count.ShouldBe(before);
        tree.Roots.Count.ShouldBe(1);
        Find(tree, "LampWarm").ShouldNotBeNull();
        tree.MatchCount.ShouldBe(0);
    }

    [Fact]
    public void An_empty_filter_restores_everything()
    {
        SceneTreeModel tree = Populated();
        tree.ApplyFilter("wall");

        tree.ApplyFilter("");

        Walk(tree.Roots).ShouldAllBe(n => n.Match == SceneTreeMatch.Match);
        tree.MatchCount.ShouldBe(tree.Count);
    }

    [Fact]
    public void A_node_added_under_a_live_filter_is_classified_rather_than_admitted()
    {
        // A new node starts out matching, which is the only sane default for an
        // unfiltered tree. Under a live filter that would put a duplicate at
        // full strength beside the dimmed row it was copied from.
        SceneTreeModel tree = Populated();
        tree.ApplyFilter("t:light");

        var extra = Guid.NewGuid();
        tree.ApplyChanges(Batch(2, Added(extra, Root, "PillarC", 2, SceneNodeKind.BrushWorld)));

        Find(tree, "PillarC").Match.ShouldBe(SceneTreeMatch.None);
        tree.MatchCount.ShouldBe(1);
    }
}
