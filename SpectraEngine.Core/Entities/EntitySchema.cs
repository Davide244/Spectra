using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Entities;

/// <summary>How an entity class is placed in a level.</summary>
/// <remarks>
/// Numbering is frozen: a schema record stores this as one byte. Append only.
/// </remarks>
public enum EntityPlacement : byte
{
    /// <summary>A point in space: the node carries a transform and nothing else.</summary>
    Point = 0,

    /// <summary>
    /// A volume: the node carries brush geometry the class gives behaviour to.
    /// </summary>
    Brush = 1,

    /// <summary>
    /// Logic only, with no position anybody looks at. Still a node, because
    /// everything in this engine is a node, but its transform means nothing.
    /// </summary>
    Abstract = 2,
}

/// <summary>Where an entity class was DEFINED.</summary>
/// <remarks>
/// <b>An editor badge, and nothing else.</b> Nothing in the engine branches on
/// it: a Luau-defined class and a generated C# one produce the same
/// <see cref="EntitySchema"/> and register into the same catalogue, which is the
/// property that keeps the two producers from drifting. This exists so a person
/// reading a property panel can tell which file to open.
/// </remarks>
public enum EntityOrigin : byte
{
    /// <summary>Declared in the engine's own C#, through the entity attributes.</summary>
    EngineCSharp = 0,

    /// <summary>Declared by a game's Luau entity definition.</summary>
    Luau = 1,

    /// <summary>Declared by a game that references the engine as a library.</summary>
    SdkCSharp = 2,
}

/// <summary>
/// The closed widget vocabulary a <see cref="KeyvalueDescriptor"/> may ask for.
/// </summary>
/// <remarks>
/// <b>Byte constants rather than an enum, because the descriptor's field is the
/// wire byte itself.</b> An enum would have to be cast at every read and write
/// of that field, and a cast is exactly where a value outside the vocabulary
/// enters unnoticed. <see cref="IsDefined"/> is the one gate; a widget nobody
/// recognises degrades to <see cref="Auto"/> rather than failing, because a
/// property that cannot be shown is worse than one shown plainly.
/// </remarks>
public static class KeyvalueWidget
{
    /// <summary>Let the editor choose from the declared <see cref="KeyvalueType"/>.</summary>
    public const byte Auto = 0;

    /// <summary>A slider, which is only meaningful with both a min and a max.</summary>
    public const byte Slider = 1;

    /// <summary>A browse-for-an-asset field.</summary>
    public const byte AssetPicker = 2;

    /// <summary>A pick-an-entity field.</summary>
    public const byte EntityPicker = 3;

    /// <summary>A swatch plus a hex field, over a linear colour.</summary>
    public const byte Color = 4;

    /// <summary>A checkbox per declared bit.</summary>
    public const byte Flags = 5;

    /// <summary>Whether <paramref name="widget"/> is one this vocabulary names.</summary>
    public static bool IsDefined(byte widget) => widget <= Flags;
}

/// <summary>
/// The bits of a <see cref="KeyvalueDescriptor.Flags"/> word that this engine
/// assigns meaning to.
/// </summary>
/// <remarks>
/// <b>Bits 3 to 7 are RESERVED and are not free.</b> Later work claims them
/// (replication and per-property realm, in the format documents), so nothing may
/// assign them a second meaning here. They are written zero and masked off on
/// read: a definition produced by a newer tool must lose the bits this engine
/// does not understand rather than acting on them by accident.
/// </remarks>
public static class KeyvalueFlags
{
    /// <summary>Shown, never edited.</summary>
    public const uint ReadOnly = 1u << 0;

    /// <summary>Bound and carried, never shown.</summary>
    public const uint HideInEditor = 1u << 1;

    /// <summary>Takes effect on the next launch, and the editor says so.</summary>
    public const uint RequiresRestart = 1u << 2;

    /// <summary>The bits this engine understands. Everything else is masked off.</summary>
    public const uint DefinedMask = ReadOnly | HideInEditor | RequiresRestart;

    /// <summary>
    /// Drops every bit this engine does not assign meaning to. Applied where a
    /// descriptor is READ, never where one is written, because a writer that
    /// never sets a reserved bit and a reader that never trusts one are two
    /// different guarantees.
    /// </summary>
    public static uint Mask(uint flags) => flags & DefinedMask;
}

