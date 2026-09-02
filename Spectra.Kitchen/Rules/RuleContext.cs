using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// The recording implementation of <see cref="IRuleContext"/>: one per rule
/// invocation, holding that rule's dependency set, its outputs and its
/// diagnostics.
/// </summary>
/// <remarks>
/// <para><b>One context per rule run, never shared.</b> The three lists it holds
/// are that rule's answer, and rules will run in parallel; a shared context would
/// make each list's ORDER depend on scheduling, which is precisely what the
/// byte-identity oracles exist to catch and precisely what they are worst at
/// localising.</para>
/// <para><b>Dependencies keep first-access order and each path appears once.</b>
/// The cook key hashes inputs in declared order, so an order that depended on a
/// dictionary's iteration would make the key depend on nothing anybody
/// controls.</para>
/// <para><b>A probe followed by a read UPGRADES the record rather than adding a
/// second one</b>, and a read followed by a probe leaves the read alone. What the
/// key needs is the strongest observation made about each path: contents seen
/// beats existence seen, and either beats a miss the same run later contradicted.
/// </para>
/// <para><b>Reads are from the filesystem</b> because a cook's input is a project
/// folder on disk. That is deliberately not an <c>IContentSource</c>: a source
/// stack can be layered with a pack, and cooking a pack's own output back into
/// itself is a mistake worth making structurally impossible.</para>
/// </remarks>
public sealed class RuleContext : IRuleContext
{
    private readonly string _contentRoot;
    private readonly List<RuleDependency> _dependencies = [];
    private readonly Dictionary<string, int> _dependencyIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RuleEmission> _emissions = [];
    private readonly List<CookDiagnostic> _diagnostics = [];

    /// <summary>
    /// Creates the context for one rule run over <paramref name="sourcePath"/>.
    /// </summary>
    /// <param name="contentRoot">Absolute path of the project's content root.</param>
    /// <param name="sourcePath">Content-relative path of the asset being cooked.</param>
    /// <param name="profile">The profile the cook is running under.</param>
    public RuleContext(string contentRoot, string sourcePath, CookProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        _contentRoot = Path.GetFullPath(contentRoot);
        SourcePath = ContentRoot.NormalizeRelativePath(sourcePath);
        Profile = profile;
    }

    /// <inheritdoc/>
    public string SourcePath { get; }

    /// <inheritdoc/>
    public CookProfile Profile { get; }

    /// <summary>Every path this rule touched, in first-access order.</summary>
    public IReadOnlyList<RuleDependency> Dependencies => _dependencies;

    /// <summary>What this rule emitted, in emission order.</summary>
    public IReadOnlyList<RuleEmission> Emissions => _emissions;

    /// <summary>What this rule reported, in report order.</summary>
    public IReadOnlyList<CookDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Absolute path of <paramref name="contentPath"/> under this cook's content root.</summary>
    public string ResolveFullPath(string contentPath) => ToFullPath(Normalize(contentPath));

    /// <inheritdoc/>
    public byte[] Read(string contentPath)
    {
        string normalized = Normalize(contentPath);
        string full = ToFullPath(normalized);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Recorded before the throw, or the next cook after somebody adds the
            // file does not re-run this rule and serves the stale artifact while
            // reporting success.
            Record(normalized, RuleDependencyKind.ProbeMissing, UInt128.Zero);
            throw new RuleInputMissingException(normalized, SourcePath);
        }

        Record(normalized, RuleDependencyKind.Read, XxHash128.HashToUInt128(bytes));
        return bytes;
    }

    /// <inheritdoc/>
    public bool Probe(string contentPath)
    {
        string normalized = Normalize(contentPath);
        bool found = File.Exists(ToFullPath(normalized));
        Record(normalized, found ? RuleDependencyKind.ProbeFound : RuleDependencyKind.ProbeMissing, UInt128.Zero);
        return found;
    }

    /// <inheritdoc/>
    public void Emit(string outputPath, ReadOnlySpan<byte> payload, PackEntryKind kind = PackEntryKind.Raw)
    {
        _emissions.Add(new RuleEmission(Normalize(outputPath), kind, payload.ToArray()));
    }

    /// <inheritdoc/>
    public void Report(CookDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _diagnostics.Add(diagnostic);
    }

    private static string Normalize(string contentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
        return ContentRoot.NormalizeRelativePath(contentPath);
    }

    private string ToFullPath(string normalized) =>
        Path.Combine(_contentRoot, normalized.Replace('/', Path.DirectorySeparatorChar));

    private void Record(string normalized, RuleDependencyKind kind, UInt128 hash)
    {
        if (!_dependencyIndex.TryGetValue(normalized, out int at))
        {
            _dependencyIndex[normalized] = _dependencies.Count;
            _dependencies.Add(new RuleDependency(normalized, kind, hash));
            return;
        }

        // Strongest observation wins, and the slot keeps its first-access
        // position: a read after a probe must not move the path to the end of the
        // list, or the key changes for a rule whose behaviour did not.
        if (Strength(kind) > Strength(_dependencies[at].Kind))
            _dependencies[at] = new RuleDependency(normalized, kind, hash);
    }

    private static int Strength(RuleDependencyKind kind) => kind switch
    {
        RuleDependencyKind.Read => 2,
        RuleDependencyKind.ProbeFound => 1,
        _ => 0,
    };
}
