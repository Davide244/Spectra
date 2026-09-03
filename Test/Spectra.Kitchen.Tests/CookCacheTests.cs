using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Rules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The incremental cook, against a real project folder on a real filesystem.
/// </summary>
/// <remarks>
/// <para><b>Every property here is one a timestamp-keyed cache gets wrong.</b> A
/// checkout that rewrites mtimes without changing bytes, a file reverted to
/// content it held before, and a file appearing where a rule once looked and found
/// nothing: the first two are rebuilds that should have been skips, and the third
/// is a skip that should have been a rebuild. The third is the dangerous one,
/// because it is the only one whose symptom is a wrong artifact rather than a slow
/// build.</para>
/// <para><b>A cook is driven through <see cref="CookSession"/> wherever the
/// property is about a cook</b>, so the pack, the manifest and the counters are
/// all the real ones. The negative-dependency pin goes through
/// <see cref="CookCache"/> and a real <see cref="RuleContext"/> instead, because no
/// rule in this build probes: the recording is exactly what a material rule will
/// produce, and driving it directly is the difference between testing the
/// mechanism and waiting for a rule to exist.</para>
/// </remarks>
public class CookCacheTests
{
    [Fact]
    public void A_checkout_that_rewrites_timestamps_without_changing_bytes_is_a_no_op_cook()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/a.bin", TempProject.Bytes(64, seed: 1));
        project.WriteAsset("Data/b.bin", TempProject.Bytes(65, seed: 2));
        project.WriteAsset("Materials/wall.spectramat", "shader = Lit\n");

        CookResult first = Cook(project);
        first.Succeeded.ShouldBeTrue();
        first.CacheHits.ShouldBe(0);
        first.CacheMisses.ShouldBe(3);

        byte[] firstPack = File.ReadAllBytes(first.OutputPath!);
        RewriteEveryAssetInPlace(project).ShouldBe(3);

        CookResult second = Cook(project);

