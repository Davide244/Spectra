using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using static SpectraEngine.Bsp.Tests.SpatialTestHelpers;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The W5 per-material render path, end to end: the compile splits each cell's
/// geometry per face material (<see cref="ChunkMesh.Submeshes"/>), the render
/// thread uploads one GPU mesh per (cell, material) and resolves each one's
/// material once at swap time, and the draw list emits one item per
/// (visible chunk, material) while culling stays per chunk. The properties that
/// must survive the split are pinned here: deterministic submesh order,
/// dirty-cell-only GPU churn, unchanged culling behaviour and chunk stats, and
/// an allocation-free per-frame build.
/// </summary>
/// <remarks>
/// Headless like the other scene suites: the test thread plays the render
/// thread. Cell landmarks: with a 2-unit cube brush, (10,10,10) and (20,20,20)
/// both sit inside cell (0,0,0), and (80,16,16) mid-cell (2,0,0).
/// </remarks>
public sealed class StaticWorldMaterialTests
{
    // ------------------------------------------------------------------
    // (a) The compile-side split.
    // ------------------------------------------------------------------

    [Fact]
    public void A_cell_with_two_materials_produces_two_submeshes_and_one_material_produces_one()
    {
        (MaterialRef low, MaterialRef high) = InternPair();
        var scene = new Scene("Test");
        var renderer = new FakeRenderer();

        // Both brushes land in cell (0,0,0); the far one is a separate,
        // single-material cell — the control.
        AddBrushNode(scene, "a", new Vector3(10f, 10f, 10f), low);
        AddBrushNode(scene, "b", new Vector3(20f, 20f, 20f), high);
        AddBrushNode(scene, "far", new Vector3(80f, 16f, 16f), MaterialRef.Default);

        scene.RebuildStaticWorld(renderer);
        CsgWorld world = scene.StaticWorld.ShouldNotBeNull();

        ChunkMesh mixed = world.ChunkMeshes.Single(m => m.Coord == new ChunkCoord(0, 0, 0));
        mixed.Submeshes.Count.ShouldBe(2);
        mixed.Submeshes.Select(s => s.Material).ShouldBe(new[] { low, high });

        ChunkMesh uniform = world.ChunkMeshes.Single(m => m.Coord == new ChunkCoord(2, 0, 0));
        ChunkSubmesh only = uniform.Submeshes.ShouldHaveSingleItem();
        only.Material.IsDefault.ShouldBeTrue();
        only.Indices.Length.ShouldBeGreaterThan(0);

        // The split partitions: no triangle is lost or duplicated, and every
        // submesh's indices address its own vertices.
        int mixedIndices = mixed.Submeshes.Sum(s => s.Indices.Length);
        mixedIndices.ShouldBe(uniform.Submeshes.Sum(s => s.Indices.Length) * 2); // two identical boxes
        foreach (ChunkSubmesh submesh in mixed.Submeshes)
        {
            submesh.Indices.Length.ShouldBeGreaterThan(0);
            submesh.Indices.Max().ShouldBeLessThan((uint)(submesh.Vertices.Length / 8));
        }
    }

    [Fact]
    public void Submeshes_are_ordered_by_ascending_material_id_not_emission_order()
    {
        (MaterialRef low, MaterialRef high) = InternPair();
        var scene = new Scene("Test");

        // Emission order is placement order, so adding the HIGH-id brush first
        // makes "ascending material id" and "emission order" disagree — the
        // only arrangement in which the ordering rule is observable.
        AddBrushNode(scene, "high", new Vector3(10f, 10f, 10f), high);
        AddBrushNode(scene, "low", new Vector3(20f, 20f, 20f), low);
        scene.RebuildStaticWorld(new FakeRenderer());

        ChunkMesh cell = scene.StaticWorld.ShouldNotBeNull().ChunkMeshes
            .Single(m => m.Coord == new ChunkCoord(0, 0, 0));
        cell.Submeshes.Select(s => s.Material.Id).ShouldBe(new[] { low.Id, high.Id });
    }

