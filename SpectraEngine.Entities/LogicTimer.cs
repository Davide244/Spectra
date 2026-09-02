using SpectraEngine.Core.Entities;

namespace SpectraEngine.Entities;

/// <summary>
/// Fires an output on a repeating interval, for as long as it is enabled.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>Enable</c> RESTARTS the interval, it does not resume one.</b> A timer
/// disabled two seconds into a five second interval and enabled again waits five
/// seconds, not three. The alternative needs the timer to remember how much of an
/// interval had elapsed when it stopped, which turns "switch this on" into a
/// question about when it was last switched off - and a level designer wiring a
/// button to <c>Enable</c> is asking for the interval to begin now. It also makes
/// <c>Enable</c> and <c>ResetTimer</c> agree, which is what stops the two reading
/// as the same input with different names.
/// </para>
/// <para>
/// <b>A think does not reschedule itself; this entity reschedules from inside its
/// own think.</b> That is the runtime's rule (an entity that wants to run again
/// says so), and it is what makes <c>Disable</c> a matter of simply not asking
/// again rather than a cancel that has to reach into the world's queue.
/// </para>
/// <para>
/// <b>The interval is floored rather than refused.</b> A zero or negative refire
/// time is a think that is always due, which the world would dispatch every tick
/// until <c>MaxDispatchesPerTick</c> tripped and reported a runaway cascade with
/// this timer's name on it. Refusing the value with a throw is not available
/// either: the binder assigns straight into this property, and
/// <see cref="Entity.ParseKeyValue"/>'s contract is that one unreadable field
/// must not take down the load of a whole level.
/// </para>
/// </remarks>
[SpectraEntity("logic_timer", Group = "Logic", Placement = EntityPlacement.Abstract)]
public sealed partial class LogicTimer : Entity
{
    /// <summary>
    /// The shortest interval a timer will run at, in seconds.
    /// </summary>
    /// <remarks>
    /// One hundredth of a second is well under a fixed tick, so it means "every
    /// tick" without meaning "the same instant forever".
    /// </remarks>
    public const float MinimumInterval = 0.01f;

    /// <summary>Fired at the end of every interval.</summary>
    [EntityOutput]
    public const string OnTimer = nameof(OnTimer);

    private float _refireInterval = 1f;

    /// <summary>Whether the timer starts switched off.</summary>
    [Keyvalue(
        "startdisabled",
        Display = "Start disabled",
        Tooltip = "The timer does not run until something sends it Enable.",
        Default = "0")]
    public bool StartDisabled { get; set; }

    /// <summary>Seconds between fires, floored at <see cref="MinimumInterval"/>.</summary>
    [Keyvalue(
        "refiretime",
        Display = "Refire time",
        Tooltip = "Seconds between fires.",
        Default = "1",
        Min = MinimumInterval)]
    public float RefireInterval
    {
        get => _refireInterval;
        // A NaN fails this comparison and lands on the floor, which is the right
        // answer: the value cannot order against anything, so scheduling from it
        // would put an event in the queue that no comparison ever makes due.
        set => _refireInterval = value >= MinimumInterval ? value : MinimumInterval;
    }

    /// <summary>Whether the timer is currently running.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>How many times this timer has fired since it spawned.</summary>
    public int FireCount { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Started in <c>OnActivate</c> rather than <c>OnSpawn</c>, so the first
    /// interval is scheduled after every entity in the world exists: a timer with
    /// a very short interval could otherwise deliver its first <c>OnTimer</c> at a
    /// target that has not been built.
    /// </remarks>
    protected override void OnActivate()
    {
        if (!StartDisabled)
            Start();
    }

    /// <inheritdoc/>
    protected override void Think()
    {
        FireCount++;
        FireOnTimer();

        // Re-armed only while enabled, so a Disable that arrived during the
        // interval ends the loop here rather than needing to reach the queue.
        if (IsEnabled)
            SetNextThinkIn(RefireInterval);
    }

    /// <summary>Starts the timer, restarting the interval from now.</summary>
    [EntityInput("Enable")]
    private void Enable(ref EntityInputContext context) => Start();

    /// <summary>Stops the timer.</summary>
    [EntityInput("Disable")]
    private void Disable(ref EntityInputContext context) => Stop();

    /// <summary>Starts a stopped timer, or stops a running one.</summary>
    [EntityInput("Toggle")]
    private void Toggle(ref EntityInputContext context)
    {
        if (IsEnabled)
            Stop();
        else
            Start();
    }

    /// <summary>Restarts the interval from now, without firing.</summary>
    /// <remarks>
    /// Does nothing on a stopped timer, because restarting one would be an
    /// <c>Enable</c> under another name.
    /// </remarks>
    [EntityInput("ResetTimer")]
    private void ResetTimer(ref EntityInputContext context)
    {
        if (IsEnabled)
            Start();
    }

    /// <summary>Fires <c>OnTimer</c> now, leaving the schedule alone.</summary>
    /// <remarks>
    /// The interval is deliberately not restarted: this input exists so something
    /// else can borrow the timer's output, and a level that wanted the schedule
    /// moved would send <c>ResetTimer</c> as well.
    /// </remarks>
    [EntityInput("FireTimer")]
    private void FireTimer(ref EntityInputContext context)
    {
        FireCount++;
        FireOnTimer(context.Activator);
    }

    /// <summary>Sets the interval from the parameter, in seconds.</summary>
    /// <remarks>
    /// The interval already running is left to finish. Rescheduling here would
    /// mean a level that adjusts the interval every tick never fires at all, and
    /// "takes effect at the next interval" is the only rule that behaves the same
    /// whether the input arrives once or continuously.
    /// </remarks>
    [EntityInput("RefireTime")]
    private void RefireTime(ref EntityInputContext context)
    {
        if (KeyvalueWire.TryParseFloat(context.Parameter, out float seconds))
            RefireInterval = seconds;
    }

    private void Start()
    {
        IsEnabled = true;
        SetNextThinkIn(RefireInterval);
    }

    private void Stop()
    {
        IsEnabled = false;
        CancelThink();
    }
}
