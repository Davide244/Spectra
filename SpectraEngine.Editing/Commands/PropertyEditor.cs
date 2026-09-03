using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Undo;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

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

    /// <summary>
    /// Which keyvalue this edit names, for
    /// <see cref="PropertyId.EntityKeyvalue"/>. Empty for every other property.
    /// </summary>
    /// <remarks>
    /// <b>The row's identity is the pair, so an edit's is too.</b> Every
    /// keyvalue row wears one id and is told apart by its key; an edit carrying
    /// the id alone could not say WHICH property of the entity it meant.
    /// </remarks>
    public string Key { get; init; } = string.Empty;

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
    /// <param name="inGesture">
    /// True while the caller is holding a transaction open around a continuous
    /// gesture, so this edit joins it instead of becoming its own history
    /// entry.
    /// </param>
    /// <returns>How many nodes actually changed.</returns>
    /// <remarks>
    /// <b><paramref name="inGesture"/> is what makes a scrubbable field
    /// possible.</b> A drag across a number emits one edit per pointer move,
    /// and each opening its own transaction would put sixty entries in the
    /// history for one gesture - which is not merely untidy: undo would then
    /// walk back through the drag one frame at a time, and the user's "put it
    /// back" would take sixty presses. Inside an open transaction the stack
    /// offers each new command to the ones already recorded and
    /// <see cref="ICoalescingCommand"/> absorbs them, so the whole drag ends as
    /// one command per node whose before/after span the gesture. Commands that
    /// do not coalesce simply accumulate inside the transaction and still
    /// commit as the single entry a transaction always commits as.
    /// </remarks>
    public static int Apply(
        UndoStack undo, IReadOnlyList<SceneNode> targets, PropertyEdit edit, bool inGesture = false)
    {
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
            return 0;

        // The scene's schema catalogue, read once. An entity keyvalue's DEFAULT
        // is what the panel shows for a key nobody has authored, so it is also
        // what "this edit changes nothing" has to be measured against; without
        // it, tabbing through an untouched field would write the declared
        // default into the map as an explicit member.
        EntitySchemaCatalog? schemas = undo.Scene.EntitySchemas;

        var commands = new List<IEditorCommand>();
        foreach (SceneNode node in targets)
        {
            if (node is null) continue;
            if (Build(node, edit, schemas) is { } command)
                commands.Add(command);
        }

        if (commands.Count == 0)
            return 0;

        // The transaction is opened only once there is something to put in it,
        // so a no-op commit leaves the history untouched rather than pushing an
        // empty entry. Inside a gesture the caller owns the transaction, and
        // opening a second one here would throw rather than nest.
        bool ownTransaction = !inGesture;
        if (ownTransaction)
            undo.BeginTransaction(NameOf(edit.Id));

        foreach (IEditorCommand command in commands)
            undo.Execute(command);

        if (ownTransaction)
            undo.CommitTransaction();

        return commands.Count;
    }

    /// <summary>
    /// Builds the command for one node, or null when the node does not carry
    /// the property or already holds the value.
    /// </summary>
    private static IEditorCommand? Build(
        SceneNode node, PropertyEdit edit, EntitySchemaCatalog? schemas) => edit.Id switch
    {
        PropertyId.NodeName => BuildName(node, edit),
        PropertyId.Position or PropertyId.Rotation or PropertyId.Scale => BuildTransform(node, edit),
        PropertyId.BrushKind => BuildBrushKind(node, edit),
        PropertyId.BrushOperation => BuildBrushOperation(node, edit),
        PropertyId.BrushSize => BuildBrushSize(node, edit),
        PropertyId.LightKind or PropertyId.LightColor or PropertyId.LightIntensity
            or PropertyId.LightRange or PropertyId.LightEnabled
            or PropertyId.LightInnerAngle or PropertyId.LightOuterAngle
            or PropertyId.LightWidth or PropertyId.LightHeight
            or PropertyId.LightRadius => BuildLight(node, edit),

        PropertyId.EntityKeyvalue => BuildEntityKeyvalue(node, edit, schemas),

        // NodeId, MeshModel, MeshSubmesh and EntityClassname are read-only, and
        // PropertyId.None is not a property. Silently ignored rather than
        // thrown: the panel never offers them, so reaching here means a caller
        // built an edit by hand and a throw would be a crash rather than a
        // correction.
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
                // A BRUSH placement must stay rigid. A non-rigid world matrix
                // on a brush node makes every later placement snapshot
                // defective, so the static world stops recompiling for the rest
                // of the session while the viewport goes on showing the last
                // good one - a level that silently stops responding to edits,
                // with the reason in a log file. The panel does not offer the
                // row (see NodeInspector), and this refuses it anyway, because
                // the two guards protect against different mistakes: one is a
                // UI that could grow the row back, the other is any caller at
                // all.
                if (node.Brush is not null)
                    return null;

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

    // Matched by NAME against the dropdown's own labels. A kind with no case
    // falls back to Directional, which is the behaviour the two-kind ternary
    // had; what changed is that every kind the panel OFFERS now has a case, and
    // NodeInspector.KindLabel is its other half.
    private static LightKind ParseKind(string? text) => text switch
    {
        "Point" => LightKind.Point,
        "Spot" => LightKind.Spot,
        "Rect" => LightKind.Rect,
        "Disc" => LightKind.Disc,
        _ => LightKind.Directional,
    };

    private static IEditorCommand? BuildLight(SceneNode node, PropertyEdit edit)
    {
        if (node.Light is not { } light) return null;

        SetLightCommand.Settings current = SetLightCommand.Settings.From(light);
        SetLightCommand.Settings next = edit.Id switch
        {
            PropertyId.LightKind => current with { Kind = ParseKind(edit.Text) },
            PropertyId.LightColor => current with { Color = Merge(current.Color, edit.Vector, edit.Axes) },
            PropertyId.LightIntensity => current with { Intensity = edit.Number },
            PropertyId.LightRange => current with { Range = edit.Number },
            PropertyId.LightEnabled => current with { Enabled = edit.Flag },

            // Clamped by Light's own setters when they land, not refused here:
            // an angle has a meaningful ceiling and an extent a meaningful
            // floor, so a value past either means "as far as it goes" rather
            // than an error - unlike range and intensity, which throw.
            PropertyId.LightInnerAngle => current with { InnerAngle = edit.Number },
            PropertyId.LightOuterAngle => current with { OuterAngle = edit.Number },
            PropertyId.LightWidth => current with { Width = edit.Number },
            PropertyId.LightHeight => current with { Height = edit.Number },
            PropertyId.LightRadius => current with { Radius = edit.Number },

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
    /// Writes one keyvalue on a node's entity payload, as text.
    /// </summary>
    /// <remarks>
    /// <b>Text in, text out, and the axis mask is applied to the TOKENS.</b>
    /// Everything else here converts a typed value; a keyvalue's wire form is
    /// its value, so this arm's only real work is the per-axis merge that makes
    /// a bulk edit possible over string storage.
    /// </remarks>
    private static IEditorCommand? BuildEntityKeyvalue(
        SceneNode node, PropertyEdit edit, EntitySchemaCatalog? schemas)
    {
        if (node.Entity is not { } entity) return null;

        // A keyvalue with no name cannot be written to a map or read back out of
        // one, and the command refuses it too. Refused here as well because the
        // two guards protect against different mistakes: one against a UI that
        // built a row without a key, the other against any caller at all.
        if (string.IsNullOrEmpty(edit.Key)) return null;

        // The EFFECTIVE current value: what the panel is showing, which for a
        // key nobody has authored is the schema's declared default. Both uses
        // below want that rather than the stored text - the merge takes its
        // untouched components from it, and the records-nothing check compares
        // against it.
        string effective = entity.TryGetValue(edit.Key, out string stored)
            ? stored
            : DefaultFor(schemas, entity.ClassName, edit.Key);

        string next = edit.Axes == PropertyAxes.All
            ? edit.Text
            : MergeWireAxes(effective, edit.Text, edit.Axes);

        // A commit that produces the value the node already effectively has
        // records nothing, exactly as a rename does for an unchanged name. For
        // an ABSENT key that also keeps the declared default out of the file:
        // the map format writes a member only when it differs from its default,
        // and tabbing through a field the user never touched must not be what
        // makes one appear.
        if (string.Equals(next, effective, StringComparison.Ordinal))
            return null;

        return SetEntityKeyvalueCommand.Capture(node, edit.Key, next);
    }

    /// <summary>
    /// What the class declares for <paramref name="key"/>, or empty when
    /// nothing declares it.
    /// </summary>
    /// <remarks>
    /// A linear scan, because a class carries a handful of properties and a
    /// per-schema index would be a cache to keep in step with a catalogue that
    /// is already immutable.
    /// </remarks>
    private static string DefaultFor(EntitySchemaCatalog? schemas, string className, string key)
    {
        if (schemas is null || !schemas.TryGetSchema(className, out EntitySchema? schema))
            return "";

        IReadOnlyList<KeyvalueDescriptor> declared = schema.Keyvalues;
        for (int i = 0; i < declared.Count; i++)
        {
            if (string.Equals(declared[i].Name, key, StringComparison.Ordinal))
                return declared[i].Default;
        }

        return "";
    }

    /// <summary>
    /// Splices the masked components of <paramref name="edited"/> into
    /// <paramref name="current"/>, leaving every other component's text exactly
    /// as it was.
    /// </summary>
    /// <remarks>
    /// <b>Spliced at the token level, never parsed and reformatted.</b> Reading
    /// "1 2 3" into a vector, merging, and writing it back through
    /// <c>KeyvalueWire</c> produces the same three numbers and not necessarily
    /// the same three TOKENS: an author who wrote "1.0", "+1" or "1e0" would
    /// have it silently rewritten by an edit to a component beside it, and every
    /// commit would then dirty a line of their map file that nobody touched.
    /// Copying the untouched spans verbatim keeps the interior whitespace too,
    /// for the same reason.
    /// <para>
    /// A value that is not three whitespace-separated components has no
    /// per-axis structure to preserve, so the edit is written whole. That
    /// covers both the malformed case and the absent-with-no-default one.
    /// </para>
    /// </remarks>
    private static string MergeWireAxes(string current, string edited, PropertyAxes axes)
    {
        Span<int> currentStart = stackalloc int[3];
        Span<int> currentEnd = stackalloc int[3];
        Span<int> editedStart = stackalloc int[3];
        Span<int> editedEnd = stackalloc int[3];

        if (!TryFindComponents(current, currentStart, currentEnd)
            || !TryFindComponents(edited, editedStart, editedEnd))
        {
            return edited;
        }

        var merged = new StringBuilder(current.Length + edited.Length);
        int copied = 0;
        for (int i = 0; i < 3; i++)
        {
            if (!axes.HasFlag(AxisAt(i)))
                continue;

            merged.Append(current, copied, currentStart[i] - copied);
            merged.Append(edited, editedStart[i], editedEnd[i] - editedStart[i]);
            copied = currentEnd[i];
        }

        merged.Append(current, copied, current.Length - copied);
        return merged.ToString();
    }

    // Exactly three, in both directions: too few is a truncated value and too
    // many is a value of some other type, and either would put the mask on the
    // wrong component. Mirrors KeyvalueWire's own strictness.
    private static bool TryFindComponents(string text, Span<int> starts, Span<int> ends)
    {
        int found = 0;
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                break;

            if (found == 3)
                return false;

            starts[found] = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;
            ends[found] = i;
            found++;
        }

        return found == 3;
    }

    private static PropertyAxes AxisAt(int index) => index switch
    {
        0 => PropertyAxes.X,
        1 => PropertyAxes.Y,
        _ => PropertyAxes.Z,
    };

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
        PropertyId.EntityKeyvalue => "Entity Property",
        _ => "Light",
    };
}