    [Fact]
    public void Two_compiles_of_a_mixed_world_produce_bit_identical_submeshes()
    {
        (MaterialRef low, MaterialRef high) = InternPair();
        BrushPlacement[] placements =
        [
            new(Brush.CreateBox(new Vector3(-1f), new Vector3(1f), high),
                Matrix4x4.CreateTranslation(10f, 10f, 10f)),
            new(Brush.CreateBox(new Vector3(-1f), new Vector3(1f), low).WithFaceMaterial(2, high),
                Matrix4x4.CreateTranslation(20f, 20f, 20f)),
        ];

        CsgWorld first = CsgWorld.Build(placements);
        CsgWorld second = CsgWorld.Build(placements);

        second.ChunkMeshes.Count.ShouldBe(first.ChunkMeshes.Count);
        for (int c = 0; c < first.ChunkMeshes.Count; c++)
        {
            ChunkMesh a = first.ChunkMeshes[c];
            ChunkMesh b = second.ChunkMeshes[c];
            b.Coord.ShouldBe(a.Coord);
            b.Submeshes.Count.ShouldBe(a.Submeshes.Count);
            // A mixed cell is what makes this test non-vacuous.
            a.Submeshes.Count.ShouldBe(2);
            for (int s = 0; s < a.Submeshes.Count; s++)
            {
                b.Submeshes[s].Material.ShouldBe(a.Submeshes[s].Material);
                b.Submeshes[s].Vertices.SequenceEqual(a.Submeshes[s].Vertices).ShouldBeTrue(
                    $"submesh #{s} vertices diverged for cell {a.Coord}");
                b.Submeshes[s].Indices.SequenceEqual(a.Submeshes[s].Indices).ShouldBeTrue(
                    $"submesh #{s} indices diverged for cell {a.Coord}");
            }
        }
    }

    // ------------------------------------------------------------------
    // (b) The GPU swap: one mesh per (cell, material), dirty cells only.
    // ------------------------------------------------------------------

