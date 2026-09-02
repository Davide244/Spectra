namespace SpectraEngine.Core.Entities;

/// <summary>
/// What travels with one input: who started the chain, who sent this message,
/// and the argument it carries.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Activator"/> and <see cref="Caller"/> are different questions
/// and a chain answers them differently.</b> The activator is whoever set the
/// whole cascade going - the player who stepped on the trigger - and it is
/// carried unchanged down every hop; the caller is whichever entity fired the
/// output being delivered right now, and it changes at every hop. A single
/// "sender" field would collapse the two and make a door unable to tell which
/// player opened it once a relay stands between them.
/// </para>
/// <para>
/// <b><see cref="Parameter"/> is a string because keyvalues are.</b> An input
/// converts it by its own declared type through <see cref="KeyvalueWire"/>,
/// which is what lets a console command hand a raw token to the same machinery
/// a map file feeds.
/// </para>
/// <para>
/// <b>Passed by <c>ref</c>.</b> Every entity resolved from one target name is
/// handed the same context rather than a copy per delivery, and a later member
/// an input writes back (a result, a handled flag) can be added without
/// rewriting the signature of every override in every game.
/// </para>
/// </remarks>
public struct EntityInputContext
{
    /// <param name="activator">Who started the chain, or null.</param>
    /// <param name="caller">Who fired the output being delivered, or null.</param>
    /// <param name="parameter">The argument, empty for none.</param>
    public EntityInputContext(Entity? activator, Entity? caller, string? parameter)
    {
        Activator = activator;
        Caller = caller;
        Parameter = parameter ?? "";
    }

    /// <summary>Whoever set this cascade going, carried unchanged down every hop.</summary>
    public Entity? Activator { get; }

    /// <summary>The entity whose output produced this message. Changes at every hop.</summary>
    public Entity? Caller { get; }

    /// <summary>The argument, in wire form. Never null; empty means "none".</summary>
    public string Parameter { get; }
}
