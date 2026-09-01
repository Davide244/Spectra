using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

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
    public static void Describe(SceneNode node, List<PropertyRow> into)
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
    public static void Describe(IReadOnlyList<SceneNode> nodes, List<PropertyRow> into)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        if (nodes.Count == 0)
            return;

        if (nodes.Count == 1)
        {
            Describe(nodes[0], into);
            return;
        }

        var merged = new SortedDictionary<PropertyId, PropertyRow>();
        var scratch = new List<PropertyRow>();

        foreach (SceneNode node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            Describe(node, scratch);

            foreach (PropertyRow row in scratch)
            {
                if (!merged.TryGetValue(row.Id, out PropertyRow existing))
                {
                    merged[row.Id] = row with { PresentCount = 1, SelectionCount = nodes.Count };
                    continue;
                }

                merged[row.Id] = existing with
                {
                    PresentCount = existing.PresentCount + 1,
                    MixedAxes = existing.MixedAxes | Disagreement(existing, row),
                };
            }
        }

        foreach (PropertyRow row in merged.Values)
            into.Add(row);
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
