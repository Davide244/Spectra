using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// What a node IS, derived from what it carries.
/// </summary>
/// <remarks>
/// <b>This is the only fact about a node that crosses to a UI</b> beyond its id,
/// its name and its place in the graph, so a wrong answer here is a wrong icon
/// on every row of that kind, forever, with nothing reporting it. The priority
/// order is the part worth pinning: a brush node legitimately carries a mesh
/// renderer too, and for a level editor what it IS is the brush.
/// </remarks>
public sealed class SceneNodeKindTests
{
    private static Brush Box(BrushOperation operation = BrushOperation.Additive) =>
        Brush.CreateBox(Vector3.Zero, new Vector3(1f, 1f, 1f)).WithOperation(operation);

    [Fact]
    public void A_bare_node_is_empty()
    {
        var scene = new Scene("Kinds");
        SceneNode node = scene.Root.CreateChild("Marker");

        SceneNodeClassifier.Classify(node).ShouldBe(SceneNodeKind.Empty);
    }

    [Fact]
    public void A_node_with_children_and_no_payload_is_a_group()
    {
        // There is no group marker on a node: grouping creates a plain parent,
        // so this IS the definition rather than an approximation of it.
        var scene = new Scene("Kinds");
        SceneNode node = scene.Root.CreateChild("Group");
        node.CreateChild("Child");

        SceneNodeClassifier.Classify(node).ShouldBe(SceneNodeKind.Group);
    }

    [Fact]
    public void A_group_stops_being_one_when_it_is_emptied()
    {
        // Which is what a tree should show: the row's icon follows the node,
        // not a flag somebody set once.
        var scene = new Scene("Kinds");
        SceneNode node = scene.Root.CreateChild("Group");
        SceneNode child = node.CreateChild("Child");

        node.RemoveChild(child);

        SceneNodeClassifier.Classify(node).ShouldBe(SceneNodeKind.Empty);
    }

    [Fact]
    public void An_additive_world_brush_is_world_geometry()
    {
        var scene = new Scene("Kinds");
        SceneNode node = scene.Root.CreateChild("Wall");
        node.Brush = Box();

        SceneNodeClassifier.Classify(node).ShouldBe(SceneNodeKind.BrushWorld);
    }

    [Fact]
    public void An_additive_part_brush_is_a_part()
    {
        var scene = new Scene("Kinds");
        SceneNode node = scene.Root.CreateChild("Crate");
        node.Brush = Box();
        node.BrushKind = BrushKind.Part;

        SceneNodeClassifier.Classify(node).ShouldBe(SceneNodeKind.BrushPart);
    }

    [Fact]
    public void A_subtractive_brush_outranks_its_kind()
    {
        // A subtractive brush renders nothing at all, so the tree is the only
        // place one can be seen; calling it "world" or "part" would hide the
        // one thing about it that matters.
        var scene = new Scene("Kinds");
        SceneNode node = scene.Root.CreateChild("DoorwayCut");
        node.Brush = Box(BrushOperation.Subtractive);

        SceneNodeClassifier.Classify(node).ShouldBe(SceneNodeKind.BrushSubtractive);

        node.BrushKind = BrushKind.Part;
        SceneNodeClassifier.Classify(node).ShouldBe(SceneNodeKind.BrushSubtractive);
    }

    [Fact]
    public void A_light_is_a_light()
    {
        var scene = new Scene("Kinds");
        SceneNode node = scene.Root.CreateChild("Sun");
        node.Light = new Light();

        SceneNodeClassifier.Classify(node).ShouldBe(SceneNodeKind.Light);
    }

    [Fact]
    public void A_brush_that_also_carries_children_is_still_a_brush()
    {
        // Priority order, stated as a test: the payload wins over the shape of
        // the graph around it.
        var scene = new Scene("Kinds");
        SceneNode node = scene.Root.CreateChild("Wall");
        node.Brush = Box();
        node.CreateChild("Decal");

        SceneNodeClassifier.Classify(node).ShouldBe(SceneNodeKind.BrushWorld);
    }
}
