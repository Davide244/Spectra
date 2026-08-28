using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// Part brushes sharing one <see cref="Brush"/> instance resolve to one GPU
/// mesh, and the draw list carries one item per node.
/// </summary>
/// <remarks>
/// <b>This is the property instancing depends on, and nothing pinned it.</b>
/// <c>PartBrushMeshCache</c> keys on brush <em>reference identity</em>, so N
/// nodes sharing one brush upload once and emit N draws differing only in world
/// matrix. That is a batch, already present in the draw list today, waiting for
/// something to collapse it (roadmap <c>R12</c>).
/// <para>
/// The failure this exists to catch is silent and expensive in both directions.
/// Give <see cref="Brush"/> value equality, or key the cache by anything
/// structural, and N nodes become N uploads: the picture is identical, nothing
/// throws, and the batch quietly stops existing. Share a mesh too eagerly (key
/// on something coarser than the brush) and retexturing one prop retextures
/// every copy of it.
/// </para>
/// <para>
/// It runs against a real renderer rather than a fake because the sharing is
/// only meaningful in terms of actual GPU meshes: a stub that returned a new
/// object per call would pass a reference check that means nothing.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class SharedBrushBatchGlTests
{
    private readonly GlRendererFixture _fixture;

    public SharedBrushBatchGlTests(GlRendererFixture fixture) => _fixture = fixture;

    private const int Copies = 6;

    private static Brush Box(float halfExtent = 0.5f) =>
        Brush.CreateBox(new Vector3(-halfExtent), new Vector3(halfExtent), default);

    // N part-brush nodes spread along x so none is culled, each carrying
    // whatever brush the factory hands it for that index.
    private static Scene BuildScene(System.Func<int, Brush> brushFor)
    {
        var scene = new Scene();
        for (int i = 0; i < Copies; i++)
        {
            SceneNode node = scene.Root.CreateChild($"Prop{i}");
            node.LocalPosition = new Vector3(i * 2f, 0f, -10f);
            // Kind before brush, exactly as the demo places props: the brush
            // setter dirties the static world, and a part must never be
            // admitted to the placement list even for one frame.
            node.BrushKind = BrushKind.Part;
            node.Brush = brushFor(i);
        }
        return scene;
    }

    private static Camera LookingAtTheProps()
    {
        var camera = new Camera
        {
            Position = new Vector3(Copies, 0f, 10f),
            AspectRatio = 1f,
        };
        camera.LookAt(new Vector3(Copies, 0f, -10f));
        return camera;
    }

    private RenderView Draws(Scene scene)
    {
        scene.ProcessPartBrushMeshes(_fixture.Renderer);
        var view = new RenderView();
        scene.BuildRenderView(LookingAtTheProps(), view);
        return view;
    }

    [Fact]
    public void Nodes_sharing_one_brush_share_one_gpu_mesh()
    {
        Brush shared = Box();
        Scene scene = BuildScene(_ => shared);

        RenderView view = Draws(scene);

        view.PartBrushesVisible.ShouldBe(Copies);
        view.Items.Count.ShouldBe(Copies);

        var meshes = new HashSet<Mesh>();
        foreach (RenderItem item in view.Items)
            meshes.Add(item.Mesh);

        meshes.Count.ShouldBe(1, "one brush instance is one upload, however many nodes carry it");

        scene.ReleasePartBrushMeshes(_fixture.Renderer);
    }

    [Fact]
    public void Those_draws_differ_only_in_their_world_matrix()
    {
        // The other half of what makes them a batch: same mesh, same material,
        // N transforms. If the transforms collapsed too, the props would stack.
        Brush shared = Box();
        Scene scene = BuildScene(_ => shared);

        RenderView view = Draws(scene);

        var worlds = new HashSet<Matrix4x4>();
        Material? material = view.Items[0].Material;
        foreach (RenderItem item in view.Items)
        {
            worlds.Add(item.World);
            item.Material.ShouldBe(material);
        }

        worlds.Count.ShouldBe(Copies);

        scene.ReleasePartBrushMeshes(_fixture.Renderer);
    }

    [Fact]
    public void Structurally_equal_brushes_are_still_separate_uploads()
    {
        // Reference identity, deliberately: two equal-looking brushes are two
        // independent upload sites, and sharing one mesh between them would
        // need refcounting the cache does not have. Asserted so that giving
        // Brush value equality some day fails here rather than silently
        // changing what the draw list contains.
        Scene scene = BuildScene(_ => Box());

        RenderView view = Draws(scene);

        var meshes = new HashSet<Mesh>();
        foreach (RenderItem item in view.Items)
            meshes.Add(item.Mesh);

        meshes.Count.ShouldBe(Copies);

        scene.ReleasePartBrushMeshes(_fixture.Renderer);
    }

    [Fact]
    public void Retexturing_one_copy_leaves_the_others_on_the_shared_mesh()
    {
        // A brush is immutable, so retexturing returns a NEW instance: the
        // edited node leaves the shared batch and the rest stay in it. The bug
        // this rules out is a cache keyed coarsely enough that editing one prop
        // edits every copy of it.
        Brush shared = Box();
        Scene scene = BuildScene(_ => shared);
        Draws(scene);

        SceneNode first = scene.Root.Children[0];
        first.Brush = shared.WithFaceMaterial(0, MaterialRegistry.Intern("Materials/other.spectramat"));

        RenderView view = Draws(scene);

        // The five untouched nodes still resolve to exactly one mesh between
        // them, and it is not one of the edited node's.
        var untouched = new HashSet<Mesh>();
        var edited = new HashSet<Mesh>();
        foreach (RenderItem item in view.Items)
        {
            if (item.World.Translation == first.WorldMatrix.Translation)
                edited.Add(item.Mesh);
            else
                untouched.Add(item.Mesh);
        }

        untouched.Count.ShouldBe(1, "editing one copy must not disturb the batch the rest are in");

        // Two, not one: the edit gave that brush a second face material, and a
        // brush is split per material. Worth asserting rather than tolerating,
        // because it is the mechanism by which one node becomes two draws.
        edited.Count.ShouldBe(2);
        edited.Overlaps(untouched).ShouldBeFalse();

        scene.ReleasePartBrushMeshes(_fixture.Renderer);
    }
}
