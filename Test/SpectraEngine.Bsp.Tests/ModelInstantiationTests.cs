using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="ModelInstantiator"/>: a loaded model becoming a live scene
/// subtree.
/// </summary>
/// <remarks>
/// The interesting property is not the shape of the subtree — it is that the
/// subtree lands in the scene the same way hand-built nodes do. Membership
/// events must fire for every node, the spatial index must end up holding every
/// renderable one with correct world bounds, and a raycast must be able to hit
/// the imported triangles. Those are what make an imported prop a first-class
/// citizen of the graph instead of geometry bolted onto its side.
/// </remarks>
public sealed class ModelInstantiationTests
{
    private const string Crate = "Models/crate.obj";
    private const string Signpost = "Models/signpost.gltf";
    private const float Tolerance = 1e-3f;

    [Fact]
    public void The_imported_hierarchy_becomes_the_node_subtree()
    {
        var (assets, _) = CreateAttached();
        ModelAsset model = assets.LoadModel(Crate);
        var scene = new Scene("Test");

        SceneNode root = ModelInstantiator.InstantiateInto(scene.Root, model);

        root.Parent.ShouldBeSameAs(scene.Root);
        root.Children.Count.ShouldBe(2);
        root.MeshRenderer.ShouldBeNull("the model's root node draws nothing itself");

        SceneNode sides = root.Children[0];
        SceneNode caps = root.Children[1];
        sides.Name.ShouldBe("Crate_Sides");
        caps.Name.ShouldBe("Crate_Caps");

        // Each node references the shared asset meshes, in import order.
        sides.MeshRenderer.ShouldNotBeNull().Mesh.ShouldBeSameAs(model.Meshes[0]);
        caps.MeshRenderer.ShouldNotBeNull().Mesh.ShouldBeSameAs(model.Meshes[1]);
        sides.MeshRenderer!.Material.Name.ShouldBe("crate_body");
        caps.MeshRenderer!.Material.Name.ShouldBe("crate_trim");

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Node_transforms_from_the_file_land_on_the_scene_nodes()
    {
        var (assets, _) = CreateAttached();
        ModelAsset model = assets.LoadModel(Signpost);
        var scene = new Scene("Test");

        SceneNode root = ModelInstantiator.InstantiateInto(scene.Root, model, "Signpost A");
        // Place the whole instance somewhere: local transforms are the model's,
        // world transforms compose with wherever the instance was put.
        root.LocalPosition = new Vector3(100f, 0f, -50f);

        root.Name.ShouldBe("Signpost A");
        SceneNode sign = root.Children[1];
        sign.Name.ShouldBe("Sign");
        sign.LocalPosition.Y.ShouldBe(26f, Tolerance);

        Vector3 signWorld = sign.WorldPosition;
        signWorld.X.ShouldBe(100f, Tolerance);
        signWorld.Y.ShouldBe(26f, Tolerance);
        signWorld.Z.ShouldBe(-50f, Tolerance);

        // The yaw survived too: the sign's local +X points 20 degrees off world +X.
        Vector3 axis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, sign.WorldMatrix));
        axis.X.ShouldBe(MathF.Cos(MathF.PI * 20f / 180f), Tolerance);
        axis.Z.ShouldBe(-MathF.Sin(MathF.PI * 20f / 180f), Tolerance);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Attaching_the_instance_announces_every_node_once()
    {
        var (assets, _) = CreateAttached();
        ModelAsset model = assets.LoadModel(Crate);
        var scene = new Scene("Test");

        var added = new List<SceneNode>();
        scene.NodeAdded += added.Add;

        SceneNode root = ModelInstantiator.InstantiateInto(scene.Root, model);

        // Root plus its two parts, parents before children.
        added.Count.ShouldBe(3);
        added[0].ShouldBeSameAs(root);
        added.ShouldContain(root.Children[0]);
        added.ShouldContain(root.Children[1]);
        added.Distinct().Count().ShouldBe(3);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Building_detached_raises_nothing_until_it_is_attached()
    {
        var (assets, _) = CreateAttached();
        ModelAsset model = assets.LoadModel(Crate);
        var scene = new Scene("Test");
        int added = 0;
        scene.NodeAdded += _ => added++;

        SceneNode root = ModelInstantiator.Instantiate(model);
        root.Parent.ShouldBeNull();
        added.ShouldBe(0);
        scene.Bvh.LeafCount.ShouldBe(0);

        // Editing before attaching is the point of the detached overload.
        root.LocalPosition = new Vector3(0f, 64f, 0f);
        scene.Root.AddChild(root);

        added.ShouldBe(3);
        scene.Bvh.LeafCount.ShouldBe(2);
        scene.Bvh.Validate();

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Every_renderable_node_is_indexed_with_its_world_bounds()
    {
        var (assets, _) = CreateAttached();
        ModelAsset model = assets.LoadModel(Crate);
        var scene = new Scene("Test");

        SceneNode root = ModelInstantiator.InstantiateInto(scene.Root, model);
        root.LocalPosition = new Vector3(200f, 0f, 0f);

        // Two mesh nodes; the group node is not spatial and must not be indexed.
        scene.Bvh.LeafCount.ShouldBe(2);
        scene.Bvh.Validate();

        scene.Bvh.TryGetWorldBounds(root.Children[0], out Aabb bounds).ShouldBeTrue();
        bounds.Min.X.ShouldBe(184f, Tolerance);
        bounds.Max.X.ShouldBe(216f, Tolerance);
        bounds.Min.Y.ShouldBe(0f, Tolerance);
        bounds.Max.Y.ShouldBe(32f, Tolerance);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_raycast_hits_the_instantiated_models_triangles()
    {
        var (assets, _) = CreateAttached();
        ModelAsset model = assets.LoadModel(Crate);
        var scene = new Scene("Test");

        SceneNode root = ModelInstantiator.InstantiateInto(scene.Root, model);
        root.LocalPosition = new Vector3(0f, 0f, 0f);

        // Straight at the crate's -Z wall, which sits 16 units from the origin.
        var ray = new Ray3(new Vector3(0f, 16f, -100f), Vector3.UnitZ);
        scene.Raycast(ray, out SceneRaycastHit hit).ShouldBeTrue();

        hit.Node.ShouldBeSameAs(root.Children[0]);
        hit.Distance.ShouldBe(84f, Tolerance);
        hit.Point.Z.ShouldBe(-16f, Tolerance);

        // Moving the instance moves what the ray finds — the index follows the
        // node, not the asset.
        root.LocalPosition = new Vector3(0f, 0f, 400f);
        scene.Raycast(ray, out hit).ShouldBeTrue();
        hit.Distance.ShouldBe(484f, Tolerance);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Instances_share_the_assets_gpu_meshes()
    {
        var (assets, renderer) = CreateAttached();
        ModelAsset model = assets.LoadModel(Crate);
        var scene = new Scene("Test");

        SceneNode a = ModelInstantiator.InstantiateInto(scene.Root, model, "A");
        SceneNode b = ModelInstantiator.InstantiateInto(scene.Root, model, "B");
        b.LocalPosition = new Vector3(64f, 0f, 0f);

        a.Children[0].MeshRenderer!.Mesh.ShouldBeSameAs(b.Children[0].MeshRenderer!.Mesh);
        a.Children[0].MeshRenderer!.Material.ShouldBeSameAs(b.Children[0].MeshRenderer!.Material);
        // Two instances, still two GPU meshes.
        renderer.CreatedMeshes.Count.ShouldBe(2);
        scene.Bvh.LeafCount.ShouldBe(4);
        scene.Bvh.Validate();

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_node_drawing_several_materials_becomes_a_node_per_submesh()
    {
        string root = CreateTempContentRoot();
        CopyTexture(root, "dev_grid.png");
        // One object, two material groups: the importer splits it into two
        // meshes hanging off the same node.
        WriteModel(root, "twotone.obj", """
            mtllib twotone.mtl
            o Twotone
            v 0 0 0
            v 4 0 0
            v 0 4 0
            v 4 4 0
            usemtl red
            f 1 2 3
            usemtl blue
            f 2 4 3
            """);
        WriteModel(root, "twotone.mtl", """
            newmtl red
            map_Kd ../Textures/dev_grid.png

            newmtl blue
            Kd 0 0 1
            """);

        var (assets, _) = CreateAttached(root);
        ModelAsset model = assets.LoadModel("Models/twotone.obj");
        model.Data.ShouldNotBeNull().Meshes.Count.ShouldBe(2);

        var scene = new Scene("Test");
        SceneNode instance = ModelInstantiator.InstantiateInto(scene.Root, model);

        SceneNode group = instance.Children.ShouldHaveSingleItem();
        group.Name.ShouldBe("Twotone");
        group.MeshRenderer.ShouldBeNull("a multi-material node holds its parts as children");
        group.Children.Count.ShouldBe(2);
        group.Children[0].MeshRenderer.ShouldNotBeNull();
        group.Children[1].MeshRenderer.ShouldNotBeNull();
        group.Children[0].MeshRenderer!.Material
            .ShouldNotBeSameAs(group.Children[1].MeshRenderer!.Material);

        scene.Bvh.LeafCount.ShouldBe(2);
        scene.Bvh.Validate();

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Instantiating_a_model_that_is_not_loaded_yet_throws()
    {
        var (assets, _) = CreateAttached();

        ModelAsset model = assets.RequestModel(Crate);
        // Deliberately not pumped: the handle exists but has no geometry, and
        // silently producing an empty subtree would be far worse than throwing.
        Should.Throw<InvalidOperationException>(() => ModelInstantiator.Instantiate(model))
            .Message.ShouldContain(Crate);

        assets.ReleaseGraphicsResources();
    }

    // ---- helpers ---------------------------------------------------------

    private static (AssetManager Assets, FakeRenderer Renderer) CreateAttached()
        => CreateAttached(ContentRoot.Path);

    private static (AssetManager Assets, FakeRenderer Renderer) CreateAttached(string root)
    {
        var assets = new AssetManager(NullLogger<AssetManager>.Instance, root, hotReloadEnabled: false);
        var renderer = new FakeRenderer();
        assets.AttachRenderer(renderer);
        return (assets, renderer);
    }

    private static string CreateTempContentRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "SpectraModelInstanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Models"));
        Directory.CreateDirectory(Path.Combine(root, "Textures"));
        Directory.CreateDirectory(Path.Combine(root, "Materials"));
        return root;
    }

    private static void CopyTexture(string root, string fileName)
        => File.Copy(
            ContentRoot.ResolveAbsolute(ContentRoot.Path, $"Textures/{fileName}"),
            Path.Combine(root, "Textures", fileName));

    private static void WriteModel(string root, string fileName, string contents)
        => File.WriteAllText(Path.Combine(root, "Models", fileName), contents);
}
