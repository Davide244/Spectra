using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Images;
using Spectra.Kitchen.Packs;
using Spectra.Kitchen.Rules;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics;
using System;
using System.IO;
using System.Linq;
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// <see cref="AssetManager"/> over cooked content: a material naming a
/// <c>.png</c> that resolves to the <c>.simage</c> beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this file exists for is the one the content layer has already
/// paid for once</b>, recorded on <see cref="AssetManager"/> itself: the manager
/// reaches content in several places, and if the existence probe and the open
/// disagree about which file an image IS, every material in a packed build binds
/// the magenta placeholder while every log line reads healthy. The image fork
/// added a fourth thing those reads have to agree about, so the probe and the open
/// are asserted together, on one manager, over content that only exists in its
/// cooked form.
/// </para>
/// <para>
/// <b>Against a real renderer rather than a fake one.</b> A fake proves the
/// routing; the cooked branch also creates a GPU texture from a payload it does
/// not own, and the whole point of that path is that no copy stands between the
/// mapped bytes and the driver.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class CookedAssetGlTests : IDisposable
{
    private readonly GlRendererFixture _fixture;
    private readonly string _root;

    private const string SourcePath = "Textures/orientation_probe.png";
    private const string CookedPath = "Textures/orientation_probe.simage";
    private const string MaterialPath = "Materials/probe.spectramat";

    public CookedAssetGlTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
        _root = Path.Combine(Path.GetTempPath(), $"spectra_cooked_{Guid.NewGuid():N}");
    }

    [Fact]
    public void A_texture_named_as_a_png_loads_from_the_simage_beside_it()
    {
        // The tree holds NO .png at all, which is what a shipped build looks like:
        // a cook emits the .simage and does not also copy the source, because
        // shipping both would double every texture for content nothing reads.
        WriteCooked(Cook());
        using AssetManager assets = Open();

        TextureAsset texture = assets.LoadTexture(SourcePath, TextureFilter.Nearest, TextureWrap.Clamp);

        texture.IsPlaceholder.ShouldBeFalse();
        texture.Texture.Format.ShouldBe(TextureFormat.Bc7);

        // And it is still keyed by the path the caller asked for, because that is
        // the identity a material, a model and a pack id all use.
        texture.RelativePath.ShouldBe(SourcePath);
    }

    [Fact]
    public void A_materials_texture_slot_probes_the_same_path_the_open_takes()
    {
        // The exact bug the seven-sites note describes, one asset kind further on:
        // a probe that looked only for the authored file would bind the magenta
        // placeholder into every material of a cooked build and report nothing.
        WriteCooked(Cook());
        File.WriteAllText(
            Path.Combine(_root, "Materials", "probe.spectramat"),
            $"shader = lit\ntexture uDiffuse = {SourcePath}, nearest, clamp\n");

        using AssetManager assets = Open();
        Material material = assets.LoadMaterial(MaterialPath);

        material.ShouldNotBe(assets.DefaultMaterial);
        material.TextureCount.ShouldBe(1);

        material.TryGetTexture("uDiffuse", out _, out Texture? bound).ShouldBeTrue();

        // The format is the assertion: the placeholder is an RGB8 checker, so a
        // slot that fell back to it reads Rgb8 here, and nothing else about the
        // material would look wrong.
        bound.Format.ShouldBe(TextureFormat.Bc7);
    }

    [Fact]
    public void An_async_request_lands_the_cooked_texture_through_the_pump()
    {
        // The path that carries the payload across a thread boundary. A cooked
        // upload holds its ContentBlob until the pump has created the GPU texture,
        // because the mips it describes are offsets into those very bytes - and on
        // a mounted pack those bytes are a memory-mapped view whose unmapping under
        // a live span is an access violation with no managed stack.
        WriteCooked(Cook());
        using AssetManager assets = Open();

        TextureAsset handle = assets.RequestTexture(SourcePath, TextureFilter.Nearest, TextureWrap.Clamp);
        handle.IsPlaceholder.ShouldBeTrue("a request returns immediately on the placeholder");

        Pump(assets, handle);

        handle.IsPlaceholder.ShouldBeFalse();
        handle.Texture.Format.ShouldBe(TextureFormat.Bc7);
        handle.LoadFailed.ShouldBeFalse();
    }

    [Fact]
    public void A_pack_mounted_alone_serves_its_cooked_textures_with_no_loose_file_anywhere()
    {
        // The shipped shape: one .spack, nothing loose. It also exercises the
        // no-copy branch for real, since a mounted pack hands out spans into its
        // mapped view rather than pooled arrays.
        Directory.CreateDirectory(_root);
        string packPath = Path.Combine(_root, "content.spack");

        var writer = new PackWriter();
        writer.Add(CookedPath, PackEntryKind.Image, Cook());
        writer.WriteToFile(packPath);

        using var pack = new PackSource(NullLogger.Instance, packPath);
        var stack = new ContentSourceStack();
        stack.Mount(pack);

        using var assets = new AssetManager(NullLogger<AssetManager>.Instance, _root, stack, hotReloadEnabled: false);
        assets.AttachRenderer(_fixture.Renderer);

        TextureAsset texture = assets.LoadTexture(SourcePath, TextureFilter.Nearest, TextureWrap.Clamp);

        texture.IsPlaceholder.ShouldBeFalse();
        texture.Texture.Format.ShouldBe(TextureFormat.Bc7);

        // Released before the mount is: the manager holds the GPU texture, never
        // the bytes, so a pack reference kept past the upload would defer the
        // unmount for the life of the process.
        assets.ReleaseGraphicsResources();
    }

    [Fact]
    public void A_simage_from_another_profile_version_degrades_rather_than_taking_the_frame_down()
    {
        // Cooked artifacts version the strict way, and the runtime's answer to one
        // it cannot read has to be the same soft landing an unreadable PNG gets:
        // the magenta placeholder and a warning, never an exception out of the
        // render loop. The cooker is where a stale artifact is fatal.
        WriteCooked(CookForVersion(EngineInfo.TextureFormatVersion + 1));
        using AssetManager assets = Open();

        TextureAsset handle = assets.RequestTexture(SourcePath, TextureFilter.Nearest, TextureWrap.Clamp);
        Pump(assets, handle);

        handle.IsPlaceholder.ShouldBeTrue();
        handle.LoadFailed.ShouldBeTrue("a refused .simage must stay retryable, exactly like a failed decode");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Failing a test on its own cleanup helps nobody.
        }
    }

    // --- helpers -------------------------------------------------------------

    private static byte[] Cook()
    {
        var context = new RuleContext(ContentRoot.Path, SourcePath, CookProfile.Ship);
        new ImageRule().Cook(context);
        return context.Emissions.Single().Payload;
    }

    // The same container at a version this build does not read. Written through the
    // writer rather than by patching a byte, so what is being refused is a file a
    // future cooker would legitimately produce.
    private static byte[] CookForVersion(int profileVersion)
    {
        DecodedImage image = ImageDecoder.DecodeFile(
            Path.Combine(ContentRoot.Path, "Textures", "orientation_probe.png"));

        return Ktx2Writer.Write(
            TextureFormat.Bc7,
            image.Width,
            image.Height,
            ImageBlockEncoder.Encode(image, TextureFormat.Bc7, BCnEncoder.Encoder.CompressionQuality.Fast),
            SimageRowOrder.BottomUp,
            profileVersion);
    }

    private void WriteCooked(byte[] cooked)
    {
        Directory.CreateDirectory(Path.Combine(_root, "Textures"));
        Directory.CreateDirectory(Path.Combine(_root, "Materials"));
        File.WriteAllBytes(Path.Combine(_root, "Textures", "orientation_probe.simage"), cooked);
    }

    private AssetManager Open()
    {
        var assets = new AssetManager(NullLogger<AssetManager>.Instance, _root, hotReloadEnabled: false);
        assets.AttachRenderer(_fixture.Renderer);
        return assets;
    }

    // The decode runs on the thread pool, so the pump is driven until the result
    // lands rather than once. Bounded, because a test that spins forever on a
    // regression is worse than one that fails.
    private static void Pump(AssetManager assets, TextureAsset handle)
    {
        for (int i = 0; i < 2000 && handle.IsPlaceholder && !handle.LoadFailed; i++)
        {
            assets.PumpPendingUploads();
            System.Threading.Thread.Sleep(1);
        }
    }
}
