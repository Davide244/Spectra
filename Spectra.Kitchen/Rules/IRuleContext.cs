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
    /// Whether this cook was asked to keep every brush's authored planes in the
    /// compiled map.
    /// </summary>
    /// <remarks>
    /// Here for the same reason <see cref="Profile"/>, <see cref="Targets"/> and
    /// <see cref="AudioSampleRate"/> are: a setting reaches a rule through this
    /// interface or not at all, and only settings a rule may declare in
    /// <see cref="IRule.SettingsRead"/> live here. It says what to KEEP and never
    /// what a loader should do with it: a compiled map's brush planes are data, and
    /// whether any one of them may be carved again is the node record's
    /// <c>BakedIntoChunks</c> contract.
    /// </remarks>
    bool KeepBrushSource { get; }

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
    /// Every file under <paramref name="contentPath"/>, recursively, as
    /// content-relative paths in ordinal order. An absent directory lists nothing.
    /// </summary>
    /// <remarks>
    /// <para><b>For an asset that is a FOLDER.</b> A <c>.smap</c> bundle is a
    /// directory holding a document and, later, its scripts as real files, so a
    /// rule over one cannot name its inputs without asking what is in it. Sorted
    /// ordinal here rather than by the caller, for the reason
    /// <c>ContentWalker</c> already records: <c>Directory.EnumerateFiles</c> has no
    /// documented order, so a walk is a different list on a different filesystem.
    /// </para>
    /// <para><b>This is NOT a recorded dependency, and the cost is stated rather
    /// than hidden.</b> The cache decides a hit by restating each recorded
    /// observation against today's filesystem, and every observation it can restate
    /// is about one PATH: a listing is a question about a directory, and there is
    /// no form of it the cache could re-ask. So the files this returns become
    /// dependencies when the rule reads them - which covers an edited file and a
    /// deleted one - while a file newly ADDED to a bundle does not invalidate the
    /// rule that would have read it. That window closes the day a directory
    /// observation joins <c>RuleDependencyKind</c>, and until then a rule over a
    /// folder is one <c>scook clean</c> away from being right.</para>
    /// </remarks>
    IReadOnlyList<string> ListFiles(string contentPath);

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
