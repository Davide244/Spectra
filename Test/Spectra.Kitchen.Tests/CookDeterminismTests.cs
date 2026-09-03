using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The three cook oracles: two clean cooks, a cached cook against a clean one, and
/// <c>-j1</c> against <c>-jN</c>, each producing one artifact byte for byte.
/// </summary>
/// <remarks>
/// <para><b>Through the scook BINARY rather than through <c>CookSession</c>, and
/// that is the whole reason this is a separate file.</b> .NET randomises the
/// string hash seed per PROCESS, so a dictionary iteration order that leaked into
/// a cooked byte would be stable inside one test host and different between two
/// runs of the tool - which is the failure somebody reports as "CI says the pack
/// changed and nothing changed". An in-process comparison cannot see that class of
/// bug at all, because both cooks would share the seed.</para>
/// <para><b>The fixture is sized so the parallel oracle is not vacuous.</b> Thirty
/// six assets across four folders at sizes spanning four orders of magnitude,
/// arranged so that neither the walk order nor the size order is the completion
/// order. Three identical small files finish in the order the workers were handed
/// them, and would pass just as happily with the outcome array replaced by a list
/// appended to as each rule returned.</para>
/// <para><b>The MANIFEST is compared beside the pack wherever it can be.</b> A pack
/// sorts its entries by asset id, so it absorbs a scheduling leak in the very place
/// one would first show up; the manifest is written in walk order and absorbs
/// nothing. The cached oracle is the one that cannot compare manifests, because a
/// <c>skipped</c> member appearing on every asset there is the point rather than a
/// difference.</para>
/// </remarks>
public class CookDeterminismTests
{
    private const int AssetCount = 36;

    private static readonly string[] Folders = ["Textures", "Models", "Materials", "Audio"];

    // Out of step with the folder cycle deliberately: four folders and six sizes
    // means an asset's size does not follow from where it sits in the walk, so the
    // order rules finish in is neither of the two orders the cook writes in.
    private static readonly int[] Sizes = [24, 262_144, 1_024, 65_536, 96, 8_192];

    [Fact]
    public void Two_clean_cooks_in_two_processes_are_byte_identical()
    {
        ScookProcess.Require();

        using var project = new TempProject();
        WriteFixture(project);

        CookRun first = Cook(project, "clean-a", "--no-cache");
        CookRun second = Cook(project, "clean-b", "--no-cache");

        // Nothing in a cook may depend on a clock, on a path on the cooking
        // machine, on the order a directory listing came back in, or on a hash seed
        // this process happened to be given.
        second.Pack.ShouldBe(first.Pack);
        second.Manifest.ShouldBe(first.Manifest);
    }

    [Fact]
    public void A_cached_cook_and_a_clean_cook_are_byte_identical()
    {
        ScookProcess.Require();

        using var project = new TempProject();
        WriteFixture(project);

        // The first run fills .spectra-cook/ as a side effect; the second is the
        // one under test.
        CookRun clean = Cook(project, "cold");
        CookRun cached = Cook(project, "warm");

        // Asserted rather than assumed: without this the oracle silently degrades
        // into a second copy of the one above the moment anything stops the cache
        // hitting, and it would keep passing.
        cached.Stdout.ShouldContain($"{AssetCount} from cache");

        cached.Pack.ShouldBe(clean.Pack);
    }

    [Fact]
    public void One_worker_and_many_workers_produce_the_same_pack()
    {
        ScookProcess.Require();

        using var project = new TempProject();
        WriteFixture(project);

        CookRun serial = Cook(project, "j1", "--no-cache", "-j", "1");
        CookRun parallel = Cook(project, "j8", "--no-cache", "-j", "8");

        // The same assertion: a run that quietly clamped to one worker would agree
        // with -j1 for the least interesting reason there is.
        parallel.Stdout.ShouldContain("8 workers");
        serial.Stdout.ShouldNotContain("workers");

        parallel.Pack.ShouldBe(serial.Pack);
        parallel.Manifest.ShouldBe(serial.Manifest);
    }

    [Fact]
    public void A_parallel_cook_pairs_every_result_with_the_asset_it_came_from()
    {
        ScookProcess.Require();

        using var project = new TempProject();
        WriteFixture(project);

        CookRun parallel = Cook(project, "paired", "--no-cache", "-j", "8");

        // The manifest is the one artifact carrying the cook's OWN order rather
        // than the container's: a pack sorts its entries by asset id, so a result
        // that landed in the wrong slot reaches a reader as a byte difference to
        // bisect rather than as the thing it is. Under a raw copy an asset's source
        // path, the input it read and the output it emitted are one string, so a
        // record naming two is a worker's answer applied to somebody else's asset.
        string[] records = ManifestRecords(parallel.Manifest);
        records.Length.ShouldBe(AssetCount);

        var sources = new List<string>(records.Length);
        foreach (string record in records)
        {
            string[] paths = PathsIn(record);

            paths.Length.ShouldBe(3, $"expected a source, an input and an output in: {record}");
            paths.Distinct(StringComparer.Ordinal).Count().ShouldBe(1, $"mis-paired result: {record}");
            sources.Add(paths[0]);
        }

        // And in walk order, which is ordinal ascending by content path.
        sources.ShouldBe([.. sources.Order(StringComparer.Ordinal)]);
    }

    // Sizes and folders both cycle, at lengths that share no factor with each
    // other, so a big asset and a small one sit next to each other everywhere in
    // the walk rather than in runs.
    private static void WriteFixture(TempProject project)
    {
        for (int i = 0; i < AssetCount; i++)
        {
            project.WriteAsset(
                $"{Folders[i % Folders.Length]}/asset_{i:D2}.bin",
                TempProject.Bytes(Sizes[i % Sizes.Length], seed: (byte)i));
        }
    }

    private static CookRun Cook(TempProject project, string label, params string[] extra)
    {
        // Every run gets its own output folder and its own manifest, both OUTSIDE
        // the content root: written under Assets/ they would become content the
        // next cook walks, and each run would cook the one before it.
        string output = Path.Combine(project.Root, label);
        string manifest = Path.Combine(project.Root, label + "-manifest.json");

        ScookProcess.Result run = ScookProcess.Run(
            ["cook", project.Root, "-o", output, "--manifest", manifest, .. extra]);

        run.ExitCode.ShouldBe(0, $"scook failed: {run.Stderr}");

        string pack = Directory.GetFiles(output, "*.spack").Single();
        return new CookRun(run.Stdout, File.ReadAllBytes(pack), File.ReadAllBytes(manifest));
    }

    // Read back by scanning rather than by parsing the document: the manifest's own
    // shape has its own tests, and what is wanted here is the order the records
    // were written in and which paths ended up on one record. One asset per LINE is
    // what makes that readable at all.
    private static string[] ManifestRecords(byte[] manifest) =>
        [.. Encoding.UTF8.GetString(manifest)
            .Split('\n')
            .Where(static line => line.Contains("\"rule\":\"", StringComparison.Ordinal))];

    // Every path named on one record, in the order it was written: the asset's own
    // first, then its inputs, then its outputs.
    private static string[] PathsIn(string record)
    {
        const string Opening = "\"path\":\"";

        return [.. record
            .Split(Opening)
            .Skip(1)
            .Select(static part => part[..part.IndexOf('"')])];
    }

    private readonly record struct CookRun(string Stdout, byte[] Pack, byte[] Manifest);
}
