using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Packs;
using Spectra.Kitchen.Rules;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.IO;
using System.Linq;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The material cook: validated source text, packed verbatim, with every
/// reference it names resolved against the project.
/// </summary>
/// <remarks>
/// <para><b>The payload claim and the validation claim are separate, and both
/// matter.</b> A material is packed byte for byte because the parser is
/// deliberately forward-compatible and a binary re-encoding would have to decide
/// what to do with a key it has no field for; what cooking one is FOR is the
/// reference check, which is the only thing standing between a shipped build and
/// a wall of magenta.</para>
/// <para><b>Every failure here is silent at runtime.</b> A missing texture is a
/// placeholder and a warning, a missing shader is the built-in lit program and a
/// warning: the picture is wrong and nothing stops. That is why the assertions
/// are about what is IN the pack and what the cook REFUSED, never about an
/// exception.</para>
/// </remarks>
public class MaterialRuleTests
{
    private const string MaterialPath = "Materials/wall.spectramat";
    private const string TexturePath = "Textures/wall_brick.png";
    private const string CookedTexturePath = "Textures/wall_brick.simage";

    private const string Body =
        "shader = lit\ntexture uDiffuse = Textures/wall_brick.png, linearmipmap, repeat\n";

    [Fact]
    public void A_material_is_packed_verbatim_and_carries_the_material_entry_kind()
    {
        using var project = new TempProject();
        project.WriteAsset(TexturePath, TempProject.Png(8, 8, seed: 1));
        byte[] source = project.WriteAsset(MaterialPath, Body);

        CookResult result = Cook(project);

        CookedAsset asset = result.Assets.Single(a => a.SourcePath == MaterialPath);
        asset.Rule.ShouldBe(RuleKind.Material);

        // Same path in as out: a material's identity is not rewritten by cooking,
        // exactly as the texture it names is not.
        asset.Outputs.Single().Path.ShouldBe(MaterialPath);

        var pack = project.Track(new PackSource(NullLogger.Instance, result.OutputPath!));
        pack.TryOpen(MaterialPath, out ContentBlob? blob).ShouldBeTrue();
        using (blob)
        {
            // Byte for byte, because the parser is forward-compatible by design
            // and a cooked form that dropped what it could not model would delete
            // authored data on the way past.
            blob.Span.ToArray().ShouldBe(source);
        }

        // The entry kind moved off Raw, which is the only place a reader or an
        // inspect can see that this file was looked at rather than copied.
        PackContents contents = PackContents.Read(result.OutputPath!);
        int index = Enumerable.Range(0, contents.Entries.Count)
            .Single(i => contents.NameOf(i) == MaterialPath);

        contents.Entries[index].EntryKind.ShouldBe(PackEntryKind.Material);
    }

    [Fact]
    public void A_texture_reference_resolves_through_the_simage_redirection()
    {
        // The material names the PNG, which is not there; the project ships the
        // cooked form beside it, which is. The engine resolves that pair through
        // ImageContentPath and so does this rule, so a project holding
        // pre-compressed textures cooks rather than reporting every one of them
        // as missing. A rule spelling the redirection its own way would be a
        // third expression of it, and the two would drift.
        using var project = new TempProject();
        project.WriteAsset(CookedTexturePath, TempProject.Bytes(64, seed: 2));
        project.WriteAsset(MaterialPath, Body);

        Cook(project).Succeeded.ShouldBeTrue();

        // And the redirection is the shared one, not a coincidence of spelling.
        ImageContentPath.CookedPathFor(TexturePath).ShouldBe(CookedTexturePath);
    }

    [Fact]
    public void Adding_the_cooked_texture_a_material_probed_for_re_cooks_that_material()
    {
        // The negative dependency, which is the half that makes an incremental
        // cook correct rather than merely fast. The first cook probes for
        // Textures/wall_brick.simage, does not find it, and resolves the PNG;
        // dropping a .simage in afterwards changes which bytes that material's
        // texture would come from, so the material must not be served from cache.
        using var project = new TempProject();
        project.WriteAsset(TexturePath, TempProject.Png(8, 8, seed: 3));
        project.WriteAsset(MaterialPath, Body);

        CookResult first = Cook(project, cache: true, label: "cold");
        first.Succeeded.ShouldBeTrue();

        CookResult unchanged = Cook(project, cache: true, label: "warm");
        unchanged.Assets.Single(a => a.SourcePath == MaterialPath).FromCache.ShouldBeTrue();

        project.WriteAsset("Textures/other.simage", TempProject.Bytes(32, seed: 4));

        // A .simage somewhere else must NOT invalidate it: a dependency set that
        // widened to the whole folder would make every material re-cook whenever
        // any texture changed.
        Cook(project, cache: true, label: "unrelated")
            .Assets.Single(a => a.SourcePath == MaterialPath).FromCache.ShouldBeTrue();

        project.WriteAsset(CookedTexturePath, TempProject.Bytes(48, seed: 5));

        Cook(project, cache: true, label: "arrived")
            .Assets.Single(a => a.SourcePath == MaterialPath).FromCache.ShouldBeFalse();
    }

