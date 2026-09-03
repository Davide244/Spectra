using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Packs;
using Spectra.Kitchen.Rules;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Projects;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Spectra.Kitchen.Cooking;

/// <summary>
/// One cook of one project: walk the content, run a rule per asset, write the
/// pack.
/// </summary>
/// <remarks>
/// <para><b>This is the whole spine, and the scheduler is what the rest of its
/// shape was for.</b> Every asset goes through a rule, every rule reads through a
/// context that records what it touched, and every output is placed by content
/// path - so the only thing a worker ever owns is one rule's own answer, and the
/// pack is assembled from those answers on the calling thread.</para>
/// <para><b>Nothing a worker produces is applied where it lands.</b> Outcomes go
/// into an array indexed by work item, never appended in completion order, and the
/// diagnostics, the pack entries, the loose files, the byte counts and the manifest
/// records are all applied afterwards in that index order. That is the whole
/// determinism argument, and it is stronger than a promise about any one of those
/// consumers: a cooked byte cannot depend on which worker won a race, because no
/// cooked byte is written by a worker.</para>
/// <para><b>The cache sits exactly where that shape put it.</b> A rule is asked
/// for from the cache before it is run and recorded after, keyed on what the
/// context already recorded rather than on a separate declaration of it - which is
/// what makes the declared input set and the accessed input set the same set. A
/// cache miss and a cache-off run are the same code path, so nothing about a
/// cooked byte depends on whether the cache was consulted.</para>
/// <para><b>A failed cook writes no pack.</b> The runtime degrades and the cooker
/// does not: a half-written pack that mounts is worse than none, because it ships.
/// </para>
/// <para><b>What "failed" MEANS is <see cref="CookGate"/>'s answer, not this
/// class's.</b> Every diagnostic goes into a <see cref="CookDiagnosticLog"/> that
/// applies the gate on the way in, so a rule's chosen severity is normalised
/// rather than merely accepted and <c>--strict</c> is honoured in one place
/// instead of at each reporting site. The gate is the same table
/// <c>PackVerifier</c> reads, which is what keeps the cook and the verify from
/// being two opinions about what a valid pack is.</para>
/// <para><b>The pack is named after the manifest FILE, not after the project's
/// display name.</b> A display name is free text that may contain characters no
/// filesystem accepts, and a cook that fails on a project called "Kirby: Ex" would
/// be reporting a naming problem as an I/O one.</para>
/// </remarks>
public sealed class CookSession
{
    private readonly ProjectLayout _layout;
    private readonly CookSettings _settings;
    private readonly CookRuleSet _rules = new();

    /// <summary>The extension a cooked pack is written with.</summary>
    /// <remarks>
    /// Four-byte magic <c>SPAK</c>, extension <c>.spack</c>. They are deliberately
    /// different lengths and neither is a typo for the other.
    /// </remarks>
    public const string PackExtension = ".spack";

    /// <summary>Creates a session over an opened project.</summary>
    public CookSession(ProjectLayout layout, CookSettings settings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(settings);

        settings.Validate();
        _layout = layout;
        _settings = settings;
    }

    /// <summary>Where output goes: <c>-o</c> if it was given, else the project's <c>cooked/</c>.</summary>
    public string OutputDirectory => _settings.OutputPath ?? _layout.CookedPath;

    /// <summary>
    /// Where the cook cache lives: <c>.spectra-cook/</c> at the project root.
    /// </summary>
    /// <remarks>
    /// At the project root rather than under <c>cooked/</c>, and not moved by
    /// <c>-o</c>: the cache is keyed on the project's own content and on the
    /// toolchain, so pointing one cook's output somewhere else must not hand it a
    /// different cache. It is derived data all the same, which is why a
    /// <c>clean</c> removes it.
    /// </remarks>
    public string CacheDirectory => Path.Combine(_layout.Root, CookCache.DirectoryName);

