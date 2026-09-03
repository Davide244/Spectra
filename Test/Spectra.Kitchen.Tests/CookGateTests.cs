using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Packs;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The asymmetry, pinned: the cooker refuses exactly where the running engine
/// degrades to a default material and a magenta texture.
/// </summary>
/// <remarks>
/// <para><b>READ THIS BEFORE "FIXING" EITHER SIDE.</b> Two behaviours over
/// identical inputs are deliberately different here, and both are correct:</para>
/// <list type="bullet">
/// <item><description><b>The running engine degrades.</b> A frame must keep
/// rendering, so a missing material becomes <c>AssetManager.DefaultMaterial</c>
/// and a missing texture becomes the magenta placeholder, each with a warning.
/// That is a pinned invariant of the engine (see
/// <c>SpectraEngine.Bsp.Tests.MaterialAssetTests</c>, which proves it against a
/// real <c>AssetManager</c>), and it must not become conditional on where the
/// content came from - which is exactly why strictness is a property of the
/// mounted <see cref="ContentSourceStack"/> rather than of
/// <c>AssetManager</c>.</description></item>
/// <item><description><b>The cooker refuses.</b> A build step whose job is to
/// stop broken data shipping must not share the runtime's soft landing, or a
/// cook succeeds, a pack mounts, every log line reads healthy and the shipped
/// game shows checkerboards.</description></item>
/// </list>
/// <para><b>The failure this file exists to catch is somebody making one match
/// the other</b> - softening the cook because "the engine handles it", or
/// hardening the engine because "the cooker says it is fatal". Either change
/// passes its own local tests and destroys the property. So both halves are
/// asserted in ONE test against ONE piece of content, and the verdict both sides
/// read is asserted beside them.</para>
/// <para>The written form of all this is
/// <c>docs/formats-and-pipeline.md</c> 4.2; <see cref="CookGate"/> is the code
/// form.</para>
/// </remarks>
public class CookGateTests
{
    private const string Material = "Materials/wall.spectramat";
    private const string Texture = "Textures/wall_brick.png";

    private const string NamesAMissingTexture =
        "shader = lit\ntexture uDiffuse = Textures/wall_brick.png, linearmipmap, repeat\n";

    [Fact]
    public void The_cooker_refuses_exactly_where_the_running_engine_degrades()
    {
        using var project = new TempProject();
        project.WriteAsset(Material, NamesAMissingTexture);

        // --- The cooker's half: refused, and no pack written. ----------------
        CookResult cooked = new CookSession(project.Layout, new CookSettings { UseCache = false }).Run();

        cooked.Succeeded.ShouldBeFalse();
        cooked.OutputPath.ShouldBeNull();

        CookDiagnostic refused = cooked.Diagnostics.Single(d => d.IsError);
        refused.Id.ToString().ShouldBe("SC5001");
        refused.Message.ShouldContain(Texture);

        // Fatal because the GATE says so, not because MaterialRule chose Error.
        // A rule that reported this as a warning would still fail the cook.
        CookGate.Verdict(CookDiagnosticCodes.MaterialTextureMissing).ShouldBe(CookGateVerdict.Fatal);
        CookGate.Apply(
                CookDiagnostic.Warning(CookDiagnosticCodes.MaterialTextureMissing, "as a warning"),
                strict: false)
            .IsError.ShouldBeTrue();

        // --- The same content in a pack, refused by the verifier too. --------
        // Built by hand precisely because the cook above refused to build one:
        // that refusal IS the property under test, so the artifact half needs a
        // fixture the cook would never produce.
        string pack = Path.Combine(project.Root, "hole.spack");
        var writer = new PackWriter();
        writer.Add(Material, PackEntryKind.Material, Encoding.UTF8.GetBytes(NamesAMissingTexture));
        writer.WriteToFile(pack);

        PackVerifyResult verified = PackVerifier.Verify(pack);
        verified.Succeeded.ShouldBeFalse();
        verified.Diagnostics.Single(d => d.IsError).Id.ToString().ShouldBe("SC5001");

        // --- The runtime's half: the identical lookup, and it does NOT fail. -
        var mounted = project.Track(new PackSource(NullLogger.Instance, pack));

        var runtime = new ContentSourceStack();
        runtime.Mount(mounted);
        runtime.TryOpen(Texture, out ContentBlob? degraded).ShouldBeFalse();
        degraded.ShouldBeNull();

        // One lookup, two consequences, and the consequence belongs to the stack
        // that was mounted rather than to the code doing the looking.
        var validation = new ContentSourceStack(strict: true);
        validation.Mount(mounted);
        Should.Throw<FileNotFoundException>(() => validation.TryOpen(Texture, out _));
    }

