using Spectra.Kitchen.Packs;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Serialization;
using System.Text.Json;

namespace Spectra.Kitchen.CLI;

/// <summary>
/// Renders what <c>scook inspect</c> found, for a person and for a script.
/// </summary>
/// <remarks>
/// <para><b>Both forms read the same <see cref="PackContents"/>.</b> Two
/// renderings of one file are allowed to differ in layout and must not differ in
/// content, which they cannot here because neither of them reads the pack.</para>
/// <para><b>Kind and codec are printed by NAME, through a hand-written table.</b>
/// <c>Enum.ToString</c> is reflection over metadata that a trimmed publish
/// removes, so it would print names in every debug run and numbers in the AOT
/// binary this project ships - the same discipline every other closed vocabulary
/// in this tool follows. An unrecognised byte prints as its number rather than
/// being guessed at, because the kinds are append-only and a pack from a newer
/// cooker legitimately carries one this build has no word for.</para>
/// </remarks>
internal static class PackReport
{
    /// <summary>Format version of the JSON document, which is not the pack's.</summary>
    public const int JsonVersion = 1;

    public static void WriteText(PackContents contents, TextWriter output, AnsiStyle s)
    {
        PackHeader header = contents.Header;

        output.WriteLine($"{s.Title}{contents.Path}{s.Reset}");
        output.WriteLine(
            $"  format {s.Value}v{header.FormatVersion}{s.Reset} " +
            $"{s.Dim}(needs a reader implementing v{header.MinReaderVersion}){s.Reset}, " +
            $"engine {s.Value}{header.EngineVersion >> 20}.{(header.EngineVersion >> 10) & 0x3FF}." +
            $"{header.EngineVersion & 0x3FF}{s.Reset}, sequence {s.Value}{header.PackSequence}{s.Reset}");
        output.WriteLine($"  flags {s.Value}{DescribeFlags(header)}{s.Reset}");
        output.WriteLine(
            $"  entry table at {s.Value}{header.EntryTableOffset}{s.Reset}, " +
            $"data section at {s.Value}{header.DataSectionOffset}{s.Reset}, " +
            $"{s.Value}{contents.FileLength}{s.Reset} bytes total");
        output.WriteLine($"  digest {s.Value}{contents.StoredDigest:X32}{s.Reset} {s.Dim}(declared, not checked){s.Reset}");
        output.WriteLine();

        // Widened to this pack's own longest name rather than to a fixed column:
        // content paths are long and a truncated one is a row nobody can act on.
        int nameWidth = "name".Length;
        for (int i = 0; i < contents.Entries.Count; i++)
            nameWidth = Math.Max(nameWidth, contents.NameOf(i).Length);

        output.WriteLine(
            $"  {s.Header}{"id",-32}  {Pad("name", nameWidth)}  {"kind",-10} {"codec",-8} " +
            $"{"stored",12} {"uncompressed",12}{s.Reset}");

        for (int i = 0; i < contents.Entries.Count; i++)
        {
            PackEntry entry = contents.Entries[i];
            string name = contents.NameOf(i);

            output.WriteLine(
                $"  {entry.AssetId:X32}  {Pad(name.Length == 0 ? "-" : name, nameWidth)}  " +
                $"{DescribeKind(entry.EntryKind),-10} {DescribeCodec(entry.EntryCodec),-8} " +
                $"{entry.StoredSize,12} {entry.UncompressedSize,12}");
        }

        output.WriteLine();
        output.WriteLine($"  {s.Value}{contents.Entries.Count}{s.Reset} entries");
    }

    public static byte[] WriteJson(PackContents contents)
    {
        var records = new List<byte[]>(contents.Entries.Count);
        for (int i = 0; i < contents.Entries.Count; i++)
        {
            PackEntry entry = contents.Entries[i];
            string name = contents.NameOf(i);

            records.Add(CanonicalJson.Compact(w =>
            {
                w.WriteStartObject();
                w.WriteString("id", entry.AssetId.ToString("X32"));
                if (name.Length > 0) w.WriteString("name", name);
                w.WriteString("kind", DescribeKind(entry.EntryKind));
                w.WriteString("codec", DescribeCodec(entry.EntryCodec));
                w.WriteNumber("offset", entry.PayloadOffset);
                w.WriteNumber("stored", entry.StoredSize);
                w.WriteNumber("uncompressed", entry.UncompressedSize);
                w.WriteEndObject();
            }));
        }

        return CanonicalJson.Write(writer =>
        {
            PackHeader header = contents.Header;

            writer.WriteStartObject();
            writer.WriteNumber("scookInspect", JsonVersion);

            // The FILE, not the path it was given: a report naming a relative
            // path is a report that means something different in every directory
            // it is read from.
            writer.WriteString("pack", contents.Path.Replace('\\', '/'));

            writer.WritePropertyName("header");
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", header.FormatVersion);
            writer.WriteNumber("minReaderVersion", header.MinReaderVersion);
            writer.WriteString("flags", DescribeFlags(header));
            writer.WriteNumber("packSequence", header.PackSequence);
            writer.WriteNumber("entryTableOffset", header.EntryTableOffset);
            writer.WriteNumber("dataSectionOffset", header.DataSectionOffset);
            writer.WriteNumber("totalFileSize", header.TotalFileSize);
            writer.WriteString("digest", contents.StoredDigest.ToString("X32"));
            writer.WriteEndObject();

            CanonicalJson.WriteRecordArray(writer, "entries", records);
            writer.WriteEndObject();
        });
    }

    private static string Pad(string value, int width) =>
        value.Length >= width ? value : value + new string(' ', width - value.Length);

    private static string DescribeFlags(PackHeader header)
    {
        var parts = new List<string>(4);
        if (header.EntriesSortedByAssetId) parts.Add("sorted");
        if ((header.PackFlags & PackFlags.NameTablePresent) != 0) parts.Add("names");
        if ((header.PackFlags & PackFlags.IsPatchPack) != 0) parts.Add("patch");
        if ((header.PackFlags & PackFlags.IsModPack) != 0) parts.Add("mod");

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static string DescribeKind(PackEntryKind kind) => kind switch
    {
        PackEntryKind.Raw => "raw",
        PackEntryKind.Image => "image",
        PackEntryKind.Model => "model",
        PackEntryKind.Audio => "audio",
        PackEntryKind.Material => "material",
        PackEntryKind.Shader => "shader",
        PackEntryKind.Script => "script",
        PackEntryKind.Map => "map",
        PackEntryKind.EntityDefs => "entitydefs",
        PackEntryKind.Bundle => "bundle",
        PackEntryKind.Video => "video",
        PackEntryKind.Tombstone => "tombstone",
        _ => ((byte)kind).ToString(),
    };

    private static string DescribeCodec(PackCodec codec) => codec switch
    {
        PackCodec.None => "none",
        PackCodec.Deflate => "deflate",
        PackCodec.Zstandard => "zstd",
        _ => ((byte)codec).ToString(),
    };
}
