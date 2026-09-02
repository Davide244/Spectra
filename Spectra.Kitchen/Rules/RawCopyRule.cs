using SpectraEngine.Core.Assets.Packs;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// Copies an asset into the pack unchanged.
/// </summary>
/// <remarks>
/// <para><b>It is the floor of the rule set, not a placeholder.</b> A cooked build
/// carries files that have no cooked format of their own and never will: the
/// project manifest, a text file a game reads at runtime, anything a rule has not
/// been written for yet. Those still have to reach the runtime through the pack,
/// or a packed build would resolve less content than a loose one, which is the one
/// difference between the two modes the engine must never have.</para>
/// <para><b>It is also what makes the tool real from day one.</b> A cook that
/// produces a mountable pack of raw entries is a pipeline whose ends are joined:
/// the walker, the rule seam, the dependency recording, the writer and the reader
/// are all exercised, and each later rule replaces one lane of it rather than
/// completing it.</para>
/// </remarks>
public sealed class RawCopyRule : IRule
{
    /// <inheritdoc/>
    public RuleKind Kind => RuleKind.RawCopy;

    /// <inheritdoc/>
    /// <remarks>
    /// Raise this whenever the bytes this rule emits for a given input can
    /// change. Copying cannot change, so the only thing that would move it is a
    /// change to WHAT gets copied.
    /// </remarks>
    public int Version => 1;

    /// <inheritdoc/>
    public void Cook(IRuleContext context)
    {
        byte[] payload = context.Read(context.SourcePath);
        context.Emit(context.SourcePath, payload, PackEntryKind.Raw);
    }
}
