using Spectra.Kitchen.Rules;
using SpectraEngine.Core.Assets.Packs;
using System;
using System.Collections.Generic;
using System.IO;

namespace Spectra.Kitchen.Cache;

/// <summary>One output a cached rule run emitted, by name and by hash.</summary>
/// <param name="Path">Normalised content-relative path the engine resolves it by.</param>
/// <param name="Kind">The pack entry kind the rule asked for.</param>
/// <param name="ContentHash">Its name in the <see cref="ContentStore"/>.</param>
/// <param name="Length">Uncompressed byte count, so a manifest can be rebuilt without the payload.</param>
public readonly record struct CachedOutput(string Path, PackEntryKind Kind, UInt128 ContentHash, int Length);

/// <summary>One remembered run of one rule: what it was keyed as, and what it emitted.</summary>
/// <param name="Key">The <see cref="CookCacheKey"/> that run was made under.</param>
/// <param name="Outputs">What it emitted, by name and by content hash.</param>
public sealed record CookGeneration(UInt128 Key, IReadOnlyList<CachedOutput> Outputs);

/// <summary>
/// What a rule has done to one asset: the paths it last touched, and the last few
/// keys those paths produced.
/// </summary>
/// <param name="SourcePath">The asset the rule was asked to cook. Its identity here.</param>
/// <param name="Dependencies">
/// The rule's most recent access SHAPE - which paths it touched and how, MISSES
/// INCLUDED. The misses are not a separate list because
/// <see cref="RuleDependency"/> already carries the distinction as a kind, and two
/// lists that must agree about one path is a way for them to disagree. This is the
/// set of questions a later cook asks again; the answers are what the key is built
/// from.
/// </param>
/// <param name="Generations">
/// Recent runs, MOST RECENT FIRST, capped at <see cref="CookGraph.GenerationsKept"/>.
/// </param>
/// <remarks>
/// <para><b>More than one generation, and that is what makes a revert a hit rather
/// than a rebuild.</b> With one key per rule, editing a file and changing it back
/// leaves the graph holding only the intermediate key, so the return to the
/// original content is a miss and the cook re-does work whose exact output is
/// already sitting in the store. Remembering a few keys per rule makes the revert
/// resolve, which is the second half of what content hashing buys over timestamps.
/// </para>
/// <para><b>The cap is small on purpose.</b> Every remembered generation is a full
/// set of payloads held in the content store, and nothing sweeps that store yet,
/// so the number is a disk budget rather than a tuning knob.</para>
/// </remarks>
public sealed record CookGraphRecord(
    string SourcePath,
    IReadOnlyList<RuleDependency> Dependencies,
    IReadOnlyList<CookGeneration> Generations);

/// <summary>
/// One record per rule, persisted as <c>graph.bin</c>.
/// </summary>
/// <remarks>
/// <para><b>Hand-rolled and AOT-safe, like every other codec in this arc.</b> A
/// reflection-based serializer is what trimming removes, and the failure would be
/// a published cook tool that silently treats every cache as empty while a debug
/// run is incremental.</para>
/// <para><b>A file that does not parse is discarded, never thrown.</b> A cache is
/// derived data: the only correct response to one that cannot be read is to
/// rebuild it. Failing the cook instead would turn a corrupt cache into a build
/// nobody can run without knowing to delete a hidden folder.</para>
/// <para><b>Records are written sorted by source path</b>, so the file is a
/// function of what the cache holds rather than of the order a cook happened to
/// walk a directory in. Nothing depends on those bytes for identity; it is so that
/// a cache file misbehaving can be diffed between two runs.</para>
/// <para><b>Generations are addressed per record, never in a global key table.</b>
/// A cache key carries the rule, the settings, the toolchain and the inputs, and
/// for every rule that reads its own subject that already separates two assets -
/// but a rule that emits without reading its own path would key two different
/// assets identically, and a table keyed on the key alone would then serve one
/// asset's bytes for the other. Keeping generations inside the record makes that
/// unreachable rather than merely unlikely.</para>
/// </remarks>
public sealed class CookGraph
{
    private const uint Magic = 0x52474353; // "SCGR" little-endian
    private const uint FormatVersion = 1;

    /// <summary>How many past runs of one rule are remembered.</summary>
    public const int GenerationsKept = 4;

    private readonly Dictionary<string, CookGraphRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records held.</summary>
    public int Count => _records.Count;

    /// <summary>Whether anything changed since this was loaded.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Why a graph file on disk was discarded, or null when there was nothing
    /// wrong with it (or nothing there).
    /// </summary>
    /// <remarks>
    /// Reported by the session as an info rather than swallowed: a cook that
    /// rebuilds everything because its cache would not parse looks exactly like a
    /// slow cook, and "why is this not incremental" is unanswerable without it.
    /// </remarks>
    public string? DiscardedReason { get; private set; }

    /// <summary>The record for <paramref name="sourcePath"/>, if there is one.</summary>
    public bool TryGet(string sourcePath, out CookGraphRecord record) =>
        _records.TryGetValue(sourcePath, out record!);

    /// <summary>
    /// Records one rule run: it becomes the newest generation and the current
    /// access shape.
    /// </summary>
    public void Set(
        string sourcePath,
        IReadOnlyList<RuleDependency> dependencies,
        UInt128 key,
        IReadOnlyList<CachedOutput> outputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(outputs);

        var generations = new List<CookGeneration>(GenerationsKept) { new(key, outputs) };

        if (_records.TryGetValue(sourcePath, out CookGraphRecord? existing))
        {
            // The re-recorded key is dropped from its old position rather than
            // left there: a duplicate would spend one of the remembered slots on
            // an answer the newest generation already gives.
            for (int i = 0; i < existing.Generations.Count && generations.Count < GenerationsKept; i++)
            {
                if (existing.Generations[i].Key == key) continue;
                generations.Add(existing.Generations[i]);
            }
        }

        _records[sourcePath] = new CookGraphRecord(sourcePath, [.. dependencies], generations);
        IsDirty = true;
    }

