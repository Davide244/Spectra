using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The <see cref="Scene.RebuildStaticWorld"/> guard contract: a brush node
/// whose world transform is non-rigid (scaled), singular, or non-finite (NaN)
/// must be rejected with <see cref="InvalidOperationException"/> before any
/// derived data or GPU work happens. The renderer argument is passed as null because a correct
/// rebuild throws while collecting/validating brush nodes — long before the
/// renderer would be touched — which keeps these tests free of any GPU,
/// window, or driver dependency. (The rebuild path with valid brushes ends in
/// renderer.CreateMesh and therefore cannot be exercised headlessly; carve and
/// BSP behaviour for valid transforms is covered via CsgWorld.Build instead.)
/// </summary>
public sealed class SceneStaticWorldTests
{
    private static Scene CreateSceneWithBrushNode(out SceneNode brushNode)
    {
        var scene = new Scene("Test");
        brushNode = scene.Root.CreateChild("brush");
        brushNode.Brush = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        return scene;
    }

    [Fact]
    public void Scaled_brush_node_is_rejected()
    {
        Scene scene = CreateSceneWithBrushNode(out SceneNode node);
        node.LocalScale = new Vector3(2f, 1f, 1f);

        Should.Throw<InvalidOperationException>(() => scene.RebuildStaticWorld(null!));
    }

    [Fact]
    public void Brush_node_under_a_scaled_parent_is_rejected()
    {
        // The brush node itself is untouched; the non-rigid scale arrives
        // through the composed world matrix — that is what must be validated.
        var scene = new Scene("Test");
        SceneNode parent = scene.Root.CreateChild("parent");
        parent.LocalScale = new Vector3(1f, 3f, 1f);
        SceneNode child = parent.CreateChild("brush");
        child.Brush = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

        Should.Throw<InvalidOperationException>(() => scene.RebuildStaticWorld(null!));
    }

    [Fact]
    public void Singular_brush_node_transform_is_rejected()
    {
        Scene scene = CreateSceneWithBrushNode(out SceneNode node);
        node.LocalScale = Vector3.Zero;

        Should.Throw<InvalidOperationException>(() => scene.RebuildStaticWorld(null!));
    }

    [Fact]
    public void Brush_node_with_NaN_scale_is_rejected()
    {
        // NaN is the guard's blind spot if unchecked: every rigidity predicate
        // is "comparison > tolerance => defect" and NaN comparisons are all
        // false, so without an explicit finiteness screen a NaN basis would
        // pass as rigid and poison carve/snap/weld/BSP silently.
        Scene scene = CreateSceneWithBrushNode(out SceneNode node);
        node.LocalScale = new Vector3(float.NaN, 1f, 1f);

        Should.Throw<InvalidOperationException>(() => scene.RebuildStaticWorld(null!));
    }

    [Fact]
    public void Brush_node_with_NaN_position_is_rejected()
    {
        // Translation never feeds the basis-vector checks at all, so a NaN
        // position is only caught by the finiteness screen.
        Scene scene = CreateSceneWithBrushNode(out SceneNode node);
        node.LocalPosition = new Vector3(0f, float.NaN, 0f);

        Should.Throw<InvalidOperationException>(() => scene.RebuildStaticWorld(null!));
    }

    [Fact]
    public void Rebuild_without_brush_nodes_yields_null_world_and_clears_dirty()
    {
        // Documented early-out: no brush nodes means no derived world and no
        // GPU work, so even a null renderer must never be dereferenced.
        var scene = new Scene("Test");
        scene.Root.CreateChild("empty");
        scene.MarkStaticWorldDirty();

        scene.RebuildStaticWorld(null!);

        scene.StaticWorld.ShouldBeNull();
        scene.StaticWorldDirty.ShouldBeFalse();
    }
}
