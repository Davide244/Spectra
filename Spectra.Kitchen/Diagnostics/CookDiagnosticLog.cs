using System.Collections.Generic;

namespace Spectra.Kitchen.Diagnostics;

/// <summary>
/// Everything one run has to say, in the order it found it, with
/// <see cref="CookGate"/> applied on the way in.
/// </summary>
/// <remarks>
/// <para><b>A list with the gate welded to its <see cref="Add"/>, so the gate
/// cannot be bypassed by forgetting it.</b> The cook and the verifier both
/// collected into a plain <c>List</c> and each chose a severity at every
/// reporting site; the correct call was then one line that every new site had to
/// remember, at about forty sites and growing. Making it the only way in is the
/// same shape as <c>IRuleContext</c>: declared-equals-accessed by construction
/// rather than by author discipline.</para>
/// <para><b>The counts are kept as things arrive rather than swept
/// afterwards</b>, because both callers ask "did this fail yet" in the middle of
/// their run - the cook to decide whether to write a pack at all - and a sweep
/// per question is a sweep per diagnostic in the limit.</para>
/// </remarks>
internal sealed class CookDiagnosticLog
{
    private readonly List<CookDiagnostic> _entries = [];
    private readonly bool _strict;

    /// <summary>Creates a log for a run that is, or is not, <c>--strict</c>.</summary>
    public CookDiagnosticLog(bool strict) => _strict = strict;

    /// <summary>What was said, in the order it was said, after the gate.</summary>
    public IReadOnlyList<CookDiagnostic> Entries => _entries;

    /// <summary>How many of them failed the run.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>How many of them merely warned.</summary>
    public int WarningCount { get; private set; }

    /// <summary>Whether anything so far refuses the artifact.</summary>
    public bool Failed => ErrorCount > 0;

    /// <summary>Records <paramref name="diagnostic"/> at the gate's severity.</summary>
    public void Add(CookDiagnostic diagnostic)
    {
        CookDiagnostic decided = CookGate.Apply(diagnostic, _strict);
        _entries.Add(decided);

        if (decided.IsError) ErrorCount++;
        else if (decided.Severity == CookDiagnosticSeverity.Warning) WarningCount++;
    }

    /// <summary>Records each of <paramref name="diagnostics"/> in order.</summary>
    public void AddRange(IReadOnlyList<CookDiagnostic> diagnostics)
    {
        for (int i = 0; i < diagnostics.Count; i++) Add(diagnostics[i]);
    }
}
