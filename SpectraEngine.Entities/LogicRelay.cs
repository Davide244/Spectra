using SpectraEngine.Core.Entities;

namespace SpectraEngine.Entities;

/// <summary>
/// Passes a trigger on: one input in, one output out, with an enabled bit so a
/// level can switch a whole branch of its wiring off.
/// </summary>
/// <remarks>
/// <para>
/// <b>It REFIRES WHILE PENDING, and that is v1's deliberate answer.</b> A second
/// <c>Trigger</c> arriving before an earlier one's wire delay has elapsed queues
/// a second <c>OnTrigger</c> rather than being swallowed. The alternative -
/// Source's "wait for refire" behaviour, where a relay ignores input until its
/// delayed output has been delivered - needs the relay to know that a delivery is
/// outstanding, and in this engine the delay lives on the WIRE
/// (<see cref="EntityConnection.Delay"/>), not on the relay: one relay's output
/// can carry five wires with five different delays, so "pending" is not a state
/// the relay has. Refiring is therefore the only answer it can implement
/// honestly, and a level that wants the other one puts a
/// <c>logic_timer</c> in front.
/// </para>
/// <para>
/// <b>There is no <c>CancelPending</c> input, for the same reason.</b> Source has
/// one because the relay owns its pending events; here they are in the world's
/// queue, which has no cancel, so the input would be a control that does nothing
/// - and a control that does nothing teaches, within one session, that this
/// engine's controls are decorative.
/// </para>
/// </remarks>
[SpectraEntity("logic_relay", Group = "Logic", Placement = EntityPlacement.Abstract)]
public sealed partial class LogicRelay : Entity
{
    /// <summary>Fired every time an accepted <c>Trigger</c> passes through.</summary>
    [EntityOutput]
    public const string OnTrigger = nameof(OnTrigger);

    /// <summary>
    /// Whether the relay starts switched off, so a level can arm a branch of its
    /// wiring later rather than at load.
    /// </summary>
    [Keyvalue(
        "startdisabled",
        Display = "Start disabled",
        Tooltip = "The relay ignores Trigger until something sends it Enable.",
        Default = "0")]
    public bool StartDisabled { get; set; }

    /// <summary>Whether the relay is currently passing triggers on.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>How many triggers this relay has passed on since it spawned.</summary>
    public int TriggerCount { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Read in <c>OnSpawn</c> rather than in the keyvalue's setter, because
    /// keyvalues arrive in authored order and a level is free to write
    /// <c>startdisabled</c> after anything else; the enabled bit is settled once,
    /// after every key has been parsed.
    /// </remarks>
    protected override void OnSpawn() => IsEnabled = !StartDisabled;

    /// <summary>Passes the trigger on, if this relay is enabled.</summary>
    [EntityInput("Trigger")]
    private void Trigger(ref EntityInputContext context)
    {
        if (!IsEnabled)
            return;

        TriggerCount++;
        // The activator travels on unchanged: whoever set the cascade going is
        // still whoever set it going on the far side of a relay, which is the
        // whole reason activator and caller are separate fields.
        FireOnTrigger(context.Activator);
    }

    /// <summary>Starts passing triggers on.</summary>
    [EntityInput("Enable")]
    private void Enable(ref EntityInputContext context) => IsEnabled = true;

    /// <summary>Stops passing triggers on.</summary>
    [EntityInput("Disable")]
    private void Disable(ref EntityInputContext context) => IsEnabled = false;

    /// <summary>Flips whether triggers are passed on.</summary>
    [EntityInput("Toggle")]
    private void Toggle(ref EntityInputContext context) => IsEnabled = !IsEnabled;
}
