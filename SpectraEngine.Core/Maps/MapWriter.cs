using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using SpectraEngine.Core.Serialization;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SpectraEngine.Core.Maps;

/// <summary>
/// Writes a <see cref="MapDocument"/> as canonical UTF-8 JSON.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every rule here exists to make a diff small.</b> The file is reviewed,
/// merged and hand-edited, so the writer's job is not merely to produce valid
/// JSON but to produce the <i>same</i> JSON for the same document, on every
/// platform and every run - otherwise a save with no edits in it lands in
/// someone's pull request as a thousand changed lines.
/// </para>
/// <para>
/// <b>Records that are just numbers are written on one line, and not by hand.</b>
/// <see cref="Utf8JsonWriter"/> with <c>Indented</c> breaks every array across
/// lines, which turns a six-plane brush into forty of them and makes a changed
/// plane a multi-line hunk. The fix is to render those records through a
/// <i>second</i>, un-indented writer and emit the result with
/// <see cref="Utf8JsonWriter.WriteRawValue(ReadOnlySpan{byte}, bool)"/> - which
/// is documented not to re-indent or re-encode raw content. Doing it that way
/// rather than concatenating strings means escaping and float formatting stay
/// the library's problem, so the compact path and the indented path cannot
/// disagree about how a number is spelled.
/// </para>
/// </remarks>
public static class MapWriter
{
    /// <summary>Renders <paramref name="document"/> to canonical UTF-8 bytes, with no BOM.</summary>
    public static byte[] Write(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return CanonicalJson.Write(writer => WriteDocument(writer, document));
    }

    private static void WriteDocument(Utf8JsonWriter writer, MapDocument document)
    {
        writer.WriteStartObject();

        CanonicalJson.Flush(writer, document.Unknown, -1);

        writer.WriteNumber(MapFormat.FormatVersionMember, document.FormatVersion);
        CanonicalJson.Flush(writer, document.Unknown, 0);

        writer.WriteNumber(MapFormat.MinimumReadableMember, document.MinimumReadableVersion);
        CanonicalJson.Flush(writer, document.Unknown, 1);

        writer.WriteString(MapFormat.EngineMember, document.Engine);
        CanonicalJson.Flush(writer, document.Unknown, 2);

        writer.WritePropertyName(MapFormat.SceneMember);
        WriteSceneInfo(writer, document.Scene);
        CanonicalJson.Flush(writer, document.Unknown, 3);

        if (document.Editor is { } editor)
        {
            writer.WritePropertyName(MapFormat.EditorMember);
            writer.WriteRawValue(editor.Raw);
        }
        CanonicalJson.Flush(writer, document.Unknown, 4);

        writer.WritePropertyName(MapFormat.NodesMember);
        writer.WriteStartArray();
        foreach (MapNode node in document.Nodes)
            WriteNode(writer, node);
        writer.WriteEndArray();
        CanonicalJson.Flush(writer, document.Unknown, 5);

        writer.WriteEndObject();
    }

    private static void WriteSceneInfo(Utf8JsonWriter writer, MapSceneInfo scene)
    {
        writer.WriteStartObject();
        CanonicalJson.Flush(writer, scene.Unknown, -1);
        writer.WriteString(MapFormat.NameMember, scene.Name);
        CanonicalJson.Flush(writer, scene.Unknown, 0);
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, MapNode node)
    {
        writer.WriteStartObject();

        CanonicalJson.Flush(writer, node.Unknown, -1);

        // Lowercase "D" format, per the format's GUID rule.
        writer.WriteString(MapFormat.IdMember, node.Id.ToString("D"));
        CanonicalJson.Flush(writer, node.Unknown, 0);

        writer.WriteString(MapFormat.NameMember, node.Name);
        CanonicalJson.Flush(writer, node.Unknown, 1);

        // Omitted iff the node declares nothing, which is what "inherit" is.
        if (node.Realm is { } realm)
            writer.WriteString(MapFormat.RealmMember, realm);
        CanonicalJson.Flush(writer, node.Unknown, 2);

        if (node.State is { } state)
            writer.WriteString(MapFormat.StateMember, state);
        CanonicalJson.Flush(writer, node.Unknown, 3);

        // Omitted iff World. Never a numeric enum: the file is merged by people,
        // and a renumbering would silently re-admit part brushes to the carve.
        if (node.Kind is { } kind && kind != BrushKind.World)
            writer.WriteString(MapFormat.KindMember, MapFormat.ToWire(kind));
        CanonicalJson.Flush(writer, node.Unknown, 4);

        writer.WritePropertyName(MapFormat.TransformMember);
        writer.WriteRawValue(CompactTransform(node.Transform));
        CanonicalJson.Flush(writer, node.Unknown, 5);

        if (node.Brush is { } brush)
        {
            writer.WritePropertyName(MapFormat.BrushMember);
            WriteBrush(writer, brush);
        }
        CanonicalJson.Flush(writer, node.Unknown, 6);

        if (node.Mesh is { } mesh)
        {
            writer.WritePropertyName(MapFormat.MeshMember);
            writer.WriteRawValue(CompactMesh(mesh));
        }
        CanonicalJson.Flush(writer, node.Unknown, 7);

        if (node.Light is { } light)
        {
            writer.WritePropertyName(MapFormat.LightMember);
            writer.WriteRawValue(CompactLight(light));
        }
        CanonicalJson.Flush(writer, node.Unknown, 8);

        if (node.Editor is { } editor)
        {
            writer.WritePropertyName(MapFormat.EditorMember);
            writer.WriteRawValue(editor.Raw);
        }
        CanonicalJson.Flush(writer, node.Unknown, 9);

        writer.WritePropertyName(MapFormat.ChildrenMember);
        writer.WriteStartArray();
        foreach (MapNode child in node.Children)
            WriteNode(writer, child);
        writer.WriteEndArray();
        CanonicalJson.Flush(writer, node.Unknown, 10);

        writer.WriteEndObject();
    }