/// <summary>
/// One editable property of an entity class: its name, how it is presented, and
/// the bounds an editor enforces.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Default"/> is a STRING, never a typed value.</b> Keyvalues are
/// string-typed on the wire, so a typed default would turn the editor's "is this
/// still the default?" comparison into a conversion round trip that drifts
/// silently: two values that format to the same text but compare unequal, or the
/// reverse. Compare the text, and convert exactly once, at bind time.
/// </para>
/// <para>
/// <b><see cref="Min"/> and <see cref="Max"/> use NaN for unbounded</b>, which
/// means they are compared with <see cref="float.IsNaN(float)"/> and never with
/// <c>==</c>: NaN is unequal to itself, so an equality test reports every bound
/// as present and clamps every value against a NaN, which yields NaN.
/// </para>
/// </remarks>
/// <param name="Name">The wire name, as it appears in a map file.</param>
/// <param name="Display">The label an editor shows, or empty to use the name.</param>
/// <param name="Tooltip">One sentence of help, or empty.</param>
/// <param name="Default">The default VALUE, formatted as it would be written.</param>
/// <param name="Type">What the value means. See <see cref="KeyvalueType"/>.</param>
/// <param name="Widget">A <see cref="KeyvalueWidget"/> value.</param>
/// <param name="Min">Lower bound, or NaN for unbounded.</param>
/// <param name="Max">Upper bound, or NaN for unbounded.</param>
/// <param name="Flags">A <see cref="KeyvalueFlags"/> bit set.</param>
/// <param name="Choices">
/// The permitted values for <see cref="KeyvalueType.Choices"/>, empty otherwise.
/// </param>
public readonly record struct KeyvalueDescriptor(
    string Name,
    string Display,
    string Tooltip,
    string Default,
    KeyvalueType Type,
    byte Widget,
    float Min,
    float Max,
    uint Flags,
    IReadOnlyList<(string Value, string Display)> Choices)
{
    /// <summary>The choice list of every descriptor that declares no choices.</summary>
    public static readonly IReadOnlyList<(string Value, string Display)> NoChoices = [];

    /// <summary>Whether <see cref="Min"/> is a real bound.</summary>
    public bool HasMin => !float.IsNaN(Min);

    /// <summary>Whether <see cref="Max"/> is a real bound.</summary>
    public bool HasMax => !float.IsNaN(Max);

    /// <summary>Whether <see cref="KeyvalueFlags.ReadOnly"/> is set.</summary>
    public bool IsReadOnly => (Flags & KeyvalueFlags.ReadOnly) != 0;

    /// <summary>Whether <see cref="KeyvalueFlags.HideInEditor"/> is set.</summary>
    public bool IsHiddenInEditor => (Flags & KeyvalueFlags.HideInEditor) != 0;

    /// <summary>Whether <see cref="KeyvalueFlags.RequiresRestart"/> is set.</summary>
    public bool RequiresRestart => (Flags & KeyvalueFlags.RequiresRestart) != 0;
}

/// <summary>
/// Everything an editor and a runtime know about one entity CLASS: its name, how
/// it is placed, the properties it exposes, and the inputs and outputs it wires.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is exactly one of these types, and every producer builds it.</b> A
/// source generator emits one per attributed C# class; a Luau definition builds
/// one through a host function; an exported schema file is read back into one.
/// The editor's property panel and its wiring UI consume this and have no other
/// input, which is what makes parity between the producers structural rather
/// than something to keep checking.
/// </para>
/// <para>
/// <b>Describes a class, never an instance.</b> What a placed entity actually
/// carries is <see cref="EntityData"/>, and the two are deliberately not linked
/// by a reference: a map may name a class this build has never heard of, and
/// that map must still round-trip.
/// </para>
/// <para>
/// <b>The lists are owned by the schema and must not be mutated afterwards.</b>
/// They are stored rather than copied because the intended producer is a
/// generator emitting static arrays once at start-up, and a defensive copy per
/// class would allocate a second set of every entity definition in the game to
/// protect against a caller that does not exist.
/// </para>
/// </remarks>
public sealed class EntitySchema
{
    /// <param name="className">The wire name, as a map file spells it.</param>
    /// <param name="displayName">The label an editor shows, or empty to use the class name.</param>
    /// <param name="group">The category an editor files it under, or empty.</param>
    /// <param name="placement">How the class is placed. See <see cref="EntityPlacement"/>.</param>
    /// <param name="origin">Where it was defined. An editor badge; see <see cref="EntityOrigin"/>.</param>
    /// <param name="keyvalues">The properties it exposes, in declaration order.</param>
    /// <param name="inputs">The input names it accepts.</param>
    /// <param name="outputs">The output names it fires.</param>
    /// <exception cref="ArgumentException"><paramref name="className"/> is empty.</exception>
    public EntitySchema(
        string className,
        string displayName = "",
        string group = "",
        EntityPlacement placement = EntityPlacement.Point,
        EntityOrigin origin = EntityOrigin.EngineCSharp,
        IReadOnlyList<KeyvalueDescriptor>? keyvalues = null,
        IReadOnlyList<string>? inputs = null,
        IReadOnlyList<string>? outputs = null)
    {
        // A class with no name cannot be registered, cannot be looked up, and
        // cannot be written to a map: refused here rather than three layers down.
        ArgumentException.ThrowIfNullOrEmpty(className);

        ClassName = className;
        DisplayName = displayName;
        Group = group;
        Placement = placement;
        Origin = origin;
        Keyvalues = keyvalues ?? [];
        Inputs = inputs ?? [];
        Outputs = outputs ?? [];
    }

    /// <summary>The wire name, as a map file spells it.</summary>
    public string ClassName { get; }

    /// <summary>The label an editor shows, or empty to fall back to <see cref="ClassName"/>.</summary>
    public string DisplayName { get; }

    /// <summary>The category an editor files this class under, or empty.</summary>
    public string Group { get; }

    /// <summary>How the class is placed in a level.</summary>
    public EntityPlacement Placement { get; }

    /// <summary>Where the class was defined. A badge; nothing branches on it.</summary>
    public EntityOrigin Origin { get; }

    /// <summary>
    /// The properties the class exposes, in DECLARATION order, which is the order
    /// a panel lays them out and the order an exported schema writes them.
    /// </summary>
    public IReadOnlyList<KeyvalueDescriptor> Keyvalues { get; }

    /// <summary>The input names the class accepts.</summary>
    public IReadOnlyList<string> Inputs { get; }

    /// <summary>The output names the class fires.</summary>
    public IReadOnlyList<string> Outputs { get; }
}
