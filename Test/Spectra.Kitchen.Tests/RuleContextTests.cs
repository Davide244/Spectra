using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Rules;
using SpectraEngine.Core.Assets.Packs;
using System;
using System.IO;
using System.Linq;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The dependency recording, which is the reason <see cref="IRuleContext"/> has
/// the shape it has.
/// </summary>
/// <remarks>
/// Every input a rule sees arrives through <c>Read</c> or <c>Probe</c>, so the
/// declared set IS the accessed set by construction. These tests pin that, and one
/// of them pins the half that is easy to leave out: a probe that MISSED. Without
/// it, adding the file later never invalidates the rule that looked for it and a
/// watch loop serves a stale cook while reporting success.
/// </remarks>
public class RuleContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"spectra_rulectx_{Guid.NewGuid():N}");

    public RuleContextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_read_lands_in_the_dependency_set_with_the_content_hash()
    {
        Write("Textures/wall.png", [1, 2, 3, 4]);
        var context = NewContext("Textures/wall.png");

        byte[] bytes = context.Read("Textures/wall.png");

        bytes.ShouldBe(new byte[] { 1, 2, 3, 4 });
        context.Dependencies.Count.ShouldBe(1);
        context.Dependencies[0].Path.ShouldBe("Textures/wall.png");
        context.Dependencies[0].Kind.ShouldBe(RuleDependencyKind.Read);
        context.Dependencies[0].ContentHash.ShouldNotBe(UInt128.Zero);
    }

    [Fact]
    public void A_probe_that_found_the_file_is_recorded_without_reading_it()
    {
        Write("Materials/wall.spectramat", [7]);
        var context = NewContext("Materials/wall.spectramat");

        context.Probe("Materials/wall.spectramat").ShouldBeTrue();

        context.Dependencies.Count.ShouldBe(1);
        context.Dependencies[0].Kind.ShouldBe(RuleDependencyKind.ProbeFound);

        // No hash, because no bytes were seen: a rule that only asked whether a
        // file exists does not change when its contents do, and hashing anyway
        // would make every probe cost a read.
        context.Dependencies[0].ContentHash.ShouldBe(UInt128.Zero);
    }

    [Fact]
    public void A_probe_that_missed_is_recorded_too()
    {
        var context = NewContext("Materials/wall.spectramat");

        context.Probe("Textures/wall_brick.png").ShouldBeFalse();

        // The single most common incremental-build bug is not recording this. It
        // costs one list entry, and without it adding wall_brick.png later never
        // re-cooks the material that looked for it.
        context.Dependencies.Count.ShouldBe(1);
        context.Dependencies[0].Path.ShouldBe("Textures/wall_brick.png");
        context.Dependencies[0].Kind.ShouldBe(RuleDependencyKind.ProbeMissing);
        context.Dependencies[0].IsMissing.ShouldBeTrue();
    }

    [Fact]
    public void A_read_that_missed_is_recorded_before_it_throws()
    {
        var context = NewContext("Materials/wall.spectramat");

        Should.Throw<RuleInputMissingException>(() => context.Read("Textures/absent.png"));

        // Recorded on the failure path as well, or the rule that could not finish
        // is exactly the one that never re-runs when the file appears.
        context.Dependencies.Count.ShouldBe(1);
        context.Dependencies[0].Path.ShouldBe("Textures/absent.png");
        context.Dependencies[0].Kind.ShouldBe(RuleDependencyKind.ProbeMissing);
    }

    [Fact]
    public void Every_read_and_probe_of_a_whole_rule_run_lands_in_the_set()
    {
        Write("Materials/wall.spectramat", [1]);
        Write("Textures/wall_brick.png", [2]);

        var context = NewContext("Materials/wall.spectramat");

        // A material rule's shape: read the material, probe for each texture it
        // named, one of which the author has not added yet.
        context.Read("Materials/wall.spectramat");
        context.Probe("Textures/wall_brick.png");
        context.Probe("Textures/wall_normal.png");

        context.Dependencies.Select(d => d.Path)
            .ShouldBe(["Materials/wall.spectramat", "Textures/wall_brick.png", "Textures/wall_normal.png"]);

        context.Dependencies.Select(d => d.Kind)
            .ShouldBe([RuleDependencyKind.Read, RuleDependencyKind.ProbeFound, RuleDependencyKind.ProbeMissing]);
    }

    [Fact]
    public void One_path_touched_twice_keeps_one_record_at_its_first_position()
    {
        Write("Textures/a.png", [1]);
        Write("Textures/b.png", [2]);

        var context = NewContext("Textures/a.png");

        context.Probe("Textures/a.png");
        context.Read("Textures/b.png");
        context.Read("Textures/a.png");

        // The cook key hashes inputs in DECLARED order, so a read after a probe
        // must upgrade the record in place rather than move the path to the end:
        // otherwise the key changes for a rule whose behaviour did not.
        context.Dependencies.Select(d => d.Path).ShouldBe(["Textures/a.png", "Textures/b.png"]);
        context.Dependencies[0].Kind.ShouldBe(RuleDependencyKind.Read);
        context.Dependencies[0].ContentHash.ShouldNotBe(UInt128.Zero);
    }

    [Fact]
    public void A_path_is_normalised_before_it_is_recorded()
    {
        Write("Textures/wall.png", [1]);
        var context = NewContext("Textures/wall.png");

        context.Read(@"\Textures\wall.png");

        // Asset identity is one string whether content came from a folder or an
        // archive, so what is recorded is the same key the pack's id hashes from.
        context.Dependencies.Count.ShouldBe(1);
        context.Dependencies[0].Path.ShouldBe("Textures/wall.png");
    }

    [Fact]
    public void An_emission_carries_its_kind_and_a_copy_of_the_payload()
    {
        var context = NewContext("Textures/wall.png");
        var payload = new byte[] { 9, 8, 7 };

        context.Emit("Textures/wall.png", payload, PackEntryKind.Image);
        payload[0] = 0;

        context.Emissions.Count.ShouldBe(1);
        context.Emissions[0].Path.ShouldBe("Textures/wall.png");
        context.Emissions[0].Kind.ShouldBe(PackEntryKind.Image);
        context.Emissions[0].Payload.ShouldBe(new byte[] { 9, 8, 7 });
    }

    [Fact]
    public void A_reported_diagnostic_is_buffered_on_the_context()
    {
        var context = NewContext("Textures/wall.png");

        context.Report(CookDiagnostic.Warning(CookDiagnosticCodes.ContentNotCooked, "nothing to do here"));

        // Buffered per rule rather than written as it happens: N workers writing
        // to one stream tear lines apart, and every line being parseable is the
        // whole diagnostic contract.
        context.Diagnostics.Count.ShouldBe(1);
        context.Diagnostics[0].Severity.ShouldBe(CookDiagnosticSeverity.Warning);
    }

    private RuleContext NewContext(string sourcePath) => new(_root, sourcePath, CookProfile.Ship);

    private void Write(string contentPath, byte[] bytes)
    {
        string full = Path.Combine(_root, contentPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }
}