    [Fact]
    public void A_material_naming_a_shader_nothing_provides_is_refused_and_names_it()
    {
        using var project = new TempProject();
        project.WriteAsset(TexturePath, TempProject.Png(8, 8, seed: 6));
        project.WriteAsset(
            MaterialPath, "shader = glowy\ntexture uDiffuse = Textures/wall_brick.png\n");

        CookResult result = Cook(project);

        result.Succeeded.ShouldBeFalse();

        // Silent at runtime: AssetManager falls back to the built-in lit program
        // and logs a warning, so the surface draws with a program its author did
        // not choose and the build says nothing.
        var missing = result.Diagnostics.Single(d => d.Id.ToString() == "SC5003");
        missing.Message.ShouldContain("glowy");
        missing.File.ShouldBe(MaterialPath);
    }

    [Fact]
    public void The_built_in_shader_and_a_project_shader_both_resolve()
    {
        using var project = new TempProject();
        project.WriteAsset(TexturePath, TempProject.Png(8, 8, seed: 7));

        // 'lit' by the name the parser documents, matched case-insensitively
        // because AssetManager matches it that way and a cook that disagreed
        // would refuse files the engine loads.
        project.WriteAsset("Materials/a.spectramat", "shader = Lit\n");

        // A built-in by its file's own name: those ship embedded in the engine
        // assembly, so they are present whether or not a pack carries them.
        project.WriteAsset("Materials/b.spectramat", "shader = GBufferFill\n");

        // And one the project supplies itself, under the folder a shader resolves
        // from. It is real source, because the shader rule will compile it.
        project.WriteAsset($"{BaseShaders.ContentFolder}/Glowy.spectrashade", BaseShaders.Lit);
        project.WriteAsset("Materials/c.spectramat", "shader = Glowy\n");

        // No shader key at all is the built-in too, which is what every material
        // written before the key existed says.
        project.WriteAsset("Materials/d.spectramat", "float uRoughness = 0.5\n");

        CookResult result = Cook(project);

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.Diagnostics.ShouldNotContain(d => d.Id.ToString() == "SC5003");
    }

    [Fact]
    public void An_unknown_key_warns_and_the_material_still_reaches_the_pack()
    {
        using var project = new TempProject();
        project.WriteAsset(TexturePath, TempProject.Png(8, 8, seed: 8));
        project.WriteAsset(MaterialPath, Body + "frobnicate uThing = 3\n");

        CookResult result = Cook(project);

        // Tolerated, because that tolerance is the format's forward-compatibility
        // hinge - and said out loud, because a silently weaker material is
        // otherwise indistinguishable from a correct one.
        result.Succeeded.ShouldBeTrue(Describe(result));
        result.Diagnostics.Single(d => d.Id.ToString() == "SC5002")
            .Message.ShouldContain("frobnicate");

        result.Assets.Single(a => a.SourcePath == MaterialPath).Outputs.ShouldNotBeEmpty();
    }

    [Fact]
    public void The_rule_claims_material_files_and_nothing_else()
    {
        MaterialRule.Handles(MaterialPath).ShouldBeTrue();
        MaterialRule.Handles("Materials/WALL.SPECTRAMAT").ShouldBeTrue();
        MaterialRule.Handles(TexturePath).ShouldBeFalse();
        MaterialRule.Handles("Shaders/Lit.spectrashade").ShouldBeFalse();
        MaterialRule.Handles("Materials/notes.txt").ShouldBeFalse();

        // The extension comes from the parser rather than being spelled here, so
        // the rule and the thing it parses cannot disagree about what a material
        // file is called.
        MaterialParser.FileExtension.ShouldBe(".spectramat");
    }

    [Fact]
    public void The_engines_own_materials_cook_clean()
    {
        // The real thing rather than a fixture: ten hand-authored materials over
        // eight real textures, through the real rule. A synthetic file proves the
        // mechanism; this proves it against the content somebody actually edits,
        // which is where a path spelled two ways would show up.
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        Directory.Exists(assets).ShouldBeTrue($"the engine's content should be beside the test binary: {assets}");

        using var project = new TempProject();
        CopyTree(assets, project.Layout.AssetsPath);

        CookResult result = Cook(project);

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.Assets.Count(a => a.Rule == RuleKind.Material).ShouldBeGreaterThan(0);
    }

    private static CookResult Cook(TempProject project, bool cache = false, string label = "out") =>
        new CookSession(
                project.Layout,
                new CookSettings { UseCache = cache, OutputPath = Path.Combine(project.Root, label) })
            .Run();

    private static string Describe(CookResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static d => d.ToString()));

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);

        foreach (string directory in Directory.GetDirectories(from))
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
    }
}
