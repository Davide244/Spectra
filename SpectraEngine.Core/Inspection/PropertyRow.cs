using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Inspection;

/// <summary>
/// Every property the inspector can show, named once.
/// </summary>
/// <remarks>
/// <b>An enum rather than a string key, and that is what keeps this
/// AOT-safe.</b> The obvious property grid reflects over an object and
/// discovers its members, which is exactly the pattern this engine cannot use:
/// trimming removes what only reflection names. Enumerating the properties by
/// hand costs one line each and turns "does this build under NativeAOT" from a
/// question into a non-question.
/// </remarks>
public enum PropertyId
{
    /// <summary>Not a property; the default value of an unset row.</summary>
    None = 0,

    NodeName,
    NodeId,

    Position,
    Rotation,
    Scale,

    BrushKind,
    BrushOperation,
    BrushSize,

    LightKind,
    LightColor,
    LightIntensity,
    LightRange,
    LightEnabled,

    MeshModel,
    MeshSubmesh,
}

/// <summary>How a row is edited.</summary>
/// <remarks>
/// <b>A small closed vocabulary, deliberately.</b> The panel renders one editor
/// per kind, so a component added later gets an editor for free as long as its
/// properties fit these shapes. That is the whole trade: a fixed set of widgets
/// against never hand-writing a panel per component. It is also the same
/// vocabulary an entity property panel will need, which is why it is worth
/// fixing now rather than growing one control at a time.
/// </remarks>
public enum PropertyKind
{
    /// <summary>Shown, never edited: an id, a resolved asset path.</summary>
    ReadOnlyText,

    /// <summary>A single line of text.</summary>
    Text,

    /// <summary>One number.</summary>
    Number,

    /// <summary>Three numbers, labelled x, y and z.</summary>
    Vector3,

    /// <summary>Linear RGB, edited as three numbers.</summary>
    Color,

    /// <summary>A checkbox.</summary>
    Boolean,

    /// <summary>One of a fixed set of names.</summary>
    Choice,
}

/// <summary>
/// Which components of a three-number value a row or an edit refers to.
/// </summary>
/// <remarks>
/// <b>Per component, because that is the bulk edit people actually reach
/// for.</b> "Put all of these on the floor" sets y and must leave x and z
/// alone; a row that could only report "these vectors differ" and only write
/// all three would turn that gesture into a way to stack every selected object
/// at one point.
/// </remarks>
[Flags]
public enum PropertyAxes
{
    None = 0,
    X = 1 << 0,
    Y = 1 << 1,
    Z = 1 << 2,
    All = X | Y | Z,
}

