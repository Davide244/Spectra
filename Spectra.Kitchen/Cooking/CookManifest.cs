using Spectra.Kitchen.Rules;
using SpectraEngine.Core;
using SpectraEngine.Core.Serialization;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Spectra.Kitchen.Cooking;

/// <summary>
/// The cook manifest: every asset, its id, its inputs and its output hash, as
/// canonical JSON.
/// </summary>
/// <remarks>
/// <para><b>This is the artifact CI diffs.</b> An output hash that moved when no
/// input hash did is a determinism failure, and finding one here costs a text diff
/// where finding it later costs a patch that will not apply on a player's machine.
/// </para>
/// <para><b>The missing probes are in it, and they are the interesting half.</b>
/// A manifest listing only the files a rule found cannot answer "why did adding
/// this file change that artifact", which is the question an incremental-build bug
/// is always asked as.</para>
/// <para><b>Canonical JSON, through the one implementation.</b> The settings in it
/// fail silently: <c>NewLine</c> defaults to the platform's, so byte identity would
/// hold only within one operating system, and the default encoder escapes
/// non-ASCII into unmergeable noise. A manifest that differs by the OS that wrote
/// it is a manifest nobody can diff.</para>
/// <para><b>No timestamps, no absolute paths, no machine name.</b> Same rule the
/// pack writer follows and for the same reason: two cooks of one tree must produce
/// one file.</para>
/// </remarks>
public static class CookManifest
{
    /// <summary>Format version of the manifest document itself.</summary>
    public const int FormatVersion = 1;

    /// <summary>Renders the manifest for one cook.</summary>
    public static byte[] Write(string projectName, CookProfile profile, IReadOnlyList<CookedAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var records = new List<byte[]>(assets.Count);
        foreach (CookedAsset asset in assets)
            records.Add(CanonicalJson.Compact(w => WriteAsset(w, asset)));

        return CanonicalJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("scookManifest", FormatVersion);
            writer.WriteString("engine", EngineInfo.VersionString);
            writer.WriteString("project", projectName);
            writer.WriteString("profile", ToWire(profile));
            CanonicalJson.WriteRecordArray(writer, "assets", records);
            writer.WriteEndObject();
        });
    }

    /// <summary>The profile's spelling, which is the command line's.</summary>
    public static string ToWire(CookProfile profile) => profile switch
    {
        CookProfile.Ship => "ship",
        CookProfile.Fast => "fast",
        CookProfile.Preview => "preview",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown cook profile."),
    };

    /// <summary>The rule kind's spelling.</summary>
    public static string ToWire(RuleKind kind) => kind switch
    {
        RuleKind.RawCopy => "rawcopy",
        RuleKind.Image => "image",
        RuleKind.Model => "model",
        RuleKind.Audio => "audio",
        RuleKind.Material => "material",
        RuleKind.Shader => "shader",
        RuleKind.Script => "script",
        RuleKind.Map => "map",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown rule kind."),
    };

    private static void WriteAsset(Utf8JsonWriter writer, CookedAsset asset)
    {
        writer.WriteStartObject();
        writer.WriteString("path", asset.SourcePath);
        writer.WriteString("rule", ToWire(asset.Rule));
        writer.WriteNumber("ruleVersion", asset.RuleVersion);

        // Written only when true, so a clean cook's manifest is not carrying a
        // "skipped": false against every asset in the project. A diff between a
        // clean run and a cached one legitimately shows this member appearing:
        // that IS the difference between the two runs, and the pack they produced
        // is byte-identical regardless.
        if (asset.FromCache) writer.WriteBoolean("skipped", true);

        writer.WritePropertyName("inputs");
        writer.WriteStartArray();
        foreach (RuleDependency dependency in asset.Dependencies)
        {
            if (dependency.Kind == RuleDependencyKind.ProbeMissing) continue;

            writer.WriteStartObject();
            writer.WriteString("path", dependency.Path);
            writer.WriteString("kind", dependency.Kind == RuleDependencyKind.Read ? "read" : "probe");
            if (dependency.Kind == RuleDependencyKind.Read)
                writer.WriteString("hash", dependency.ContentHash.ToString("X32"));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Separate from inputs rather than a kind inside it, because this list is
        // the one a reviewer is pointed at when an artifact did not rebuild.
        writer.WritePropertyName("missing");
        writer.WriteStartArray();
        foreach (RuleDependency dependency in asset.Dependencies)
        {
            if (dependency.Kind != RuleDependencyKind.ProbeMissing) continue;
            writer.WriteStringValue(dependency.Path);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("outputs");
        writer.WriteStartArray();
        foreach (CookedOutput output in asset.Outputs)
        {
            writer.WriteStartObject();
            writer.WriteString("path", output.Path);
            writer.WriteString("id", output.AssetId.ToString("X32"));
            writer.WriteString("hash", output.ContentHash.ToString("X32"));
            writer.WriteNumber("bytes", output.Length);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }
}