    /// <summary>Runs the cook.</summary>
    public CookResult Run()
    {
        var diagnostics = new CookDiagnosticLog(_settings.Strict);
        var assets = new List<CookedAsset>();
        var emitted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var writer = new PackWriter();
        var looseFiles = new List<RuleEmission>();

        long payloadBytes = 0;
        int entryCount = 0;

        string contentRoot = _layout.AssetsPath;
        if (!Directory.Exists(contentRoot))
        {
            diagnostics.Add(CookDiagnostic.Error(
                CookDiagnosticCodes.ContentRootMissing,
                $"The project has no '{ProjectFormat.AssetsFolder}' folder at '{contentRoot}', so there is " +
                "nothing to cook.",
                _layout.ManifestPath));

            return Finish(assets, diagnostics, null, null, 0, 0, 0);
        }

        ReportUncookedMaps(diagnostics);

        CookCache? cache = OpenCache(diagnostics);

        // The work list is built whole, in walk order, before anything runs: every
        // ordering promise this class makes is an index into it - an outcome slot,
        // a diagnostic's position, a manifest row. Each rule is resolved here
        // rather than inside a worker, which keeps CookRuleSet on one thread and
        // keeps a rule set that grows state out of the shared-state question
        // entirely.
        IReadOnlyList<ContentFile> content = ContentWalker.Walk(contentRoot);
        var work = new WorkItem[content.Count];
        var cookedPaths = new List<string>(content.Count);

        for (int i = 0; i < content.Count; i++)
        {
            work[i] = new WorkItem(content[i], _rules.Resolve(content[i].ContentPath));
            cookedPaths.Add(content[i].ContentPath);
        }

        // Clamped to the work, so what a summary line reports is the parallelism
        // this cook could actually have had rather than the number somebody typed.
        // A project with nothing in it scheduled nothing and reports none, which is
        // the same answer the content-root failure above gives.
        int workers = work.Length == 0 ? 0 : Math.Clamp(_settings.Jobs, 1, work.Length);
        var outcomes = new RuleOutcome[work.Length];

        // Level-synchronous, and today there is exactly ONE level, because nothing
        // in this build can order two work items: no rule declares a dependency on
        // another rule's OUTPUT, so a topological sort would sort one group. A
        // level is therefore a RANGE of the work list rather than a set - the day a
        // rule does declare one, the list is sorted into level order and this
        // becomes a loop over ranges, and none of the ordering rules below, which
        // are what the byte-identity oracles rest on, has to move.
        RunLevel(contentRoot, cache, work, outcomes, 0, work.Length, workers);

        for (int i = 0; i < work.Length; i++)
        {
            WorkItem item = work[i];
            RuleOutcome outcome = outcomes[i];

            // Buffered per rule and flushed here rather than written where they
            // happened: N workers writing to one stream tear lines apart, and the
            // whole diagnostic contract is that each line is IDE-parseable. That
            // makes this a correctness requirement of the output format.
            if (outcome.Failure is not null) diagnostics.Add(outcome.Failure);

            // Straight in: the log applies CookGate, which is the ONE place a
            // severity is decided. The strict promotion used to be spelled here,
            // beside a dozen reporting sites that each chose their own severity,
            // and two answers to one question drift the moment a second reporter
            // of the same code appears - which is exactly what the material rule
            // and the verifier's material arm now are.
            diagnostics.AddRange(outcome.Reports);

            var outputs = new List<CookedOutput>(outcome.Emissions.Count);
            foreach (RuleEmission emission in outcome.Emissions)
            {
                if (emitted.TryGetValue(emission.Path, out string? firstOwner))
                {
                    diagnostics.Add(CookDiagnostic.Error(
                        CookDiagnosticCodes.PackEntryCollision,
                        $"'{emission.Path}' is emitted by both '{firstOwner}' and '{item.File.ContentPath}'. " +
                        "One content path is one asset.",
                        item.File.FullPath));
                    continue;
                }

                emitted.Add(emission.Path, item.File.ContentPath);

                if (_settings.Loose) looseFiles.Add(emission);
                else writer.Add(emission.Path, emission.Kind, emission.Payload);

                outputs.Add(new CookedOutput(
                    emission.Path,
                    PackAssetId.FromNormalized(emission.Path),
                    XxHash128.HashToUInt128(emission.Payload),
                    emission.Payload.Length));

                payloadBytes += emission.Payload.Length;
                entryCount++;
            }

            assets.Add(new CookedAsset(
                item.File.ContentPath, item.Rule.Kind, item.Rule.Version, [.. outcome.Dependencies], outputs)
            {
                FromCache = outcome.FromCache,
            });
        }

        // Saved before the pack is written, and even when the cook then fails:
        // every rule recorded above genuinely ran to completion with the inputs it
        // recorded, and throwing that away because a LATER asset failed would make
        // a project with one broken file re-cook the other thousand on every
        // attempt to fix it.
        CloseCache(cache, cookedPaths, diagnostics);

        if (diagnostics.Failed)
            return Finish(assets, diagnostics, cache, null, 0, 0, workers);

        string? output = _settings.Loose
            ? WriteLoose(looseFiles, diagnostics)
            : WritePack(writer, diagnostics);

        if (output is null || diagnostics.Failed)
            return Finish(assets, diagnostics, cache, null, 0, 0, workers);

        WriteManifest(assets, diagnostics);

        return Finish(assets, diagnostics, cache, output, entryCount, payloadBytes, workers);
    }

