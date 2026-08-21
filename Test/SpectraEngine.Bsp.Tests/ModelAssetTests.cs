using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The model half of the asset pipeline against a <see cref="FakeRenderer"/>:
/// import to GPU meshes, material resolution, the async pump, and unload.
/// </summary>
/// <remarks>
/// Same division of labour as <see cref="AssetManagerTests"/> — the test thread
/// plays the render thread and only the import itself runs off-thread — so no
/// assertion depends on how fast the thread pool gets to it.
/// </remarks>
public sealed class ModelAssetTests
{
    private const string Crate = "Models/crate.obj";
    private const string Signpost = "Models/signpost.gltf";

    // Only ever hit when an import never lands, i.e. on a real failure.
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Sync_load_creates_one_gpu_mesh_per_submesh()
    {
        var (assets, renderer) = CreateAttached();

        ModelAsset model = assets.LoadModel(Crate);

        model.IsReady.ShouldBeTrue();
        model.Error.ShouldBeNull();
        model.RelativePath.ShouldBe(Crate);
        model.Data.ShouldNotBeNull().Meshes.Count.ShouldBe(2);
        model.Meshes.Count.ShouldBe(2);

        // The arrays that reached the GPU are exactly the ones the import
        // produced — no re-packing between the two.
        for (int i = 0; i < model.Meshes.Count; i++)
        {
            var uploaded = model.Meshes[i].ShouldBeOfType<FakeMesh>();
            uploaded.VertexData.ShouldBe(model.Data.Meshes[i].Vertices);
            uploaded.IndexData.ShouldBe(model.Data.Meshes[i].Indices);
            uploaded.IndexCount.ShouldBe((uint)model.Data.Meshes[i].Indices.Length);
        }

        renderer.CreatedMeshes.Count.ShouldBe(2);
        assets.ModelCount.ShouldBe(1);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Each_submesh_gets_the_material_its_face_group_named()
    {
        var (assets, _) = CreateAttached();

        ModelAsset model = assets.LoadModel(Crate);
        ModelData data = model.Data.ShouldNotBeNull();

        Material body = model.MaterialFor(data.Meshes[0]);
        Material trim = model.MaterialFor(data.Meshes[1]);

        body.ShouldNotBeSameAs(trim);
        body.Name.ShouldBe("crate_body");
        trim.Name.ShouldBe("crate_trim");
        body.ShouldNotBeSameAs(assets.DefaultMaterial);

        // Each one carries the texture its .mtl named, through the shared
        // texture cache (so nothing was uploaded twice).
        body.TryGetTexture("uDiffuse", out int unit, out Texture? bodyTexture).ShouldBeTrue();
        unit.ShouldBe(0);
        assets.TryGetTexture("Textures/checker_orange.png", out TextureAsset? cached).ShouldBeTrue();
        cached.Texture.ShouldBeSameAs(bodyTexture);

        trim.TryGetTexture("uDiffuse", out _, out Texture? trimTexture).ShouldBeTrue();
        trimTexture.ShouldNotBeSameAs(bodyTexture);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Unreferenced_material_slots_cost_no_texture_loads()
    {
        var (assets, _) = CreateAttached();

        ModelAsset model = assets.LoadModel(Crate);
        ModelData data = model.Data.ShouldNotBeNull();

        // Assimp's OBJ reader always emits a spare "DefaultMaterial" slot that
        // no face group uses. It stays in the table (indices must keep meaning
        // what the file said) but resolves to the shared fallback.
        int unreferenced = -1;
        for (int i = 0; i < data.Materials.Count; i++)
        {
            bool used = false;
            for (int m = 0; m < data.Meshes.Count; m++)
                used |= data.Meshes[m].MaterialIndex == i;
            if (!used) unreferenced = i;
        }

        unreferenced.ShouldBeGreaterThanOrEqualTo(0);
        model.Materials[unreferenced].ShouldBeSameAs(assets.DefaultMaterial);

        // Two textures for the two real materials, plus the placeholder.
        assets.TextureCount.ShouldBe(2);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void The_same_path_returns_the_same_handle_and_imports_once()
    {
        var (assets, renderer) = CreateAttached();

        ModelAsset first = assets.LoadModel(Crate);
        ModelAsset second = assets.LoadModel(Crate);
        ModelAsset third = assets.LoadModel("Models\\crate.obj");

        second.ShouldBeSameAs(first);
        third.ShouldBeSameAs(first);
        renderer.CreatedMeshes.Count.ShouldBe(2);
        assets.ModelCount.ShouldBe(1);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Async_request_starts_unready_and_lands_on_a_pump()
    {
        var (assets, _) = CreateAttached();

        ModelAsset model = assets.RequestModel(Signpost);
        model.IsReady.ShouldBeFalse();
        model.Meshes.ShouldBeEmpty();

        PumpUntil(assets, () => model.IsReady);

        model.Error.ShouldBeNull();
        model.Meshes.Count.ShouldBe(2);
        model.Data.ShouldNotBeNull().Root.Children.Count.ShouldBe(2);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Polling_request_while_an_import_runs_never_queues_a_second_one()
    {
        var (assets, renderer) = CreateAttached();

        ModelAsset model = assets.RequestModel(Signpost);
        // The natural "wait for it" shape: ask again every frame. Each of these
        // must be free, or a slow import turns into a task storm.
        for (int i = 0; i < 50; i++)
            assets.RequestModel(Signpost).ShouldBeSameAs(model);

        model.ImportPending.ShouldBeTrue();
        PumpUntil(assets, () => model.IsReady);

        model.ImportPending.ShouldBeFalse();
        renderer.CreatedMeshes.Count.ShouldBe(2);

        // Ready now, so further requests do not import at all.
        assets.RequestModel(Signpost).ShouldBeSameAs(model);
        model.ImportPending.ShouldBeFalse();

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_failed_async_import_reports_on_the_handle_instead_of_throwing()
    {
        string root = CreateTempContentRoot();
        AssetManager assets = Attach(root, out _);

        ModelAsset model = assets.RequestModel("Models/absent.obj");
        PumpUntil(assets, () => model.Error is not null);

        model.IsReady.ShouldBeFalse();
        model.Error.ShouldNotBeNull().ShouldContain("absent.obj");

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_failed_sync_load_throws_so_load_time_code_can_react()
    {
        string root = CreateTempContentRoot();
        AssetManager assets = Attach(root, out _);

        Should.Throw<FileNotFoundException>(() => assets.LoadModel("Models/absent.obj"));

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void An_authored_spectramat_overrides_the_material_the_model_file_named()
    {
        string root = CreateTempContentRoot();
        CopyTexture(root, "dev_grid.png");
        WriteModel(root, "prop.obj", """
            mtllib prop.mtl
            usemtl prop_skin
            v 0 0 0
            v 4 0 0
            v 0 4 0
            f 1 2 3
            """);
        WriteModel(root, "prop.mtl", """
            newmtl prop_skin
            map_Kd ../Textures/dev_grid.png
            """);
        // The engine's own material file wins: it knows about shaders and
        // sampler states, which an exported .mtl never does.
        File.WriteAllText(
            Path.Combine(root, "Materials", "prop_skin.spectramat"),
            """
            shader = lit

            texture uDiffuse = Textures/dev_grid.png, nearest, clamp
            color   uBaseColor = #FF0000
            """);

        AssetManager assets = Attach(root, out _);
        ModelAsset model = assets.LoadModel("Models/prop.obj");

        Material material = model.Materials[model.Data.ShouldNotBeNull().Meshes[0].MaterialIndex];
        material.SourcePath.ShouldBe("Materials/prop_skin.spectramat");
        material.TryGetVector3("uBaseColor", out var color).ShouldBeTrue();
        color.X.ShouldBe(1f);
        // Same instance the material cache hands to anything else naming it.
        assets.TryGetMaterial("Materials/prop_skin.spectramat", out Material? cached).ShouldBeTrue();
        material.ShouldBeSameAs(cached);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_material_with_no_usable_texture_falls_back_to_the_default_material()
    {
        string root = CreateTempContentRoot();
        WriteModel(root, "plain.obj", """
            mtllib plain.mtl
            usemtl plain
            v 0 0 0
            v 4 0 0
            v 0 4 0
            f 1 2 3
            """);
        WriteModel(root, "plain.mtl", """
            newmtl plain
            Kd 0.5 0.5 0.5
            """);

        AssetManager assets = Attach(root, out _);
        ModelAsset model = assets.LoadModel("Models/plain.obj");

        Material material = model.MaterialFor(model.Data.ShouldNotBeNull().Meshes[0]);
        material.ShouldBeSameAs(assets.DefaultMaterial);
        // Never null and always drawable — that is the whole contract.
        material.TryGetTexture("uDiffuse", out _, out Texture? texture).ShouldBeTrue();
        texture.ShouldBeSameAs(assets.PlaceholderTexture);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void The_idle_pump_stays_allocation_free_with_models_loaded()
    {
        var (assets, _) = CreateAttached();
        assets.LoadModel(Crate);
        assets.RequestModel(Signpost);
        PumpUntil(assets, () => assets.TryGetModel(Signpost, out ModelAsset? m) && m.IsReady);

        // Warm up: JIT the model drain and clear anything the loads left behind.
        for (int i = 0; i < 200; i++) assets.PumpPendingUploads();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) assets.PumpPendingUploads();
        long after = GC.GetAllocatedBytesForCurrentThread();

        // The model queue joined a pump that runs every frame forever, so it has
        // to carry the same weight as the texture one: a struct payload and a
        // TryDequeue that touches no allocator when the queue is empty.
        (after - before).ShouldBe(0);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Unload_destroys_the_models_gpu_meshes_and_drops_the_cache_entry()
    {
        var (assets, renderer) = CreateAttached();

        ModelAsset model = assets.LoadModel(Crate);
        var meshes = model.Meshes.Cast<FakeMesh>().ToArray();

        assets.UnloadModel(Crate).ShouldBeTrue();

        meshes.ShouldAllBe(m => m.Disposed);
        model.Meshes.ShouldBeEmpty();
        model.IsReady.ShouldBeFalse();
        assets.ModelCount.ShouldBe(0);
        assets.UnloadModel(Crate).ShouldBeFalse();

        // Loading again is a fresh import, not a resurrection of dead handles.
        ModelAsset reloaded = assets.LoadModel(Crate);
        reloaded.IsReady.ShouldBeTrue();
        reloaded.Meshes.ShouldAllBe(m => !((FakeMesh)m).Disposed);
        renderer.CreatedMeshes.Count.ShouldBe(4);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Import_landing_after_an_unload_creates_no_orphan_meshes()
    {
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        var renderer = new FakeRenderer();
        assets.AttachRenderer(renderer);

        // The editor's load/unload loop: the handle leaves the cache while its
        // import is still on the thread pool.
        const int cycles = 5;
        for (int i = 0; i < cycles; i++)
        {
            ModelAsset requested = assets.RequestModel(Crate);
            assets.UnloadModel(Crate).ShouldBeTrue();
            // Unloading ends the handle's life; advertising a pending import on
            // it would be a lie, since no result can ever be applied to it.
            requested.ImportPending.ShouldBeFalse();

            int expected = i + 1;
            PumpUntil(assets, () => DroppedImports(logger) == expected);

            // Meshes published on an evicted handle are invisible to
            // ReleaseModelResources, which only walks the cache — before the fix
            // this leaked one mesh pair per cycle.
            requested.IsReady.ShouldBeFalse();
            requested.Meshes.ShouldBeEmpty();
        }

        assets.ModelCount.ShouldBe(0);
        renderer.LiveMeshes.ShouldBeEmpty("every GPU mesh must be owned by a cached handle");

        assets.ReleaseGraphicsResources();
        renderer.CreatedMeshes.ShouldAllBe(m => m.Disposed);
    }

    [Fact]
    public void Releasing_graphics_resources_destroys_every_model_mesh()
    {
        var (assets, _) = CreateAttached();

        ModelAsset crate = assets.LoadModel(Crate);
        ModelAsset signpost = assets.LoadModel(Signpost);
        var meshes = crate.Meshes.Concat(signpost.Meshes).Cast<FakeMesh>().ToArray();
        meshes.Length.ShouldBe(4);

        assets.ReleaseGraphicsResources();

        meshes.ShouldAllBe(m => m.Disposed);
        assets.ModelCount.ShouldBe(0);
    }

    // ---- helpers ---------------------------------------------------------

    private static (AssetManager Assets, FakeRenderer Renderer) CreateAttached()
    {
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);
        var renderer = new FakeRenderer();
        assets.AttachRenderer(renderer);
        return (assets, renderer);
    }

    private static AssetManager Attach(string root, out FakeRenderer renderer)
    {
        renderer = new FakeRenderer();
        var assets = new AssetManager(NullLogger<AssetManager>.Instance, root, hotReloadEnabled: false);
        assets.AttachRenderer(renderer);
        return assets;
    }

    // Imports the pump dropped because their handle had left the cache.
    private static int DroppedImports(CapturingLogger logger)
        => logger.MessagesAt(LogLevel.Debug).Count(m => m.Contains("Dropping the import"));

    private static string CreateTempContentRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "SpectraModelAssetTests", Guid.NewGuid().ToString("N"));
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

    // Plays the render loop: pump, then yield, until the condition holds.
    private static void PumpUntil(AssetManager assets, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + PumpTimeout;
        while (DateTime.UtcNow < deadline)
        {
            assets.PumpPendingUploads();
            if (condition()) return;
            Thread.Sleep(2);
        }

        throw new TimeoutException($"Condition not met within {PumpTimeout.TotalSeconds:0} s of pumping.");
    }
}
