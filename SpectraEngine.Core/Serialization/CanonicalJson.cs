using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SpectraEngine.Core.Serialization;

/// <summary>
/// The house rules for every authored text document the engine writes: maps,
/// projects, and whatever comes next.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists as one implementation because the settings in it fail
/// silently.</b> Two of them in particular produce valid, correct-looking JSON
/// and quietly break the one promise these formats are for. A second copy of
/// them is not a duplication smell, it is a defect waiting for someone to fix
/// one and not the other.
/// </para>
/// <para>
/// <b><c>NewLine</c> defaults to <see cref="Environment.NewLine"/>.</b> A
/// writer that leaves it alone emits CRLF on Windows and LF everywhere else, so
/// byte identity holds only within one operating system and a team gets a
/// whole-file diff every time the file crosses platforms.
/// </para>
/// <para>
/// <b>The default <see cref="JavaScriptEncoder"/> escapes <c>+ &lt; &gt; &amp;</c>
/// and every non-ASCII character</b> to <c>\uXXXX</c>, which would turn every
/// inline script and every non-ASCII name into unmergeable noise in files whose
/// entire purpose is being read and merged by people.
/// </para>
/// <para>
/// <b>And <see cref="Utf8JsonWriter.WriteRawValue(ReadOnlySpan{byte}, bool)"/>
/// does not indent raw content at all</b>, which is what makes preserved
/// members round-trip and what makes an array of raw records need its own line
/// layout.
/// </para>
/// </remarks>
public static class CanonicalJson
{
    /// <summary>Spaces per indent level. Fixed, because it is part of the bytes.</summary>
    public const int IndentSize = 2;

    public static JsonWriterOptions WriterOptions => new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = IndentSize,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    /// <summary>
    /// Reader settings. Comments are disallowed and trailing commas refused,
    /// because both would be dropped on the next save and a round trip that
    /// silently deletes a reviewer's comment is worse than one that refuses to
    /// load.
    /// </summary>
    public static JsonReaderOptions ReaderOptions => new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>Renders a document to canonical UTF-8 bytes, with no BOM and a trailing newline.</summary>
    /// <remarks>
    /// The trailing newline is not cosmetic: without one, every diff that
    /// touches the last line reports "\ No newline at end of file", and any
    /// editor that adds one on save puts a spurious change into the next commit.
    /// </remarks>
    public static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            write(writer);

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Renders one record on a single line, through a second un-indented
    /// writer.
    /// </summary>
    /// <remarks>
    /// <b>Never by string concatenation.</b> Going through the library means
    /// escaping and float formatting stay its problem, so the compact path and
    /// the indented path cannot disagree about how a number is spelled or a
    /// string escaped.
    /// </remarks>
    public static byte[] Compact(Action<Utf8JsonWriter> write)
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

    /// <summary>Writes an array of already-compacted records, one per line.</summary>
    /// <remarks>
    /// One record per line is a merge decision: each record is the unit a person
    /// edits, so a change to one should be a one-line diff. The array's own
    /// layout is built here because <c>WriteRawValue</c> will not indent it, and
    /// the whitespace is the only thing written by hand.
    /// </remarks>
    public static void WriteRecordArray(Utf8JsonWriter writer, string member, List<byte[]> records)
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

    /// <summary>
    /// Emits every preserved member anchored to <paramref name="anchor"/>, the
    /// index of the canonical member just written (-1 before the first).
    /// </summary>
    public static void Flush(Utf8JsonWriter writer, List<PreservedMember> unknown, int anchor)
    {
        for (int i = 0; i < unknown.Count; i++)
        {
            if (unknown[i].Anchor != anchor) continue;
            writer.WritePropertyName(unknown[i].Name);
            writer.WriteRawValue(unknown[i].Raw);
        }
    }

    /// <summary>
    /// Captures the current member's value exactly as it appears in the source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader must be positioned on the property NAME, and
    /// <paramref name="utf8"/> must be the whole document as one contiguous
    /// span: <see cref="Utf8JsonReader.TokenStartIndex"/> and
    /// <see cref="Utf8JsonReader.BytesConsumed"/> are relative to the reader's
    /// own input, so a multi-segment sequence or a chunked read would slice the
    /// wrong bytes silently rather than fail.
    /// </para>
    /// <para>
    /// The name is taken by the caller and the span starts at the VALUE. The
    /// obvious alternative, capturing from the property name, produces a span
    /// that already contains the name and then emits it twice when replayed
    /// through <c>WritePropertyName</c> plus <c>WriteRawValue</c>.
    /// </para>
    /// </remarks>
    public static byte[] CaptureValue(ref Utf8JsonReader reader, ReadOnlySpan<byte> utf8)
    {
        reader.Read();
        long start = reader.TokenStartIndex;
        reader.Skip();
        return utf8[(int)start..(int)reader.BytesConsumed].ToArray();
    }

    /// <summary>Strips a UTF-8 byte order mark, if one is present.</summary>
    /// <remarks>
    /// A file someone saved from an editor that insists on a BOM is still a file
    /// they want to open. The next save writes it back canonically, which is a
    /// one-time diff rather than a refusal.
    /// </remarks>
    public static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> utf8) =>
        utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF ? utf8[3..] : utf8;
}