    [Fact]
    public void The_swap_creates_one_gpu_mesh_per_cell_and_material()
    {
        (MaterialRef low, MaterialRef high) = InternPair();
        var scene = new Scene("Test");
        var renderer = new FakeRenderer();
        AddBrushNode(scene, "a", new Vector3(10f, 10f, 10f), low);
        AddBrushNode(scene, "b", new Vector3(20f, 20f, 20f), high);
        AddBrushNode(scene, "far", new Vector3(80f, 16f, 16f), MaterialRef.Default);

        scene.RebuildStaticWorld(renderer);

        // Two cells, three (cell, material) pairs, three GPU meshes.
        scene.StaticWorldChunkMeshes.Count.ShouldBe(2);
        renderer.CreatedMeshes.Count.ShouldBe(3);
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh mixed).ShouldBeTrue();
        mixed.Submeshes.Length.ShouldBe(2);
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(2, 0, 0), out StaticWorldChunkMesh far).ShouldBeTrue();
        far.Submeshes.Length.ShouldBe(1);

        // Each GPU mesh got exactly its own submesh's arrays, and the entries
        // are index-aligned with the artifact's submeshes.
        for (int s = 0; s < mixed.Submeshes.Length; s++)
        {
            StaticWorldSubmesh entry = mixed.Submeshes[s];
            ChunkSubmesh source = mixed.Artifact.ShouldNotBeNull().Submeshes[s];
            entry.SourceMaterial.ShouldBe(source.Material);
            var uploaded = (FakeMesh)entry.Mesh;
            uploaded.VertexData.SequenceEqual(source.Vertices).ShouldBeTrue();
            uploaded.IndexData.SequenceEqual(source.Indices).ShouldBeTrue();
        }
    }

    [Fact]
    public void Editing_one_cell_recreates_only_that_cells_material_meshes()
    {
        (MaterialRef low, MaterialRef high) = InternPair();
        var (scene, renderer, logger) = CreateScene();
        SceneNode edited = AddBrushNode(scene, "a", new Vector3(10f, 10f, 10f), low);
        AddBrushNode(scene, "b", new Vector3(20f, 20f, 20f), high);
        AddBrushNode(scene, "far", new Vector3(80f, 16f, 16f), MaterialRef.Default);

        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);
        renderer.CreatedMeshes.Count.ShouldBe(3);
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh mixedBefore).ShouldBeTrue();
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(2, 0, 0), out StaticWorldChunkMesh farBefore).ShouldBeTrue();

        edited.LocalPosition = new Vector3(11f, 10f, 10f); // still mid-cell (0,0,0)
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 2);

        // Exactly the edited cell churned — both of its materials, because the
        // artifact (and therefore the whole cell) was rebuilt — and the far
        // cell's single mesh was neither recreated nor destroyed.
        renderer.CreatedMeshes.Count.ShouldBe(5);
        foreach (StaticWorldSubmesh stale in mixedBefore.Submeshes)
            ((FakeMesh)stale.Mesh).Disposed.ShouldBeTrue();
        farBefore.SingleFakeMesh().Disposed.ShouldBeFalse();

        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh mixedAfter).ShouldBeTrue();
        mixedAfter.Submeshes.Length.ShouldBe(2);
        mixedAfter.Submeshes.Select(s => s.SourceMaterial).ShouldBe(new[] { low, high });
        for (int s = 0; s < mixedAfter.Submeshes.Length; s++)
            mixedAfter.Submeshes[s].Mesh.ShouldNotBeSameAs(mixedBefore.Submeshes[s].Mesh);

        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(2, 0, 0), out StaticWorldChunkMesh farAfter).ShouldBeTrue();
        farAfter.SingleMesh().ShouldBeSameAs(farBefore.SingleMesh());
        farAfter.Artifact.ShouldBeSameAs(farBefore.Artifact);
    }

    [Fact]
    public void Repeated_recompiles_of_a_mixed_world_leak_no_gpu_meshes()
    {
        // The soak property, machine-checked: the demo bobs a brush every frame,
        // so a live editing session drives thousands of recompiles through the
        // swap path. Each one replaces its cell's per-material meshes — and if
        // the replaced ones were disposed without being deregistered (or not
        // released at all), the renderer's tracking list would grow without
        // bound while the visible world stayed the same size. Steady-state live
        // mesh count is the invariant; CreatedMeshes only ever grows.
        (MaterialRef low, MaterialRef high) = InternPair();
        var (scene, renderer, logger) = CreateScene();
        SceneNode edited = AddBrushNode(scene, "a", new Vector3(10f, 10f, 10f), low);
        AddBrushNode(scene, "b", new Vector3(20f, 20f, 20f), high);
        AddBrushNode(scene, "far", new Vector3(80f, 16f, 16f), MaterialRef.Default);

        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);

        // Three (cell, material) pairs: the mixed cell's two plus the far cell's
        // one. This is the count every later compile must return to.
        const int SteadyStateMeshes = 3;
        renderer.LiveMeshes.Count.ShouldBe(SteadyStateMeshes);

        // Nudge the same brush inside its own cell, so every pass re-meshes the
        // mixed cell (two materials) and carries the far cell untouched.
        const int Recompiles = 60;
        for (int i = 0; i < Recompiles; i++)
        {
            int target = scene.StaticWorldCompileCount + 1;
            // A fresh position every pass — repeating one would leave the scene
            // clean and the pump would wait for a compile that never comes. The
            // walk stays well inside cell (0,0,0) (which spans 0..32), so the
            // world keeps its two cells and three (cell, material) pairs.
            edited.LocalPosition = new Vector3(10f + (i + 1) * 0.05f, 10f, 10f);
            PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount >= target);

            // Asserted every pass, not just at the end: a leak that plateaus
            // after the first few edits is still a leak, and only the per-pass
            // check pins the step where it appeared.
            renderer.LiveMeshes.Count.ShouldBe(
                SteadyStateMeshes,
                $"live GPU meshes drifted after {i + 1} recompile(s)");
        }

        // The churn really happened — otherwise the invariant above would hold
        // trivially — and every mesh dropped from the live list was disposed too.
        renderer.CreatedMeshes.Count.ShouldBeGreaterThan(SteadyStateMeshes + Recompiles);
        foreach (FakeMesh mesh in renderer.CreatedMeshes.Except(renderer.LiveMeshes))
            mesh.Disposed.ShouldBeTrue("a deregistered mesh must also have been disposed");
        foreach (FakeMesh live in renderer.LiveMeshes)
            live.Disposed.ShouldBeFalse("a live mesh must still be renderable");

        // And the world is still the same shape it started as.
        scene.StaticWorldChunkMeshes.Count.ShouldBe(2);
        scene.StaticWorldChunkMeshes.Sum(c => c.Submeshes.Length).ShouldBe(SteadyStateMeshes);
    }

    [Fact]
    public void A_failed_material_mesh_creation_rolls_back_the_whole_cell()
    {
        // The budget dies between a mixed cell's two materials: the half-built
        // cell must leave nothing behind, and the previous world must keep
        // rendering (create-before-destroy, now one level deeper).
        (MaterialRef low, MaterialRef high) = InternPair();
        var (scene, renderer, logger) = CreateScene();
        SceneNode edited = AddBrushNode(scene, "a", new Vector3(10f, 10f, 10f), low);
        AddBrushNode(scene, "b", new Vector3(20f, 20f, 20f), high);
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);

        CsgWorld previousWorld = scene.StaticWorld.ShouldNotBeNull();
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh before).ShouldBeTrue();
        before.Submeshes.Length.ShouldBe(2);

        edited.LocalPosition = new Vector3(11f, 10f, 10f);
        renderer.CreateMeshBudget = 1; // the cell's SECOND material throws
        PumpUntilCreateMeshFails(scene, renderer, logger);

        scene.StaticWorld.ShouldBeSameAs(previousWorld);
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh after).ShouldBeTrue();
        after.Submeshes.ShouldBe(before.Submeshes);
        foreach (StaticWorldSubmesh entry in before.Submeshes)
            ((FakeMesh)entry.Mesh).Disposed.ShouldBeFalse();

        // The one replacement that WAS created got rolled back, not leaked.
        renderer.CreatedMeshes.Count.ShouldBe(3);
        renderer.CreatedMeshes[2].Disposed.ShouldBeTrue();
    }

    // ------------------------------------------------------------------
    // (c) Resolution: material ids become assets on the render thread.
    // ------------------------------------------------------------------

    [Fact]
    public void Uploaded_submeshes_carry_the_material_the_asset_manager_resolved()
    {
        MaterialRef accent = MaterialRegistry.Intern(AccentMaterialPath);
        var renderer = new FakeRenderer();
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(renderer);

        var scene = new Scene("Test")
        {
            Assets = assets,
            StaticWorldMaterial = NoopMaterial,
        };
        AddBrushNode(scene, "plain", new Vector3(10f, 10f, 10f), MaterialRef.Default);
        AddBrushNode(scene, "accent", new Vector3(20f, 20f, 20f), accent);

        scene.RebuildStaticWorld(renderer);

        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh cell).ShouldBeTrue();
        cell.Submeshes.Length.ShouldBe(2);

        // The default face falls back to the scene's world material; the
        // materialed face resolves through the asset manager to the very
        // instance a direct load returns (one material object per path).
        StaticWorldSubmesh plain = cell.Submeshes.Single(s => s.SourceMaterial.IsDefault);
        plain.Material.ShouldBeSameAs(NoopMaterial);
        StaticWorldSubmesh accented = cell.Submeshes.Single(s => s.SourceMaterial == accent);
        accented.Material.ShouldBeSameAs(assets.LoadMaterial(AccentMaterialPath));

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_face_whose_material_path_escapes_the_content_root_still_compiles_and_uploads()
    {
        // Interning only trims and folds separators, so a path like this reaches
        // path normalisation at resolve time — which rejects it. That resolve
        // happens inside the GPU swap on the render thread, where a throw is not
        // a content problem but a render-thread crash that repeats on every
        // compile (the async path rethrows out of ProcessStaticWorldCompilation
        // straight into the engine's "render thread crashed" shutdown).
        MaterialRef escaping = MaterialRegistry.Intern($"../evil_{Guid.NewGuid():N}.spectramat");
        var renderer = new FakeRenderer();
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(renderer);

        var scene = new Scene("Test") { Assets = assets };
        AddBrushNode(scene, "escaping", new Vector3(10f, 10f, 10f), escaping);

        Should.NotThrow(() => scene.RebuildStaticWorld(renderer));

        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh cell).ShouldBeTrue();
        StaticWorldSubmesh only = cell.Submeshes.ShouldHaveSingleItem();
        // Drawable, magenta, and logged — the standing signal for bad content.
        only.Material.ShouldBeSameAs(assets.DefaultMaterial);
        logger.MessagesAt(Microsoft.Extensions.Logging.LogLevel.Warning)
            .ShouldContain(m => m.Contains("not usable"), customMessage: logger.Describe());

        // And it stays recoverable: a second compile does not throw either.
        scene.MarkStaticWorldDirty();
        Should.NotThrow(() => scene.RebuildStaticWorld(renderer));

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Without_an_asset_manager_every_face_falls_back_to_the_world_material()
    {
        (MaterialRef low, MaterialRef high) = InternPair();
        var scene = new Scene("Test") { StaticWorldMaterial = NoopMaterial };
        AddBrushNode(scene, "a", new Vector3(10f, 10f, 10f), low);
        AddBrushNode(scene, "b", new Vector3(20f, 20f, 20f), high);

        scene.RebuildStaticWorld(new FakeRenderer());

        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh cell).ShouldBeTrue();
        cell.Submeshes.Length.ShouldBe(2); // still split — the geometry is what it is
        foreach (StaticWorldSubmesh entry in cell.Submeshes)
            entry.Material.ShouldBeSameAs(NoopMaterial);
    }

    [Fact]
    public void RefreshStaticWorldMaterials_re_resolves_without_touching_gpu_meshes()
    {
        var scene = new Scene("Test");
        var renderer = new FakeRenderer();
        AddBrushNode(scene, "a", new Vector3(10f, 10f, 10f), MaterialRef.Default);
        scene.RebuildStaticWorld(renderer); // no world material assigned yet

        scene.StaticWorldChunkMeshes.ShouldHaveSingleItem().SingleMaterial().ShouldBeNull();
        Mesh uploaded = scene.StaticWorldChunkMeshes[0].SingleMesh();

        scene.StaticWorldMaterial = NoopMaterial;
        scene.RefreshStaticWorldMaterials();

        scene.StaticWorldChunkMeshes.ShouldHaveSingleItem().SingleMaterial().ShouldBeSameAs(NoopMaterial);
        scene.StaticWorldChunkMeshes[0].SingleMesh().ShouldBeSameAs(uploaded);
        renderer.CreatedMeshes.Count.ShouldBe(1);
        ((FakeMesh)uploaded).Disposed.ShouldBeFalse();
    }

    // ------------------------------------------------------------------
    // (d) The draw list: per-material items, per-chunk culling.
    // ------------------------------------------------------------------

    [Fact]
    public void World_items_are_one_per_visible_chunk_and_material_with_per_chunk_stats()
    {
        (MaterialRef low, MaterialRef high) = InternPair();
        var scene = new Scene("Test") { StaticWorldMaterial = NoopMaterial };

        // One brush in view wearing two materials (one retextured face), one
        // brush far behind the camera — culling must still work per chunk.
        SceneNode front = scene.Root.CreateChild("front");
        front.LocalPosition = new Vector3(0f, 0f, -5f);
        front.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f), low).WithFaceMaterial(2, high);
        AddBrushNode(scene, "behind", new Vector3(0f, 0f, 100f), MaterialRef.Default);

        scene.RebuildStaticWorld(new FakeRenderer());
        scene.StaticWorldChunkMeshes.Count.ShouldBe(2);

        var view = new RenderView();
        scene.BuildRenderView(MakeCamera(), view);

        // Chunk stats count CHUNKS; the item list counts per-material draws.
        view.WorldChunksTotal.ShouldBe(2);
        view.WorldChunksVisible.ShouldBe(1);
        view.WorldItems.Count.ShouldBe(2);
        view.WorldMaterialBatchesVisible.ShouldBe(2);
        view.WorldMaterialBatchesTotal.ShouldBe(3);

        StaticWorldChunkMesh visible = scene.StaticWorldChunkMeshes
            .Single(c => c.RenderBounds.Max.Z < 0f);
        for (int i = 0; i < view.WorldItems.Count; i++)
        {
            RenderItem item = view.WorldItems[i];
            item.Mesh.ShouldBeSameAs(visible.Submeshes[i].Mesh); // ascending material id, as uploaded
            item.Material.ShouldBeSameAs(visible.Submeshes[i].Material);
            item.World.ShouldBe(Matrix4x4.Identity);
        }
    }

    [Fact]
    public void BuildRenderView_is_allocation_free_with_multiple_materials()
    {
        (MaterialRef low, MaterialRef high) = InternPair();
        var scene = new Scene("Test") { StaticWorldMaterial = NoopMaterial };
        for (int x = 0; x < 6; x++)
        {
            for (int z = 0; z < 6; z++)
            {
                SceneNode node = scene.Root.CreateChild($"b{x}_{z}");
                node.LocalPosition = new Vector3(x * 4f - 10f, 0f, -z * 4f - 5f);
                node.Brush = Brush.CreateBox(new Vector3(-0.5f), new Vector3(0.5f), (x + z) % 2 == 0 ? low : high);
            }
        }
        scene.RebuildStaticWorld(new FakeRenderer());

        var camera = MakeCamera();
        var view = new RenderView();
        for (int i = 0; i < 50; i++)
            scene.BuildRenderView(camera, view);

        // The multi-material path must be what is being measured.
        view.WorldMaterialBatchesVisible.ShouldBeGreaterThan(view.WorldChunksVisible);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            scene.BuildRenderView(camera, view);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        delta.ShouldBe(0L);
    }

    // --- Helpers ------------------------------------------------------------

    private const string AccentMaterialPath = "Materials/checker_orange.spectramat";

    // Camera at z = 3 looking down -Z (the Camera defaults, restated explicitly
    // so the tests don't silently depend on them) — same fixture RenderViewTests
    // culls against.
    private static Camera MakeCamera() => new()
    {
        Position = new Vector3(0f, 0f, 3f),
        Yaw = -MathF.PI / 2f,
        Pitch = 0f,
        AspectRatio = 16f / 9f,
    };

    // Two distinct materials with the low id interned first. Ids are handed out
    // in interning order and never reused, so `low.Id < high.Id` holds however
    // many other tests interned around this one.
    private static (MaterialRef Low, MaterialRef High) InternPair()
    {
        MaterialRef low = MaterialRegistry.Intern($"Materials/w5_low_{Guid.NewGuid():N}.spectramat");
        MaterialRef high = MaterialRegistry.Intern($"Materials/w5_high_{Guid.NewGuid():N}.spectramat");
        low.Id.ShouldBeLessThan(high.Id);
        return (low, high);
    }

    private static (Scene Scene, FakeRenderer Renderer, CapturingLogger Logger) CreateScene() =>
        (new Scene("Test"), new FakeRenderer(), new CapturingLogger());

    // A 2-unit cube brush node wearing `material` on every face.
    private static SceneNode AddBrushNode(Scene scene, string name, Vector3 position, MaterialRef material)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.LocalPosition = position;
        node.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f), material);
        return node;
    }

    // Same slow-machine-proof pump loop as the other scene compile suites.
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(30);

    private static void PumpUntil(Scene scene, FakeRenderer renderer, CapturingLogger logger, Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            scene.ProcessStaticWorldCompilation(renderer, logger);
            if (condition())
                return;
            if (stopwatch.Elapsed > CompileTimeout)
                throw new TimeoutException(
                    $"Timed out waiting for a compile to land. Captured log:{Environment.NewLine}{logger.Describe()}");
            Thread.Sleep(1);
        }
    }

    private static void PumpUntilCreateMeshFails(Scene scene, FakeRenderer renderer, CapturingLogger logger)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                scene.ProcessStaticWorldCompilation(renderer, logger);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Simulated CreateMesh failure"))
            {
                return;
            }
            if (stopwatch.Elapsed > CompileTimeout)
                throw new TimeoutException(
                    $"Timed out waiting for the simulated CreateMesh failure. Captured log:{Environment.NewLine}{logger.Describe()}");
            Thread.Sleep(1);
        }
    }
}
