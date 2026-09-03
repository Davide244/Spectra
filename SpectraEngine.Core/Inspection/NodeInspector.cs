using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SpectraEngine.Core.Inspection;

/// <summary>
/// Describes a scene node as a list of editable rows, grouped by the payload
/// each value came from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Render thread only.</b> It reads a live <c>SceneNode</c> and everything
/// hanging off it, so it runs where the graph does and hands back values that
/// carry no reference to any of it.
/// </para>
/// <para>
/// <b>Groups are derived, never authored.</b> A node that carries a light grows
/// a Light section; one that does not simply has no such rows. That is the
/// whole reason the panel does not need editing every time the engine grows a
/// component.
/// </para>
/// <para>
/// <b>Rotation is shown as euler degrees, which is a lossy VIEW of an exact
/// value.</b> The scene stores a quaternion and always will; three degrees are
/// what a person can type. The cost is that the numbers can read back
/// redistributed after an edit near a pole, which is inherent to euler triples
/// rather than to this conversion (see <see cref="EulerAngles"/>).
/// </para>
/// </remarks>
public static class NodeInspector
{
    public const string NodeGroup = "Node";
    public const string TransformGroup = "Transform";
    public const string BrushGroup = "Brush";
    public const string LightGroup = "Light";
    public const string MeshGroup = "Mesh";
    public const string EntityGroup = "Entity";

    /// <summary>
    /// The choice TOKENS of a descriptor's choice list, projected once per
    /// list.
    /// </summary>
    /// <remarks>
    /// <b>A weak table rather than a plain cache, because the key is somebody
    /// else's array.</b> A descriptor declares its choices as (value, display)
    /// pairs and a row needs the values alone, so a projection is unavoidable;
    /// doing it per row per publish is exactly the garbage
    /// <see cref="PropertyRow.Choices"/> already warns about, and doing it into
    /// a static dictionary would keep every schema catalogue a session ever
    /// loaded alive for the process's life. A schema's lists are built once and
    /// documented as never mutated afterwards, so keying on the list's identity
    /// is sound, and the entry dies when the schema does.
    /// </remarks>
    private static readonly ConditionalWeakTable<object, string[]> ChoiceTokenCache = new();

    private static readonly string[] BrushKindChoices = ["World", "Part"];
    private static readonly string[] BrushOperationChoices = ["Additive", "Subtractive"];
    private static readonly string[] LightKindChoices =
        ["Directional", "Point", "Spot", "Rect", "Disc"];

