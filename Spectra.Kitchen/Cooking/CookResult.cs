using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Rules;
using System;
using System.Collections.Generic;

namespace Spectra.Kitchen.Cooking;

/// <summary>One cooked output, with the two hashes a manifest diff needs.</summary>
/// <param name="Path">Content-relative path the engine resolves it by.</param>
/// <param name="AssetId">The pack id: <c>XxHash128</c> of that path.</param>
/// <param name="ContentHash">
/// <c>XxHash128</c> of the cooked bytes. This is what a CI diff compares: an
/// output hash that moved when no input hash did is a determinism failure, and it
/// is far cheaper to notice here than as a patch that will not apply.
/// </param>
/// <param name="Length">Uncompressed byte count.</param>
public readonly record struct CookedOutput(string Path, UInt128 AssetId, UInt128 ContentHash, int Length);

/// <summary>What one rule run produced, kept for the manifest and for a verify.</summary>
/// <param name="SourcePath">The authored asset.</param>
/// <param name="Rule">Which rule cooked it.</param>
/// <param name="RuleVersion">That rule's version at cook time; part of the cache key.</param>
/// <param name="Dependencies">
/// Everything the rule touched, MISSES INCLUDED. The misses are the half that
/// makes an incremental cook correct rather than merely fast.
/// </param>
/// <param name="Outputs">What it emitted.</param>
public sealed record CookedAsset(
    string SourcePath,
    RuleKind Rule,
    int RuleVersion,
    IReadOnlyList<RuleDependency> Dependencies,
    IReadOnlyList<CookedOutput> Outputs)
{
    /// <summary>
    /// Whether this asset was answered from the cook cache rather than cooked.
    /// </summary>
    /// <remarks>
    /// <b>It is in the manifest because a reviewer has to be able to tell.</b> The
    /// bytes are identical either way, which is the whole promise; what a skip
    /// changes is which run actually produced them, and that is exactly the
    /// question asked when a cached artifact turns out to be wrong.
    /// </remarks>
    public bool FromCache { get; init; }
}

/// <summary>
/// What a cook did, whether it worked, and everything it had to say.
/// </summary>
public sealed class CookResult
{
    /// <summary>Assets cooked, in walk order.</summary>
    public required IReadOnlyList<CookedAsset> Assets { get; init; }

    /// <summary>
    /// Diagnostics in rule order, which is the order they must be printed in.
    /// </summary>
    /// <remarks>
    /// Buffered per rule and flushed in rule order rather than written as they
    /// happen, because the whole diagnostic contract is that each line is
    /// IDE-parseable and N workers writing to one stream tear lines apart. That
    /// makes the buffering a correctness requirement of the output format.
    /// </remarks>
    public required IReadOnlyList<CookDiagnostic> Diagnostics { get; init; }

    /// <summary>The pack that was written, or the loose tree's root, or null when nothing was.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Entries in the pack, or files in the loose tree.</summary>
    public int EntryCount { get; init; }

    /// <summary>Bytes of cooked payload, before any container overhead.</summary>
    public long PayloadBytes { get; init; }

    /// <summary>Errors among <see cref="Diagnostics"/>, after any strict promotion.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Warnings among <see cref="Diagnostics"/>.</summary>
    public int WarningCount { get; init; }

    /// <summary>Assets answered from the cook cache. Zero when the cache is off.</summary>
    public int CacheHits { get; init; }

    /// <summary>Assets whose rule had to run while the cache was on.</summary>
    public int CacheMisses { get; init; }

    /// <summary>Whether the cook produced its artifact.</summary>
    public bool Succeeded => ErrorCount == 0 && OutputPath is not null;
}
