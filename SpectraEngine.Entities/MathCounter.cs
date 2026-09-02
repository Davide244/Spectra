using SpectraEngine.Core.Entities;
using System;

namespace SpectraEngine.Entities;

/// <summary>
/// Holds a number a level can add to, subtract from and read back, with optional
/// bounds that announce themselves when the count reaches them.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>OnHitMax</c> fires on the TRANSITION, never on every hit.</b> A counter
/// pinned at its ceiling by five more <c>Add</c>s fires once, on the arrival, and
/// nothing on the four that follow; it arms again by leaving the ceiling. The
/// other reading - fire on every change that lands at the bound - turns "the
/// third crate is on the pressure plate, open the door" into "open the door five
/// times", and every level built on it then has to defend against its own
/// counter. The same rule governs <c>OnHitMin</c>.
/// </para>
/// <para>
/// <b>The transition memory is DERIVED from the value, not stored beside it.</b>
/// "Was it at the bound before this change?" is read off the value that was
/// there, so <c>SetValueNoFire</c> needs no special handling to stay consistent:
/// it moves the value, fires nothing, and the next arrival at a bound is judged
/// against where the counter actually is. A stored "already announced" flag is
/// the same thing with one more way to be wrong.
/// </para>
/// <para>
/// <b>Bounds are active only while <c>max</c> is above <c>min</c></b>, which is
/// why both default to zero: a pair of zeros is an unclamped counter, and there
/// is no separate "clamping" switch to leave in the wrong position. Setting a
/// bound re-clamps the value and fires nothing, because changing the shape of a
/// counter is configuration rather than counting.
/// </para>
/// </remarks>
[SpectraEntity("math_counter", Group = "Logic", Placement = EntityPlacement.Abstract)]
public sealed partial class MathCounter : Entity
{
    /// <summary>Fired with the new value whenever the count changes or is asked for.</summary>
    [EntityOutput]
    public const string OutValue = nameof(OutValue);

    /// <summary>Fired when the count ARRIVES at its ceiling, once per arrival.</summary>
    [EntityOutput]
    public const string OnHitMax = nameof(OnHitMax);

    /// <summary>Fired when the count ARRIVES at its floor, once per arrival.</summary>
    [EntityOutput]
    public const string OnHitMin = nameof(OnHitMin);

    /// <summary>The count this counter starts at, before any clamping.</summary>
    [Keyvalue(
        "startvalue",
        Display = "Start value",
        Tooltip = "The count at load, clamped into the bounds if there are any.",
        Default = "0")]
    public float StartValue { get; set; }

    /// <summary>The floor, active only while <see cref="Maximum"/> is above it.</summary>
    [Keyvalue(
        "min",
        Display = "Minimum",
        Tooltip = "The floor. Equal to the maximum means the counter is unclamped.",
        Default = "0")]
    public float Minimum { get; set; }

    /// <summary>The ceiling, active only while it is above <see cref="Minimum"/>.</summary>
    [Keyvalue(
        "max",
        Display = "Maximum",
        Tooltip = "The ceiling. Equal to the minimum means the counter is unclamped.",
        Default = "0")]
    public float Maximum { get; set; }

    /// <summary>The current count.</summary>
    public float Value { get; private set; }

    /// <summary>Whether the bounds are in force.</summary>
    public bool IsClamped => Maximum > Minimum;

    /// <summary>
    /// How many inputs carried an argument this counter could not use.
    /// </summary>
    /// <remarks>
    /// Counted rather than logged, because an entity has no reporting channel for
    /// a refused INPUT the way it has <c>RefuseKeyvalue</c> for a refused
    /// keyvalue. A count is at least visible to a test and to a future debug
    /// panel, which "nothing happened" is not.
    /// </remarks>
    public int RefusedInputCount { get; private set; }

    /// <inheritdoc/>
    protected override void OnSpawn() => Value = Clamp(StartValue);

    /// <summary>Adds the parameter to the count. No argument adds one.</summary>
    /// <remarks>
    /// One is the default because the commonest wiring in any level is a trigger
    /// counting the things that pass through it, and writing <c>1</c> on every
    /// such wire is ceremony that buys nothing.
    /// </remarks>
    [EntityInput("Add")]
    private void Add(ref EntityInputContext context) => Move(Amount(ref context), ref context);

    /// <summary>Subtracts the parameter from the count. No argument subtracts one.</summary>
    [EntityInput("Subtract")]
    private void Subtract(ref EntityInputContext context) => Move(-Amount(ref context), ref context);

    /// <summary>Sets the count, firing the outputs the new value earns.</summary>
    [EntityInput("SetValue")]
    private void SetValue(ref EntityInputContext context)
    {
        if (TryRead(ref context, out float value))
            Assign(value, context.Activator, announce: true);
    }

    /// <summary>Sets the count silently, firing nothing.</summary>
    [EntityInput("SetValueNoFire")]
    private void SetValueNoFire(ref EntityInputContext context)
    {
        if (TryRead(ref context, out float value))
            Assign(value, context.Activator, announce: false);
    }

    /// <summary>Sets the ceiling and re-clamps the count, firing nothing.</summary>
    [EntityInput("SetHitMax")]
    private void SetHitMax(ref EntityInputContext context)
    {
        if (!TryRead(ref context, out float value))
            return;

        Maximum = value;
        Value = Clamp(Value);
    }

    /// <summary>Sets the floor and re-clamps the count, firing nothing.</summary>
    [EntityInput("SetHitMin")]
    private void SetHitMin(ref EntityInputContext context)
    {
        if (!TryRead(ref context, out float value))
            return;

        Minimum = value;
        Value = Clamp(Value);
    }

    /// <summary>Fires <c>OutValue</c> with the current count, changing nothing.</summary>
    [EntityInput("GetValue")]
    private void GetValue(ref EntityInputContext context) =>
        FireOutValue(context.Activator, KeyvalueWire.Format(Value));

    private void Move(float delta, ref EntityInputContext context) =>
        Assign(Value + delta, context.Activator, announce: true);

    private void Assign(float value, Entity? activator, bool announce)
    {
        // An arithmetic overflow reaches infinity, which KeyvalueWire.Format
        // refuses to write because it cannot be read back. Refusing the change is
        // the only answer that keeps the counter's own output readable.
        if (!float.IsFinite(value))
        {
            RefusedInputCount++;
            return;
        }

        bool wasAtMaximum = IsClamped && Value >= Maximum;
        bool wasAtMinimum = IsClamped && Value <= Minimum;

        Value = Clamp(value);

        if (!announce)
            return;

        FireOutValue(activator, KeyvalueWire.Format(Value));

        if (IsClamped && Value >= Maximum && !wasAtMaximum)
            FireOnHitMax(activator);

        if (IsClamped && Value <= Minimum && !wasAtMinimum)
            FireOnHitMin(activator);
    }

    private float Clamp(float value) => IsClamped ? Math.Clamp(value, Minimum, Maximum) : value;

    // An absent or unreadable argument means one, which is what makes Add and
    // Subtract total: every wire into them does something.
    private float Amount(ref EntityInputContext context)
    {
        if (context.Parameter.Length == 0)
            return 1f;

        if (KeyvalueWire.TryParseFloat(context.Parameter, out float amount))
            return amount;

        RefusedInputCount++;
        return 1f;
    }

    // Nothing to default to here: an unreadable argument to SetValue names no
    // value at all, so the count is left where it was.
    private bool TryRead(ref EntityInputContext context, out float value)
    {
        if (KeyvalueWire.TryParseFloat(context.Parameter, out value))
            return true;

        RefusedInputCount++;
        return false;
    }
}
