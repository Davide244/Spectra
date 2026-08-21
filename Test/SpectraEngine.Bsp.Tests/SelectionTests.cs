using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="SelectionSet"/> semantics: Select replaces, Add appends in
/// stable insertion order, Toggle flips, and SelectionChanged fires exactly
/// once per operation that actually changed something. Only nodes belonging
/// to the owning scene are admissible, and nodes leaving the scene are
/// deselected automatically via the scene's NodeRemoved event.
/// </summary>
public sealed class SelectionTests
{
    [Fact]
    public void Select_replaces_the_previous_selection_and_fires_once()
    {
        var (scene, a, b, c) = CreateSceneWithThreeNodes();
        scene.Selection.Add(a);
        scene.Selection.Add(b);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.Select(c);

        scene.Selection.Items.ShouldBe(new[] { c });
        fired.ShouldBe(1); // one replace, not one clear + one add
    }

    [Fact]
    public void Select_of_the_already_sole_selected_node_fires_nothing()
    {
        var (scene, a, _, _) = CreateSceneWithThreeNodes();
        scene.Selection.Select(a);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.Select(a);

        fired.ShouldBe(0);
        scene.Selection.Items.ShouldBe(new[] { a });
    }

    [Fact]
    public void Add_appends_in_insertion_order()
    {
        var (scene, a, b, c) = CreateSceneWithThreeNodes();

        scene.Selection.Add(b);
        scene.Selection.Add(a);
        scene.Selection.Add(c);

        // Stable insertion order, not tree or alphabetical order.
        scene.Selection.Items.ShouldBe(new[] { b, a, c });
        scene.Selection.Count.ShouldBe(3);
    }

    [Fact]
    public void Add_of_an_already_selected_node_is_a_silent_no_op()
    {
        var (scene, a, b, _) = CreateSceneWithThreeNodes();
        scene.Selection.Add(a);
        scene.Selection.Add(b);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.Add(a);

        fired.ShouldBe(0);
        scene.Selection.Items.ShouldBe(new[] { a, b });
    }

    [Fact]
    public void Toggle_adds_when_absent_and_removes_when_present()
    {
        var (scene, a, _, _) = CreateSceneWithThreeNodes();
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.Toggle(a);
        scene.Selection.Contains(a).ShouldBeTrue();
        fired.ShouldBe(1);

        scene.Selection.Toggle(a);
        scene.Selection.Contains(a).ShouldBeFalse();
        scene.Selection.Count.ShouldBe(0);
        fired.ShouldBe(2);
    }

    [Fact]
    public void Deselect_removes_only_the_given_node_and_preserves_order()
    {
        var (scene, a, b, c) = CreateSceneWithThreeNodes();
        scene.Selection.Add(a);
        scene.Selection.Add(b);
        scene.Selection.Add(c);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.Deselect(b);

        scene.Selection.Items.ShouldBe(new[] { a, c });
        fired.ShouldBe(1);
    }

    [Fact]
    public void Deselect_of_an_unselected_node_fires_nothing()
    {
        var (scene, a, b, _) = CreateSceneWithThreeNodes();
        scene.Selection.Add(a);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.Deselect(b);

        fired.ShouldBe(0);
        scene.Selection.Items.ShouldBe(new[] { a });
    }

    [Fact]
    public void Clear_empties_the_selection_and_fires_once()
    {
        var (scene, a, b, _) = CreateSceneWithThreeNodes();
        scene.Selection.Add(a);
        scene.Selection.Add(b);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.Clear();

        scene.Selection.Count.ShouldBe(0);
        fired.ShouldBe(1);
    }

    [Fact]
    public void Clear_of_an_empty_selection_fires_nothing()
    {
        var (scene, _, _, _) = CreateSceneWithThreeNodes();
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Selection.Clear();

        fired.ShouldBe(0);
    }

    // --- Ownership rules ----------------------------------------------------

    [Fact]
    public void Selecting_a_node_from_another_scene_throws()
    {
        var (scene, _, _, _) = CreateSceneWithThreeNodes();
        var other = new Scene("Other");
        SceneNode foreign = other.Root.CreateChild("foreign");

        Should.Throw<ArgumentException>(() => scene.Selection.Select(foreign));
        Should.Throw<ArgumentException>(() => scene.Selection.Add(foreign));
        Should.Throw<ArgumentException>(() => scene.Selection.Toggle(foreign));
        scene.Selection.Count.ShouldBe(0);
    }

    [Fact]
    public void Selecting_a_detached_node_throws()
    {
        var (scene, _, _, _) = CreateSceneWithThreeNodes();
        var detached = new SceneNode("detached");

        Should.Throw<ArgumentException>(() => scene.Selection.Select(detached));
        Should.Throw<ArgumentException>(() => scene.Selection.Add(detached));
        Should.Throw<ArgumentException>(() => scene.Selection.Toggle(detached));
        scene.Selection.Count.ShouldBe(0);
    }

    // --- Auto-deselection ---------------------------------------------------

    [Fact]
    public void Removing_a_selected_node_auto_deselects_it()
    {
        var (scene, a, b, _) = CreateSceneWithThreeNodes();
        scene.Selection.Add(a);
        scene.Selection.Add(b);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Root.RemoveChild(a);

        scene.Selection.Items.ShouldBe(new[] { b });
        fired.ShouldBe(1);
    }

    [Fact]
    public void Removing_a_subtree_deselects_every_selected_descendant()
    {
        var scene = new Scene("Test");
        SceneNode group = scene.Root.CreateChild("group");
        SceneNode child = group.CreateChild("child");
        SceneNode kept = scene.Root.CreateChild("kept");
        scene.Selection.Add(group);
        scene.Selection.Add(child);
        scene.Selection.Add(kept);

        scene.Root.RemoveChild(group);

        // Both departed nodes are dropped; the unrelated node survives.
        scene.Selection.Items.ShouldBe(new[] { kept });
    }

    [Fact]
    public void Removing_an_unselected_node_fires_no_selection_event()
    {
        var (scene, a, b, _) = CreateSceneWithThreeNodes();
        scene.Selection.Add(a);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        scene.Root.RemoveChild(b);

        fired.ShouldBe(0);
        scene.Selection.Items.ShouldBe(new[] { a });
    }

    [Fact]
    public void Reparenting_within_the_scene_keeps_the_selection()
    {
        // A move inside the scene fires no NodeRemoved, so the selection must
        // survive it untouched.
        var scene = new Scene("Test");
        SceneNode oldParent = scene.Root.CreateChild("old-parent");
        SceneNode newParent = scene.Root.CreateChild("new-parent");
        SceneNode moved = oldParent.CreateChild("moved");
        scene.Selection.Select(moved);
        int fired = 0;
        scene.Selection.SelectionChanged += () => fired++;

        newParent.AddChild(moved);

        scene.Selection.Items.ShouldBe(new[] { moved });
        fired.ShouldBe(0);
    }

    [Fact]
    public void Moving_a_selected_node_to_another_scene_deselects_it()
    {
        var (scene, a, _, _) = CreateSceneWithThreeNodes();
        scene.Selection.Select(a);
        var other = new Scene("Other");

        other.Root.AddChild(a);

        scene.Selection.Count.ShouldBe(0);
        // And it never leaked into the destination's selection.
        other.Selection.Count.ShouldBe(0);
    }

    private static (Scene Scene, SceneNode A, SceneNode B, SceneNode C) CreateSceneWithThreeNodes()
    {
        var scene = new Scene("Test");
        return (scene,
            scene.Root.CreateChild("a"),
            scene.Root.CreateChild("b"),
            scene.Root.CreateChild("c"));
    }
}
