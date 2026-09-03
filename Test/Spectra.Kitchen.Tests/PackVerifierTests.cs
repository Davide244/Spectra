using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Packs;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using System;
using System.IO;
using System.Linq;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// Cooked-only validation: the gate that catches a pack which mounts cleanly and
/// is broken anyway.
/// </summary>
/// <remarks>
/// <b>Every case here is a file that passes some earlier check.</b> A pack whose
/// header is wrong is already refused by the mount and has its own suite; what is
/// left, and what these fixtures build on purpose, is the class of pack that a
/// running game will happily load and then render wrongly - a material whose
/// texture nobody cooked, a payload that hashes correctly and does not decode, a
/// table that is intact and no longer searchable.
/// </remarks>
public class PackVerifierTests
{
    [Fact]
    public void A_freshly_cooked_project_verifies_clean()
    {
        using var project = new TempProject();
        project.WriteAsset("Textures/wall_brick.png", TempProject.Png(16, 16, seed: 1));
        project.WriteAsset(
            "Materials/wall.spectramat",
            "shader = lit\ntexture uDiffuse = Textures/wall_brick.png, linearmipmap, repeat\n");

        PackVerifyResult result = Verify(Cook(project));

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.ErrorCount.ShouldBe(0);
        result.WarningCount.ShouldBe(0);
        result.EntriesChecked.ShouldBe(2);

        // The number a cook's own summary cannot give: two entries were written,
        // and one of them points at the other.
        result.ReferencesChecked.ShouldBe(1);
    }

    [Fact]
    public void A_material_naming_a_texture_nobody_cooked_fails_and_the_diagnostic_names_the_path()
    {
        using var project = new TempProject();
        project.WriteAsset(
            "Materials/wall.spectramat",
            "shader = lit\ntexture uDiffuse = Textures/wall_brick.png, linearmipmap, repeat\n");

        // The cook itself is happy: there is no material rule, so the file is
        // raw-copied and nothing in the cook ever reads what is inside it. That
        // is exactly the hole this verb exists to close.
        string pack = Cook(project);

        PackVerifyResult result = Verify(pack);

        result.Succeeded.ShouldBeFalse();

        CookDiagnostic missing = result.Diagnostics.Single(d => d.IsError);
        missing.Id.ToString().ShouldBe("SC5001");
        missing.Message.ShouldContain("Materials/wall.spectramat");
        missing.Message.ShouldContain("Textures/wall_brick.png");
        missing.Message.ShouldContain("uDiffuse");
    }

    [Fact]
    public void A_validation_run_throws_where_the_running_engine_degrades()
    {
        using var project = new TempProject();
        project.WriteAsset(
            "Materials/wall.spectramat",
            "shader = lit\ntexture uDiffuse = Textures/wall_brick.png\n");

        var pack = project.Track(new PackSource(NullLogger.Instance, Cook(project)));

        // BOTH halves in one test, because the failure mode this guards is
        // somebody "fixing" one to match the other. The runtime's degradation to
        // a default material and a magenta texture is a pinned invariant; the
        // cooker stopping the build is the whole point of a build step. They are
        // the same lookup with different consequences, which is why the
        // consequence belongs to the STACK that was mounted rather than to
        // AssetManager.
        var runtime = new ContentSourceStack();
        runtime.Mount(pack);
        runtime.TryOpen("Textures/wall_brick.png", out ContentBlob? degraded).ShouldBeFalse();
        degraded.ShouldBeNull();

        var validation = new ContentSourceStack(strict: true);
        validation.Mount(pack);
        Should.Throw<FileNotFoundException>(
            () => validation.TryOpen("Textures/wall_brick.png", out _));
    }

    [Fact]
    public void A_flipped_payload_byte_is_reported_as_a_broken_digest()
    {
        using var project = new TempProject();
        project.WriteAsset("Textures/wall_brick.png", TempProject.Png(16, 16, seed: 2));

        string pack = Cook(project);
        PackSurgery.CorruptFirstPayload(pack);

        PackVerifyResult result = Verify(pack);

        result.Succeeded.ShouldBeFalse();

        CookDiagnostic refused = result.Diagnostics.Single(d => d.IsError);
        refused.Id.ToString().ShouldBe("SC9003");
        refused.Message.ShouldContain("digest");
    }

    [Fact]
    public void A_rewritten_digest_is_reported_against_the_bytes_it_does_not_match()
    {
        using var project = new TempProject();
        project.WriteAsset("Textures/wall_brick.png", TempProject.Png(16, 16, seed: 3));

        string pack = Cook(project);
        PackSurgery.CorruptDigest(pack);

        PackVerifyResult result = Verify(pack);

        result.Succeeded.ShouldBeFalse();
        result.Diagnostics.Single(d => d.IsError).Id.ToString().ShouldBe("SC9003");
    }

