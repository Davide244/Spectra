using System;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// Declares that a class is an entity type, and names the class a map file
/// spells to place one.
/// </summary>
/// <remarks>
/// <para>
/// <b>These attributes live in Core so that both producers can reach them.</b> A
/// source generator matches them by METADATA NAME, never by loading this
/// assembly, and a game that references the engine as a library gets them by
/// referencing Core and nothing else. Core must therefore never reference the
/// generator: the dependency runs one way, exactly as it does for the editing
/// assembly, and the attributes are the whole of the shared surface.
/// </para>
/// <para>
/// <b>Nothing reads these at RUNTIME.</b> They are compile-time input to a
/// generator that emits an <see cref="EntitySchema"/> and a registration; reading
/// them back with reflection is what an AOT build removes, so a member the
/// generator does not consume does not exist as far as a published game is
/// concerned.
/// </para>
/// <para>
/// <b>An unset named argument is absent, not defaulted.</b> A generator reads the
/// argument list rather than an instance, so it can tell "the author wrote
/// nothing" from "the author wrote the default" and infer the missing value
/// (a display name from the class name, a <see cref="KeyvalueAttribute.Type"/>
/// from the member's own type). That is why no member here carries a sentinel
/// for "unspecified": the absence IS the sentinel.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SpectraEntityAttribute : Attribute
{
    /// <param name="className">The wire name, as a map file spells it.</param>
    public SpectraEntityAttribute(string className) => ClassName = className;

    /// <summary>The wire name, as a map file spells it.</summary>
    public string ClassName { get; }

    /// <summary>The label an editor shows. Unset means "derive it from the class name".</summary>
    public string Display { get; set; } = "";

    /// <summary>The category an editor files this class under.</summary>
    public string Group { get; set; } = "";

    /// <summary>How instances are placed. See <see cref="EntityPlacement"/>.</summary>
    public EntityPlacement Placement { get; set; } = EntityPlacement.Point;
}

/// <summary>
/// Declares a member as one of an entity's keyvalues, and names it as a map file
/// spells it.
/// </summary>
/// <remarks>
/// <b><see cref="Default"/> is a STRING, like every other default in this
/// vocabulary.</b> It is the text a map would carry, so the editor's "is this
/// still the default?" question stays a string comparison and never becomes a
/// type-conversion round trip that drifts.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class KeyvalueAttribute : Attribute
{
    /// <param name="name">The wire name, as a map file spells it.</param>
    public KeyvalueAttribute(string name) => Name = name;

    /// <summary>The wire name, as a map file spells it.</summary>
    public string Name { get; }

    /// <summary>The label an editor shows. Unset means "derive it from the name".</summary>
    public string Display { get; set; } = "";

    /// <summary>One sentence of help, shown beside the field.</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>The value this keyvalue has when a map does not carry it, as text.</summary>
    public string Default { get; set; } = "";

    /// <summary>
    /// What the value means. Unset means "infer it from the member's own type",
    /// which is what a generator does when this argument is absent.
    /// </summary>
    public KeyvalueType Type { get; set; }

    /// <summary>How an editor should present it. A <see cref="KeyvalueWidget"/> value.</summary>
    public byte Widget { get; set; } = KeyvalueWidget.Auto;

    /// <summary>Lower bound, or NaN (the default) for unbounded.</summary>
    public float Min { get; set; } = float.NaN;

    /// <summary>Upper bound, or NaN (the default) for unbounded.</summary>
    public float Max { get; set; } = float.NaN;
}

/// <summary>
/// Declares a method as an input another entity's output may be wired to.
/// </summary>
/// <remarks>
/// The name is stated rather than taken from the method, because it is content:
/// maps already written name it, so renaming the method must not rename the
/// input and break every map that wires it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class EntityInputAttribute : Attribute
{
    /// <param name="name">The wire name, as a map file spells it.</param>
    public EntityInputAttribute(string name) => Name = name;

    /// <summary>The wire name, as a map file spells it.</summary>
    public string Name { get; }
}

/// <summary>
/// Declares a member as an output other entities may be wired to.
/// </summary>
/// <remarks>
/// <b>No name argument: the member's own name is the output's name.</b> An
/// output is declared and fired in one place, so there is no second spelling for
/// the two to disagree about, and the convention that a member called
/// <c>OnPickedUp</c> is the output <c>OnPickedUp</c> is worth more than the
/// freedom to rename one of them.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class EntityOutputAttribute : Attribute
{
}
