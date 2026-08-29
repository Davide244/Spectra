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
    private static readonly string[] LightKindChoices = ["Directional", "Point"];

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
        into.Add(PropertyRow.OfVector(TransformGroup, "Position", PropertyId.Position, local.Position));
        into.Add(PropertyRow.OfVector(
            TransformGroup, "Rotation", PropertyId.Rotation,
            EulerAngles.FromQuaternion(local.Rotation).AsDegrees));
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
        into.Add(PropertyRow.OfVector(BrushGroup, "Size", PropertyId.BrushSize, bounds.Max - bounds.Min));
    }

    private static void DescribeLight(Light light, List<PropertyRow> into)
    {
        into.Add(PropertyRow.OfChoice(
            LightGroup, "Kind", PropertyId.LightKind,
            light.Kind == LightKind.Point ? "Point" : "Directional", LightKindChoices));

        // Linear RGB, and labelled so, because the number here is not the number
        // in a colour picker: a .spectramat colour directive is authored in sRGB
        // and stored linear, and showing one as though it were the other is how
        // a light ends up mysteriously twice as bright as the material beside it.
        into.Add(PropertyRow.OfColor(LightGroup, "Color (linear)", PropertyId.LightColor, light.Color));
        into.Add(PropertyRow.OfNumber(LightGroup, "Intensity", PropertyId.LightIntensity, light.Intensity));

        // Shown for a directional light too, even though it means nothing there:
        // Light.Range is still stored and still validated on set, so hiding it
        // would hide a value that can still refuse an edit.
        into.Add(PropertyRow.OfNumber(LightGroup, "Range", PropertyId.LightRange, light.Range));
        into.Add(PropertyRow.OfFlag(LightGroup, "Enabled", PropertyId.LightEnabled, light.Enabled));
    }
}
