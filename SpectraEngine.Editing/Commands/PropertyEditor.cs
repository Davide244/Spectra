using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Undo;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// One value a property panel is asking to write.
/// </summary>
/// <remarks>
/// <b><see cref="Axes"/> is what makes a bulk edit safe.</b> Editing the y of a
/// mixed position must leave x and z as each node had them; writing the whole
/// vector back would stack the selection at one point. For a scalar, a flag or
/// a choice there is one value to write, and the mask is
/// <see cref="PropertyAxes.All"/>.
/// </remarks>
public readonly record struct PropertyEdit
{
    public required PropertyId Id { get; init; }

    /// <summary>Which components of <see cref="Vector"/> to write.</summary>
    public PropertyAxes Axes { get; init; } = PropertyAxes.All;

    public string Text { get; init; } = string.Empty;
    public float Number { get; init; }
    public Vector3 Vector { get; init; }
    public bool Flag { get; init; }

    public PropertyEdit() { }
}

/// <summary>
/// Applies a property-panel edit to a selection, as one undoable entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>One commit is one history entry, however many nodes it touched.</b> A
/// bulk edit over fifty objects that pushed fifty entries would take fifty
/// Ctrl+Z presses to undo, which is not what the user did. The whole apply runs
/// inside one transaction, exactly as a gizmo drag does.
/// </para>
/// <para>
/// <b>A node whose value already matches records nothing.</b> The panel commits
/// on Enter and on losing focus, so tabbing through fields without changing
/// anything is an ordinary thing to do; without this it would fill the history
/// with entries that undo to themselves. It also means a bulk edit over a
/// selection where only three nodes differed records three commands, not fifty.
/// </para>
/// <para>
/// <b>Typed commands per property, rather than one setter keyed by a
/// string.</b> Every existing command in this assembly captures absolute
/// before/after values of a specific shape, which is what makes undo
/// idempotent; a generic property setter would have to carry a boxed value and
/// re-discover how to write it, and would lose the type checking that stops a
/// rotation being written into a scale.
/// </para>
/// <para>
/// <b>Render thread only</b>, like every other scene mutation.
/// </para>
/// </remarks>
public static class PropertyEditor
{
    /// <summary>
    /// Writes <paramref name="edit"/> to every node in
    /// <paramref name="targets"/> that carries the property.
    /// </summary>
    /// <returns>How many nodes actually changed.</returns>
    public static int Apply(
        UndoStack undo, IReadOnlyList<SceneNode> targets, PropertyEdit edit)
    {
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
            return 0;

        var commands = new List<IEditorCommand>();
        foreach (SceneNode node in targets)
        {
            if (node is null) continue;
            if (Build(node, edit) is { } command)
                commands.Add(command);
        }

        if (commands.Count == 0)
            return 0;

        // The transaction is opened only once there is something to put in it,
        // so a no-op commit leaves the history untouched rather than pushing an
        // empty entry.
        undo.BeginTransaction(NameOf(edit.Id));
        foreach (IEditorCommand command in commands)
            undo.Execute(command);
        undo.CommitTransaction();

        return commands.Count;
    }

    /// <summary>
    /// Builds the command for one node, or null when the node does not carry
    /// the property or already holds the value.
    /// </summary>
    private static IEditorCommand? Build(SceneNode node, PropertyEdit edit) => edit.Id switch
    {
        PropertyId.NodeName => BuildName(node, edit),
        PropertyId.Position or PropertyId.Rotation or PropertyId.Scale => BuildTransform(node, edit),
        PropertyId.BrushKind => BuildBrushKind(node, edit),
        PropertyId.BrushOperation => BuildBrushOperation(node, edit),
        PropertyId.BrushSize => BuildBrushSize(node, edit),
        PropertyId.LightKind or PropertyId.LightColor or PropertyId.LightIntensity
            or PropertyId.LightRange or PropertyId.LightEnabled => BuildLight(node, edit),

        // NodeId, MeshModel and MeshSubmesh are read-only, and PropertyId.None
        // is not a property. Silently ignored rather than thrown: the panel
        // never offers them, so reaching here means a caller built an edit by
        // hand and a throw would be a crash rather than a correction.
        _ => null,
    };

    private static IEditorCommand? BuildName(SceneNode node, PropertyEdit edit)
    {
        string name = edit.Text.Trim();

        // An empty name would leave a row in the tree with nothing to click.
        if (name.Length == 0 || string.Equals(node.Name, name, StringComparison.Ordinal))
            return null;

        return SetNodeNameCommand.Capture(node, name);
    }

    private static IEditorCommand? BuildTransform(SceneNode node, PropertyEdit edit)
    {
        Transform current = node.LocalTransform;
        Transform next = current;

        switch (edit.Id)
        {
            case PropertyId.Position:
                next.Position = Merge(current.Position, edit.Vector, edit.Axes);
                break;

            case PropertyId.Scale:
                next.Scale = Merge(current.Scale, edit.Vector, edit.Axes);
                break;

            case PropertyId.Rotation:
                // Merged in DEGREES, not on the quaternion. Editing the yaw of a
                // mixed selection has to leave each node's own pitch and roll
                // alone, and there is no way to say that to a quaternion: it has
                // no separable components.
                Vector3 currentDegrees = EulerAngles.FromQuaternion(current.Rotation).AsDegrees;
                Vector3 merged = Merge(currentDegrees, edit.Vector, edit.Axes);
                next.Rotation = EulerAngles.FromDegrees(merged).ToQuaternion();
                break;
        }

        // Exact equality, matching the transform setters' own early-out: an
        // absolute value replayed onto itself is free, and recording it would
        // put an entry in the history that undoes to itself.
        if (next.Position == current.Position
            && next.Rotation == current.Rotation
            && next.Scale == current.Scale)
        {
            return null;
        }

        return SetLocalTransformCommand.Capture(node, next);
    }

