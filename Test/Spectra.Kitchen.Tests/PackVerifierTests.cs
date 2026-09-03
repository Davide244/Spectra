using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Packs;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Packs;
using System;
using System.IO;
using System.Linq;
using System.Text;

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
        // Written by hand rather than cooked, and that is what the material rule
        // changed: a cook of this project now refuses it at SC5001 before a pack
        // exists at all, which is the right answer and would make this a test of
        // the cook. The claim HERE is about the ARTIFACT - a pack that mounts
        // cleanly and is missing a texture some material names, however it came
        // to be that way - so the fixture has to be able to produce one the cook
        // never would. CookGateTests is where the two verdicts are held together.
        using var project = new TempProject();
        string pack = Path.Combine(project.Root, "hole.spack");

        var writer = new PackWriter();
        writer.Add(
            "Materials/wall.spectramat",
            PackEntryKind.Material,
            Encoding.UTF8.GetBytes(
                "shader = lit\ntexture uDiffuse = Textures/wall_brick.png, linearmipmap, repeat\n"));
        writer.WriteToFile(pack);

        PackVerifyResult result = Verify(pack);

        result.Succeeded.ShouldBeFalse();

        CookDiagnostic missing = result.Diagnostics.Single(d => d.IsError);
        missing.Id.ToString().ShouldBe("SC5001");
        missing.Message.ShouldContain("Materials/wall.spectramat");
        missing.Message.ShouldContain("Textures/wall_brick.png");
        missing.Message.ShouldContain("uDiffuse");
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

    // --- the 7xxx arm: a compiled map's own references -------------------------

    [Fact]
    public void A_project_with_a_map_in_it_verifies_clean_and_counts_the_levels_references()
    {
        using var project = new TempProject();
        MapFixture fixture = MapFixture.Fresh();
        fixture.WriteMaterials(project);
        fixture.WriteBundle(project, "Room.smap");

        PackVerifyResult result = Verify(Cook(project));

        result.Succeeded.ShouldBeTrue(Describe(result));

        // Two materials from the map's own asset table, on top of whatever the
        // materials themselves name. A zero here would mean the arm never ran and
        // every case below would be unfalsifiable.
        result.ReferencesChecked.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void A_compiled_map_naming_assets_nobody_cooked_fails_in_the_MAP_band()
    {
        // Hand-written rather than cooked, for the reason the material case above
        // is: the cook refuses this project before a pack exists. The claim here is
        // about the ARTIFACT - a level in a pack whose materials are not in the
        // same pack, however it came to be that way - which is the failure a
        // shipped build ships as a grey room with every log line reading healthy.
        using var project = new TempProject();
        string pack = Path.Combine(project.Root, "holes.spack");

        var writer = new PackWriter();
        writer.Add("Maps/Room.scmap", PackEntryKind.Map, ScmapFixture.Build());
        writer.WriteToFile(pack);

        PackVerifyResult result = Verify(pack);

        result.Succeeded.ShouldBeFalse();

        // Every row of the fixture's asset table: two materials, a texture and a
        // model, each reported in the band that names the failing SUBSYSTEM.
        CookDiagnostic[] missing = result.Diagnostics.Where(d => d.IsError).ToArray();
        missing.Length.ShouldBe(ScmapFixture.AssetPaths.Length);
        missing.ShouldAllBe(d => d.Id.ToString() == "SC7008");

        string all = string.Join(Environment.NewLine, missing.Select(d => d.Message));
        foreach (string path in ScmapFixture.AssetPaths)
            all.ShouldContain(path);

        all.ShouldContain("Maps/Room.scmap");
        all.ShouldContain("material");
        all.ShouldContain("texture");
        all.ShouldContain("model");
    }

    [Fact]
    public void A_compiled_map_this_engine_would_refuse_at_boot_is_caught_before_it_ships()
    {
        // The digest cannot see this: a level baked at another format version
        // hashes perfectly and is still a map the runtime refuses on frame zero.
        // Edited BEFORE the pack is written, so the pack's own digest agrees and
        // the only thing that can complain is the reader.
        using var project = new TempProject();
        string pack = Path.Combine(project.Root, "stale.spack");

        byte[] map = ScmapFixture.Build();
        BitConverter.GetBytes((ushort)(EngineInfo.CompiledMapFormatVersion + 1)).CopyTo(map, 0x04);

        var writer = new PackWriter();
        writer.Add("Maps/Room.scmap", PackEntryKind.Map, map);
        writer.WriteToFile(pack);

        PackVerifyResult result = Verify(pack);

        CookDiagnostic refused = result.Diagnostics.Single(d => d.IsError);
        refused.Id.ToString().ShouldBe("SC7009");
        refused.Message.ShouldContain("Recook");
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
