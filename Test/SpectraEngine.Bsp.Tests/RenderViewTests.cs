using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using static SpectraEngine.Bsp.Tests.SpatialTestHelpers;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="Scene.BuildRenderView"/> — the per-frame draw-list build shared
/// by every backend pipeline. It must cull mesh nodes against the real camera
/// frustum, cull the compiled static world PER CHUNK against each chunk's
/// render AABB (conservatively — a chunk with visible geometry is never
/// dropped), report accurate visible/total stats for both, emit items in an
/// order that is stable for an unchanged scene, and allocate nothing in
/// steady state.
/// </summary>
public sealed class RenderViewTests
{
    // Camera at z = 3 looking down -Z (the Camera defaults, restated
    // explicitly so the tests don't silently depend on them).
    private static Camera MakeCamera() => new()
    {
        Position = new Vector3(0f, 0f, 3f),
        Yaw = -MathF.PI / 2f,
        Pitch = 0f,
        AspectRatio = 16f / 9f,
    };

    [Fact]
    public void Culls_a_node_behind_the_camera_and_keeps_one_in_front()
    {
        var scene = new Scene("Test");
        SceneNode front = CreateMeshNode(scene.Root, "front", new Vector3(0f, 0f, -5f));
        CreateMeshNode(scene.Root, "behind", new Vector3(0f, 0f, 50f));

        var view = new RenderView();
        scene.BuildRenderView(MakeCamera(), view);

        view.Items.Count.ShouldBe(1);
        view.Items[0].Mesh.ShouldBeSameAs(front.MeshRenderer!.Mesh);
        view.Items[0].Material.ShouldBeSameAs(front.MeshRenderer!.Material);
        view.Items[0].World.ShouldBe(front.WorldMatrix);

        view.VisibleCount.ShouldBe(1);
        view.TotalCount.ShouldBe(2);
    }

    [Fact]
    public void Brush_nodes_emit_no_items_and_do_not_count_as_mesh_nodes()
    {
        var scene = new Scene("Test");
        CreateBrushNode(scene.Root, "wall", new Vector3(0f, 0f, -5f)); // squarely in view

        var view = new RenderView();
        scene.BuildRenderView(MakeCamera(), view);

        // Brush geometry renders through the compiled static world, never
        // per-node — and here nothing was compiled yet.
        view.Items.ShouldBeEmpty();
        view.VisibleCount.ShouldBe(0);
        view.TotalCount.ShouldBe(0);
        view.WorldItems.ShouldBeEmpty();
        view.WorldChunksTotal.ShouldBe(0);
    }

    [Fact]
    public void World_chunks_are_frustum_culled_per_chunk_with_accurate_stats()
    {
        var scene = new Scene("Test");
        // Two brushes many cells apart: one squarely in view, one far behind
        // the camera — two chunks, of which only one may survive the cull.
        CreateBrushNode(scene.Root, "front", new Vector3(0f, 0f, -5f));
        CreateBrushNode(scene.Root, "behind", new Vector3(0f, 0f, 100f));
        scene.StaticWorldMaterial = NoopMaterial;
        scene.RebuildStaticWorld(new FakeRenderer());
        scene.StaticWorldChunkMeshes.Count.ShouldBe(2);

        var view = new RenderView();
        scene.BuildRenderView(MakeCamera(), view);

        // Exactly the front chunk survives, drawn with the shared world
        // material and an identity matrix (chunk vertices are world-space).
        RenderItem item = view.WorldItems.ShouldHaveSingleItem();
        StaticWorldChunkMesh front = scene.StaticWorldChunkMeshes
            .Single(c => c.RenderBounds.Max.Z < 0f);
        item.Mesh.ShouldBeSameAs(front.SingleMesh());
        item.Material.ShouldBeSameAs(NoopMaterial);
        item.World.ShouldBe(Matrix4x4.Identity);

        view.WorldChunksTotal.ShouldBe(2);
        view.WorldChunksVisible.ShouldBe(1);
        view.Items.ShouldBeEmpty(); // the brush nodes themselves still emit nothing
    }

