using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Colour space as it travels from a <c>.spectramat</c> line to a GPU texture.
/// </summary>
/// <remarks>
/// <para>
/// Three separate claims live here, and they are separate because they can fail
/// independently: the file format says what a texture is, the cache treats that
/// as part of the texture's identity, and a format that cannot carry sRGB says
/// so out loud rather than degrading in silence.
/// </para>
/// <para>
/// <see cref="ColorSpaceTests"/> pins the arithmetic; this pins the plumbing.
/// </para>
/// </remarks>
public sealed class TextureColorSpaceTests
{
    private const string Grid = "Textures/dev_grid.png";
    private const string Mask = "Textures/gradient_mask.png";

    // ---- the file format -------------------------------------------------

    [Fact]
    public void A_texture_is_srgb_unless_the_line_says_data()
    {
        MaterialDefinition definition = MaterialParser.Parse("""
            texture uDiffuse  = Textures/wall_brick.png
            texture uNormal   = Textures/wall_normal.png, data
            texture uEmissive = Textures/glow.png, srgb
            """, "spaces.spectramat");

        definition.Warnings.ShouldBeEmpty();

        // An image file is a picture until its author says otherwise. The
        // default has to be this way round: an albedo silently loaded as data
        // renders dark with nothing in the log, whereas a normal map loaded as
        // colour is obvious the moment anyone looks at the surface.
        definition.TryGetTextureSlot("uDiffuse", out MaterialTextureSlot diffuse).ShouldBeTrue();
        diffuse.ColorSpace.ShouldBe(TextureColorSpace.Srgb);

        definition.TryGetTextureSlot("uNormal", out MaterialTextureSlot normal).ShouldBeTrue();
        normal.ColorSpace.ShouldBe(TextureColorSpace.Linear);

        definition.TryGetTextureSlot("uEmissive", out MaterialTextureSlot emissive).ShouldBeTrue();
        emissive.ColorSpace.ShouldBe(TextureColorSpace.Srgb);
    }

    [Fact]
    public void The_keyword_is_data_because_linear_already_means_a_filter()
    {
        MaterialDefinition definition = MaterialParser.Parse("""
            texture uNormal = Textures/n.png, linear, clamp, data
            """, "order.spectramat");

        definition.Warnings.ShouldBeEmpty();
        definition.TryGetTextureSlot("uNormal", out MaterialTextureSlot slot).ShouldBeTrue();

        // The point of the test: one line, both meanings, no ambiguity. 'linear'
        // was already the bilinear filter when this option was added, so reusing
        // it for the colour space would have made this exact line unparseable.
        slot.Filter.ShouldBe(TextureFilter.Linear);
        slot.Wrap.ShouldBe(TextureWrap.Clamp);
        slot.ColorSpace.ShouldBe(TextureColorSpace.Linear);
    }

    [Fact]
    public void Options_are_order_independent()
    {
        MaterialDefinition definition = MaterialParser.Parse("""
            texture uA = Textures/a.png, data, nearest, clamp
            texture uB = Textures/b.png, clamp, data, nearest
            """, "shuffled.spectramat");

        definition.Warnings.ShouldBeEmpty();
        definition.TryGetTextureSlot("uA", out MaterialTextureSlot a).ShouldBeTrue();
        definition.TryGetTextureSlot("uB", out MaterialTextureSlot b).ShouldBeTrue();

        a.Filter.ShouldBe(b.Filter);
        a.Wrap.ShouldBe(b.Wrap);
        a.ColorSpace.ShouldBe(b.ColorSpace);
        a.ColorSpace.ShouldBe(TextureColorSpace.Linear);
    }

    [Fact]
    public void An_unknown_option_still_warns_and_names_the_new_choices()
    {
        MaterialDefinition definition = MaterialParser.Parse("""
            texture uDiffuse = Textures/a.png, gamma
            """, "typo.spectramat");

        definition.Warnings.ShouldContain(w => w.Contains("unknown option 'gamma'"));
        definition.Warnings.ShouldContain(w => w.Contains("srgb/data"));

        // The bad option costs that option, never the texture.
        definition.TryGetTextureSlot("uDiffuse", out MaterialTextureSlot slot).ShouldBeTrue();
        slot.ColorSpace.ShouldBe(TextureColorSpace.Srgb);
    }

    // ---- the cache -------------------------------------------------------

