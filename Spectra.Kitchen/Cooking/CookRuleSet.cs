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
/// a packed build resolves less content than a loose one. That is how <c>.png</c>
/// reached a pack before <see cref="ImageRule"/> existed, and it is still how a
/// project manifest, a text file a game reads at runtime and anything else with no
/// cooked format of its own gets there.</para>
/// </remarks>
public sealed class CookRuleSet
{
    private readonly RawCopyRule _rawCopy = new();
    private readonly ShaderRule _shader = new();
    private readonly ImageRule _image = new();

    /// <summary>The rule that cooks <paramref name="contentPath"/>.</summary>
    public IRule Resolve(string contentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);

        // Matched on the EXTENSION through the rule's own predicate rather than
        // on a string spelled here: the rule already has to name what it cooks in
        // order to name what it emits, and a second spelling of ".spectrashade"
        // in this file is a rule that silently stops being reached.
        if (ShaderRule.Handles(contentPath)) return _shader;
        if (ImageRule.Handles(contentPath)) return _image;

        // Everything else falls through to the raw copy, which is the floor
        // rather than a placeholder: content with no cooked format of its own
        // still has to reach the runtime.
        return _rawCopy;
    }
}