    [Fact]
    public void Chunk_culling_never_drops_a_chunk_containing_visible_geometry()
    {
        // A brush field scattered around (and behind) the camera across many
        // cells. Camera oracle: any chunk with at least one triangle vertex
        // inside the frustum MUST appear in the world item list —
        // Frustum.Intersects is conservative, so extra survivors are
        // legitimate, missing ones never are.
        var scene = new Scene("Test");
        int n = 0;
        for (int x = -2; x <= 2; x++)
        {
            for (int z = -3; z <= 2; z++)
                CreateBrushNode(scene.Root, $"b{n++}", new Vector3(x * 40f, 0f, z * 40f - 5f), half: 2f);
        }
        scene.StaticWorldMaterial = NoopMaterial;
        scene.RebuildStaticWorld(new FakeRenderer());
        scene.StaticWorldChunkMeshes.Count.ShouldBeGreaterThan(4);

        var camera = MakeCamera();
        var view = new RenderView();
        scene.BuildRenderView(camera, view);

        // The cull must have actually rejected something (chunks behind the
        // camera), or the oracle below is vacuous.
        view.WorldChunksTotal.ShouldBe(scene.StaticWorldChunkMeshes.Count);
        view.WorldChunksVisible.ShouldBeGreaterThan(0);
        view.WorldChunksVisible.ShouldBeLessThan(view.WorldChunksTotal);

        Frustum frustum = camera.GetFrustum();
        var visibleMeshes = new HashSet<Mesh>();
        foreach (RenderItem item in view.WorldItems)
            visibleMeshes.Add(item.Mesh);

        foreach (StaticWorldChunkMesh chunk in scene.StaticWorldChunkMeshes)
        {
            bool hasVisibleVertex = false;
            foreach (ChunkSubmesh submesh in chunk.Artifact!.Submeshes)
            {
                float[] vertices = submesh.Vertices;
                for (int i = 0; i < vertices.Length; i += 8) // 8 floats per vertex, position first
                {
                    if (frustum.Contains(new Vector3(vertices[i], vertices[i + 1], vertices[i + 2])))
                    {
                        hasVisibleVertex = true;
                        break;
                    }
                }
            }

            if (hasVisibleVertex)
            {
                visibleMeshes.Contains(chunk.SingleMesh()).ShouldBeTrue(
                    $"chunk {chunk.Coord} has geometry inside the frustum but was culled");
            }
        }
    }

    [Fact]
    public void TotalCount_follows_component_and_membership_edits()
    {
        var scene = new Scene("Test");
        SceneNode meshNode = CreateMeshNode(scene.Root, "cube", new Vector3(0f, 0f, -5f));
        SceneNode brushNode = CreateBrushNode(scene.Root, "wall", new Vector3(2f, 0f, -5f));

        var camera = MakeCamera();
        var view = new RenderView();

        scene.BuildRenderView(camera, view);
        view.TotalCount.ShouldBe(1); // the brush node carries no mesh

        // A brush node gaining a renderer starts counting (component swap on
        // an already-indexed leaf)...
        brushNode.MeshRenderer = new MeshRenderer(CreateCubeMesh(0.5f), NoopMaterial);
        scene.BuildRenderView(camera, view);
        view.TotalCount.ShouldBe(2);
        view.VisibleCount.ShouldBe(2);

        // ...a mesh node losing its renderer stops (the node leaves the index
        // entirely — it has no brush either)...
        meshNode.MeshRenderer = null;
        scene.BuildRenderView(camera, view);
        view.TotalCount.ShouldBe(1);
        view.VisibleCount.ShouldBe(1);

        // ...and detaching a counted node drops it too.
        scene.Root.RemoveChild(brushNode);
        scene.BuildRenderView(camera, view);
        view.TotalCount.ShouldBe(0);
        view.VisibleCount.ShouldBe(0);
    }

    [Fact]
    public void Item_order_is_stable_across_builds_of_an_unchanged_scene()
    {
        var scene = new Scene("Test");
        for (int i = 0; i < 12; i++)
            CreateMeshNode(scene.Root, $"m{i}", new Vector3((i % 4) * 2f - 3f, (i / 4) * 2f - 2f, -6f - i));

        var camera = MakeCamera();
        var first = new RenderView();
        var second = new RenderView();
        scene.BuildRenderView(camera, first);
        scene.BuildRenderView(camera, second);

        first.Items.Count.ShouldBeGreaterThan(0);
        second.Items.Count.ShouldBe(first.Items.Count);
        for (int i = 0; i < first.Items.Count; i++)
            second.Items[i].ShouldBe(first.Items[i]); // record-struct equality: same mesh, material, matrix
    }

    [Fact]
    public void BuildRenderView_is_allocation_free_after_warmup()
    {
        var scene = new Scene("Test");
        for (int x = 0; x < 6; x++)
        for (int z = 0; z < 6; z++)
        {
            var position = new Vector3(x * 4f - 10f, 0f, -z * 4f - 5f);
            if ((x + z) % 2 == 0)
                CreateMeshNode(scene.Root, $"m{x}_{z}", position);
            else
                CreateBrushNode(scene.Root, $"b{x}_{z}", position);
        }
        scene.StaticWorldMaterial = NoopMaterial;
        scene.RebuildStaticWorld(new FakeRenderer());

        var camera = MakeCamera();
        var view = new RenderView();

        // Warmup: grows the view's item list and the scene's scratch list to
        // their steady-state capacity and flushes any pending BVH refits.
        for (int i = 0; i < 50; i++)
            scene.BuildRenderView(camera, view);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            scene.BuildRenderView(camera, view);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        delta.ShouldBe(0L);
        view.VisibleCount.ShouldBeGreaterThan(0); // the run actually drew something
        view.WorldChunksVisible.ShouldBeGreaterThan(0); // ...and the chunk path was exercised
    }
}
