namespace Spectra.Kitchen.Rules;

/// <summary>
/// One cook rule: turns one authored asset into the cooked outputs that stand for
/// it.
/// </summary>
/// <remarks>
/// <para><b><see cref="Version"/> is part of the cache key, and raising it is how
/// a rule change invalidates everything it produced.</b> Nothing derives it: a
/// rule whose output changes without its version moving serves cached artifacts
/// from the old code forever, and the artifacts are valid, so nothing anywhere
/// reports it. Raise it in the same commit that changes the output.</para>
/// <para><b>A rule is stateless between calls and must stay that way.</b> Rules
/// run level-synchronously over the dependency graph and will run in parallel;
/// anything a rule remembers between two <see cref="Cook"/> calls is state whose
/// contents depend on scheduling, which is exactly what the byte-identity oracles
/// exist to catch and exactly what they are worst at localising.</para>
/// </remarks>
public interface IRule
{
    /// <summary>What this rule cooks. Part of the cache key.</summary>
    RuleKind Kind { get; }

    /// <summary>
    /// This rule's own version, raised whenever its output can change. Part of
    /// the cache key.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Cooks <see cref="IRuleContext.SourcePath"/>, reading through the context
    /// and emitting through it.
    /// </summary>
    void Cook(IRuleContext context);
}