    // One asset and the rule that will cook it: the unit a worker is handed, and
    // the index every ordering promise above is stated against.
    private readonly record struct WorkItem(ContentFile File, IRule Rule);

    // What one rule run answered, and the only thing that crosses back from a
    // worker. The failure is kept apart from the reports rather than placed first
    // in one list, because --strict promotes what a rule SAID and never the failure
    // that stopped it, and one list would need the promotion to know which entry it
    // was looking at.
    private sealed record RuleOutcome(
        bool FromCache,
        IReadOnlyList<RuleDependency> Dependencies,
        IReadOnlyList<RuleEmission> Emissions,
        CookDiagnostic? Failure,
        IReadOnlyList<CookDiagnostic> Reports);

    // The parallel phase, and the only thing in a cook that runs on more than one
    // thread. It writes one outcome slot per work item and touches nothing else:
    // not the pack writer, not the diagnostic list, not a counter that decides a
    // byte.
    private void RunLevel(
        string contentRoot,
        CookCache? cache,
        WorkItem[] work,
        RuleOutcome[] outcomes,
        int start,
        int count,
        int workers)
    {
        if (count == 0) return;

        // -j1 goes through this same call rather than a serial branch beside it. A
        // second implementation of the level is a second thing to keep in step, and
        // the oracle that -j1 and -jN produce one pack would then be comparing two
        // code paths instead of proving one.
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers };

        try
        {
            Parallel.For(
                start, start + count, options, i => outcomes[i] = RunOne(contentRoot, cache, work[i]));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            // A rule that throws something the per-rule catch below does not name is
            // a bug in that rule, and it has to reach the top carrying its own
            // message and its own stack. Parallel.For would otherwise deliver it
            // wrapped, and what a person would then see is "One or more errors
            // occurred" where the actual fault used to be.
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
    }

    // One work item, start to finish, on whichever worker took it. Everything it
    // touches is either immutable (the settings, the rule) or its own (the
    // context); the cache is the one shared thing, and it is safe for the workers
    // by construction rather than by this method's care.
    private RuleOutcome RunOne(string contentRoot, CookCache? cache, WorkItem item)
    {
        if (cache is not null &&
            cache.TryReplay(contentRoot, item.File.ContentPath, item.Rule, _settings, out CachedRun? replay))
        {
            // A replayed run reported nothing, because a rule that reported anything
            // was never recorded in the first place.
            return new RuleOutcome(true, replay.Dependencies, replay.Emissions, null, []);
        }

        var context = new RuleContext(
            contentRoot, item.File.ContentPath, _settings.Profile, _settings.Targets);
        CookDiagnostic? failure = null;

        try
        {
            item.Rule.Cook(context);
        }
        catch (RuleInputMissingException ex)
        {
            failure = CookDiagnostic.Error(
                CookDiagnosticCodes.InputMissing, ex.Message, item.File.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            failure = CookDiagnostic.Error(
                CookDiagnosticCodes.RuleFailed,
                $"The {item.Rule.Kind} rule failed on '{item.File.ContentPath}': {ex.Message}",
                item.File.FullPath);
        }

        // A rule that failed, or that had anything to say, is not recorded. The
        // cache stores bytes and dependencies and not diagnostics, so a later hit
        // would serve the artifact and swallow the message, which is an incremental
        // build quietly hiding what it was asked to be loud about.
        if (cache is not null && failure is null && context.Diagnostics.Count == 0)
        {
            cache.Record(
                item.File.ContentPath, item.Rule, _settings, context.Dependencies, context.Emissions);
        }

        return new RuleOutcome(
            false, context.Dependencies, context.Emissions, failure, context.Diagnostics);
    }

    // Opening the cache is allowed to fail into "no cache": a hidden folder that
    // cannot be read is a reason to do the work, never a reason to refuse to.
    private CookCache? OpenCache(CookDiagnosticLog diagnostics)
    {
        if (!_settings.UseCache) return null;

        CookCache cache;
        try
        {
            cache = new CookCache(CacheDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CookDiagnostic.Warning(
                CookDiagnosticCodes.CacheNotWritable,
                $"The cook cache at '{CacheDirectory}' could not be opened, so this is a clean cook: {ex.Message}"));

            return null;
        }

        if (cache.DiscardedReason is not null)
        {
            diagnostics.Add(CookDiagnostic.Info(
                CookDiagnosticCodes.CacheDiscarded,
                $"The cook cache at '{CacheDirectory}' could not be read and was discarded, so this is a clean " +
                $"cook: {cache.DiscardedReason}"));
        }

        return cache;
    }

    private void CloseCache(CookCache? cache, IReadOnlyCollection<string> cooked, CookDiagnosticLog diagnostics)
    {
        if (cache is null) return;

        cache.RetainOnly(cooked);

        try
        {
            cache.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CookDiagnostic.Warning(
                CookDiagnosticCodes.CacheNotWritable,
                $"The cook cache at '{CacheDirectory}' could not be written, so the next cook will not be " +
                $"incremental: {ex.Message}"));
        }
    }