    [Fact]
    public void An_unknown_material_key_warns_by_default_and_fails_under_strict()
    {
        // The other half of 4.2, and the reason the gate is a table rather than a
        // rule: the parser tolerates an unknown key ON PURPOSE so material files
        // stay forward-compatible, so refusing one would refuse every file
        // written ahead of the engine. --strict is how a CI step asks for the
        // stricter reading, and it must reach this code and not the one above.
        using var project = new TempProject();
        project.WriteAsset(Texture, TempProject.Png(8, 8, seed: 1));
        project.WriteAsset(Material, NamesAMissingTexture + "frobnicate uThing = 3\n");

        CookGate.Verdict(CookDiagnosticCodes.MaterialFileMalformed)
            .ShouldBe(CookGateVerdict.WarningUnlessStrict);

        CookResult lax = Cook(project, strict: false);
        lax.Succeeded.ShouldBeTrue();
        lax.Diagnostics.Single(d => d.Id.ToString() == "SC5002")
            .Severity.ShouldBe(CookDiagnosticSeverity.Warning);

        CookResult strict = Cook(project, strict: true);
        strict.Succeeded.ShouldBeFalse();
        strict.Diagnostics.Single(d => d.Id.ToString() == "SC5002").IsError.ShouldBeTrue();

        // And the verify verb answers --strict the same way, because a switch
        // that meant one thing under one verb and nothing under the other is a
        // switch nobody can rely on.
        string pack = lax.OutputPath!;
        PackVerifier.Verify(pack).Succeeded.ShouldBeTrue();
        PackVerifier.Verify(pack, logger: null, targets: null, strict: true).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void Strict_never_touches_a_complaint_about_the_run_rather_than_the_data()
    {
        // A cook that failed because its own cache would not save, or because a
        // switch it accepts is not wired up yet, is failing over something that
        // says nothing about whether the content is shippable. --strict means
        // "this run is the gate on the DATA".
        foreach (CookDiagnosticId id in new[]
        {
            CookDiagnosticCodes.OptionNotImplemented,
            CookDiagnosticCodes.CacheNotWritable,
        })
        {
            CookGate.Verdict(id).ShouldBe(CookGateVerdict.Warning);
            CookGate.Apply(CookDiagnostic.Warning(id, "about the run"), strict: true)
                .IsError.ShouldBeFalse($"{id} is about the run, not the data");
        }

        // An Info stays an Info under --strict for the same reason, one step
        // quieter: "your maps are not in this pack yet" is not a build failure.
        foreach (CookDiagnosticId id in new[]
        {
            CookDiagnosticCodes.ContentNotCooked,
            CookDiagnosticCodes.CacheDiscarded,
        })
        {
            CookGate.Verdict(id).ShouldBe(CookGateVerdict.Note);
            CookGate.Apply(CookDiagnostic.Info(id, "a note"), strict: true)
                .Severity.ShouldBe(CookDiagnosticSeverity.Info);
        }
    }

    [Fact]
    public void A_diagnostic_carrying_another_tools_judgement_keeps_it()
    {
        // SC6001 stands in for a wrapped SS#### until the shader compiler numbers
        // its own diagnostics, and the compiler's Diagnostic carries a severity
        // about a specific source line. Flattening it is wrong in both
        // directions: fatal refuses a build over a shader WARNING that ssc merely
        // printed, and soft demotes a compile ERROR and ships a shader that does
        // not exist.
        CookGate.Verdict(CookDiagnosticCodes.ShaderCompileFailed).ShouldBe(CookGateVerdict.AsReported);

        CookGate.Apply(CookDiagnostic.Error(CookDiagnosticCodes.ShaderCompileFailed, "syntax"), strict: false)
            .IsError.ShouldBeTrue();

        CookGate.Apply(CookDiagnostic.Warning(CookDiagnosticCodes.ShaderCompileFailed, "unused"), strict: false)
            .Severity.ShouldBe(CookDiagnosticSeverity.Warning);

        // --strict still promotes it, because that is what --strict means for
        // every other warning in the run.
        CookGate.Apply(CookDiagnostic.Warning(CookDiagnosticCodes.ShaderCompileFailed, "unused"), strict: true)
            .IsError.ShouldBeTrue();

        // A genuinely foreign code is not the cooker's to reclassify at all.
        CookGate.Verdict(CookDiagnosticId.Wrap("SS", 42)).ShouldBe(CookGateVerdict.AsReported);
    }

    [Fact]
    public void Every_code_the_cooker_owns_has_a_verdict()
    {
        // The convention test, and the reason CookGate's default may safely be
        // Fatal: an unclassified code and a fatal one are indistinguishable at a
        // reporting site, which is the safe direction to be wrong in and a
        // useless one to debug from. This is where it is meant to be caught.
        List<CookDiagnosticId> codes = DeclaredCodes();
        codes.Count.ShouldBeGreaterThan(20, "the reflection should find the declared code table");

        var unclassified = codes.Where(id => !CookGate.IsClassified(id)).ToList();

        unclassified.ShouldBeEmpty(
            "every SC#### the cooker owns needs one line in CookGate's table: " +
            string.Join(", ", unclassified));
    }

    [Fact]
    public void The_gate_does_not_reclassify_a_diagnostic_it_already_agrees_with()
    {
        // Cheap, and it is the reason a clean run allocates nothing here: Apply
        // hands back the same instance rather than a copy whose only difference
        // is that it is a different object.
        CookDiagnostic already = CookDiagnostic.Error(CookDiagnosticCodes.PackNotMountable, "refused");
        CookGate.Apply(already, strict: false).ShouldBeSameAs(already);
    }

    private static CookResult Cook(TempProject project, bool strict) =>
        new CookSession(
                project.Layout,
                new CookSettings
                {
                    UseCache = false,
                    Strict = strict,
                    OutputPath = Path.Combine(project.Root, strict ? "strict" : "lax"),
                })
            .Run();

    // Reflection is fine HERE and nowhere in the shipped tool: a test assembly is
    // never trimmed, and the alternative - scraping CookDiagnosticCodes.cs as
    // text - would pass just as happily against a field somebody commented out.
    private static List<CookDiagnosticId> DeclaredCodes()
    {
        var codes = new List<CookDiagnosticId>();
        foreach (FieldInfo field in typeof(CookDiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(CookDiagnosticId)) continue;

            codes.Add((CookDiagnosticId)field.GetValue(null)!);
        }

        return codes;
    }
}
