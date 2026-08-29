using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
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

        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, MapFormat.WriterOptions))
            WriteDocument(writer, document);

        // A trailing newline, because this is a text file in a git repository
        // before it is anything else. Without one, every diff that touches the
        // last line reports "\ No newline at end of file", and any editor that
        // adds one on save puts a spurious change into the next commit.
        buffer.Write("\n"u8);

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteDocument(Utf8JsonWriter writer, MapDocument document)
    {
        writer.WriteStartObject();

        Flush(writer, document.Unknown, -1);

        writer.WriteNumber(MapFormat.FormatVersionMember, document.FormatVersion);
        Flush(writer, document.Unknown, 0);

        writer.WriteNumber(MapFormat.MinimumReadableMember, document.MinimumReadableVersion);
        Flush(writer, document.Unknown, 1);

        writer.WriteString(MapFormat.EngineMember, document.Engine);
        Flush(writer, document.Unknown, 2);

        writer.WritePropertyName(MapFormat.SceneMember);
        WriteSceneInfo(writer, document.Scene);
        Flush(writer, document.Unknown, 3);

        if (document.Editor is { } editor)
        {
            writer.WritePropertyName(MapFormat.EditorMember);
            writer.WriteRawValue(editor.Raw);
        }
        Flush(writer, document.Unknown, 4);

        writer.WritePropertyName(MapFormat.NodesMember);
        writer.WriteStartArray();
        foreach (MapNode node in document.Nodes)
            WriteNode(writer, node);
        writer.WriteEndArray();
        Flush(writer, document.Unknown, 5);

        writer.WriteEndObject();
    }

    private static void WriteSceneInfo(Utf8JsonWriter writer, MapSceneInfo scene)
    {
        writer.WriteStartObject();
        Flush(writer, scene.Unknown, -1);
        writer.WriteString(MapFormat.NameMember, scene.Name);
        Flush(writer, scene.Unknown, 0);
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, MapNode node)
    {
        writer.WriteStartObject();

        Flush(writer, node.Unknown, -1);

        // Lowercase "D" format, per the format's GUID rule.
        writer.WriteString(MapFormat.IdMember, node.Id.ToString("D"));
        Flush(writer, node.Unknown, 0);

        writer.WriteString(MapFormat.NameMember, node.Name);
        Flush(writer, node.Unknown, 1);

        // Omitted iff the node declares nothing, which is what "inherit" is.
        if (node.Realm is { } realm)
            writer.WriteString(MapFormat.RealmMember, realm);
        Flush(writer, node.Unknown, 2);

        if (node.State is { } state)
            writer.WriteString(MapFormat.StateMember, state);
        Flush(writer, node.Unknown, 3);

        // Omitted iff World. Never a numeric enum: the file is merged by people,
        // and a renumbering would silently re-admit part brushes to the carve.
        if (node.Kind is { } kind && kind != BrushKind.World)
            writer.WriteString(MapFormat.KindMember, MapFormat.ToWire(kind));
        Flush(writer, node.Unknown, 4);

        writer.WritePropertyName(MapFormat.TransformMember);
        writer.WriteRawValue(CompactTransform(node.Transform));
        Flush(writer, node.Unknown, 5);

        if (node.Brush is { } brush)
        {
            writer.WritePropertyName(MapFormat.BrushMember);
            WriteBrush(writer, brush);
        }
        Flush(writer, node.Unknown, 6);

        if (node.Mesh is { } mesh)
        {
            writer.WritePropertyName(MapFormat.MeshMember);
            writer.WriteRawValue(CompactMesh(mesh));
        }
        Flush(writer, node.Unknown, 7);

        if (node.Light is { } light)
        {
            writer.WritePropertyName(MapFormat.LightMember);
            writer.WriteRawValue(CompactLight(light));
        }
        Flush(writer, node.Unknown, 8);

        if (node.Editor is { } editor)
        {
            writer.WritePropertyName(MapFormat.EditorMember);
            writer.WriteRawValue(editor.Raw);
        }
        Flush(writer, node.Unknown, 9);

        writer.WritePropertyName(MapFormat.ChildrenMember);
        writer.WriteStartArray();
        foreach (MapNode child in node.Children)
            WriteNode(writer, child);
        writer.WriteEndArray();
        Flush(writer, node.Unknown, 10);

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
        WriteRecordArray(writer, MapFormat.PlanesMember, planes);

        var faces = new List<byte[]>(brush.Faces.Count);
        foreach (MapFace face in brush.Faces)
            faces.Add(CompactFace(face));
        WriteRecordArray(writer, MapFormat.FacesMember, faces);

        if (brush.KeepSource)
            writer.WriteBoolean(MapFormat.KeepSourceMember, true);

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an array of already-compacted records, one per line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The array's layout is built here because
    /// <see cref="Utf8JsonWriter.WriteRawValue(ReadOnlySpan{byte}, bool)"/> does
    /// not indent raw content at all</b> - which is the same documented
    /// behaviour that makes preserved members round-trip, seen from the other
    /// side. Left to the writer, six planes come out on one line with their
    /// closing bracket alone on the next, which is neither compact nor
    /// readable.
    /// </para>
    /// <para>
    /// <b>One record per line is a merge decision.</b> A plane and a face are
    /// each the unit a person edits, so a resized brush or a retextured face
    /// should be a one-line diff rather than a rewritten block - the same
    /// reasoning that makes a script payload an array of lines instead of one
    /// string with newlines in it. Only whitespace is hand-written; every
    /// record still comes from the library, so escaping and float formatting
    /// are never reimplemented here.
    /// </para>
    /// </remarks>
    private static void WriteRecordArray(Utf8JsonWriter writer, string member, List<byte[]> records)
    {
        writer.WritePropertyName(member);

        if (records.Count == 0)
        {
            writer.WriteRawValue("[]"u8, skipInputValidation: true);
            return;
        }

        // CurrentDepth counts the containers already open, so the records sit
        // one level in from the array's own closing bracket.
        int depth = writer.CurrentDepth;
        string outer = new(' ', depth * IndentSize);
        string inner = new(' ', (depth + 1) * IndentSize);

        var text = new StringBuilder("[\n");
        for (int i = 0; i < records.Count; i++)
        {
            text.Append(inner).Append(Encoding.UTF8.GetString(records[i]));
            text.Append(i == records.Count - 1 ? "\n" : ",\n");
        }
        text.Append(outer).Append(']');

        writer.WriteRawValue(text.ToString());
    }

    private const int IndentSize = 2;

    // --- compact records ----------------------------------------------------

    private static byte[] CompactTransform(MapTransform transform) => Compact(w =>
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

    private static byte[] CompactFace(MapFace face) => Compact(w =>
    {
        w.WriteStartObject();
        Flush(w, face.Unknown, -1);

        // Absent means the engine default material. There is no path to write
        // for it: MaterialRegistry hands out id 0 for "default" and refuses to
        // resolve a path back from it.
        if (!string.IsNullOrEmpty(face.Material))
            w.WriteString(MapFormat.MaterialMember, face.Material);
        Flush(w, face.Unknown, 0);

        // A world-aligned face omits both axes entirely, which is already how
        // FaceSurface encodes it: a zero axis means "derive the projection from
        // the face normal by the dominant-axis rule".
        if (face.UAxis is { } u)
        {
            w.WritePropertyName(MapFormat.UAxisMember);
            WriteNumbers(w, u.X, u.Y, u.Z);
        }
        Flush(w, face.Unknown, 1);

        if (face.VAxis is { } v)
        {
            w.WritePropertyName(MapFormat.VAxisMember);
            WriteNumbers(w, v.X, v.Y, v.Z);
        }
        Flush(w, face.Unknown, 2);

        if (face.UOffset != 0f) WriteFinite(w, MapFormat.UOffsetMember, face.UOffset);
        Flush(w, face.Unknown, 3);

        if (face.VOffset != 0f) WriteFinite(w, MapFormat.VOffsetMember, face.VOffset);
        Flush(w, face.Unknown, 4);

        if (face.UScale != 1f) WriteFinite(w, MapFormat.UScaleMember, face.UScale);
        Flush(w, face.Unknown, 5);

        if (face.VScale != 1f) WriteFinite(w, MapFormat.VScaleMember, face.VScale);
        Flush(w, face.Unknown, 6);

        w.WriteEndObject();
    });

    private static byte[] CompactMesh(MapMeshSource mesh) => Compact(w =>
    {
        w.WriteStartObject();
        Flush(w, mesh.Unknown, -1);

        w.WriteString(MapFormat.ModelMember, mesh.Model);
        Flush(w, mesh.Unknown, 0);

        // Omitted at zero, which is the single-submesh prop: the overwhelmingly
        // common case, and the one where the number carries no information.
        if (mesh.Submesh != 0)
            w.WriteNumber(MapFormat.SubmeshMember, mesh.Submesh);
        Flush(w, mesh.Unknown, 1);

        w.WriteEndObject();
    });

    private static byte[] CompactLight(MapLight light) => Compact(w =>
    {
        w.WriteStartObject();
        Flush(w, light.Unknown, -1);

        if (light.Kind != LightKind.Directional)
            w.WriteString(MapFormat.KindMember, MapFormat.ToWire(light.Kind));
        Flush(w, light.Unknown, 0);

        if (light.Color != Vector3.One)
        {
            w.WritePropertyName(MapFormat.ColorMember);
            WriteNumbers(w, light.Color.X, light.Color.Y, light.Color.Z);
        }
        Flush(w, light.Unknown, 1);

        if (light.Intensity != 1f) WriteFinite(w, MapFormat.IntensityMember, light.Intensity);
        Flush(w, light.Unknown, 2);

        // Never written as zero: Light.Range refuses anything not strictly
        // positive, so a zero on disk throws out of the property setter halfway
        // through a load.
        if (light.Range != 10f) WriteFinite(w, MapFormat.RangeMember, light.Range);
        Flush(w, light.Unknown, 3);

        if (!light.Enabled) w.WriteBoolean(MapFormat.EnabledMember, false);
        Flush(w, light.Unknown, 4);

        w.WriteEndObject();
    });

    private static byte[] CompactMatrix(Matrix4x4 m) => CompactNumbers(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);

    private static byte[] CompactNumbers(params float[] values) =>
        Compact(w => WriteNumbers(w, values));

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

    private static byte[] Compact(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            write(writer);
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Emits every preserved member anchored to <paramref name="anchor"/>,
    /// which is the index of the canonical member just written (-1 before the
    /// first). This is what reproduces the original interleaving rather than
    /// herding unknown members to the end of the object.
    /// </summary>
    private static void Flush(Utf8JsonWriter writer, List<PreservedMember> unknown, int anchor)
    {
        for (int i = 0; i < unknown.Count; i++)
        {
            if (unknown[i].Anchor != anchor) continue;
            writer.WritePropertyName(unknown[i].Name);
            writer.WriteRawValue(unknown[i].Raw);
        }
    }
}