    [Fact]
    public void A_payload_that_hashes_correctly_and_does_not_decode_is_still_caught()
    {
        // Written directly rather than cooked, because the cook stores every
        // entry verbatim: an undecodable payload needs a codec, and Deflate is
        // the only one this build implements.
        using var project = new TempProject();
        string pack = Path.Combine(project.Root, "compressed.spack");

        var writer = new PackWriter();
        writer.Add("Text/notes.txt", PackEntryKind.Raw, TempProject.Bytes(400, seed: 4), PackCodec.Deflate);
        writer.WriteToFile(pack);

        Verify(pack).Succeeded.ShouldBeTrue();

        PackSurgery.MakeFirstPayloadUndecodable(pack);

        PackVerifyResult result = Verify(pack);

        // This is the case the digest structurally cannot see: the damage and the
        // hash over it were written together, so the container is intact and the
        // asset inside it is not. Without the decode pass the pack ships.
        result.Succeeded.ShouldBeFalse();

        CookDiagnostic broken = result.Diagnostics.Single(d => d.IsError);
        broken.Id.ToString().ShouldBe("SC9004");
        broken.Message.ShouldContain("Text/notes.txt");
        broken.Message.ShouldContain("Deflate");
    }

    [Fact]
    public void The_entry_table_on_disk_is_proved_sorted_rather_than_assumed_to_be()
    {
        using var project = new TempProject();
        for (int i = 0; i < 6; i++)
            project.WriteAsset($"Data/t{i}.bin", TempProject.Bytes(32, seed: (byte)i));

        string pack = Cook(project);
        PackSurgery.SwapFirstTwoEntries(pack);

        PackVerifyResult result = Verify(pack);

        result.Succeeded.ShouldBeFalse();

        // The writer sorts, which is a claim about the code that wrote a pack.
        // This is a claim about the bytes, and it is the only one of the two that
        // survives the file being edited afterwards - and it names the two
        // entries, where the mount's refusal below can only name their ids.
        CookDiagnostic unsorted = result.Diagnostics.Single(d => d.Id.ToString() == "SC9005");
        unsorted.Message.ShouldContain("Data/t");
        unsorted.Message.ShouldContain("out of order");

        result.Diagnostics.ShouldContain(d => d.Id.ToString() == "SC9003");
    }

    [Fact]
    public void An_unusable_material_line_is_carried_rather_than_swallowed()
    {
        using var project = new TempProject();
        project.WriteAsset("Textures/wall_brick.png", TempProject.Png(8, 8, seed: 5));
        project.WriteAsset(
            "Materials/wall.spectramat",
            "shader = lit\ntexture uDiffuse = Textures/wall_brick.png\nfrobnicate uThing = 3\n");

        PackVerifyResult result = Verify(Cook(project));

        // The parser warns rather than throwing so material files stay
        // forward-compatible, which means an unusable line is otherwise a
        // silently weaker material with nothing anywhere saying so.
        result.Succeeded.ShouldBeTrue(Describe(result));
        result.Diagnostics.Single().Id.ToString().ShouldBe("SC5002");
        result.Diagnostics.Single().Severity.ShouldBe(CookDiagnosticSeverity.Warning);
    }

    [Fact]
    public void The_engines_own_content_cooks_and_verifies()
    {
        // The real thing: ten hand-authored materials naming eight real textures,
        // through the real cook and the real reader. A synthetic fixture proves
        // the mechanism; this proves the mechanism against the content somebody
        // actually edits, which is where a path spelled two ways or a texture
        // renamed on one side would show up.
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        Directory.Exists(assets).ShouldBeTrue($"the engine's content should be beside the test binary: {assets}");

        using var project = new TempProject();
        CopyTree(assets, project.Layout.AssetsPath);

        PackVerifyResult result = Verify(Cook(project));

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.ReferencesChecked.ShouldBeGreaterThan(0);
    }

    private static string Cook(TempProject project)
    {
        CookResult result = new CookSession(project.Layout, new CookSettings()).Run();
        result.Succeeded.ShouldBeTrue();
        return result.OutputPath!;
    }

    private static PackVerifyResult Verify(string packPath) => PackVerifier.Verify(packPath);

    private static string Describe(PackVerifyResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);

        foreach (string directory in Directory.GetDirectories(from))
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
    }
}
