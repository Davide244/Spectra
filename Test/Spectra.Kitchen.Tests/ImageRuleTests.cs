using BCnEncoder.Encoder;
using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Images;
using Spectra.Kitchen.Rules;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics;
using System.IO;
using System.Linq;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The image cook: a PNG in, a <c>.simage</c> of BC blocks out, and nothing that
/// depends on who ran the cook.
/// </summary>
/// <remarks>
/// <para><b>Everything this rule can get wrong is silent at runtime.</b> A texture
/// that raw-copied instead of cooking still renders; one cooked at the wrong
/// quality still renders; one whose blocks differ between two hosts still renders,
/// and is a content-addressed cache handing one machine the other machine's
/// artifact. So the assertions here are about the ENTRY in the pack and about
/// bytes, never about a picture - the picture is
/// <c>CookedTextureGlTests</c>'s job, against a real driver.</para>
/// </remarks>
public class ImageRuleTests
{
    private const string SourcePath = "Textures/wall_brick.png";
    private const string CookedPath = "Textures/wall_brick.simage";

    [Fact]
    public void A_png_is_cooked_to_a_simage_rather_than_copied()
    {
        using var project = new TempProject();
        project.WriteAsset(SourcePath, TempProject.Png(16, 16, seed: 3));

        CookResult result = new CookSession(project.Layout, new CookSettings { UseCache = false }).Run();

        result.Succeeded.ShouldBeTrue();

        CookedAsset asset = result.Assets.Single();
        asset.Rule.ShouldBe(RuleKind.Image);

        // The authored file is NOT also in the pack: shipping both would double
        // every texture in the build for content nothing reads.
        CookedOutput output = asset.Outputs.Single();
        output.Path.ShouldBe(CookedPath);

        var pack = project.Track(new PackSource(NullLogger.Instance, result.OutputPath!));
        pack.Exists(CookedPath).ShouldBeTrue();
        pack.Exists(SourcePath).ShouldBeFalse();

        pack.TryOpen(CookedPath, out ContentBlob? blob).ShouldBeTrue();
        using (blob)
        {
            SimageInfo info = SimageReader.Read(blob.Span, CookedPath);
            info.Format.ShouldBe(TextureFormat.Bc7);
            info.Width.ShouldBe(16);
            info.Height.ShouldBe(16);

            // A whole chain, because a block-compressed level cannot be
            // downsampled on the GPU: what the cooker does not supply, nothing can
            // make later.
            info.MipCount.ShouldBe(5);

            // A quarter of the RGBA8 the loose file would have uploaded, which is
            // the point of cooking one at all.
            info.PayloadBytes.ShouldBeLessThan(16 * 16 * 4);
        }
    }

    [Fact]
    public void A_single_channel_image_cooks_to_BC4_rather_than_to_a_colour_format()
    {
        // The format is chosen from the CHANNEL COUNT, which is the only thing the
        // cooker knows: a one-channel file is a mask, a height field, an AO or a
        // roughness map, and BC4 stores exactly one interpolated channel. Cooking
        // it as BC7 would spend four times the memory carrying three copies of it.
        using var project = new TempProject();
        project.WriteAsset("Textures/mask.png", TempProject.Png(8, 8, channels: 1));

        CookResult result = new CookSession(project.Layout, new CookSettings { UseCache = false }).Run();
        result.Succeeded.ShouldBeTrue(string.Join('\n', result.Diagnostics));

        var pack = project.Track(new PackSource(NullLogger.Instance, result.OutputPath!));
        pack.TryOpen("Textures/mask.simage", out ContentBlob? blob).ShouldBeTrue();
        using (blob)
            SimageReader.Read(blob.Span, "mask").Format.ShouldBe(TextureFormat.Bc4);
    }

    [Fact]
    public void A_file_named_png_that_is_not_one_is_an_error_rather_than_a_raw_copy()
    {
        // Falling back to a copy would be worse than failing: the pack would carry
        // a broken PNG under a path the engine resolves, the runtime would degrade
        // it to the magenta placeholder, and the build log would say a texture
        // cooked.
        using var project = new TempProject();
        project.WriteAsset(SourcePath, TempProject.Bytes(64));

        CookResult result = new CookSession(project.Layout, new CookSettings { UseCache = false }).Run();

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.ShouldContain(d => d.IsError && d.Id.ToString() == "SC2001");
        result.Assets.Single(a => a.SourcePath == SourcePath).Outputs.ShouldBeEmpty();
    }