    /// <summary>
    /// Drops every record whose source is not in <paramref name="live"/>.
    /// </summary>
    /// <remarks>
    /// Without this the graph grows for the life of the project and keeps naming
    /// assets that were deleted years ago. It does NOT sweep the content store:
    /// reclaiming payloads needs a reachability pass that no verb runs yet, so the
    /// store is append-only today and that is a named limitation rather than an
    /// oversight.
    /// </remarks>
    public void RetainOnly(IReadOnlyCollection<string> live)
    {
        ArgumentNullException.ThrowIfNull(live);

        var keep = new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
        List<string>? drop = null;

        foreach (string path in _records.Keys)
        {
            if (keep.Contains(path)) continue;

            drop ??= [];
            drop.Add(path);
        }

        if (drop is null) return;

        foreach (string path in drop) _records.Remove(path);
        IsDirty = true;
    }

    /// <summary>Loads the graph, or an empty one when it is absent or unreadable.</summary>
    public static CookGraph Load(string path)
    {
        var graph = new CookGraph();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return graph;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            graph.DiscardedReason = ex.Message;
            return graph;
        }

        try
        {
            graph.Read(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            graph._records.Clear();
            graph.DiscardedReason = ex.Message;
        }

        return graph;
    }

    /// <summary>Writes the graph, creating its directory.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var keys = new List<string>(_records.Keys);
        keys.Sort(StringComparer.Ordinal);

        var bytes = new List<byte>(64 + (keys.Count * 128));
        CacheBytes.U32(bytes, Magic);
        CacheBytes.U32(bytes, FormatVersion);
        CacheBytes.U32(bytes, (uint)keys.Count);

        foreach (string key in keys)
        {
            CookGraphRecord record = _records[key];

            CacheBytes.Str(bytes, record.SourcePath);

            CacheBytes.U32(bytes, (uint)record.Dependencies.Count);
            for (int i = 0; i < record.Dependencies.Count; i++)
            {
                RuleDependency dependency = record.Dependencies[i];
                CacheBytes.Str(bytes, dependency.Path);
                bytes.Add((byte)dependency.Kind);
                CacheBytes.U128(bytes, dependency.ContentHash);
            }

            CacheBytes.U32(bytes, (uint)record.Generations.Count);
            foreach (CookGeneration generation in record.Generations)
            {
                CacheBytes.U128(bytes, generation.Key);
                CacheBytes.U32(bytes, (uint)generation.Outputs.Count);

                for (int i = 0; i < generation.Outputs.Count; i++)
                {
                    CachedOutput output = generation.Outputs[i];
                    CacheBytes.Str(bytes, output.Path);
                    bytes.Add((byte)output.Kind);
                    CacheBytes.U128(bytes, output.ContentHash);
                    CacheBytes.U32(bytes, (uint)output.Length);
                }
            }
        }

        File.WriteAllBytes(path, [.. bytes]);
        IsDirty = false;
    }

    private void Read(ReadOnlySpan<byte> bytes)
    {
        var reader = new CacheReader(bytes);
        if (reader.U32() != Magic) throw new InvalidDataException("Not a cook graph.");
        if (reader.U32() != FormatVersion) throw new InvalidDataException("Cook graph version mismatch.");

        uint recordCount = reader.U32();
        for (uint r = 0; r < recordCount; r++)
        {
            string sourcePath = reader.Str();

            uint dependencyCount = reader.U32();
            var dependencies = new RuleDependency[dependencyCount];
            for (uint i = 0; i < dependencyCount; i++)
            {
                string path = reader.Str();
                RuleDependencyKind kind = ToDependencyKind(reader.U8());
                UInt128 hash = reader.U128();
                dependencies[i] = new RuleDependency(path, kind, hash);
            }

            uint generationCount = reader.U32();
            var generations = new CookGeneration[generationCount];
            for (uint g = 0; g < generationCount; g++)
            {
                UInt128 key = reader.U128();
                uint outputCount = reader.U32();
                var outputs = new CachedOutput[outputCount];

                for (uint i = 0; i < outputCount; i++)
                {
                    string path = reader.Str();
                    var kind = (PackEntryKind)reader.U8();
                    UInt128 hash = reader.U128();
                    int length = checked((int)reader.U32());
                    outputs[i] = new CachedOutput(path, kind, hash, length);
                }

                generations[g] = new CookGeneration(key, outputs);
            }

            _records[sourcePath] = new CookGraphRecord(sourcePath, dependencies, generations);
        }
    }

    // Refused rather than cast, because the whole file turns on this byte: a value
    // this build has no name for would land on Read (which is zero) and make a
    // negative dependency read as a positive one, which is the exact bug the
    // recording exists to prevent.
    private static RuleDependencyKind ToDependencyKind(byte value) => value switch
    {
        (byte)RuleDependencyKind.Read => RuleDependencyKind.Read,
        (byte)RuleDependencyKind.ProbeFound => RuleDependencyKind.ProbeFound,
        (byte)RuleDependencyKind.ProbeMissing => RuleDependencyKind.ProbeMissing,
        _ => throw new InvalidDataException(
            $"Cook graph names dependency kind {value}, which this build has no name for."),
    };
}