    private static void WriteBrush(Utf8JsonWriter writer, MapBrush brush)
    {
        writer.WriteStartObject();

        // First inside the record, and omitted iff Additive. Position is fixed
        // because this is the member that decides whether the brush adds solid
        // or removes it, and a reader scanning a diff should not have to hunt.
        if (brush.Operation != BrushOperation.Additive)
            writer.WriteString(MapFormat.OperationMember, MapFormat.ToWire(brush.Operation));

        if (brush.Transform != Matrix4x4.Identity)
        {
            writer.WritePropertyName(MapFormat.BrushTransformMember);
            writer.WriteRawValue(CompactMatrix(brush.Transform));
        }

        var planes = new List<byte[]>(brush.Planes.Count);
        foreach (Vector4 plane in brush.Planes)
            planes.Add(CompactNumbers(plane.X, plane.Y, plane.Z, plane.W));
        CanonicalJson.WriteRecordArray(writer, MapFormat.PlanesMember, planes);

        var faces = new List<byte[]>(brush.Faces.Count);
        foreach (MapFace face in brush.Faces)
            faces.Add(CompactFace(face));
        CanonicalJson.WriteRecordArray(writer, MapFormat.FacesMember, faces);

        if (brush.KeepSource)
            writer.WriteBoolean(MapFormat.KeepSourceMember, true);

        writer.WriteEndObject();
    }

    private const int IndentSize = 2;

    // --- compact records ----------------------------------------------------

