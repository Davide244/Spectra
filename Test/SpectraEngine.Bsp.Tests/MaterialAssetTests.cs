using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Graphics;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The material half of the asset pipeline, exercised headlessly against a
/// <see cref="FakeRenderer"/> — same division of labour as
/// <see cref="AssetManagerTests"/>, except material loading is fully
/// synchronous, so no pumping is involved.
/// </summary>
/// <remarks>
/// The theme running through these tests is that <em>content problems must not
/// be able to crash a frame</em>: a missing file, a missing texture, a shader
/// that never resolved. Each one degrades to something drawable and logs, and
/// <see cref="AssetManager.DefaultMaterial"/> is the backstop that makes that
/// possible — which is why several tests do nothing but prove it is never null.
/// </remarks>
public sealed class MaterialAssetTests
{
    private const string DevGrid = "Materials/dev_grid.spectramat";
    private const string CheckerGray = "Materials/checker_gray.spectramat";
    private const string GridTexture = "Textures/dev_grid.png";

    [Fact]
    public void Loads_a_shipped_material_with_its_shader_parameters_and_texture()
    {
        var (assets, renderer) = CreateAttached();

        Material material = assets.LoadMaterial(DevGrid);

        material.Name.ShouldBe("dev_grid");
        material.SourcePath.ShouldBe(DevGrid);
        material.Shader.ShouldBeSameAs(renderer.DefaultShader);

        // #8C8C99 as three linear components.
        material.TryGetVector3("uBaseColor", out Vector3 baseColor).ShouldBeTrue();
        baseColor.ShouldBe(new Vector3(0x8C / 255f, 0x8C / 255f, 0x99 / 255f));

        material.TryGetTexture("uDiffuse", out int unit, out Texture? texture).ShouldBeTrue();
        unit.ShouldBe(0);
        texture.ShouldNotBeSameAs(assets.PlaceholderTexture);
        ((FakeTexture)texture).Width.ShouldBe(128);

        // The texture went through the texture cache, so the material shares one
        // GPU texture with anything else that names the same image.
        assets.TryGetTexture(GridTexture, out TextureAsset? asset).ShouldBeTrue();
        asset.Texture.ShouldBeSameAs(texture);
        asset.Filter.ShouldBe(TextureFilter.LinearMipmap);
        asset.Wrap.ShouldBe(TextureWrap.Repeat);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Texture_sampling_options_from_the_file_reach_the_gpu_upload()
    {
        var (assets, _) = CreateAttached();

        // checker_gray.spectramat asks for nearest filtering on purpose.
        assets.LoadMaterial(CheckerGray);

        assets.TryGetTexture("Textures/checker_gray.png", out TextureAsset? asset).ShouldBeTrue();
        var texture = (FakeTexture)asset.Texture;
        texture.Filter.ShouldBe(TextureFilter.Nearest);
        texture.Wrap.ShouldBe(TextureWrap.Repeat);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Same_path_returns_the_same_instance_and_parses_once()
    {
        var (assets, renderer) = CreateAttached();

        Material first = assets.LoadMaterial(DevGrid);
        Material second = assets.LoadMaterial(DevGrid);
        // Different spelling, same asset: the cache key is normalised exactly
        // like the texture cache's.
        Material third = assets.LoadMaterial("Materials\\dev_grid.spectramat");

        second.ShouldBeSameAs(first);
        third.ShouldBeSameAs(first);
        assets.MaterialCount.ShouldBe(1);
        // Placeholder + one diffuse upload: the repeat loads never touched disk.
        renderer.CreatedTextures.Count.ShouldBe(2);

        Material other = assets.LoadMaterial(CheckerGray);
        other.ShouldNotBeSameAs(first);
        assets.MaterialCount.ShouldBe(2);

        assets.TryGetMaterial(DevGrid, out Material? found).ShouldBeTrue();
        found.ShouldBeSameAs(first);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Unknown_keys_warn_but_the_material_still_loads()
    {
        var logger = new CapturingLogger();
        string root = CreateTempContentRoot();
        try
        {
            WriteMaterial(root, "future.spectramat", """
                shader = lit
                doubleSided = true
                texture uDiffuse = Textures/dev_grid.png
                color uBaseColor = 1 0.5 0
                """);

            var assets = Attach(logger, root, out FakeRenderer renderer);
            Material material = assets.LoadMaterial("Materials/future.spectramat");

            material.ShouldNotBeSameAs(assets.DefaultMaterial);
            material.Shader.ShouldBeSameAs(renderer.DefaultShader);
            material.TryGetVector3("uBaseColor", out Vector3 color).ShouldBeTrue();
            color.ShouldBe(new Vector3(1f, 0.5f, 0f));
            material.TryGetTexture("uDiffuse", out _, out _).ShouldBeTrue();

            logger.MessagesAt(LogLevel.Warning).ShouldContain(
                m => m.Contains("doubleSided") && m.Contains("unknown key"), customMessage: logger.Describe());
            logger.MessagesAt(LogLevel.Error).ShouldBeEmpty(logger.Describe());

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_texture_falls_back_to_the_placeholder_with_a_warning()
    {
        var logger = new CapturingLogger();
        string root = CreateTempContentRoot();
        try
        {
            WriteMaterial(root, "gone.spectramat", """
                texture uDiffuse = Textures/not_here.png
                texture uMask    = Textures/dev_grid.png
                float uScale = 3
                """);

            var assets = Attach(logger, root, out _);
            Material material = assets.LoadMaterial("Materials/gone.spectramat");

            // The slot is bound to the placeholder rather than left empty: an
            // unbound sampler reads whatever the previous draw left on the unit.
            material.TryGetTexture("uDiffuse", out int unit, out Texture? missing).ShouldBeTrue();
            unit.ShouldBe(0);
            missing.ShouldBeSameAs(assets.PlaceholderTexture);

            // The healthy slot beside it is unaffected, as is the rest of the file.
            material.TryGetTexture("uMask", out int maskUnit, out Texture? mask).ShouldBeTrue();
            maskUnit.ShouldBe(1);
            mask.ShouldNotBeSameAs(assets.PlaceholderTexture);
            material.TryGetFloat("uScale", out float scale).ShouldBeTrue();
            scale.ShouldBe(3f);

            logger.MessagesAt(LogLevel.Warning).ShouldContain(
                m => m.Contains("not_here.png") && m.Contains("placeholder"), customMessage: logger.Describe());
            logger.MessagesAt(LogLevel.Error).ShouldBeEmpty(logger.Describe());

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Texture_path_escaping_the_content_root_falls_back_to_the_placeholder()
    {
        var logger = new CapturingLogger();
        string root = CreateTempContentRoot();
        try
        {
            WriteMaterial(root, "escape.spectramat", "texture uDiffuse = ../../etc/passwd");

            var assets = Attach(logger, root, out _);
            Material material = assets.LoadMaterial("Materials/escape.spectramat");

            // Content references stay inside the content root; a file that tries
            // otherwise is a warning, not an exception out of the draw loop.
            material.TryGetTexture("uDiffuse", out _, out Texture? texture).ShouldBeTrue();
            texture.ShouldBeSameAs(assets.PlaceholderTexture);
            logger.MessagesAt(LogLevel.Warning).ShouldContain(
                m => m.Contains("not usable"), customMessage: logger.Describe());

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_material_file_falls_back_to_the_default_material()
    {
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(new FakeRenderer());

        Material material = assets.LoadMaterial("Materials/does_not_exist.spectramat");

        material.ShouldBeSameAs(assets.DefaultMaterial);
        logger.MessagesAt(LogLevel.Warning).Count(m => m.Contains("does_not_exist")).ShouldBe(1);

        // Cached under the requested key, so a caller that asks every frame pays
        // a dictionary probe instead of another stat() and another warning.
        assets.LoadMaterial("Materials/does_not_exist.spectramat").ShouldBeSameAs(material);
        logger.MessagesAt(LogLevel.Warning).Count(m => m.Contains("does_not_exist")).ShouldBe(1);

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Unknown_shader_name_warns_and_falls_back_to_the_built_in_lit_shader()
    {
        var logger = new CapturingLogger();
        string root = CreateTempContentRoot();
        try
        {
            WriteMaterial(root, "exotic.spectramat", """
                shader = raymarched_clouds
                float uScale = 1
                """);

            var assets = Attach(logger, root, out FakeRenderer renderer);
            Material material = assets.LoadMaterial("Materials/exotic.spectramat");

            material.Shader.ShouldBeSameAs(renderer.DefaultShader);
            logger.MessagesAt(LogLevel.Warning).ShouldContain(
                m => m.Contains("raymarched_clouds"), customMessage: logger.Describe());

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Shader_resolver_hook_wins_over_the_default_shader()
    {
        var logger = new CapturingLogger();
        string root = CreateTempContentRoot();
        try
        {
            WriteMaterial(root, "custom.spectramat", "shader = glowy");

            var assets = Attach(logger, root, out _);
            var custom = new NoopShaderProgram();
            assets.ShaderResolver = name => name == "glowy" ? custom : null;

            assets.LoadMaterial("Materials/custom.spectramat").Shader.ShouldBeSameAs(custom);
            logger.MessagesAt(LogLevel.Warning).ShouldBeEmpty(logger.Describe());

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Default_material_is_never_null_at_any_point_in_the_managers_life()
    {
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);

        // Before a renderer exists: not drawable, but a real object — this is
        // what stops a null reference reaching the draw loop.
        Material material = assets.DefaultMaterial.ShouldNotBeNull();
        material.Name.ShouldBe(AssetManager.DefaultMaterialName);
        material.Shader.ShouldBeNull();
        material.TryGetVector3("uBaseColor", out Vector3 white).ShouldBeTrue();
        white.ShouldBe(Vector3.One);

        var renderer = new FakeRenderer();
        assets.AttachRenderer(renderer);

        // Attaching completes the same instance rather than replacing it, so a
        // mesh that grabbed it early starts drawing correctly.
        assets.DefaultMaterial.ShouldBeSameAs(material);
        material.Shader.ShouldBeSameAs(renderer.DefaultShader);
        material.TryGetTexture("uDiffuse", out int unit, out Texture? texture).ShouldBeTrue();
        unit.ShouldBe(0);
        texture.ShouldBeSameAs(assets.PlaceholderTexture);

        assets.ReleaseGraphicsResources();

        // After teardown it survives, stripped back to non-drawable: its texture
        // was just destroyed, so keeping the binding would resolve to a disposed
        // GPU object.
        assets.DefaultMaterial.ShouldBeSameAs(material);
        material.Shader.ShouldBeNull();
        material.TextureCount.ShouldBe(0);

        assets.Shutdown();
        assets.DefaultMaterial.ShouldNotBeNull();
    }

    [Fact]
    public void A_renderer_without_a_default_shader_still_yields_a_usable_default_material()
    {
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        var renderer = new FakeRenderer();
        renderer.ClearDefaultShader();

        assets.AttachRenderer(renderer);

        assets.DefaultMaterial.ShouldNotBeNull();
        assets.DefaultMaterial.Shader.ShouldBeNull();
        // Applying a shaderless material is a no-op, not a crash: the pipelines
        // skip such an item, and nothing here may throw either.
        Should.NotThrow(assets.DefaultMaterial.Apply);
        logger.MessagesAt(LogLevel.Warning).ShouldContain(
            m => m.Contains("no default shader"), customMessage: logger.Describe());

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Releasing_graphics_resources_drops_the_material_cache()
    {
        var (assets, _) = CreateAttached();
        assets.LoadMaterial(DevGrid);
        assets.LoadMaterial(CheckerGray);
        assets.MaterialCount.ShouldBe(2);

        assets.ReleaseGraphicsResources();

        assets.MaterialCount.ShouldBe(0);
        assets.TryGetMaterial(DevGrid, out _).ShouldBeFalse();
    }

    [Fact]
    public void Loading_a_material_before_a_renderer_is_attached_fails_loudly()
    {
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);

        // A caller error, unlike every content problem above: without a renderer
        // there is nowhere to put the textures.
        Should.Throw<InvalidOperationException>(() => assets.LoadMaterial(DevGrid));
    }

    [Fact]
    public void A_material_that_omits_a_parameter_pushes_a_default_instead_of_the_last_draws_value()
    {
        string root = CreateTempContentRoot();
        try
        {
            WriteMaterial(root, "tinted.spectramat", """
                shader = lit
                texture uDiffuse = Textures/dev_grid.png
                color uBaseColor = 1 0 0
                """);
            // No colour line at all — the parser accepts this silently, which is
            // exactly why the material has to carry a defined value anyway.
            WriteMaterial(root, "untinted.spectramat", """
                shader = lit
                texture uDiffuse = Textures/dev_grid.png
                """);

            var assets = Attach(NullLogger<AssetManager>.Instance, root, out _);
            Material tinted = assets.LoadMaterial("Materials/tinted.spectramat");
            Material untinted = assets.LoadMaterial("Materials/untinted.spectramat");

            // The file's own value still wins over the seeded default.
            tinted.TryGetVector3("uBaseColor", out Vector3 red).ShouldBeTrue();
            red.ShouldBe(new Vector3(1f, 0f, 0f));
            untinted.TryGetVector3("uBaseColor", out Vector3 seeded).ShouldBeTrue();
            seeded.ShouldBe(Vector3.One);

            // Every backend keeps uniform state between draws, so a material
            // that writes nothing for uBaseColor inherits whatever the previous
            // draw left there — and on the very first draw inherits the
            // backend's zero-initialised value, i.e. albedo * 0 = solid black.
            var shader = new RecordingShaderProgram();
            tinted.Shader = shader;
            untinted.Shader = shader;

            tinted.Apply();
            shader.Vectors3["uBaseColor"].ShouldBe(new Vector3(1f, 0f, 0f));

            untinted.Apply();
            shader.Vectors3["uBaseColor"].ShouldBe(
                Vector3.One, "an untinted surface must not inherit the previous material's tint");

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../evil.spectramat")]
    [InlineData("C:/evil.spectramat")]
    [InlineData("/")]
    public void Resolving_an_interned_path_that_normalisation_rejects_degrades_to_the_default(string badPath)
    {
        var logger = new CapturingLogger();
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(new FakeRenderer());

        // MaterialRegistry only trims and folds separators, so a path like this
        // survives interning and reaches path normalisation at resolve time.
        MaterialRef reference = MaterialRegistry.Intern(badPath);

        // ResolveMaterial runs inside the static-world GPU swap on the render
        // thread: a throw here ends the render thread and repeats on every
        // compile, so it has to degrade like every other bad content reference.
        Material material = Should.NotThrow(() => assets.ResolveMaterial(reference));
        material.ShouldBeSameAs(assets.DefaultMaterial);

        // Warned about once, not once per compile.
        Should.NotThrow(() => assets.ResolveMaterial(reference));
        logger.MessagesAt(LogLevel.Warning).Count(m => m.Contains(badPath)).ShouldBe(1, logger.Describe());
        logger.MessagesAt(LogLevel.Error).ShouldBeEmpty(logger.Describe());

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void Two_materials_sharing_an_image_with_different_sampling_get_their_own_texture()
    {
        string root = CreateTempContentRoot();
        try
        {
            WriteMaterial(root, "sharp.spectramat", "texture uDiffuse = Textures/dev_grid.png, nearest, clamp");
            WriteMaterial(root, "tiled.spectramat", "texture uDiffuse = Textures/dev_grid.png, linearmipmap, repeat");

            var assets = Attach(NullLogger<AssetManager>.Instance, root, out _);
            Material sharp = assets.LoadMaterial("Materials/sharp.spectramat");
            Material tiled = assets.LoadMaterial("Materials/tiled.spectramat");

            sharp.TryGetTexture("uDiffuse", out _, out Texture? sharpTexture).ShouldBeTrue();
            tiled.TryGetTexture("uDiffuse", out _, out Texture? tiledTexture).ShouldBeTrue();

            // Load order used to decide which of the two got its sampler state;
            // the loser rendered with the other's, and a repeat-tiled surface
            // clamped to the edge is a smear, not a tile.
            sharpTexture.ShouldNotBeSameAs(tiledTexture);
            ((FakeTexture)sharpTexture).Filter.ShouldBe(TextureFilter.Nearest);
            ((FakeTexture)sharpTexture).Wrap.ShouldBe(TextureWrap.Clamp);
            ((FakeTexture)tiledTexture).Filter.ShouldBe(TextureFilter.LinearMipmap);
            ((FakeTexture)tiledTexture).Wrap.ShouldBe(TextureWrap.Repeat);

            assets.ReleaseGraphicsResources();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Applying_a_material_allocates_nothing()
    {
        var (assets, _) = CreateAttached();
        Material material = assets.LoadMaterial(DevGrid);
        material.Shader.ShouldNotBeNull();

        // Warm up: JIT Apply and the dictionary enumerators it walks.
        for (int i = 0; i < 200; i++) material.Apply();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) material.Apply();
        long after = GC.GetAllocatedBytesForCurrentThread();

        // Apply runs once per draw call, every frame — the per-frame budget is
        // zero, which is why the maps are walked with struct enumerators and a
        // texture binding resolves with a field read.
        (after - before).ShouldBe(0);

        assets.ReleaseGraphicsResources();
    }

    // ---- helpers ---------------------------------------------------------

    private static (AssetManager Assets, FakeRenderer Renderer) CreateAttached()
    {
        var renderer = new FakeRenderer();
        var assets = new AssetManager(
            NullLogger<AssetManager>.Instance, ContentRoot.Path, hotReloadEnabled: false);
        assets.AttachRenderer(renderer);
        return (assets, renderer);
    }

    private static AssetManager Attach(ILogger logger, string root, out FakeRenderer renderer)
    {
        renderer = new FakeRenderer();
        var assets = new AssetManager(logger, root, hotReloadEnabled: false);
        assets.AttachRenderer(renderer);
        return assets;
    }

    // A throwaway content root with the repo's textures copied in, so a material
    // written here can reference real images without touching the repo folder.
    private static string CreateTempContentRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "SpectraMaterialTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Materials"));
        Directory.CreateDirectory(Path.Combine(root, "Textures"));
        File.Copy(
            ContentRoot.ResolveAbsolute(ContentRoot.Path, GridTexture),
            Path.Combine(root, "Textures", "dev_grid.png"));
        return root;
    }

    private static void WriteMaterial(string root, string fileName, string contents)
        => File.WriteAllText(Path.Combine(root, "Materials", fileName), contents);
}
