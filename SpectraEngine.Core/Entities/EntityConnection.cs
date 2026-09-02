namespace SpectraEngine.Core.Entities;

/// <summary>
/// One wire from an entity's output to another entity's input: when
/// <see cref="Output"/> fires, every entity named <see cref="TargetName"/> is
/// sent <see cref="Input"/> with <see cref="Parameter"/>, after
/// <see cref="Delay"/> seconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The target is a NAME, resolved when the output fires and not before.</b>
/// The entity being wired to may be spawned later, may be spawned many times, or
/// may not exist at all in this map; a reference captured at load time would be
/// wrong for all three, and the third would have to fail a load over a wire
/// somebody left dangling on purpose.
/// </para>
/// <para>
/// <b><see cref="Parameter"/> is a string because keyvalues are.</b> An input
/// converts it by its own declared type, which is what lets a console command
/// hand a raw token to the same machinery a map file feeds without a conversion
/// layer between them.
/// </para>
/// <para>
/// <b>A value, not an object.</b> Connections are compared, copied and written
/// far more often than they are mutated, and an entity's wiring is part of what
/// a duplicate must not share with its original.
/// </para>
/// </remarks>
/// <param name="Output">The name of the output that fires this wire.</param>
/// <param name="TargetName">The name of the entity or entities to send to.</param>
/// <param name="Input">The name of the input to send.</param>
/// <param name="Parameter">The argument to send, empty for none.</param>
/// <param name="Delay">Seconds to wait before sending. Zero fires on the same tick.</param>
/// <param name="TimesToFire">
/// How many times this wire may fire before it is removed, or
/// <see cref="Infinite"/> for no limit.
/// </param>
public readonly record struct EntityConnection(
    string Output,
    string TargetName,
    string Input,
    string Parameter,
    float Delay,
    int TimesToFire)
{
    /// <summary>
    /// The <see cref="TimesToFire"/> value meaning "as often as it fires".
    /// </summary>
    /// <remarks>
    /// Negative rather than a large number or a nullable, because it is written
    /// as an integer on the wire and a sentinel keeps the record fixed-width.
    /// Any negative value reads as infinite (see <see cref="FiresForever"/>), so
    /// a count decremented past the end cannot wrap into a finite one.
    /// </remarks>
    public const int Infinite = -1;

    /// <summary>Whether this wire has no firing limit.</summary>
    public bool FiresForever => TimesToFire < 0;
}