    /// <summary>
    /// Fills <paramref name="into"/> with the node's rows, in group order.
    /// </summary>
    /// <remarks>
    /// The list is cleared and refilled rather than rebuilt, because the caller
    /// does this once per published snapshot and a fresh list per publish is
    /// render-thread garbage for a panel that mostly shows the same rows.
    /// </remarks>
    /// <param name="node">The node to describe.</param>
    /// <param name="into">The list to fill; cleared first.</param>
    /// <param name="schemas">
    /// What the entity classes in this scene DECLARE, or null when nothing
    /// supplied any. A null catalogue is not an error and not an empty panel:
    /// an entity's authored keyvalues are still shown, as text, which is the
    /// same answer an unknown classname gets.
    /// </param>
    public static void Describe(
        SceneNode node, List<PropertyRow> into, EntitySchemaCatalog? schemas = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        into.Add(PropertyRow.OfText(NodeGroup, "Name", PropertyId.NodeName, node.Name));
        into.Add(PropertyRow.ReadOnly(NodeGroup, "Id", PropertyId.NodeId, node.Id.ToString("D")));

        Transform local = node.LocalTransform;
        into.Add(PropertyRow.OfVector(TransformGroup, "Position", PropertyId.Position, local.Position, "su"));
        into.Add(PropertyRow.OfVector(
            TransformGroup, "Rotation", PropertyId.Rotation,
            EulerAngles.FromQuaternion(local.Rotation).AsDegrees, "deg"));
        // A brush node has no editable scale, and offering one was a way to
        // stop the level compiling. Brush placements must stay rigid: the resize
        // tool rebuilds the brush's own extents rather than scaling its node,
        // and the Brush section's Size row is the same measurement in the same
        // units. So the row is simply absent, rather than present and refused.
        if (node.Brush is null)
            into.Add(PropertyRow.OfVector(TransformGroup, "Scale", PropertyId.Scale, local.Scale));

        if (node.Brush is { } brush)
            DescribeBrush(node, brush, into);

        if (node.Light is { } light)
            DescribeLight(light, into);

        if (node.MeshSource is { } mesh)
        {
            into.Add(PropertyRow.ReadOnly(MeshGroup, "Model", PropertyId.MeshModel, mesh.ModelPath));
            into.Add(PropertyRow.ReadOnly(
                MeshGroup, "Submesh", PropertyId.MeshSubmesh,
                mesh.MeshIndex.ToString(CultureInfo.InvariantCulture)));
        }
        else if (node.MeshRenderer is not null)
        {
            // A mesh built in code names no file, and saying so here is the only
            // place a person finds out why that node will not survive a save.
            into.Add(PropertyRow.ReadOnly(MeshGroup, "Model", PropertyId.MeshModel, "(built in code)"));
        }

        if (node.Entity is { } entity)
            DescribeEntity(entity, schemas, into);
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the rows for a whole selection,
    /// merged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The UNION of the selection's properties, not the intersection.</b> A
    /// row carried by only some of the selected nodes is still shown and still
    /// editable, and the edit reaches the nodes that have it. Hiding it would
    /// mean that selecting one extra object silently removed the field somebody
    /// was about to type into.
    /// </para>
    /// <para>
    /// <b>Disagreement is tracked PER AXIS, which is what makes a bulk edit
    /// useful.</b> "Put all of these on the floor" sets y and must leave x and
    /// z alone. A row that could only say "these vectors differ", and only
    /// write all three back, would turn that gesture into a way to stack the
    /// whole selection at one point.
    /// </para>
    /// <para>
    /// <b>The merged order is <see cref="PropertyId"/>'s declaration order,
    /// which is deliberately the display order.</b> Merging in first-seen order
    /// would make the sections depend on which node happened to be selected
    /// first, so a selection of a light and a brush would lay itself out
    /// differently depending on click order, and the panel's group-by-run
    /// assumption would break with it.
    /// </para>
    /// </remarks>
    /// <param name="nodes">The selection.</param>
    /// <param name="into">The list to fill; cleared first.</param>
    /// <param name="schemas">What the entity classes in this scene declare, or null.</param>
    public static void Describe(
        IReadOnlyList<SceneNode> nodes, List<PropertyRow> into, EntitySchemaCatalog? schemas = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        if (nodes.Count == 0)
            return;

        if (nodes.Count == 1)
        {
            Describe(nodes[0], into, schemas);
            return;
        }

        var merged = new SortedDictionary<RowSlot, PropertyRow>();
        var slots = new Dictionary<(PropertyId Id, string Key), RowSlot>();
        var scratch = new List<PropertyRow>();
        int keyedSeen = 0;

        foreach (SceneNode node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            Describe(node, scratch, schemas);

            foreach (PropertyRow row in scratch)
            {
                // Keyed by the PAIR. Merging on the id alone would fold every
                // keyvalue an entity carries into one row, since they all wear
                // PropertyId.EntityKeyvalue - the panel would show one field
                // holding whichever key was described first, and a bulk edit
                // would write it over the rest.
                (PropertyId Id, string Key) identity = (row.Id, row.Key ?? "");

                if (!slots.TryGetValue(identity, out RowSlot slot))
                {
                    // First appearance decides the slot, and the ORDER inside
                    // one id is first-seen rather than alphabetical: for the
                    // ordinary selection - several nodes of one class - that is
                    // the schema's declaration order, which is authored data
                    // and not this panel's to reshuffle. The outer sort stays
                    // PropertyId's declaration order, so the sections still lay
                    // out the same whichever node was clicked first.
                    slot = new RowSlot(row.Id, identity.Key.Length == 0 ? 0 : ++keyedSeen);
                    slots.Add(identity, slot);
                    merged.Add(slot, row with { PresentCount = 1, SelectionCount = nodes.Count });
                    continue;
                }

                PropertyRow existing = merged[slot];
                merged[slot] = existing with
                {
                    PresentCount = existing.PresentCount + 1,
                    MixedAxes = existing.MixedAxes | Disagreement(existing, row),
                };
            }
        }

        foreach (PropertyRow row in merged.Values)
            into.Add(row);
    }

    /// <summary>Where one merged row sits: its property, then its key's turn.</summary>
    /// <remarks>
    /// <b>An ordinal rather than the key string, deliberately.</b> Sorting the
    /// keys themselves would lay a schema's properties out alphabetically, and
    /// a schema author's declaration order is the order they meant. Ordering by
    /// first appearance keeps that order for a homogeneous selection and stays
    /// deterministic for a mixed one, because the selection's own order is.
    /// </remarks>
    private readonly record struct RowSlot(PropertyId Id, int Order) : IComparable<RowSlot>
    {
        public int CompareTo(RowSlot other)
        {
            int byProperty = ((int)Id).CompareTo((int)other.Id);
            return byProperty != 0 ? byProperty : Order.CompareTo(other.Order);
        }
    }

    /// <summary>
    /// Which parts of two rows for the same property disagree.
    /// </summary>
    /// <remarks>
    /// <b>Exact comparison, on purpose.</b> Two positions that differ in the
    /// last ulp really are different positions, and a tolerance here would
    /// report them as settled and then quietly write one of them over the
    /// other on the next bulk edit. The panel is free to round what it DISPLAYS;
    /// what it must not do is round what it compares.
    /// </remarks>
    private static PropertyAxes Disagreement(in PropertyRow a, in PropertyRow b) => a.Kind switch
    {
        PropertyKind.Vector3 or PropertyKind.Color =>
            (a.Vector.X == b.Vector.X ? PropertyAxes.None : PropertyAxes.X)
            | (a.Vector.Y == b.Vector.Y ? PropertyAxes.None : PropertyAxes.Y)
            | (a.Vector.Z == b.Vector.Z ? PropertyAxes.None : PropertyAxes.Z),

        PropertyKind.Number => a.Number == b.Number ? PropertyAxes.None : PropertyAxes.All,
        PropertyKind.Boolean => a.Flag == b.Flag ? PropertyAxes.None : PropertyAxes.All,

        // Text, Choice and ReadOnlyText all compare their string. An id is
        // read-only and always differs across a multi-selection, which is
        // correct and is why the panel renders a mixed read-only row as a
        // placeholder rather than as one node's value.
        _ => string.Equals(a.Text, b.Text, StringComparison.Ordinal)
            ? PropertyAxes.None
            : PropertyAxes.All,
    };

    private static void DescribeBrush(SceneNode node, Brush brush, List<PropertyRow> into)
    {
        // Kind is on the NODE and operation is on the BRUSH, and they are shown
        // in that order because that is the order they are decided in: kind
        // decides whether the brush is admitted to the world at all, operation
        // decides whether it adds solid or removes it.
        into.Add(PropertyRow.OfChoice(
            BrushGroup, "Kind", PropertyId.BrushKind,
            node.BrushKind == BrushKind.Part ? "Part" : "World", BrushKindChoices));

        into.Add(PropertyRow.OfChoice(
            BrushGroup, "Operation", PropertyId.BrushOperation,
            brush.Operation == BrushOperation.Subtractive ? "Subtractive" : "Additive",
            BrushOperationChoices));

        // Size rather than the planes: a plane list is the truth and is not
        // something anybody types. The bounds are what a resize gesture already
        // works in, so the number here and the number the gizmo reports agree.
        Aabb bounds = brush.LocalBounds;
        into.Add(PropertyRow.OfVector(BrushGroup, "Size", PropertyId.BrushSize, bounds.Max - bounds.Min, "su"));
    }

    private static void DescribeLight(Light light, List<PropertyRow> into)
    {
        into.Add(PropertyRow.OfChoice(
            LightGroup, "Kind", PropertyId.LightKind, KindLabel(light.Kind), LightKindChoices));

        // Linear RGB, and labelled so, because the number here is not the number
        // in a colour picker: a .spectramat colour directive is authored in sRGB
        // and stored linear, and showing one as though it were the other is how
        // a light ends up mysteriously twice as bright as the material beside it.
        into.Add(PropertyRow.OfColor(LightGroup, "Color", PropertyId.LightColor, light.Color));
        into.Add(PropertyRow.OfNumber(LightGroup, "Intensity", PropertyId.LightIntensity, light.Intensity));

        // Shown for a directional light too, even though it means nothing there:
        // Light.Range is still stored and still validated on set, so hiding it
        // would hide a value that can still refuse an edit.
        into.Add(PropertyRow.OfNumber(LightGroup, "Range", PropertyId.LightRange, light.Range, "su"));
        into.Add(PropertyRow.OfFlag(LightGroup, "Enabled", PropertyId.LightEnabled, light.Enabled));

        // SHOWN PER KIND, unlike range above, and the difference is deliberate.
        // Range is stored and validated for every light, so hiding it would hide
        // a value that can still refuse an edit; a cone angle on a rect light is
        // stored and read by nothing at all, so a row for it would be a field
        // that accepts a number and changes no pixel - which is worse than an
        // absent row, because it teaches that the panel's fields are decorative.
        switch (light.Kind)
        {
            case LightKind.Spot:
                into.Add(PropertyRow.OfNumber(
                    LightGroup, "Inner angle", PropertyId.LightInnerAngle, light.InnerAngle, "deg"));
                into.Add(PropertyRow.OfNumber(
                    LightGroup, "Outer angle", PropertyId.LightOuterAngle, light.OuterAngle, "deg"));
                break;

            case LightKind.Rect:
                into.Add(PropertyRow.OfNumber(
                    LightGroup, "Width", PropertyId.LightWidth, light.Width, "su"));
                into.Add(PropertyRow.OfNumber(
                    LightGroup, "Height", PropertyId.LightHeight, light.Height, "su"));
                break;

            case LightKind.Disc:
                into.Add(PropertyRow.OfNumber(
                    LightGroup, "Radius", PropertyId.LightRadius, light.Radius, "su"));
                break;
        }
    }

    /// <summary>
    /// Turns an entity payload into rows: the class it names, the properties
    /// its schema declares, and whatever else it is carrying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The schema decides which rows EXIST; the payload decides what they
    /// hold.</b> A key the schema names but the node has not authored shows the
    /// declared default, because that is the value the entity will run with -
    /// showing an empty field there would be a lie about what the level does.
    /// </para>
    /// <para>
    /// <b>Stored keys the schema does not name are shown anyway, as text.</b>
    /// That is the whole reason a map naming a class this build has never heard
    /// of is worth opening: <c>EntityData</c> is strings precisely so such a map
    /// round-trips, and a panel that showed nothing for it would make the data
    /// invisible while it was still in the file. It is also the only view a
    /// placeholder entity has.
    /// </para>
    /// </remarks>
    private static void DescribeEntity(
        EntityData entity, EntitySchemaCatalog? schemas, List<PropertyRow> into)
    {
        // Read-only: a classname is not a property, it is which set of
        // properties there ARE. Retyping it would rewrite the whole section
        // under the reader's cursor and orphan every keyvalue the old class
        // named, so changing a class is a verb of its own rather than a field.
        into.Add(PropertyRow.ReadOnly(
            EntityGroup, "Class", PropertyId.EntityClassname, entity.ClassName));

        int entityStart = into.Count;

        EntitySchema? schema = null;
        schemas?.TryGetSchema(entity.ClassName, out schema);

        if (schema is not null)
        {
            IReadOnlyList<KeyvalueDescriptor> declared = schema.Keyvalues;
            for (int i = 0; i < declared.Count; i++)
            {
                KeyvalueDescriptor descriptor = declared[i];

                // "Bound and carried, never shown" is what the flag says, so it
                // gets no row - and the loop below has to treat it as named all
                // the same, or the key comes straight back as an unknown one
                // and the flag means nothing.
                if (descriptor.IsHiddenInEditor)
                    continue;

                string value = entity.TryGetValue(descriptor.Name, out string stored)
                    ? stored
                    : descriptor.Default;

                into.Add(RowFor(descriptor, value));
            }
        }

        foreach (KeyValuePair<string, string> keyvalue in entity.Keyvalues)
        {
            if (schema is not null && IsDeclared(schema, keyvalue.Key))
                continue;

            // A hand-written file may legally carry the same key twice - the
            // reader preserves both rather than dropping one - and two rows
            // sharing an identity would collide in the merge and in the panel's
            // shape. The first one wins here, matching EntityData.TryGetValue,
            // which is the value the entity will actually bind.
            if (AlreadyListed(into, entityStart, keyvalue.Key))
                continue;

            into.Add(PropertyRow.OfText(
                EntityGroup, keyvalue.Key, PropertyId.EntityKeyvalue, keyvalue.Value, keyvalue.Key));
        }
    }

    private static bool IsDeclared(EntitySchema schema, string key)
    {
        IReadOnlyList<KeyvalueDescriptor> declared = schema.Keyvalues;
        for (int i = 0; i < declared.Count; i++)
        {
            if (string.Equals(declared[i].Name, key, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool AlreadyListed(List<PropertyRow> rows, int from, string key)
    {
        for (int i = from; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Key, key, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The row one declared keyvalue gets: which editor, and the value read out
    /// of its wire string.
    /// </summary>
    /// <remarks>
    /// <b>A value the declared type cannot carry degrades to TEXT rather than
    /// to a zero.</b> A typed row parses the wire string, and a parse that
    /// failed would show 0 or the origin - and then write that back on the next
    /// commit, destroying whatever the author had actually written. Showing the
    /// text as it stands is the only answer that cannot lose it.
    /// </remarks>
    private static PropertyRow RowFor(in KeyvalueDescriptor descriptor, string value)
    {
        string label = descriptor.Display.Length > 0 ? descriptor.Display : descriptor.Name;
        string key = descriptor.Name;

        if (descriptor.IsReadOnly)
            return PropertyRow.ReadOnly(EntityGroup, label, PropertyId.EntityKeyvalue, value, key);

        if (!KeyvalueWire.IsWellFormed(descriptor.Type, value))
            return PropertyRow.OfText(EntityGroup, label, PropertyId.EntityKeyvalue, value, key);

        switch (descriptor.Type)
        {
            case KeyvalueType.Bool:
                KeyvalueWire.TryParseBool(value, out bool flag);
                return PropertyRow.OfFlag(EntityGroup, label, PropertyId.EntityKeyvalue, flag, key);

            case KeyvalueType.Int:
                KeyvalueWire.TryParseInt(value, out int whole);
                return PropertyRow.OfNumber(
                    EntityGroup, label, PropertyId.EntityKeyvalue, whole, "", key);

            case KeyvalueType.Float:
                KeyvalueWire.TryParseFloat(value, out float number);
                return PropertyRow.OfNumber(
                    EntityGroup, label, PropertyId.EntityKeyvalue, number, "", key);

            case KeyvalueType.Vec3:
                KeyvalueWire.TryParseVec3(value, out Vector3 vector);
                return PropertyRow.OfVector(
                    EntityGroup, label, PropertyId.EntityKeyvalue, vector, "", key);

            case KeyvalueType.Angles:
                // The one typed row that carries a unit, and it carries it for
                // the same reason a rotation row does: three bare numbers under
                // a label say nothing about whether they are degrees.
                KeyvalueWire.TryParseAngles(value, out Vector3 degrees);
                return PropertyRow.OfVector(
                    EntityGroup, label, PropertyId.EntityKeyvalue, degrees, "deg", key);

            case KeyvalueType.Color:
                KeyvalueWire.TryParseColor(value, out Vector3 linear);
                return PropertyRow.OfColor(
                    EntityGroup, label, PropertyId.EntityKeyvalue, linear, key);

            case KeyvalueType.Choices:
                // The TOKENS, not the display names: the row's value is the wire
                // string and the panel matches its dropdown by text, so handing
                // it display names would leave every choice unselected and the
                // first edit would write a display name into the map.
                return PropertyRow.OfChoice(
                    EntityGroup, label, PropertyId.EntityKeyvalue, value,
                    ChoiceTokensOf(descriptor.Choices), key);

            // Everything else is text in v1: a targetname, a node reference, an
            // asset path and a flag word all want a widget of their own, and a
            // wrong widget over a right string is worse than a plain field.
            default:
                return PropertyRow.OfText(EntityGroup, label, PropertyId.EntityKeyvalue, value, key);
        }
    }

    private static string[] ChoiceTokensOf(IReadOnlyList<(string Value, string Display)> choices)
    {
        if (choices is null || choices.Count == 0)
            return [];

        return ChoiceTokenCache.GetValue(choices, static list =>
        {
            var declared = (IReadOnlyList<(string Value, string Display)>)list;
            var tokens = new string[declared.Count];
            for (int i = 0; i < tokens.Length; i++)
                tokens[i] = declared[i].Value;

            return tokens;
        });
    }

    // A switch, never a ternary. The label feeds a dropdown whose selected item
    // is matched by TEXT, so a kind with no case would show as "Directional"
    // and silently rewrite itself to that the moment anybody touched the row.
    private static string KindLabel(LightKind kind) => kind switch
    {
        LightKind.Point => "Point",
        LightKind.Spot => "Spot",
        LightKind.Rect => "Rect",
        LightKind.Disc => "Disc",
        _ => "Directional",
    };
}