    private static IEditorCommand? BuildBrushKind(SceneNode node, PropertyEdit edit)
    {
        if (node.Brush is null) return null;

        BrushKind kind = string.Equals(edit.Text, "Part", StringComparison.OrdinalIgnoreCase)
            ? BrushKind.Part
            : BrushKind.World;

        return node.BrushKind == kind ? null : SetBrushKindCommand.Capture(node, kind);
    }

    private static IEditorCommand? BuildBrushOperation(SceneNode node, PropertyEdit edit)
    {
        if (node.Brush is not { } brush) return null;

        BrushOperation operation =
            string.Equals(edit.Text, "Subtractive", StringComparison.OrdinalIgnoreCase)
                ? BrushOperation.Subtractive
                : BrushOperation.Additive;

        if (brush.Operation == operation) return null;

        // WithOperation returns the same instance for an equal write, so the
        // check above is what keeps this from swapping a brush for itself and
        // invalidating its cached carve for nothing.
        return SetBrushCommand.Capture(node, brush.WithOperation(operation));
    }

    private static IEditorCommand? BuildBrushSize(SceneNode node, PropertyEdit edit)
    {
        if (node.Brush is not { } brush) return null;

        Aabb bounds = brush.LocalBounds;
        Vector3 current = bounds.Max - bounds.Min;
        Vector3 target = Merge(current, edit.Vector, edit.Axes);

        // A size is edited as a SIZE and turned into the scale factor the brush
        // wants, rather than the user being asked for a factor. One typed
        // number is then the same world measurement on every object in the
        // selection, whatever each one already measured.
        if (!IsUsable(target.X) || !IsUsable(target.Y) || !IsUsable(target.Z))
            return null;

        // A degenerate axis has no size to scale FROM, so there is no factor
        // that reaches the target. Refused rather than divided by zero.
        if (current.X <= 0f || current.Y <= 0f || current.Z <= 0f)
            return null;

        var factor = new Vector3(target.X / current.X, target.Y / current.Y, target.Z / current.Z);
        if (factor == Vector3.One) return null;

        return SetBrushCommand.Capture(node, brush.WithScaledExtents(factor));
    }

    private static IEditorCommand? BuildLight(SceneNode node, PropertyEdit edit)
    {
        if (node.Light is not { } light) return null;

        SetLightCommand.Settings current = SetLightCommand.Settings.From(light);
        SetLightCommand.Settings next = edit.Id switch
        {
            PropertyId.LightKind => current with
            {
                Kind = string.Equals(edit.Text, "Point", StringComparison.OrdinalIgnoreCase)
                    ? LightKind.Point
                    : LightKind.Directional,
            },
            PropertyId.LightColor => current with { Color = Merge(current.Color, edit.Vector, edit.Axes) },
            PropertyId.LightIntensity => current with { Intensity = edit.Number },
            PropertyId.LightRange => current with { Range = edit.Number },
            PropertyId.LightEnabled => current with { Enabled = edit.Flag },
            _ => current,
        };

        // Light's own setters throw rather than clamp: intensity refuses
        // negatives and NaN, range refuses anything not strictly positive. A
        // command carrying one of those would throw from inside Do, halfway
        // through a transaction, so it is refused here where nothing has been
        // written yet.
        if (next.Intensity < 0f || !float.IsFinite(next.Intensity)) return null;
        if (!IsUsable(next.Range)) return null;
        if (!float.IsFinite(next.Color.X) || !float.IsFinite(next.Color.Y) || !float.IsFinite(next.Color.Z))
            return null;

        return next == current ? null : SetLightCommand.Capture(node, next);
    }

    /// <summary>
    /// Takes each component from <paramref name="edited"/> where the mask says
    /// so, and from <paramref name="current"/> otherwise.
    /// </summary>
    private static Vector3 Merge(Vector3 current, Vector3 edited, PropertyAxes axes) => new(
        axes.HasFlag(PropertyAxes.X) ? edited.X : current.X,
        axes.HasFlag(PropertyAxes.Y) ? edited.Y : current.Y,
        axes.HasFlag(PropertyAxes.Z) ? edited.Z : current.Z);

    private static bool IsUsable(float value) => float.IsFinite(value) && value > 0f;

    private static string NameOf(PropertyId id) => id switch
    {
        PropertyId.NodeName => "Rename",
        PropertyId.Position => "Move",
        PropertyId.Rotation => "Rotate",
        PropertyId.Scale => "Scale",
        PropertyId.BrushKind => "Convert Brush",
        PropertyId.BrushOperation => "Brush Operation",
        PropertyId.BrushSize => "Resize",
        _ => "Light",
    };
}
