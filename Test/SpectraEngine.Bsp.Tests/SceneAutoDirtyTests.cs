using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Automatic static-world dirtying: <see cref="SceneNode"/> must mark the
/// owning scene's world dirty for exactly the edits that change brush
/// placements — brush attach/detach/replace, transform edits on any node with
/// a brush somewhere in its subtree, and (re)parenting of subtrees containing
/// brushes — and for nothing else, or every camera move would trigger a CSG
/// recompile. The subtree brush counters that make the transform test O(1)
/// are private, so counter correctness after reparenting is verified through
/// the observable dirty behaviour of moving each affected ancestor chain.
/// </summary>
public sealed class SceneAutoDirtyTests
{
    [Fact]
    public void Attaching_a_brush_marks_the_world_dirty()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("brush");
        scene.StaticWorldDirty.ShouldBeFalse();

        node.Brush = CreateUnitBrush();

        scene.StaticWorldDirty.ShouldBeTrue();
    }

    [Fact]
    public void Replacing_a_brush_marks_the_world_dirty()
    {
        var (scene, node, _) = CreateCleanSceneWithBrushNode();

        node.Brush = CreateUnitBrush(); // different instance, same shape — still a world edit

        scene.StaticWorldDirty.ShouldBeTrue();
    }

    [Fact]
    public void Detaching_a_brush_marks_the_world_dirty()
    {
        var (scene, node, _) = CreateCleanSceneWithBrushNode();

        node.Brush = null;

        scene.StaticWorldDirty.ShouldBeTrue();
    }

    [Fact]
    public void Removing_a_subtree_containing_a_brush_marks_the_world_dirty()
    {
        var (scene, node, _) = CreateCleanSceneWithBrushNode();

        scene.Root.RemoveChild(node);

        scene.StaticWorldDirty.ShouldBeTrue();
    }

    [Fact]
    public void Moving_a_brush_node_marks_the_world_dirty()
    {
        var (scene, node, _) = CreateCleanSceneWithBrushNode();

        node.LocalPosition = new Vector3(3f, 0f, 0f);

        scene.StaticWorldDirty.ShouldBeTrue();
    }

    [Fact]
    public void Moving_a_brushless_node_does_not_mark_the_world_dirty()
    {
        // A brush exists elsewhere in the scene, so a compiled world is live —
        // but the moved node has no brush in its subtree and must not touch it.
        var (scene, _, _) = CreateCleanSceneWithBrushNode();
        SceneNode plain = scene.Root.CreateChild("plain");
        scene.RebuildStaticWorld(new FakeRenderer()); // adding the node itself never dirties

        plain.LocalPosition = new Vector3(0f, 7f, 0f);

        scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void Moving_a_group_with_a_brush_descendant_marks_the_world_dirty()
    {
        var scene = new Scene("Test");
        SceneNode group = scene.Root.CreateChild("group");
        SceneNode child = group.CreateChild("brush");
        child.Brush = CreateUnitBrush();
        scene.RebuildStaticWorld(new FakeRenderer());

        group.LocalPosition = new Vector3(0f, 0f, 4f);

        scene.StaticWorldDirty.ShouldBeTrue();
    }

    [Fact]
    public void Reparenting_a_brush_node_updates_both_ancestor_chains()
    {
        var scene = new Scene("Test");
        var renderer = new FakeRenderer();
        SceneNode oldParent = scene.Root.CreateChild("old-parent");
        SceneNode newParent = scene.Root.CreateChild("new-parent");
        SceneNode brushNode = oldParent.CreateChild("brush");
        brushNode.Brush = CreateUnitBrush();
        scene.RebuildStaticWorld(renderer);

        // The move itself changes the brush's placement chain: dirty.
        newParent.AddChild(brushNode);
        scene.StaticWorldDirty.ShouldBeTrue();
        scene.RebuildStaticWorld(renderer);

        // Old chain lost its brush count: moving it must NOT recompile.
        oldParent.LocalPosition = new Vector3(1f, 0f, 0f);
        scene.StaticWorldDirty.ShouldBeFalse();

        // New chain gained the count: moving it must recompile.
        newParent.LocalPosition = new Vector3(0f, 1f, 0f);
        scene.StaticWorldDirty.ShouldBeTrue();
        scene.RebuildStaticWorld(renderer);

        // And the node's own counter survived the move.
        brushNode.LocalPosition = new Vector3(0f, 0f, 1f);
        scene.StaticWorldDirty.ShouldBeTrue();
    }

    [Fact]
    public void Reparenting_a_group_with_a_brush_descendant_updates_both_ancestor_chains()
    {
        // Same contract one level up: the counters must travel with a whole
        // moved subtree, not just with the brush node itself.
        var scene = new Scene("Test");
        var renderer = new FakeRenderer();
        SceneNode oldParent = scene.Root.CreateChild("old-parent");
        SceneNode newParent = scene.Root.CreateChild("new-parent");
        SceneNode group = oldParent.CreateChild("group");
        SceneNode brushNode = group.CreateChild("brush");
        brushNode.Brush = CreateUnitBrush();
        scene.RebuildStaticWorld(renderer);

        newParent.AddChild(group);
        scene.StaticWorldDirty.ShouldBeTrue();
        scene.RebuildStaticWorld(renderer);

        oldParent.LocalPosition = new Vector3(2f, 0f, 0f);
        scene.StaticWorldDirty.ShouldBeFalse();

        newParent.LocalPosition = new Vector3(0f, 2f, 0f);
        scene.StaticWorldDirty.ShouldBeTrue();
        scene.RebuildStaticWorld(renderer);

        group.LocalPosition = new Vector3(0f, 0f, 2f);
        scene.StaticWorldDirty.ShouldBeTrue();
    }

    private static Brush CreateUnitBrush() =>
        Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

    // A scene with one brush node whose dirty flag has been cleared by a real
    // (headless) rebuild, so each test observes only the edit it performs.
    private static (Scene Scene, SceneNode Node, FakeRenderer Renderer) CreateCleanSceneWithBrushNode()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("brush");
        node.Brush = CreateUnitBrush();

        var renderer = new FakeRenderer();
        scene.RebuildStaticWorld(renderer);
        scene.StaticWorldDirty.ShouldBeFalse();
        return (scene, node, renderer);
    }
}
