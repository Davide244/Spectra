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

namespace Spectra.Kitchen.Cooking;

/// <summary>
/// One cook of one project: walk the content, run a rule per asset, write the
/// pack.
/// </summary>
/// <remarks>
/// <para><b>This is the whole spine, and today most of it is one lane wide.</b>
/// There is no dependency DAG and no parallelism yet; what there IS is the shape
/// those need: every asset goes through a rule, every rule reads through a context
/// that records what it touched, and every output is placed by content path.</para>
/// <para><b>The cache sits exactly where that shape put it.</b> A rule is asked
/// for from the cache before it is run and recorded after, keyed on what the
/// context already recorded rather than on a separate declaration of it - which is
/// what makes the declared input set and the accessed input set the same set. A
/// cache miss and a cache-off run are the same code path, so nothing about a
/// cooked byte depends on whether the cache was consulted.</para>
/// <para><b>A failed cook writes no pack.</b> The runtime degrades and the cooker
/// does not: a half-written pack that mounts is worse than none, because it ships.
/// </para>
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
        var diagnostics = new List<CookDiagnostic>();
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

            return Finish(assets, diagnostics, null, null, 0, 0);
        }

        ReportUncookedMaps(diagnostics);

        CookCache? cache = OpenCache(diagnostics);

        IReadOnlyList<ContentFile> content = ContentWalker.Walk(contentRoot);
        var cookedPaths = new List<string>(content.Count);

        foreach (ContentFile file in content)
        {
            IRule rule = _rules.Resolve(file.ContentPath);
            cookedPaths.Add(file.ContentPath);

            IReadOnlyList<RuleDependency> dependencies;
            IReadOnlyList<RuleEmission> emissions;
            bool fromCache = false;

            if (cache is not null &&
                cache.TryReplay(contentRoot, file.ContentPath, rule, _settings, out CachedRun? replay))
            {
                fromCache = true;
                dependencies = replay.Dependencies;
                emissions = replay.Emissions;
            }
            else
            {
                var context = new RuleContext(contentRoot, file.ContentPath, _settings.Profile);
                bool failed = false;

                try
                {
                    rule.Cook(context);
                }
                catch (RuleInputMissingException ex)
                {
                    diagnostics.Add(CookDiagnostic.Error(
                        CookDiagnosticCodes.InputMissing, ex.Message, file.FullPath));
                    failed = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    diagnostics.Add(CookDiagnostic.Error(
                        CookDiagnosticCodes.RuleFailed,
                        $"The {rule.Kind} rule failed on '{file.ContentPath}': {ex.Message}",
                        file.FullPath));
                    failed = true;
                }

                // Flushed in rule order, which single-threaded is simply the order they
                // happened in and will stay true when this loop becomes parallel.
                foreach (CookDiagnostic diagnostic in context.Diagnostics)
                    diagnostics.Add(_settings.Strict ? diagnostic.AsError() : diagnostic);

                dependencies = context.Dependencies;
                emissions = context.Emissions;

                // A rule that failed, or that had anything to say, is not recorded.
                // The cache stores bytes and dependencies and not diagnostics, so a
                // later hit would serve the artifact and swallow the message, which
                // is an incremental build quietly hiding what it was asked to be
                // loud about.
                if (cache is not null && !failed && context.Diagnostics.Count == 0)
                    cache.Record(file.ContentPath, rule, _settings, dependencies, emissions);
            }

            var outputs = new List<CookedOutput>(emissions.Count);
            foreach (RuleEmission emission in emissions)
            {
                if (emitted.TryGetValue(emission.Path, out string? firstOwner))
                {
                    diagnostics.Add(CookDiagnostic.Error(
                        CookDiagnosticCodes.PackEntryCollision,
                        $"'{emission.Path}' is emitted by both '{firstOwner}' and '{file.ContentPath}'. " +
                        "One content path is one asset.",
                        file.FullPath));
                    continue;
                }

                emitted.Add(emission.Path, file.ContentPath);

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
                file.ContentPath, rule.Kind, rule.Version, [.. dependencies], outputs)
            {
                FromCache = fromCache,
            });
        }

        // Saved before the pack is written, and even when the cook then fails:
        // every rule recorded above genuinely ran to completion with the inputs it
        // recorded, and throwing that away because a LATER asset failed would make
        // a project with one broken file re-cook the other thousand on every
        // attempt to fix it.
        CloseCache(cache, cookedPaths, diagnostics);

        if (CountErrors(diagnostics) > 0)
            return Finish(assets, diagnostics, cache, null, 0, 0);

        string? output = _settings.Loose
            ? WriteLoose(looseFiles, diagnostics)
            : WritePack(writer, diagnostics);

        if (output is null || CountErrors(diagnostics) > 0)
            return Finish(assets, diagnostics, cache, null, 0, 0);

        WriteManifest(assets, diagnostics);

        return Finish(assets, diagnostics, cache, output, entryCount, payloadBytes);
    }

    // Opening the cache is allowed to fail into "no cache": a hidden folder that
    // cannot be read is a reason to do the work, never a reason to refuse to.
    private CookCache? OpenCache(List<CookDiagnostic> diagnostics)
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

    private void CloseCache(CookCache? cache, IReadOnlyCollection<string> cooked, List<CookDiagnostic> diagnostics)
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
    private void ReportUncookedMaps(List<CookDiagnostic> diagnostics)
    {
        IReadOnlyList<string> maps = _layout.DiscoverMaps();
        if (maps.Count == 0) return;

        diagnostics.Add(CookDiagnostic.Info(
            CookDiagnosticCodes.ContentNotCooked,
            $"{maps.Count} map bundle(s) under '{ProjectFormat.MapsFolder}/' are not in this pack: the map cook " +
            "rule is not built yet.",
            _layout.ManifestPath));
    }

    private string? WritePack(PackWriter writer, List<CookDiagnostic> diagnostics)
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

    private string? WriteLoose(List<RuleEmission> emissions, List<CookDiagnostic> diagnostics)
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

    private void WriteManifest(List<CookedAsset> assets, List<CookDiagnostic> diagnostics)
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

    private static int CountErrors(List<CookDiagnostic> diagnostics)
    {
        int errors = 0;
        for (int i = 0; i < diagnostics.Count; i++)
            if (diagnostics[i].IsError) errors++;
        return errors;
    }

    private static CookResult Finish(
        List<CookedAsset> assets,
        List<CookDiagnostic> diagnostics,
        CookCache? cache,
        string? output,
        int entryCount,
        long payloadBytes)
    {
        int warnings = 0;
        for (int i = 0; i < diagnostics.Count; i++)
            if (diagnostics[i].Severity == CookDiagnosticSeverity.Warning) warnings++;

        return new CookResult
        {
            Assets = assets,
            Diagnostics = diagnostics,
            OutputPath = output,
            EntryCount = entryCount,
            PayloadBytes = payloadBytes,
            ErrorCount = CountErrors(diagnostics),
            WarningCount = warnings,
            CacheHits = cache?.Hits ?? 0,
            CacheMisses = cache?.Misses ?? 0,
        };
    }
}