    [Fact]
    public void Two_cooks_of_one_image_produce_the_same_bytes()
    {
        using var project = new TempProject();
        project.WriteAsset(SourcePath, TempProject.Png(32, 32, seed: 9));

        // Byte identity, not "equivalent": the cook cache is content-addressed, so
        // an encoder that searched differently twice would make every cache entry a
        // lie while producing pictures nobody could tell apart.
        byte[] first = CookOne(project, "a");
        byte[] second = CookOne(project, "b");

        second.ShouldBe(first);
    }

    [Fact]
    public void A_parallel_encode_and_a_serial_one_agree()
    {
        // The spike measured this once against BCnEncoder 2.3.0
        // (docs/spikes/2026-09-cook-dependency-spikes.md) and the cook relies on
        // it: the encoder runs single-threaded because the cook already
        // parallelises across assets, and if the two ever stopped agreeing that
        // choice would have to become a correctness requirement rather than a
        // scheduling one. Re-measured here so the finding does not silently expire
        // with a package bump.
        DecodedImage image = ImageDecoder.Decode(TempProject.Png(32, 32, seed: 11), "parallel.png");

        byte[][] serial = ImageBlockEncoder.Encode(
            image, TextureFormat.Bc7, CompressionQuality.Balanced, parallel: false);
        byte[][] parallel = ImageBlockEncoder.Encode(
            image, TextureFormat.Bc7, CompressionQuality.Balanced, parallel: true);

        parallel.Length.ShouldBe(serial.Length);
        for (int level = 0; level < serial.Length; level++)
            parallel[level].ShouldBe(serial[level], $"level {level}");
    }

    [Fact]
    public void The_profile_changes_the_bytes_which_is_why_the_rule_declares_it()
    {
        using var project = new TempProject();
        project.WriteAsset(SourcePath, TempProject.Png(32, 32, seed: 13));

        byte[] ship = CookOne(project, "ship");
        byte[] fast = CookOne(project, "fast", CookProfile.Fast);

        // If these ever matched, CookSettingKeys.Profile on the rule would be a
        // declaration with nothing behind it - and the sibling claim, that a
        // profile change re-cooks images and skips everything else, is in
        // CookCacheTests.
        fast.ShouldNotBe(ship);
    }

    [Fact]
    public void A_cooked_texture_still_resolves_by_the_path_its_material_names()
    {
        // The identity decision: a material says Textures/wall_brick.png and is
        // never rewritten, so the redirection lives in ImageContentPath and nowhere
        // else. Both the engine's asset manager and scook verify ask it, which is
        // what stops the probe and the open from disagreeing.
        using var project = new TempProject();
        project.WriteAsset(SourcePath, TempProject.Png(8, 8, seed: 17));

        CookResult result = new CookSession(project.Layout, new CookSettings { UseCache = false }).Run();
        var pack = project.Track(new PackSource(NullLogger.Instance, result.OutputPath!));

        var stack = new ContentSourceStack();
        stack.Mount(pack);

        ImageContentPath.Resolve(stack, SourcePath).ShouldBe(CookedPath);

        // And a path with no cooked sibling comes back unchanged, which is what a
        // build with no cooked images gets.
        ImageContentPath.Resolve(stack, "Textures/absent.png").ShouldBe("Textures/absent.png");
    }

    [Fact]
    public void Handles_names_the_formats_the_decoder_can_actually_open()
    {
        // The set this rule claims and the set ImageDecoder supports have to be the
        // same set: an extension added here that stb cannot open turns every file
        // of that kind into an SC2001 rather than the raw copy it was getting
        // perfectly well before.
        ImageRule.Handles("Textures/a.png").ShouldBeTrue();
        ImageRule.Handles("Textures/a.JPG").ShouldBeTrue();
        ImageRule.Handles("Textures/a.tga").ShouldBeTrue();
        ImageRule.Handles("Textures/a.bmp").ShouldBeTrue();

        ImageRule.Handles("Textures/a.simage").ShouldBeFalse("a cooked image is not an input");
        ImageRule.Handles("Logo/LogoSpectra.ico").ShouldBeFalse();
        ImageRule.Handles("Materials/wall.spectramat").ShouldBeFalse();
    }

    // --- helpers -------------------------------------------------------------

    private static byte[] CookOne(TempProject project, string label, CookProfile profile = CookProfile.Ship)
    {
        string output = Path.Combine(project.Root, label);
        CookResult result = new CookSession(
            project.Layout,
            new CookSettings { UseCache = false, Profile = profile, OutputPath = output }).Run();

        result.Succeeded.ShouldBeTrue(string.Join('\n', result.Diagnostics));
        return File.ReadAllBytes(result.OutputPath!);
    }
}