    [Fact]
    public void One_image_asked_for_both_ways_loads_two_textures()
    {
        var (assets, renderer) = Attach(NullLogger<AssetManager>.Instance);

        TextureAsset albedo = assets.LoadTexture(
            Grid, TextureFilter.Nearest, TextureWrap.Repeat, TextureColorSpace.Srgb);
        TextureAsset data = assets.LoadTexture(
            Grid, TextureFilter.Nearest, TextureWrap.Repeat, TextureColorSpace.Linear);

        // Colour space is baked into the GPU format on all three backends, just
        // like filter and wrap, so these cannot share one texture. The case is
        // real rather than hypothetical: one grid image is legitimately both a
        // wall albedo and a mask.
        data.ShouldNotBeSameAs(albedo);
        assets.TextureCount.ShouldBe(2);
        ((FakeTexture)albedo.Texture).ColorSpace.ShouldBe(TextureColorSpace.Srgb);
        ((FakeTexture)data.Texture).ColorSpace.ShouldBe(TextureColorSpace.Linear);

        // Both stay cached; neither request decodes again.
        assets.LoadTexture(Grid, TextureFilter.Nearest, TextureWrap.Repeat, TextureColorSpace.Srgb)
            .ShouldBeSameAs(albedo);
        assets.LoadTexture(Grid, TextureFilter.Nearest, TextureWrap.Repeat, TextureColorSpace.Linear)
            .ShouldBeSameAs(data);
        renderer.CreatedTextures.Count.ShouldBe(3); // placeholder + the two variants

        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_material_carries_its_slots_colour_space_into_the_cache()
    {
        var (assets, _) = Attach(NullLogger<AssetManager>.Instance);

        // The shipped materials name no colour space, so they take the default,
        // and the handle their texture resolves to must be the sRGB variant --
        // not merely "some variant of that path".
        assets.LoadMaterial("Materials/dev_grid.spectramat");

        assets.TryGetTexture(Grid, TextureFilter.LinearMipmap, TextureWrap.Repeat,
            out TextureAsset? srgbVariant, TextureColorSpace.Srgb).ShouldBeTrue();
        srgbVariant.ColorSpace.ShouldBe(TextureColorSpace.Srgb);

        assets.TryGetTexture(Grid, TextureFilter.LinearMipmap, TextureWrap.Repeat,
            out _, TextureColorSpace.Linear).ShouldBeFalse();

        assets.ReleaseGraphicsResources();
    }

    // ---- the formats that cannot ----------------------------------------

    [Fact]
    public void A_single_channel_image_asked_for_srgb_falls_back_and_says_so()
    {
        var logger = new CapturingLogger();
        var (assets, _) = Attach(logger);

        // gradient_mask.png is one channel, and no backend has a one-channel
        // sRGB format. Falling back is right; falling back silently is not,
        // because the material author would have no way to learn that the flag
        // they wrote did nothing.
        TextureAsset asset = assets.LoadTexture(
            Mask, TextureFilter.Linear, TextureWrap.Repeat, TextureColorSpace.Srgb);

        asset.Texture.Format.ShouldBe(TextureFormat.R8);
        asset.Texture.ColorSpace.ShouldBe(TextureColorSpace.Linear);

        logger.MessagesAt(LogLevel.Warning).ShouldContain(
            m => m.Contains("gradient_mask") && m.Contains("no sRGB form"),
            customMessage: logger.Describe());
        logger.MessagesAt(LogLevel.Error).ShouldBeEmpty(logger.Describe());

        assets.ReleaseGraphicsResources();
    }

    private static (AssetManager Assets, FakeRenderer Renderer) Attach(ILogger logger)
    {
        // Hot-reload off: these tests assert on loading, and a watcher on the
        // shared repo folder would only add OS noise.
        var assets = new AssetManager(logger, ContentRoot.Path, hotReloadEnabled: false);
        var renderer = new FakeRenderer();
        assets.AttachRenderer(renderer);
        return (assets, renderer);
    }

    [Fact]
    public void The_requested_space_stays_the_cache_key_even_when_it_cannot_be_honoured()
    {
        var (assets, _) = Attach(NullLogger<AssetManager>.Instance);

        // The handle remembers what was ASKED for and the texture remembers what
        // it GOT. Keying the cache on the resolved value instead would collapse
        // these two into one entry, and the second caller would then get a
        // handle whose ColorSpace disagreed with its own request -- harmless for
        // R8 today, wrong the moment any format gains an sRGB form.
        TextureAsset asked = assets.LoadTexture(
            Mask, TextureFilter.Linear, TextureWrap.Repeat, TextureColorSpace.Srgb);
        TextureAsset plain = assets.LoadTexture(
            Mask, TextureFilter.Linear, TextureWrap.Repeat, TextureColorSpace.Linear);

        asked.ColorSpace.ShouldBe(TextureColorSpace.Srgb);
        plain.ColorSpace.ShouldBe(TextureColorSpace.Linear);
        asked.ShouldNotBeSameAs(plain);

        // Both resolved to the same thing on the GPU, which is the fallback
        // working.
        asked.Texture.ColorSpace.ShouldBe(TextureColorSpace.Linear);
        plain.Texture.ColorSpace.ShouldBe(TextureColorSpace.Linear);

        assets.ReleaseGraphicsResources();
    }
}
