using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The batched half of <see cref="SelectionSet"/>: <c>SetRange</c>,
/// <c>AddRange</c>, <c>ToggleRange</c> and the mode-carrying <c>Apply</c>.
/// </summary>
/// <remarks>
/// <b>The contract these exist to enforce is the event count.</b> Box select
/// routinely changes hundreds of nodes at once; doing it through the
/// single-node API would raise hundreds of <c>SelectionChanged</c> events and
/// thrash every UI binding watching the selection. One operation, one event —
/// and none at all when the batch turns out to describe the selection that was
/// already there, which is exactly what a marquee held still over the same
/// nodes produces every frame it is re-committed.
/// </remarks>
public sealed class SelectionRangeTests
{
    [Fact]
    public void SetRange_replaces_everything_and_fires_once()
    {
        var (scene, a, b, c) = CreateScene();
        scene.Selection.Select(a);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.SetRange([b, c]);

        scene.Selection.Items.ShouldBe(new[] { b, c });
        fired.ShouldBe(1);
    }

    [Fact]
    public void SetRange_with_the_selection_that_is_already_there_fires_nothing()
    {
        var (scene, a, b, _) = CreateScene();
        scene.Selection.SetRange([a, b]);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.SetRange([a, b]);

        fired.ShouldBe(0);
    }

    [Fact]
    public void SetRange_that_only_reorders_is_still_a_change()
    {
        var (scene, a, b, _) = CreateScene();
        scene.Selection.SetRange([a, b]);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.SetRange([b, a]);

        // Items promises a stable order that editor UI renders directly, so a
        // reshuffle of the same nodes is something a listener must hear about.
        scene.Selection.Items.ShouldBe(new[] { b, a });
        fired.ShouldBe(1);
    }

    [Fact]
    public void An_empty_SetRange_clears_the_selection_in_one_event()
    {
        var (scene, a, b, _) = CreateScene();
        scene.Selection.SetRange([a, b]);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.SetRange([]);

        scene.Selection.Count.ShouldBe(0);
        fired.ShouldBe(1);
    }

    [Fact]
    public void AddRange_appends_only_what_is_missing_and_fires_once()
    {
        var (scene, a, b, c) = CreateScene();
        scene.Selection.Select(a);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.AddRange([a, b, c]);

        scene.Selection.Items.ShouldBe(new[] { a, b, c });
        fired.ShouldBe(1);
    }

    [Fact]
    public void AddRange_of_nodes_already_selected_fires_nothing()
    {
        var (scene, a, b, _) = CreateScene();
        scene.Selection.SetRange([a, b]);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.AddRange([b, a]);

        fired.ShouldBe(0);
    }

    [Fact]
    public void ToggleRange_flips_each_node_exactly_once()
    {
        var (scene, a, b, c) = CreateScene();
        scene.Selection.SetRange([a, b]);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.ToggleRange([b, c]);

        scene.Selection.Items.ShouldBe(new[] { a, c });
        fired.ShouldBe(1);
    }

    [Fact]
    public void A_node_listed_twice_in_a_toggle_is_considered_once()
    {
        var (scene, a, _, _) = CreateScene();

        scene.Selection.ToggleRange([a, a]);

        // Flipping twice would leave it unselected — and would be impossible for
        // a caller to notice until a duplicate showed up in real data.
        scene.Selection.Items.ShouldBe(new[] { a });
    }

    [Fact]
    public void A_foreign_node_is_rejected_before_anything_is_applied()
    {
        var (scene, a, b, _) = CreateScene();
        scene.Selection.Select(a);
        var other = new Scene("Other");
        SceneNode foreign = other.Root.CreateChild("Foreign");

        Should.Throw<ArgumentException>(() => scene.Selection.SetRange([b, foreign]));

        // The whole batch is validated first, so the rejected call is a no-op
        // rather than a half-applied selection.
        scene.Selection.Items.ShouldBe(new[] { a });
    }

    [Fact]
    public void Apply_carries_the_mode_as_data()
    {
        var (scene, a, b, c) = CreateScene();
        scene.Selection.Select(a);

        scene.Selection.Apply([b], SelectionUpdate.Add);
        scene.Selection.Items.ShouldBe(new[] { a, b });

        scene.Selection.Apply([a, c], SelectionUpdate.Toggle);
        scene.Selection.Items.ShouldBe(new[] { b, c });

        scene.Selection.Apply([c], SelectionUpdate.Replace);
        scene.Selection.Items.ShouldBe(new[] { c });
    }

    [Fact]
    public void Selecting_a_thousand_nodes_raises_one_event()
    {
        var scene = new Scene("Batch");
        var nodes = new SceneNode[1000];
        for (int i = 0; i < nodes.Length; i++)
            nodes[i] = scene.Root.CreateChild($"N{i}");

        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.SetRange(nodes);

        scene.Selection.Count.ShouldBe(1000);
        fired.ShouldBe(1);
    }

    private static (Scene Scene, SceneNode A, SceneNode B, SceneNode C) CreateScene()
    {
        var scene = new Scene("Selection");
        return (scene, scene.Root.CreateChild("A"), scene.Root.CreateChild("B"), scene.Root.CreateChild("C"));
    }
}