/// <summary>
/// One row of the inspector: what it is, which group it belongs to, and its
/// current value.
/// </summary>
/// <remarks>
/// <para>
/// <b>A value that crosses the thread boundary, like everything else the host
/// publishes.</b> It carries no <c>SceneNode</c>, no <c>Brush</c> and no asset
/// handle, because a UI holding one of those would be holding something the
/// render thread mutates the instant the frame ends.
/// </para>
/// <para>
/// <b>The fields are a union that is not one.</b> Only the field matching
/// <see cref="Kind"/> is meaningful. A real discriminated union would be
/// tidier and would allocate or box; this is a struct of a few words that the
/// inspector fills a dozen of per frame, and the cost of the unused fields is
/// less than the cost of the allocation avoided.
/// </para>
/// </remarks>
public readonly record struct PropertyRow
{
    /// <summary>The section this row is filed under, derived from where the value lives.</summary>
    /// <remarks>
    /// <b>Grouping is automatic rather than authored.</b> A row's group is the
    /// payload it came from, so a node that carries a light grows a Light
    /// section and one that does not simply has no such rows. Hand-laid-out
    /// sections would mean editing the panel every time the engine grows a
    /// component, which is the cost this design exists to avoid.
    /// </remarks>
    public string Group { get; init; }

    /// <summary>The label shown to the left of the editor.</summary>
    public string Name { get; init; }

    /// <summary>Which property this is, for applying an edit back.</summary>
    public PropertyId Id { get; init; }

    /// <summary>Which editor to render.</summary>
    public PropertyKind Kind { get; init; }

    /// <summary>The value, for <see cref="PropertyKind.Text"/>, <see cref="PropertyKind.ReadOnlyText"/> and <see cref="PropertyKind.Choice"/>.</summary>
    public string Text { get; init; }

    /// <summary>The value, for <see cref="PropertyKind.Number"/>.</summary>
    public float Number { get; init; }

    /// <summary>The value, for <see cref="PropertyKind.Vector3"/> and <see cref="PropertyKind.Color"/>.</summary>
    public Vector3 Vector { get; init; }

    /// <summary>The value, for <see cref="PropertyKind.Boolean"/>.</summary>
    public bool Flag { get; init; }

    /// <summary>The options, for <see cref="PropertyKind.Choice"/>.</summary>
    /// <remarks>
    /// Shared static arrays rather than a list built per row: the choices for a
    /// brush kind are the same two strings on every node in the scene, and
    /// allocating them per row per frame would be garbage proportional to the
    /// panel's refresh rate.
    /// </remarks>
    public IReadOnlyList<string> Choices { get; init; }

    /// <summary>
    /// How many of the selected nodes carry this property at all.
    /// </summary>
    /// <remarks>
    /// Less than <see cref="SelectionCount"/> means the property is unique to
    /// part of the selection: a brush field with a light also selected, say.
    /// Such a row is still shown and still editable, and the edit reaches only
    /// the nodes that have it.
    /// </remarks>
    public int PresentCount { get; init; }

    /// <summary>How many nodes the selection held when this row was built.</summary>
    public int SelectionCount { get; init; }

    /// <summary>
    /// Which parts of the value differ across the nodes that carry it.
    /// </summary>
    /// <remarks>
    /// For a three-number value these are the axes that disagree, so a row can
    /// show two settled components and one mixed one. For every other kind it
    /// is <see cref="PropertyAxes.All"/> or nothing, since there is only one
    /// value to disagree about.
    /// </remarks>
    public PropertyAxes MixedAxes { get; init; }

    /// <summary>Whether anything about this value differs across the selection.</summary>
    public bool IsMixed => MixedAxes != PropertyAxes.None;

    /// <summary>Whether only part of the selection carries this property.</summary>
    public bool IsPartial => PresentCount < SelectionCount;

    /// <summary>Whether this row can be edited at all.</summary>
    public bool IsEditable => Kind != PropertyKind.ReadOnlyText;

    /// <summary>
    /// The unit the value is measured in, or empty when it has none.
    /// </summary>
    /// <remarks>
    /// <b>A fact about the value, so it lives with the value.</b> Without it
    /// the panel showed a light's range as "10" and a brush's size as "6 0.2
    /// 6", and the reader had to already know that one is world units and the
    /// other is not a length at all. Deriving it in the shell from the
    /// <see cref="PropertyId"/> would have worked exactly as well until the
    /// second consumer - a console readout, a tooltip, a generated document -
    /// derived it slightly differently.
    /// </remarks>
    public string Unit { get; init; }

    internal static PropertyRow ReadOnly(string group, string name, PropertyId id, string text) =>
        new() { Group = group, Name = name, Id = id, Kind = PropertyKind.ReadOnlyText, Text = text, Unit = "", Choices = [], PresentCount = 1, SelectionCount = 1 };

    internal static PropertyRow OfText(string group, string name, PropertyId id, string text) =>
        new() { Group = group, Name = name, Id = id, Kind = PropertyKind.Text, Text = text, Unit = "", Choices = [], PresentCount = 1, SelectionCount = 1 };

    internal static PropertyRow OfNumber(string group, string name, PropertyId id, float value, string unit = "") =>
        new() { Group = group, Name = name, Id = id, Kind = PropertyKind.Number, Number = value, Unit = unit, Choices = [], PresentCount = 1, SelectionCount = 1 };

    internal static PropertyRow OfVector(string group, string name, PropertyId id, Vector3 value, string unit = "") =>
        new() { Group = group, Name = name, Id = id, Kind = PropertyKind.Vector3, Vector = value, Unit = unit, Choices = [], PresentCount = 1, SelectionCount = 1 };

    internal static PropertyRow OfColor(string group, string name, PropertyId id, Vector3 value) =>
        new() { Group = group, Name = name, Id = id, Kind = PropertyKind.Color, Vector = value, Unit = "", Choices = [], PresentCount = 1, SelectionCount = 1 };

    internal static PropertyRow OfFlag(string group, string name, PropertyId id, bool value) =>
        new() { Group = group, Name = name, Id = id, Kind = PropertyKind.Boolean, Flag = value, Unit = "", Choices = [], PresentCount = 1, SelectionCount = 1 };

    internal static PropertyRow OfChoice(
        string group, string name, PropertyId id, string value, IReadOnlyList<string> choices) =>
        new() { Group = group, Name = name, Id = id, Kind = PropertyKind.Choice, Text = value, Unit = "", Choices = choices, PresentCount = 1, SelectionCount = 1 };
}