    // A project's maps are not under the content root and have no rule yet, so a
    // cook silently leaves them out. Said out loud, because a pack that mounts and
    // has no level in it looks like a working cook.
    private void ReportUncookedMaps(CookDiagnosticLog diagnostics)
    {
        IReadOnlyList<string> maps = _layout.DiscoverMaps();
        if (maps.Count == 0) return;

        diagnostics.Add(CookDiagnostic.Info(
            CookDiagnosticCodes.ContentNotCooked,
            $"{maps.Count} map bundle(s) under '{ProjectFormat.MapsFolder}/' are not in this pack: the map cook " +
            "rule is not built yet.",
            _layout.ManifestPath));
    }

    private string? WritePack(PackWriter writer, CookDiagnosticLog diagnostics)
    {
        string packPath = Path.Combine(
            OutputDirectory, Path.GetFileNameWithoutExtension(_layout.ManifestPath) + PackExtension);

        try
        {
            Directory.CreateDirectory(OutputDirectory);
            writer.WriteToFile(packPath);
            return packPath;
        }
        catch (InvalidOperationException ex)
        {
            // The writer refuses two paths that hash to one id, which is a content
            // problem rather than an I/O one and reads as nonsense under SC9001.
            diagnostics.Add(CookDiagnostic.Error(CookDiagnosticCodes.PackEntryCollision, ex.Message));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CookDiagnostic.Error(
                CookDiagnosticCodes.PackWriteFailed, $"Could not write '{packPath}': {ex.Message}"));
            return null;
        }
    }

    private string? WriteLoose(List<RuleEmission> emissions, CookDiagnosticLog diagnostics)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            foreach (RuleEmission emission in emissions)
            {
                string full = Path.Combine(
                    OutputDirectory, emission.Path.Replace('/', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, emission.Payload);
            }

            return OutputDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CookDiagnostic.Error(
                CookDiagnosticCodes.OutputNotWritable,
                $"Could not write the loose cook tree at '{OutputDirectory}': {ex.Message}"));
            return null;
        }
    }

    private void WriteManifest(List<CookedAsset> assets, CookDiagnosticLog diagnostics)
    {
        if (_settings.ManifestPath is null) return;

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_settings.ManifestPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllBytes(
                _settings.ManifestPath,
                CookManifest.Write(_layout.Project.Name, _settings.Profile, assets));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CookDiagnostic.Error(
                CookDiagnosticCodes.OutputNotWritable,
                $"Could not write the cook manifest '{_settings.ManifestPath}': {ex.Message}"));
        }
    }

    private static CookResult Finish(
        List<CookedAsset> assets,
        CookDiagnosticLog diagnostics,
        CookCache? cache,
        string? output,
        int entryCount,
        long payloadBytes,
        int workers)
    {
        return new CookResult
        {
            Assets = assets,
            Diagnostics = diagnostics.Entries,
            OutputPath = output,
            EntryCount = entryCount,
            PayloadBytes = payloadBytes,
            ErrorCount = diagnostics.ErrorCount,
            WarningCount = diagnostics.WarningCount,
            CacheHits = cache?.Hits ?? 0,
            CacheMisses = cache?.Misses ?? 0,
            Workers = workers,
        };
    }
}
