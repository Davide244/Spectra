using Spectra.Kitchen.Rules;
using System;

namespace Spectra.Kitchen.Cooking;

/// <summary>
/// Decides which rule cooks which asset.
/// </summary>
/// <remarks>
/// <para><b>A hand-written extension table, never a registry built by
/// reflection.</b> Discovering rules by scanning types is what trimming removes:
/// the cook would work in every debug run and produce a pack of raw copies in a
/// published one, with no error anywhere, because a raw copy of a PNG is a
/// perfectly valid pack entry.</para>
/// <para><b>The fallback is <see cref="RawCopyRule"/> and it is deliberately not a
/// refusal.</b> A file the cook has no rule for still has to reach the runtime, or
/// a packed build resolves less content than a loose one. When an image rule
/// lands, <c>.png</c> stops falling through to here; everything else keeps
/// working unchanged.</para>
/// </remarks>
public sealed class CookRuleSet
{
    private readonly RawCopyRule _rawCopy = new();

    /// <summary>The rule that cooks <paramref name="contentPath"/>.</summary>
    public IRule Resolve(string contentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);

        // Every extension falls through to the raw copy today. The switch is here
        // rather than arriving with the first real rule so that the first real
        // rule is one case, not a design.
        return _rawCopy;
    }
}
