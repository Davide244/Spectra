using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// Everything a rule may do: read an input, ask whether one exists, emit an
/// output, say something.
/// </summary>
/// <remarks>
/// <para><b>The shape is the feature.</b> Every input a rule sees arrives through
/// <see cref="Read"/> or <see cref="Probe"/>, and both record the access, so the
/// rule's DECLARED dependency set is its ACCESSED dependency set by construction.
/// A rule cannot read a path it did not declare because there is no other way to
/// read one: no <c>File.ReadAllBytes</c>, no content root handed over, no stream
/// it could open behind the context's back. Every incremental build system that
/// asks rules to declare their inputs separately from using them eventually ships
/// a rule where the two disagree, and the symptom is a stale artifact that looks
/// correct.</para>
/// <para><b>A miss is an access too.</b> <see cref="Probe"/> returning false and
/// <see cref="Read"/> failing both record the path as a negative dependency, which
/// is what makes adding that file later invalidate this rule. Recording only the
/// hits is the same bug as not recording at all, one file later.</para>
/// <para><b>Paths are content-relative and normalised</b> through
/// <c>ContentRoot.NormalizeRelativePath</c>, the same string the asset caches and
/// the pack's ids are keyed on, so an asset's identity is one thing whether it
/// came from a folder or an archive.</para>
/// </remarks>
public interface IRuleContext
{
    /// <summary>The content-relative path this rule was asked to cook.</summary>
    string SourcePath { get; }

    /// <summary>The profile the cook is running under.</summary>
    CookProfile Profile { get; }

    /// <summary>
    /// The graphics backends this cook was asked for, in the order they were
    /// asked for.
    /// </summary>
    /// <remarks>
    /// <b>A setting reaches a rule through this interface or not at all</b>, and
    /// only settings a rule may declare in <see cref="IRule.SettingsRead"/> live
    /// here - <see cref="Profile"/> and this. Handing a rule the whole
    /// <c>CookSettings</c> would let it read one it never declared, and the cache
    /// key is built from the declaration: the artifact would then be stale under
    /// exactly the setting change that produced it, which is the failure the
    /// per-rule declaration exists to prevent.
    /// </remarks>
    IReadOnlyList<GraphicsBackend> Targets { get; }

    /// <summary>
    /// The one sample rate every cooked sound is resampled to.
    /// </summary>
    /// <remarks>
    /// Here for the same reason <see cref="Profile"/> and <see cref="Targets"/>
    /// are: a setting reaches a rule through this interface or not at all, and
    /// only settings a rule may declare in <see cref="IRule.SettingsRead"/> live
    /// here. It is a CONTENT decision rather than a per-run one - see
    /// <c>CookSettings.AudioSampleRate</c> for why changing it after a library
    /// exists is not merely a rebuild.
    /// </remarks>
    int AudioSampleRate { get; }

    /// <summary>
    /// The bytes at <paramref name="contentPath"/>, recording the read.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning null on a miss, and records the miss BEFORE
    /// throwing: a rule that cannot proceed should say so in one place, and the
    /// negative dependency has to survive the failure or the next cook after the
    /// file appears will not re-run this rule.
    /// </remarks>
    /// <exception cref="RuleInputMissingException">There is nothing at that path.</exception>
    byte[] Read(string contentPath);

    /// <summary>
    /// Whether anything exists at <paramref name="contentPath"/>, recording the
    /// answer either way.
    /// </summary>
    bool Probe(string contentPath);

    /// <summary>
    /// Emits one cooked output at <paramref name="outputPath"/>, which becomes a
    /// pack entry or a file in the loose tree.
    /// </summary>
    /// <param name="outputPath">Content-relative path the engine will resolve it by.</param>
    /// <param name="payload">The cooked bytes, copied.</param>
    /// <param name="kind">
    /// What the payload is, which the pack entry carries as a routing hint. It is
    /// a hint rather than an authority: every cooked format also carries its own
    /// magic.
    /// </param>
    void Emit(string outputPath, ReadOnlySpan<byte> payload, PackEntryKind kind = PackEntryKind.Raw);

    /// <summary>Reports a diagnostic against this rule.</summary>
    /// <remarks>
    /// Buffered per rule and flushed in rule order by the session. Writing
    /// straight to stderr from N workers tears lines apart, and the whole
    /// diagnostic contract is that each line is parseable, so the buffering is a
    /// correctness requirement of the output format rather than tidiness.
    /// </remarks>
    void Report(CookDiagnostic diagnostic);
}
