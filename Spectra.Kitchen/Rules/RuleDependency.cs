using System;

namespace Spectra.Kitchen.Rules;

/// <summary>How a rule touched a path.</summary>
public enum RuleDependencyKind
{
    /// <summary>Read: the contents were seen, so the contents are part of the key.</summary>
    Read,

    /// <summary>Probed and found: only its EXISTENCE was seen.</summary>
    /// <remarks>
    /// Recorded without a content hash on purpose. A rule that asked whether a
    /// file exists and never opened it does not change when its bytes change, and
    /// hashing them anyway would make every probe cost a read.
    /// </remarks>
    ProbeFound,

    /// <summary>
    /// Probed or read and NOT found. The negative dependency, and the one that
    /// matters most.
    /// </summary>
    /// <remarks>
    /// Without it, adding the file later never invalidates the rule that looked
    /// for it: a watch loop serves a stale cook and reports success. That is the
    /// single most common incremental-build bug and it costs one list entry.
    /// </remarks>
    ProbeMissing,
}

/// <summary>
/// One path a rule touched, and what it saw there.
/// </summary>
/// <remarks>
/// The set of these IS the rule's declared input set, because there is no way for
/// a rule to reach a byte except through <see cref="IRuleContext"/>, which records
/// one of these on the way past. Declared-equals-accessed is then a property of
/// the shape rather than of author discipline.
/// </remarks>
/// <param name="Path">Normalised content-relative path.</param>
/// <param name="Kind">What was seen.</param>
/// <param name="ContentHash">
/// <c>XxHash128</c> of the bytes for a <see cref="RuleDependencyKind.Read"/>, and
/// zero for both probe kinds, which saw no bytes.
/// </param>
public readonly record struct RuleDependency(string Path, RuleDependencyKind Kind, UInt128 ContentHash)
{
    /// <summary>Whether the path was absent when the rule looked.</summary>
    public bool IsMissing => Kind == RuleDependencyKind.ProbeMissing;
}
