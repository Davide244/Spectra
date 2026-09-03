using SpectraEngine.Core.Assets.Sources;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// A rule's own view of the content root, shaped as an
/// <see cref="IContentSource"/> so a rule can call the engine's resolution
/// helpers instead of restating what they do.
/// </summary>
/// <remarks>
/// <para><b>It exists for exactly one function, and that is the argument for
/// it.</b> <c>ImageContentPath.Resolve</c> is the single expression of "a
/// material names <c>Textures/x.png</c> and the bytes may live at
/// <c>Textures/x.simage</c>", and this repo has already paid once for that rule
/// being written twice: an existence probe that disagrees with the open beside it
/// binds the magenta placeholder into every packed material while every log line
/// reads healthy. A cook rule asking the same question with its own
/// <c>Probe(Path.ChangeExtension(...))</c> would be the third spelling, and the
/// two would drift the first time either changed.</para>
/// <para><b>Every read and probe still goes through the context, so the
/// dependency recording is untouched.</b> That is what makes a negative answer
/// here a NEGATIVE DEPENDENCY: a material that looked for a texture and did not
/// find one re-cooks the moment somebody adds the file, which is the single most
/// common incremental-build bug and the reason the context records misses at
/// all.</para>
/// <para><b>Priority is zero and means nothing.</b> A source's priority is read
/// when it is mounted into a <c>ContentSourceStack</c>, and this is never
/// mounted into one: it is handed directly to a helper that asks it one
/// question.</para>
/// </remarks>
internal sealed class RuleContentSource : IContentSource
{
    private readonly IRuleContext _context;

    /// <summary>Wraps <paramref name="context"/> for the run it belongs to.</summary>
    public RuleContentSource(IRuleContext context) => _context = context;

    /// <inheritdoc/>
    public int Priority => 0;

    /// <inheritdoc/>
    public bool TryOpen(string path, [NotNullWhen(true)] out ContentBlob? blob)
    {
        // Probed before the read rather than catching the miss, because
        // RuleInputMissingException is how a rule STOPS, and a source must
        // answer false for a miss. Both calls record, and the context folds the
        // pair into one dependency at its first-access position.
        if (!_context.Probe(path))
        {
            blob = null;
            return false;
        }

        blob = ContentBlob.CopyOf(_context.Read(path));
        return true;
    }

    /// <inheritdoc/>
    public bool Exists(string path) => _context.Probe(path);

    /// <inheritdoc/>
    /// <remarks>
    /// Never: hot reload is a running engine's concern, and a cook has already
    /// recorded what it read in a form that outlives the process.
    /// </remarks>
    public bool TryGetWatchPath(string path, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing, deliberately. A cook's work list comes from
    /// <c>ContentWalker</c> walking the root once, and a rule that enumerated
    /// would be declaring a dependency on every file it saw - which is either a
    /// dependency set proportional to the project, or a lie about what the rule
    /// actually read.
    /// </remarks>
    public void TryEnumerate(string prefix, string extension, List<string> results)
    {
        ArgumentNullException.ThrowIfNull(results);
    }
}
