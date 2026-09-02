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
/// There is no cache, no dependency DAG and no parallelism yet; what there IS is
/// the shape those need: every asset goes through a rule, every rule reads through
/// a context that records what it touched, and every output is placed by content
/// path. Adding the cache means keying on what the context already recorded rather
/// than inventing a record of it.</para>
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

            return Finish(assets, diagnostics, null, 0, 0);
        }

        ReportUncookedMaps(diagnostics);

        IReadOnlyList<ContentFile> content = ContentWalker.Walk(contentRoot);
        foreach (ContentFile file in content)
        {
            IRule rule = _rules.Resolve(file.ContentPath);
            var context = new RuleContext(contentRoot, file.ContentPath, _settings.Profile);

            try
            {
                rule.Cook(context);
            }
            catch (RuleInputMissingException ex)
            {
                diagnostics.Add(CookDiagnostic.Error(
                    CookDiagnosticCodes.InputMissing, ex.Message, file.FullPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                diagnostics.Add(CookDiagnostic.Error(
                    CookDiagnosticCodes.RuleFailed,
                    $"The {rule.Kind} rule failed on '{file.ContentPath}': {ex.Message}",
                    file.FullPath));
            }

            // Flushed in rule order, which single-threaded is simply the order they
            // happened in and will stay true when this loop becomes parallel.
            foreach (CookDiagnostic diagnostic in context.Diagnostics)
                diagnostics.Add(_settings.Strict ? diagnostic.AsError() : diagnostic);

            var outputs = new List<CookedOutput>(context.Emissions.Count);
            foreach (RuleEmission emission in context.Emissions)
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
                file.ContentPath, rule.Kind, rule.Version, [.. context.Dependencies], outputs));
        }

        if (CountErrors(diagnostics) > 0)
            return Finish(assets, diagnostics, null, 0, 0);

        string? output = _settings.Loose
            ? WriteLoose(looseFiles, diagnostics)
            : WritePack(writer, diagnostics);

        if (output is null || CountErrors(diagnostics) > 0)
            return Finish(assets, diagnostics, null, 0, 0);

        WriteManifest(assets, diagnostics);

        return Finish(assets, diagnostics, output, entryCount, payloadBytes);
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
        };
    }
}
