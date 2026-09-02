using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The texture asset pipeline, exercised headlessly: the test thread plays the
/// render thread (attaching the renderer, calling the pump once per "frame",
/// unloading) and a <see cref="FakeRenderer"/> stands in for the GPU, so only
/// the image decode runs off-thread — the same division of labour as in the
/// engine. Waits poll the pump with a generous ceiling that is only reached on
/// genuine failure: textures are only ever swapped in by pump calls on this
/// thread, so no assertion depends on how fast the thread pool decodes.
/// </summary>
public sealed class AssetManagerTests
{
    private const string Grid = "Textures/dev_grid.png";
    private const string CheckerGray = "Textures/checker_gray.png";
    private const string Mask = "Textures/gradient_mask.png";

    // Only ever hit when a decode never lands, i.e. on a real failure.
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Sync_load_uploads_the_decoded_file_to_the_renderer()
    {
        var (assets, renderer) = CreateAttached();

        TextureAsset asset = assets.LoadTexture(Grid, TextureFilter.Nearest, TextureWrap.Clamp);

        asset.IsPlaceholder.ShouldBeFalse();
        asset.RelativePath.ShouldBe(Grid);
        asset.SourcePath.ShouldBe(ContentRoot.ResolveAbsolute(ContentRoot.Path, Grid));

        var texture = asset.Texture.ShouldBeOfType<FakeTexture>();
        texture.Width.ShouldBe(128);
        texture.Height.ShouldBe(128);
        texture.Format.ShouldBe(TextureFormat.Rgba8);
        texture.Filter.ShouldBe(TextureFilter.Nearest);
        texture.Wrap.ShouldBe(TextureWrap.Clamp);
        texture.Pixels.Length.ShouldBe(128 * 128 * 4);

        // The placeholder plus this one; both registered with the renderer.
        renderer.LiveTextures.Count.ShouldBe(2);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Same_path_returns_the_same_instance_and_uploads_once()
    {
        var (assets, renderer) = CreateAttached();

        TextureAsset first = assets.LoadTexture(Grid);
        TextureAsset second = assets.LoadTexture(Grid);
        // Different spelling, same asset: the cache key is normalised.
        TextureAsset third = assets.LoadTexture("Textures\\dev_grid.png");

        second.ShouldBeSameAs(first);
        third.ShouldBeSameAs(first);
        second.Texture.ShouldBeSameAs(first.Texture);
        assets.TextureCount.ShouldBe(1);
        // Placeholder + one upload: the repeat loads never touched the GPU.
        renderer.CreatedTextures.Count.ShouldBe(2);

        TextureAsset other = assets.LoadTexture(CheckerGray);
        other.ShouldNotBeSameAs(first);
        other.Texture.ShouldNotBeSameAs(first.Texture);
        assets.TextureCount.ShouldBe(2);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void TryGet_finds_loaded_assets_and_misses_unloaded_ones()
    {
        var (assets, _) = CreateAttached();

        assets.TryGetTexture(Grid, out _).ShouldBeFalse();

        TextureAsset loaded = assets.LoadTexture(Grid);
        assets.TryGetTexture("Textures\\dev_grid.png", out TextureAsset? found).ShouldBeTrue();
        found.ShouldBeSameAs(loaded);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Async_request_serves_the_placeholder_until_a_pump_swaps_the_real_texture_in()
    {
        var (assets, renderer) = CreateAttached();
        Texture placeholder = assets.PlaceholderTexture.ShouldNotBeNull();

        TextureAsset asset = assets.RequestTexture(Mask, TextureFilter.Linear, TextureWrap.Clamp);

        // Immediately usable: bound to the placeholder, nothing rendered untextured.
        asset.IsPlaceholder.ShouldBeTrue();
        asset.Texture.ShouldBeSameAs(placeholder);
        asset.Version.ShouldBe(0);
        // A second request before the decode lands must not queue a second one.
        assets.RequestTexture(Mask, TextureFilter.Linear, TextureWrap.Clamp).ShouldBeSameAs(asset);

        PumpUntil(assets, () => !asset.IsPlaceholder);

        asset.Texture.ShouldNotBeSameAs(placeholder);
        asset.Version.ShouldBe(1);
        var texture = asset.Texture.ShouldBeOfType<FakeTexture>();
        texture.Width.ShouldBe(64);
        texture.Height.ShouldBe(64);
        texture.Format.ShouldBe(TextureFormat.R8);
        texture.Filter.ShouldBe(TextureFilter.Linear);
        texture.Wrap.ShouldBe(TextureWrap.Clamp);

        // The placeholder is shared, so the swap must not have destroyed it.
        ((FakeTexture)placeholder).Disposed.ShouldBeFalse();
        renderer.LiveTextures.ShouldContain((FakeTexture)placeholder);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Pump_is_a_no_op_when_nothing_is_pending()
    {
        var (assets, renderer) = CreateAttached();
        assets.LoadTexture(Grid);

        assets.PumpPendingUploads().ShouldBe(0);
        assets.PumpPendingUploads().ShouldBe(0);
        renderer.CreatedTextures.Count.ShouldBe(2); // placeholder + the one sync load

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Idle_pump_allocates_nothing()
    {
        var (assets, _) = CreateAttached();
        assets.LoadTexture(Grid);

        // Warm up: JIT the pump and drain anything the load left behind.
        for (int i = 0; i < 200; i++) assets.PumpPendingUploads();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) assets.PumpPendingUploads();
        long after = GC.GetAllocatedBytesForCurrentThread();

        // The pump runs once per frame forever; steady-state per-frame work has
        // to stay allocation-free, which is why both queues carry structs and
        // the coalescing set is reused rather than rebuilt.
        (after - before).ShouldBe(0);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Failed_async_load_keeps_the_placeholder_instead_of_throwing()
    {
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(new FakeRenderer());

        TextureAsset asset = assets.RequestTexture("Textures/does_not_exist.png");

        // The decode failure crosses back on the same queue a success would, so
        // it is reported on the render thread instead of vanishing into an
        // unobserved task.
        PumpUntil(assets, () => logger.MessagesAt(LogLevel.Error).Count > 0);

        asset.IsPlaceholder.ShouldBeTrue();
        asset.Texture.ShouldBeSameAs(assets.PlaceholderTexture);
        logger.MessagesAt(LogLevel.Error).ShouldContain(
            m => m.Contains("does_not_exist.png"), customMessage: logger.Describe());

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Unload_destroys_the_texture_and_deregisters_it_from_the_renderer()
    {
        var (assets, renderer) = CreateAttached();
        TextureAsset asset = assets.LoadTexture(Grid);
        var texture = (FakeTexture)asset.Texture;

        renderer.LiveTextures.ShouldContain(texture);

        assets.UnloadTexture(Grid).ShouldBeTrue();

        texture.Disposed.ShouldBeTrue();
        renderer.LiveTextures.ShouldNotContain(texture, "DestroyTexture must deregister, not just dispose");
        assets.TryGetTexture(Grid, out _).ShouldBeFalse();
        assets.TextureCount.ShouldBe(0);
        // A handle someone is still holding degrades to the placeholder rather
        // than pointing at a disposed GPU object.
        asset.IsPlaceholder.ShouldBeTrue();
        asset.Texture.ShouldBeSameAs(assets.PlaceholderTexture);

        assets.UnloadTexture(Grid).ShouldBeFalse();

        // Reloading afterwards makes a fresh instance, not a resurrected one.
        TextureAsset reloaded = assets.LoadTexture(Grid);
        reloaded.ShouldNotBeSameAs(asset);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Releasing_graphics_resources_destroys_everything_the_manager_owns()
    {
        var (assets, renderer) = CreateAttached();
        assets.LoadTexture(Grid);
        assets.LoadTexture(CheckerGray);
        renderer.LiveTextures.Count.ShouldBe(3); // placeholder + two textures

        assets.ReleaseGraphicsResources();

        renderer.LiveTextures.ShouldBeEmpty();
        renderer.CreatedTextures.ShouldAllBe(t => t.Disposed);
        assets.TextureCount.ShouldBe(0);
        assets.PlaceholderTexture.ShouldBeNull();

        // Idempotent: the engine calls it on both the normal and the crash path.
        Should.NotThrow(assets.ReleaseGraphicsResources);
    }

    [Fact]
    public void Shutdown_after_releasing_on_the_render_thread_is_clean()
    {
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(new FakeRenderer());
        assets.LoadTexture(Grid);

        // The engine's order: GPU teardown on the render thread, then the
        // CPU-side shutdown on the main thread.
        assets.ReleaseGraphicsResources();
        assets.Shutdown();

        logger.MessagesAt(LogLevel.Warning).ShouldBeEmpty(logger.Describe());
    }

    [Fact]
    public void Shutdown_without_releasing_first_warns_instead_of_touching_the_gpu()
    {
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        var renderer = new FakeRenderer();
        assets.AttachRenderer(renderer);
        TextureAsset asset = assets.LoadTexture(Grid);

        assets.Shutdown();

        // Destroying a GPU texture from the main thread would violate the
        // threading contract, so it is reported rather than done.
        logger.MessagesAt(LogLevel.Warning).ShouldContain(
            m => m.Contains("ReleaseGraphicsResources"), customMessage: logger.Describe());
        ((FakeTexture)asset.Texture).Disposed.ShouldBeFalse();
    }

    [Fact]
    public void Requesting_a_texture_before_a_renderer_is_attached_fails_loudly()
    {
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);

        Should.Throw<InvalidOperationException>(() => assets.RequestTexture(Grid));
        Should.Throw<InvalidOperationException>(() => assets.LoadTexture(Grid));
    }

    [Fact]
    public void Hot_reload_re_decodes_the_changed_file_and_swaps_the_gpu_texture()
    {
        string root = CreateTempContentRoot();
        try
        {
            string file = Path.Combine(root, "Textures", "swap_me.png");
            File.Copy(SourceTexture("checker_gray.png"), file);

            var assets = new AssetManager(NullLogger<AssetManager>.Instance, root, hotReloadEnabled: true);
            var renderer = new FakeRenderer();
            assets.AttachRenderer(renderer);

            TextureAsset asset = assets.LoadTexture("Textures/swap_me.png");
            var original = (FakeTexture)asset.Texture;
            original.Width.ShouldBe(128);
            original.Format.ShouldBe(TextureFormat.Rgb8);
            asset.Version.ShouldBe(1);

            // Loading registered a watcher for the folder, and exactly one.
            assets.WatchedDirectoryCount.ShouldBe(1);

            // Swap the file's contents for a differently-shaped image, then
            // drive the reload through the same queue the watcher feeds — the
            // notification itself is OS-timed, the reload logic is not.
            File.Copy(SourceTexture("gradient_mask.png"), file, overwrite: true);
            assets.NotifyFileChanged(file);

            PumpUntil(assets, () => asset.Version > 1);

            asset.Texture.ShouldNotBeSameAs(original);
            asset.IsPlaceholder.ShouldBeFalse();
            var reloaded = (FakeTexture)asset.Texture;
            reloaded.Width.ShouldBe(64);
            reloaded.Height.ShouldBe(64);
            reloaded.Format.ShouldBe(TextureFormat.R8);

            // The superseded texture is destroyed, not leaked.
            original.Disposed.ShouldBeTrue();
            renderer.LiveTextures.ShouldNotContain(original);
            // Same handle throughout: materials bound to it follow the swap.
            assets.TryGetTexture("Textures/swap_me.png", out TextureAsset? current).ShouldBeTrue();
            current.ShouldBeSameAs(asset);

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            DeleteTempContentRoot(root);
        }
    }

    [Fact]
    public void Loading_several_textures_from_one_folder_creates_exactly_one_watcher()
    {
        var assets = new AssetManager(NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: true);
        assets.AttachRenderer(new FakeRenderer());

        assets.LoadTexture(Grid);
        assets.LoadTexture(CheckerGray);
        assets.LoadTexture(Mask);

        // The bug this guards against is a leaked FileSystemWatcher per load:
        // native buffers plus duplicated change notifications.
        assets.WatchedDirectoryCount.ShouldBe(1);

        assets.UnloadTexture(Grid);
        assets.WatchedDirectoryCount.ShouldBe(1, "other assets in the folder are still loaded");

        assets.UnloadTexture(CheckerGray);
        assets.UnloadTexture(Mask);
        assets.WatchedDirectoryCount.ShouldBe(0, "the last asset in the folder was unloaded");

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Decode_landing_after_an_unload_creates_no_orphan_texture()
    {
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: true);
        var renderer = new FakeRenderer();
        assets.AttachRenderer(renderer);

        // The editor's load/unload (and unload-then-reload) loop: the handle
        // leaves the cache while its decode is still on the thread pool.
        const int cycles = 20;
        for (int i = 0; i < cycles; i++)
        {
            assets.RequestTexture(Grid);
            assets.UnloadTexture(Grid).ShouldBeTrue();

            // Wait for this cycle's decode to be drained before the next one, so
            // the count below is exact rather than timing-dependent.
            int expected = i + 1;
            PumpUntil(assets, () => DroppedDecodes(logger) == expected);
        }

        assets.ReleaseGraphicsResources();

        // A texture created for a handle that is no longer in the cache is a
        // texture nothing will ever destroy: ReleaseGraphicsResources only walks
        // the cache. Before the fix this left one leaked GPU texture per cycle.
        renderer.LiveTextures.ShouldBeEmpty("every GPU texture must be owned by a cached handle");
        renderer.CreatedTextures.ShouldAllBe(t => t.Disposed);
        // And the watcher StopWatchingIfUnused disposed must not come back.
        assets.WatchedDirectoryCount.ShouldBe(0);
    }

    [Fact]
    public void A_failed_async_load_is_retried_by_a_later_request()
    {
        string root = CreateTempContentRoot();
        try
        {
            var logger = new CapturingLogger();
            var assets = new AssetManager(logger, root, hotReloadEnabled: false);
            assets.AttachRenderer(new FakeRenderer());

            // The file is not there yet — the classic case is an art tool still
            // holding the write lock, which ImageDecoder's retries cannot always
            // outlast.
            TextureAsset asset = assets.RequestTexture("Textures/late.png");
            PumpUntil(assets, () => logger.MessagesAt(LogLevel.Error).Count > 0);
            asset.IsPlaceholder.ShouldBeTrue();
            asset.LoadFailed.ShouldBeTrue();

            File.Copy(SourceTexture("checker_gray.png"), Path.Combine(root, "Textures", "late.png"));

            // Same handle, so every material already bound to it recovers too.
            assets.RequestTexture("Textures/late.png").ShouldBeSameAs(asset);
            PumpUntil(assets, () => !asset.IsPlaceholder);

            asset.LoadFailed.ShouldBeFalse();
            asset.Version.ShouldBe(1);
            ((FakeTexture)asset.Texture).Width.ShouldBe(128);

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            DeleteTempContentRoot(root);
        }
    }

    [Fact]
    public void A_failed_async_load_is_retried_by_a_later_sync_load()
    {
        string root = CreateTempContentRoot();
        try
        {
            var logger = new CapturingLogger();
            var assets = new AssetManager(logger, root, hotReloadEnabled: false);
            assets.AttachRenderer(new FakeRenderer());

            TextureAsset asset = assets.RequestTexture("Textures/late.png");
            PumpUntil(assets, () => logger.MessagesAt(LogLevel.Error).Count > 0);

            File.Copy(SourceTexture("dev_grid.png"), Path.Combine(root, "Textures", "late.png"));

            // LoadTexture is documented to read the disk; a poisoned cache entry
            // must not turn it into a silent placeholder hand-back.
            TextureAsset loaded = assets.LoadTexture("Textures/late.png");

            loaded.ShouldBeSameAs(asset);
            loaded.IsPlaceholder.ShouldBeFalse();
            loaded.LoadFailed.ShouldBeFalse();
            ((FakeTexture)loaded.Texture).Width.ShouldBe(128);

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            DeleteTempContentRoot(root);
        }
    }

    [Fact]
    public void Repeated_requests_of_a_failing_texture_queue_one_decode_at_a_time()
    {
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(new FakeRenderer());

        TextureAsset asset = assets.RequestTexture("Textures/does_not_exist.png");
        PumpUntil(assets, () => logger.MessagesAt(LogLevel.Error).Count > 0);

        // Polling a failed handle from a frame loop retries, but must not pile
        // up one decode (and one error line) per call before the first returns.
        for (int i = 0; i < 5; i++)
            assets.RequestTexture("Textures/does_not_exist.png").ShouldBeSameAs(asset);

        PumpUntil(assets, () => logger.MessagesAt(LogLevel.Error).Count >= 2);
        logger.MessagesAt(LogLevel.Error).Count.ShouldBeLessThan(
            6, "each request may only retry once the previous decode came back");

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Different_sampler_state_for_one_image_loads_a_separate_texture()
    {
        var (assets, renderer) = CreateAttached();

        TextureAsset sharp = assets.LoadTexture(Grid, TextureFilter.Nearest, TextureWrap.Clamp);
        TextureAsset tiled = assets.LoadTexture(Grid, TextureFilter.LinearMipmap, TextureWrap.Repeat);

        // Sampler state is baked into the GPU texture on every backend, so
        // sharing one handle would silently give the second caller the first
        // one's mode — a repeat-tiled floor rendered with clamp smears its edge
        // texels across the whole surface.
        tiled.ShouldNotBeSameAs(sharp);
        ((FakeTexture)sharp.Texture).Wrap.ShouldBe(TextureWrap.Clamp);
        ((FakeTexture)sharp.Texture).Filter.ShouldBe(TextureFilter.Nearest);
        ((FakeTexture)tiled.Texture).Wrap.ShouldBe(TextureWrap.Repeat);
        ((FakeTexture)tiled.Texture).Filter.ShouldBe(TextureFilter.LinearMipmap);
        assets.TextureCount.ShouldBe(2);

        // Asking again for either one still hits the cache.
        assets.LoadTexture(Grid, TextureFilter.Nearest, TextureWrap.Clamp).ShouldBeSameAs(sharp);
        assets.LoadTexture(Grid).ShouldBeSameAs(tiled);
        renderer.CreatedTextures.Count.ShouldBe(3); // placeholder + the two variants

        // A path-only lookup resolves to the first variant loaded; the exact
        // overload picks one out.
        assets.TryGetTexture(Grid, out TextureAsset? first).ShouldBeTrue();
        first.ShouldBeSameAs(sharp);
        assets.TryGetTexture(Grid, TextureFilter.LinearMipmap, TextureWrap.Repeat, out TextureAsset? exact)
            .ShouldBeTrue();
        exact.ShouldBeSameAs(tiled);

        // Unloading the path drops every variant of it.
        assets.UnloadTexture(Grid).ShouldBeTrue();
        assets.TextureCount.ShouldBe(0);
        ((FakeTexture)renderer.CreatedTextures[1]).Disposed.ShouldBeTrue();
        ((FakeTexture)renderer.CreatedTextures[2]).Disposed.ShouldBeTrue();

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Hot_reload_re_decodes_every_sampler_variant_of_the_changed_file()
    {
        string root = CreateTempContentRoot();
        try
        {
            string file = Path.Combine(root, "Textures", "swap_me.png");
            File.Copy(SourceTexture("checker_gray.png"), file);

            var assets = new AssetManager(NullLogger<AssetManager>.Instance, root, hotReloadEnabled: true);
            var renderer = new FakeRenderer();
            assets.AttachRenderer(renderer);

            TextureAsset sharp = assets.LoadTexture("Textures/swap_me.png", TextureFilter.Nearest, TextureWrap.Clamp);
            TextureAsset tiled = assets.LoadTexture("Textures/swap_me.png");

            File.Copy(SourceTexture("gradient_mask.png"), file, overwrite: true);
            assets.NotifyFileChanged(file);

            // Both variants are separate GPU textures of the same file: reloading
            // only one would leave the other showing the pre-edit image forever.
            PumpUntil(assets, () => sharp.Version > 1 && tiled.Version > 1);

            ((FakeTexture)sharp.Texture).Width.ShouldBe(64);
            ((FakeTexture)sharp.Texture).Wrap.ShouldBe(TextureWrap.Clamp);
            ((FakeTexture)tiled.Texture).Width.ShouldBe(64);
            ((FakeTexture)tiled.Texture).Wrap.ShouldBe(TextureWrap.Repeat);

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            DeleteTempContentRoot(root);
        }
    }

    [Fact]
    public void Hot_reload_disabled_registers_no_watchers()
    {
        var (assets, _) = CreateAttached();
        assets.LoadTexture(Grid);

        assets.WatchedDirectoryCount.ShouldBe(0);

        assets.ReleaseGraphicsResources();
    }

    // ---- helpers ---------------------------------------------------------

    private static (AssetManager Assets, FakeRenderer Renderer) CreateAttached()
    {
        // Hot-reload off by default here: these tests assert on loading, and a
        // watcher on the shared repo folder would only add OS noise.
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);
        var renderer = new FakeRenderer();
        assets.AttachRenderer(renderer);
        return (assets, renderer);
    }

    // Decodes the pump dropped because their handle had left the cache.
    private static int DroppedDecodes(CapturingLogger logger)
        => logger.MessagesAt(LogLevel.Debug).Count(m => m.Contains("Dropping the decode"));

    private static string SourceTexture(string fileName)
        => ContentRoot.ResolveAbsolute(ContentRoot.Path, $"Textures/{fileName}");

    private static string CreateTempContentRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "SpectraAssetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Textures"));
        return root;
    }

    /// <summary>
    /// Removes a temp content root, tolerating a decode that has not let go of
    /// its file yet.
    /// </summary>
    /// <remarks>
    /// A hot-reload test writes a texture, and the write raises a real watcher
    /// event as well as the explicit notification the test sends. Windows
    /// raises more than one event for a single write, so a re-decode can still
    /// be reading the file on a thread-pool thread after the assertions have
    /// passed, and deleting the directory into that window throws
    /// <see cref="IOException"/> from the cleanup rather than from anything the
    /// test was checking. That failure names the wrong subsystem and appears
    /// roughly one run in ten, which is worse than a test that simply fails.
    /// </remarks>
    private static void DeleteTempContentRoot(string root)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }

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