    private static byte[] CompactTransform(MapTransform transform) => CanonicalJson.Compact(w =>
    {
        w.WriteStartObject();

        // Always written, even at the origin: it is the one member whose
        // absence would read as "this node has no placement" rather than
        // "this node is at zero".
        w.WritePropertyName(MapFormat.PositionMember);
        WriteNumbers(w, transform.Position.X, transform.Position.Y, transform.Position.Z);

        if (transform.Rotation != Quaternion.Identity)
        {
            w.WritePropertyName(MapFormat.RotationMember);
            WriteNumbers(w, transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
        }

        if (transform.Scale != Vector3.One)
        {
            w.WritePropertyName(MapFormat.ScaleMember);
            WriteNumbers(w, transform.Scale.X, transform.Scale.Y, transform.Scale.Z);
        }

        w.WriteEndObject();
    });

    private static byte[] CompactFace(MapFace face) => CanonicalJson.Compact(w =>
    {
        w.WriteStartObject();
        CanonicalJson.Flush(w, face.Unknown, -1);

        // Absent means the engine default material. There is no path to write
        // for it: MaterialRegistry hands out id 0 for "default" and refuses to
        // resolve a path back from it.
        if (!string.IsNullOrEmpty(face.Material))
            w.WriteString(MapFormat.MaterialMember, face.Material);
        CanonicalJson.Flush(w, face.Unknown, 0);

        // A world-aligned face omits both axes entirely, which is already how
        // FaceSurface encodes it: a zero axis means "derive the projection from
        // the face normal by the dominant-axis rule".
        if (face.UAxis is { } u)
        {
            w.WritePropertyName(MapFormat.UAxisMember);
            WriteNumbers(w, u.X, u.Y, u.Z);
        }
        CanonicalJson.Flush(w, face.Unknown, 1);

        if (face.VAxis is { } v)
        {
            w.WritePropertyName(MapFormat.VAxisMember);
            WriteNumbers(w, v.X, v.Y, v.Z);
        }
        CanonicalJson.Flush(w, face.Unknown, 2);

        if (face.UOffset != 0f) WriteFinite(w, MapFormat.UOffsetMember, face.UOffset);
        CanonicalJson.Flush(w, face.Unknown, 3);

        if (face.VOffset != 0f) WriteFinite(w, MapFormat.VOffsetMember, face.VOffset);
        CanonicalJson.Flush(w, face.Unknown, 4);

        if (face.UScale != 1f) WriteFinite(w, MapFormat.UScaleMember, face.UScale);
        CanonicalJson.Flush(w, face.Unknown, 5);

        if (face.VScale != 1f) WriteFinite(w, MapFormat.VScaleMember, face.VScale);
        CanonicalJson.Flush(w, face.Unknown, 6);

        w.WriteEndObject();
    });

    private static byte[] CompactMesh(MapMeshSource mesh) => CanonicalJson.Compact(w =>
    {
        w.WriteStartObject();
        CanonicalJson.Flush(w, mesh.Unknown, -1);

        w.WriteString(MapFormat.ModelMember, mesh.Model);
        CanonicalJson.Flush(w, mesh.Unknown, 0);

        // Omitted at zero, which is the single-submesh prop: the overwhelmingly
        // common case, and the one where the number carries no information.
        if (mesh.Submesh != 0)
            w.WriteNumber(MapFormat.SubmeshMember, mesh.Submesh);
        CanonicalJson.Flush(w, mesh.Unknown, 1);

        w.WriteEndObject();
    });

    private static byte[] CompactLight(MapLight light) => CanonicalJson.Compact(w =>
    {
        w.WriteStartObject();
        CanonicalJson.Flush(w, light.Unknown, -1);

        if (light.Kind != LightKind.Directional)
            w.WriteString(MapFormat.KindMember, MapFormat.ToWire(light.Kind));
        CanonicalJson.Flush(w, light.Unknown, 0);

        if (light.Color != Vector3.One)
        {
            w.WritePropertyName(MapFormat.ColorMember);
            WriteNumbers(w, light.Color.X, light.Color.Y, light.Color.Z);
        }
        CanonicalJson.Flush(w, light.Unknown, 1);

        if (light.Intensity != 1f) WriteFinite(w, MapFormat.IntensityMember, light.Intensity);
        CanonicalJson.Flush(w, light.Unknown, 2);

        // Never written as zero: Light.Range refuses anything not strictly
        // positive, so a zero on disk throws out of the property setter halfway
        // through a load.
        if (light.Range != 10f) WriteFinite(w, MapFormat.RangeMember, light.Range);
        CanonicalJson.Flush(w, light.Unknown, 3);

        if (!light.Enabled) w.WriteBoolean(MapFormat.EnabledMember, false);
        CanonicalJson.Flush(w, light.Unknown, 4);

        // Written only when they differ from the default, like everything above
        // - which is also what makes byte identity hold for every file written
        // before these members existed: a directional light or a point light
        // carries the defaults, so nothing new appears in its object.
        if (light.InnerAngle != 25f) WriteFinite(w, MapFormat.InnerAngleMember, light.InnerAngle);
        CanonicalJson.Flush(w, light.Unknown, 5);

        if (light.OuterAngle != 35f) WriteFinite(w, MapFormat.OuterAngleMember, light.OuterAngle);
        CanonicalJson.Flush(w, light.Unknown, 6);

        if (light.Width != 1f) WriteFinite(w, MapFormat.WidthMember, light.Width);
        CanonicalJson.Flush(w, light.Unknown, 7);

        if (light.Height != 1f) WriteFinite(w, MapFormat.HeightMember, light.Height);
        CanonicalJson.Flush(w, light.Unknown, 8);

        if (light.Radius != 0.5f) WriteFinite(w, MapFormat.RadiusMember, light.Radius);
        CanonicalJson.Flush(w, light.Unknown, 9);

        w.WriteEndObject();
    });

    private static byte[] CompactMatrix(Matrix4x4 m) => CompactNumbers(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);

    private static byte[] CompactNumbers(params float[] values) =>
        CanonicalJson.Compact(w => WriteNumbers(w, values));

    private static void WriteNumbers(Utf8JsonWriter writer, params float[] values)
    {
        writer.WriteStartArray();
        foreach (float value in values)
            writer.WriteNumberValue(Finite(value, "array element"));
        writer.WriteEndArray();
    }

    private static void WriteFinite(Utf8JsonWriter writer, string member, float value) =>
        writer.WriteNumber(member, Finite(value, member));

    /// <summary>
    /// JSON has no NaN and no infinity, so a non-finite float cannot be written
    /// at all. Refusing it here names the member; letting it reach
    /// <see cref="Utf8JsonWriter"/> raises an <see cref="ArgumentException"/>
    /// that says only that some number was not supported.
    /// </summary>
    private static float Finite(float value, string member) =>
        float.IsFinite(value)
            ? value
            : throw new MapFormatException(
                $"Cannot write '{member}': {value} has no JSON representation.", null, 0);
}
