using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// A reader of <c>.spack</c> bytes written from the format spec rather than from
/// the engine's own types.
/// </summary>
/// <remarks>
/// <para><b>Deliberately hand-written, and it does not touch <c>PackHeader</c>,
/// <c>PackEntry</c> or <c>PackFormat</c>.</b> There is no reader yet, and a writer
/// verified only by its own reader proves the two agree rather than that either is
/// right: a field swapped in the struct would move in both at once and every test
/// would stay green. Every offset and width below is a literal taken from
/// <c>docs/formats-and-pipeline.md</c> section 2.1, so this file disagreeing with
/// the writer is what a layout regression looks like.</para>
/// <para>It is also why the constants are spelled out rather than imported: an
/// import is how the second opinion stops being one.</para>
/// </remarks>
internal static class HandParsedPack
{
    public const int HeaderSize = 64;
    public const int EntrySize = 48;
    public const int DigestSize = 16;

    public static Header ReadHeader(ReadOnlySpan<byte> pack)
    {
        ReadOnlySpan<byte> h = pack[..HeaderSize];
        return new Header(
            Magic: Encoding.ASCII.GetString(h[..4]),
            FormatVersion: BinaryPrimitives.ReadUInt16LittleEndian(h[0x04..]),
            MinReaderVersion: BinaryPrimitives.ReadUInt16LittleEndian(h[0x06..]),
            Flags: BinaryPrimitives.ReadUInt32LittleEndian(h[0x08..]),
            EntryCount: BinaryPrimitives.ReadUInt32LittleEndian(h[0x0C..]),
            EntryTableOffset: BinaryPrimitives.ReadUInt64LittleEndian(h[0x10..]),
            NameTableOffset: BinaryPrimitives.ReadUInt64LittleEndian(h[0x18..]),
            NameTableLength: BinaryPrimitives.ReadUInt64LittleEndian(h[0x20..]),
            PackSequence: BinaryPrimitives.ReadUInt32LittleEndian(h[0x28..]),
            EngineVersion: BinaryPrimitives.ReadUInt32LittleEndian(h[0x2C..]),
            DataSectionOffset: BinaryPrimitives.ReadUInt64LittleEndian(h[0x30..]),
            TotalFileSize: BinaryPrimitives.ReadUInt64LittleEndian(h[0x38..]));
    }

    public static List<Entry> ReadEntries(ReadOnlySpan<byte> pack, Header header)
    {
        var entries = new List<Entry>((int)header.EntryCount);
        for (uint i = 0; i < header.EntryCount; i++)
        {
            ReadOnlySpan<byte> e = pack.Slice((int)header.EntryTableOffset + ((int)i * EntrySize), EntrySize);
            entries.Add(new Entry(
                AssetId: BinaryPrimitives.ReadUInt128LittleEndian(e[..16]),
                PayloadOffset: BinaryPrimitives.ReadUInt64LittleEndian(e[0x10..]),
                StoredSize: BinaryPrimitives.ReadUInt64LittleEndian(e[0x18..]),
                UncompressedSize: BinaryPrimitives.ReadUInt64LittleEndian(e[0x20..]),
                NameOffset: BinaryPrimitives.ReadUInt32LittleEndian(e[0x28..]),
                NameLength: BinaryPrimitives.ReadUInt16LittleEndian(e[0x2C..]),
                Kind: e[0x2E],
                Codec: e[0x2F]));
        }

        return entries;
    }

    /// <summary>
    /// The name of <paramref name="entry"/>, read through the name table's own
    /// <c>u16</c> length prefix rather than through the entry's copy of it, so a
    /// disagreement between the two is visible.
    /// </summary>
    public static string ReadName(ReadOnlySpan<byte> pack, Header header, Entry entry)
    {
        int record = (int)header.NameTableOffset + (int)entry.NameOffset;
        ushort prefix = BinaryPrimitives.ReadUInt16LittleEndian(pack[record..]);
        return Encoding.UTF8.GetString(pack.Slice(record + sizeof(ushort), prefix));
    }

    /// <summary>The stored (still compressed, if it was) payload bytes.</summary>
    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> pack, Entry entry) =>
        pack.Slice((int)entry.PayloadOffset, (int)entry.StoredSize);

    /// <summary>The trailing digest, exactly as it sits on disk.</summary>
    public static UInt128 StoredDigest(ReadOnlySpan<byte> pack) =>
        BinaryPrimitives.ReadUInt128LittleEndian(pack[^DigestSize..]);

    /// <summary>The region the digest is supposed to cover.</summary>
    public static ReadOnlySpan<byte> DigestedRegion(ReadOnlySpan<byte> pack, Header header) =>
        pack[(int)header.EntryTableOffset..^DigestSize];

    internal sealed record Header(
        string Magic,
        ushort FormatVersion,
        ushort MinReaderVersion,
        uint Flags,
        uint EntryCount,
        ulong EntryTableOffset,
        ulong NameTableOffset,
        ulong NameTableLength,
        uint PackSequence,
        uint EngineVersion,
        ulong DataSectionOffset,
        ulong TotalFileSize);

    internal sealed record Entry(
        UInt128 AssetId,
        ulong PayloadOffset,
        ulong StoredSize,
        ulong UncompressedSize,
        uint NameOffset,
        ushort NameLength,
        byte Kind,
        byte Codec);
}