        // Content hashes are the truth and the stat cache only short-circuits
        // re-hashing, so every mtime moving under the cook costs a pass over the
        // inputs and changes not one key.
        second.CacheHits.ShouldBe(3);
        second.CacheMisses.ShouldBe(0);
        File.ReadAllBytes(second.OutputPath!).ShouldBe(firstPack);
    }

    [Fact]
    public void Reverting_a_file_to_identical_content_restores_the_cache_hit()
    {
        using var project = new TempProject();
        byte[] original = project.WriteAsset("Data/a.bin", TempProject.Bytes(32, seed: 5));

        byte[] originalPack = File.ReadAllBytes(Cook(project).OutputPath!);

        project.WriteAsset("Data/a.bin", TempProject.Bytes(32, seed: 6));
        CookResult edited = Cook(project);
        edited.CacheHits.ShouldBe(0);
        edited.CacheMisses.ShouldBe(1);

        project.WriteAsset("Data/a.bin", original);
        CookResult reverted = Cook(project);

        // A rebuild here would be correct and wasteful: the artifact it produces
        // is already in the store, and the graph remembers the key that names it.
        reverted.CacheHits.ShouldBe(1);
        reverted.CacheMisses.ShouldBe(0);
        File.ReadAllBytes(reverted.OutputPath!).ShouldBe(originalPack);
    }

    [Fact]
    public void Adding_a_file_a_rule_previously_probed_and_missed_invalidates_that_rule()
    {
        using var project = new TempProject();
        project.WriteAsset(
            "Materials/wall.spectramat", "shader = Lit\ntexture albedo = Textures/wall_brick.png\n");

        string contentRoot = project.Layout.AssetsPath;
        var cache = new CookCache(Path.Combine(project.Root, CookCache.DirectoryName));
        var rule = new RawCopyRule();
        var settings = new CookSettings();

        // Exactly what a material rule will record: it reads its own file, and it
        // looks for the texture that file names, which is not there.
        var context = new RuleContext(contentRoot, "Materials/wall.spectramat", settings.Profile);
        context.Read("Materials/wall.spectramat");
        context.Probe("Textures/wall_brick.png").ShouldBeFalse();
        context.Emit("Materials/wall.spectramat", new byte[] { 1, 2, 3 });

        context.Dependencies.Count(d => d.IsMissing).ShouldBe(1);
        cache.Record("Materials/wall.spectramat", rule, settings, context.Dependencies, context.Emissions);

        cache.TryReplay(contentRoot, "Materials/wall.spectramat", rule, settings, out CachedRun? before)
            .ShouldBeTrue();
        before!.Emissions.Single().Payload.ShouldBe(new byte[] { 1, 2, 3 });

        project.WriteAsset("Textures/wall_brick.png", TempProject.Bytes(8));

        // The pin the whole design exists for. Nothing the rule READ has changed;
        // what changed is a path it looked for and did not find, and without that
        // being recorded a watch loop serves this broken cook forever while
        // reporting success.
        cache.TryReplay(contentRoot, "Materials/wall.spectramat", rule, settings, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_settings_change_invalidates_exactly_the_rules_that_read_that_setting()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/a.bin", TempProject.Bytes(16, seed: 3));
        project.WriteAsset("Scripts/notes.txt", "hello\n");
        project.WriteAsset("Textures/wall_brick.png", TempProject.Png(8, 8, seed: 4));

        Cook(project).CacheMisses.ShouldBe(3);

        // BOTH halves of the claim, which needed a rule that actually reads a
        // setting: the image rule declares Profile, so a profile change must
        // re-encode it - the encoder searches harder for a ship build than for a
        // preview one, so the bytes really are different - while the two raw
        // copies declare nothing and must be skipped.
        CookResult underFast = Cook(project, new CookSettings { Profile = CookProfile.Fast });
        underFast.CacheHits.ShouldBe(2);
        underFast.CacheMisses.ShouldBe(1);

        // And a setting NO rule declares invalidates nothing at all, which is why
        // the declaration is per rule rather than a hash of the whole settings
        // block: otherwise changing --script-source would re-encode every texture
        // in the project.
        CookResult stripped = Cook(project, new CookSettings { ScriptSource = ScriptSourceMode.Strip });
        stripped.CacheHits.ShouldBe(3);
        stripped.CacheMisses.ShouldBe(0);
    }

    [Fact]
    public void A_cook_from_cache_and_a_cook_from_clean_produce_one_pack()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/a.bin", TempProject.Bytes(50, seed: 3));
        project.WriteAsset("Data/b.bin", TempProject.Bytes(51, seed: 4));
        project.WriteAsset("Deep/Nested/Folder/c.txt", "hello\n");

        byte[] clean = File.ReadAllBytes(Cook(project).OutputPath!);

        CookResult cached = Cook(project);

        // Asserted rather than assumed: without this the test would pass just as
        // happily against a cache that never hit anything.
        cached.CacheHits.ShouldBe(3);
        File.ReadAllBytes(cached.OutputPath!).ShouldBe(clean);

        // And the cache cannot be what makes them agree: a run that refuses to
        // read it produces the same file.
        CookResult uncached = Cook(project, new CookSettings { UseCache = false });
        uncached.CacheHits.ShouldBe(0);
        File.ReadAllBytes(uncached.OutputPath!).ShouldBe(clean);
    }

    [Fact]
    public void A_changed_input_re_cooks_the_asset_that_reads_it_and_nothing_else()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/a.bin", TempProject.Bytes(20, seed: 7));
        project.WriteAsset("Data/b.bin", TempProject.Bytes(20, seed: 8));
        project.WriteAsset("Data/c.bin", TempProject.Bytes(20, seed: 9));

        Cook(project).CacheMisses.ShouldBe(3);

        project.WriteAsset("Data/b.bin", TempProject.Bytes(21, seed: 8));
        CookResult second = Cook(project);

        second.CacheMisses.ShouldBe(1);
        second.CacheHits.ShouldBe(2);
        second.Succeeded.ShouldBeTrue();
        second.EntryCount.ShouldBe(3);
    }

    [Fact]
    public void No_cache_neither_reads_nor_writes_the_cache_folder()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/a.bin", TempProject.Bytes(12));

        string cacheRoot = Path.Combine(project.Root, CookCache.DirectoryName);

        Cook(project, new CookSettings { UseCache = false }).Succeeded.ShouldBeTrue();
        Directory.Exists(cacheRoot).ShouldBeFalse();

        Cook(project).Succeeded.ShouldBeTrue();
        Directory.Exists(cacheRoot).ShouldBeTrue();

        // Not merely "does not read": a --no-cache run must leave what is cached
        // alone, or the switch quietly becomes "throw my cache away".
        long graphSize = new FileInfo(Path.Combine(cacheRoot, "graph.bin")).Length;
        Cook(project, new CookSettings { UseCache = false }).CacheHits.ShouldBe(0);
        new FileInfo(Path.Combine(cacheRoot, "graph.bin")).Length.ShouldBe(graphSize);
    }

    [Fact]
    public void A_cached_rule_reports_as_skipped_in_the_manifest()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/a.bin", TempProject.Bytes(12));

        string manifestPath = Path.Combine(project.Root, "cook-manifest.json");

        Cook(project, new CookSettings { ManifestPath = manifestPath });
        File.ReadAllText(manifestPath).ShouldNotContain("\"skipped\"");

        CookResult second = Cook(project, new CookSettings { ManifestPath = manifestPath });
        second.CacheHits.ShouldBe(1);
        second.Assets.Single().FromCache.ShouldBeTrue();

        // The bytes are identical either way; what a skip changes is which run
        // produced them, which is exactly the question asked when a cached
        // artifact turns out to be wrong.
        File.ReadAllText(manifestPath).ShouldContain("\"skipped\":true");
    }

    [Fact]
    public void A_payload_that_left_the_content_store_is_a_miss_rather_than_a_failure()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/a.bin", TempProject.Bytes(28, seed: 11));

        byte[] pack = File.ReadAllBytes(Cook(project).OutputPath!);
        Directory.Delete(Path.Combine(project.Root, CookCache.DirectoryName, "cas"), recursive: true);

        CookResult second = Cook(project);

        // The cache has exactly one way to be wrong and it is to claim a hit it
        // should not have. Everything else about it degrades to doing the work.
        second.Succeeded.ShouldBeTrue();
        second.CacheHits.ShouldBe(0);
        second.CacheMisses.ShouldBe(1);
        File.ReadAllBytes(second.OutputPath!).ShouldBe(pack);
    }

    [Fact]
    public void A_graph_file_that_does_not_parse_is_discarded_out_loud_rather_than_thrown()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/a.bin", TempProject.Bytes(18));

        byte[] pack = File.ReadAllBytes(Cook(project).OutputPath!);
        File.WriteAllBytes(
            Path.Combine(project.Root, CookCache.DirectoryName, "graph.bin"),
            [0xDE, 0xAD, 0xBE, 0xEF]);

        CookResult second = Cook(project);

        second.Succeeded.ShouldBeTrue();
        second.CacheHits.ShouldBe(0);
        File.ReadAllBytes(second.OutputPath!).ShouldBe(pack);

        // Said out loud: a cook that rebuilds everything because its cache would
        // not parse looks exactly like a slow cook, and "why is this not
        // incremental" is unanswerable without a line saying so.
        second.Diagnostics.Single().Id.ToString().ShouldBe("SC1007");
        second.ErrorCount.ShouldBe(0);
    }

    [Fact]
    public void A_stale_record_for_an_asset_that_left_the_project_is_dropped()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/a.bin", TempProject.Bytes(10));
        project.WriteAsset("Data/gone.bin", TempProject.Bytes(11));

        Cook(project).CacheMisses.ShouldBe(2);
        long twoRecords = GraphSize(project);

        File.Delete(Path.Combine(project.Layout.AssetsPath, "Data", "gone.bin"));
        Cook(project).EntryCount.ShouldBe(1);

        // A graph that never forgets grows for the life of the project and keeps
        // naming assets that were deleted years ago.
        GraphSize(project).ShouldBeLessThan(twoRecords);
    }

    private static long GraphSize(TempProject project) =>
        new FileInfo(Path.Combine(project.Root, CookCache.DirectoryName, "graph.bin")).Length;

    private static CookResult Cook(TempProject project, CookSettings? settings = null) =>
        new CookSession(project.Layout, settings ?? new CookSettings()).Run();

    // What a git checkout does: the same bytes land in the same files with new
    // modification times. Returns the count so a test cannot pass because the
    // rewrite silently touched nothing.
    private static int RewriteEveryAssetInPlace(TempProject project)
    {
        var files = new List<string>(
            Directory.EnumerateFiles(project.Layout.AssetsPath, "*", SearchOption.AllDirectories));

        foreach (string full in files)
        {
            DateTime before = File.GetLastWriteTimeUtc(full);

            File.WriteAllBytes(full, File.ReadAllBytes(full));
            File.SetLastWriteTimeUtc(full, before.AddHours(1));

            File.GetLastWriteTimeUtc(full).ShouldNotBe(before);
        }

        return files.Count;
    }
}
