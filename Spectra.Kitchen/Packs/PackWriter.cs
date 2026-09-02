using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Spectra.Kitchen.Packs;

/// <summary>
/// Writes a <c>.spack</c> container: the artifact a cooked game ships instead of
/// loose files.
/// </summary>
/// <remarks>
/// <para><b>Writers live in this assembly and only here</b>, so no shipped game
/// binary carries pack-writing code. The engine's side of the format is the
/// format types and the reader, in <c>SpectraEngine.Core.Assets.Packs</c>.</para>
/// <para><b>A game cooks to ONE pack.</b> The priority bands and the tombstone
/// entry kind are in the format anyway, as cheap insurance: saying yes to mods
/// later must not require a format change, and a bit in a flags word costs
/// nothing to reserve now and cannot be reserved retroactively.</para>
/// <para><b>Determinism is the property this class exists to have.</b> Two writes
/// of the same entries produce byte-identical files, which is what makes an
/// incremental cook able to skip work and a patcher able to diff. Three things
/// deliver it and each fails silently if dropped: the entry order comes from the
/// ids rather than from insertion order, so a cooker that walks a directory in
/// whatever order the filesystem returned still writes one file; every alignment
/// gap is explicitly zero-filled, because a reserved byte left holding whatever
/// was in the buffer breaks byte identity in a way that is very hard to bisect;
/// and nothing in the layout depends on a clock, a path on the cooking machine or
/// a hash-set iteration order.</para>
/// <para><b>The one determinism caveat is <see cref="PackCodec.Deflate"/>.</b>
/// <c>DeflateStream</c>'s exact output is not a documented contract, so it is
/// identical for a given input within a runtime version and is not promised to be
/// across one. <see cref="PackCodec.None"/> is the default and is what every
/// cooked binary format should use anyway, because BC blocks and cooked geometry
/// are entropy-dense and compressing them forfeits the zero-copy read the whole
/// container exists for.</para>
/// </remarks>
public sealed class PackWriter
{
    // Insertion order, kept until Write sorts a copy. Sorting in place would make
    // Write mutate the writer, and Write has to be callable twice and answer the
    // same bytes both times.
    private readonly List<PendingEntry> _entries = [];

    private readonly bool _includeNameTable;
    private readonly uint _packSequence;
    private readonly PackFlags _bandFlags;

    /// <summary>
    /// Creates a writer.
    /// </summary>
    /// <param name="packSequence">
    /// Monotonic ordering key among patch packs, so mount order is decided by the
    /// packs rather than by whatever order a directory listing came back in.
    /// </param>
    /// <param name="includeNameTable">
    /// Emit the name table. On by default: it costs roughly 40 bytes an asset and
    /// it is what makes every log line, inspect row and bug report readable rather
    /// than a list of 128-bit numbers.
    /// </param>
    /// <param name="bandFlags">
    /// <see cref="PackFlags.IsPatchPack"/> or <see cref="PackFlags.IsModPack"/>,
    /// or none for a base pack. The other flags describe what the writer did and
    /// are therefore the writer's to set.
    /// </param>
    public PackWriter(
        uint packSequence = 0,
        bool includeNameTable = true,
        PackFlags bandFlags = PackFlags.None)
    {
        const PackFlags Allowed = PackFlags.IsPatchPack | PackFlags.IsModPack;
        if ((bandFlags & ~Allowed) != 0)
        {
            throw new ArgumentException(
                $"Only {nameof(PackFlags.IsPatchPack)} and {nameof(PackFlags.IsModPack)} may be set by a caller; " +
                $"'{bandFlags}' also names a flag the writer derives from what it wrote.", nameof(bandFlags));
        }

        if (bandFlags == Allowed)
        {
            throw new ArgumentException(
                "A pack is a patch or a mod, not both: the two name different mount bands.", nameof(bandFlags));
        }

        _packSequence = packSequence;
        _includeNameTable = includeNameTable;
        _bandFlags = bandFlags;
    }

    /// <summary>Number of entries added so far, tombstones included.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Adds one asset. <paramref name="contentPath"/> is normalised through
    /// <see cref="ContentRoot.NormalizeRelativePath"/> and becomes both the entry's
    /// name and, hashed, its id.
    /// </summary>
    /// <remarks>
    /// The payload is copied, and it is compressed here rather than at write time
    /// so that <see cref="Write"/> is pure layout and calling it twice cannot do
    /// the work twice or differently.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The path is empty, rooted or escapes the content root, or the kind is
    /// <see cref="PackEntryKind.Tombstone"/> (which has no payload, so it has its
    /// own method).
    /// </exception>
    public void Add(
        string contentPath,
        PackEntryKind kind,
        ReadOnlySpan<byte> payload,
        PackCodec codec = PackCodec.None)
    {
        if (kind == PackEntryKind.Tombstone)
        {
            throw new ArgumentException(
                $"A tombstone carries no payload; use {nameof(AddTombstone)}.", nameof(kind));
        }

        string normalized = ContentRoot.NormalizeRelativePath(contentPath);
        byte[] stored = Compress(payload, codec, normalized);

        _entries.Add(new PendingEntry(
            normalized,
            PackAssetId.FromNormalized(normalized),
            kind,
            codec,
            stored,
            (ulong)payload.Length));
    }

    /// <summary>
    /// Adds a deletion: an entry that says the path it names does not exist, so a
    /// higher-priority pack can remove content a lower-priority one shipped.
    /// </summary>
    public void AddTombstone(string contentPath)
    {
        string normalized = ContentRoot.NormalizeRelativePath(contentPath);

        _entries.Add(new PendingEntry(
            normalized,
            PackAssetId.FromNormalized(normalized),
            PackEntryKind.Tombstone,
            PackCodec.None,
            [],
            0));
    }

    /// <summary>Writes the pack to <paramref name="path"/>.</summary>
    public void WriteToFile(string path)
    {
        using FileStream stream = File.Create(path);
        Write(stream);
    }

    /// <summary>
    /// Writes the pack. Leaves the writer unchanged, so a second call produces
    /// byte-identical output.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The machine is big-endian.</exception>
    /// <exception cref="InvalidOperationException">
    /// Two entries share an asset id, or the pack exceeds a field's range.
    /// </exception>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PackFormat.RequireLittleEndian();

        PendingEntry[] sorted = SortAndCheckForCollisions();
        byte[] nameTable = _includeNameTable ? BuildNameTable(sorted) : [];

        // Layout, computed whole before a byte is written: the payload offsets go
        // in the entry table, which is written before the payloads themselves.
        long entryTableOffset = PackFormat.HeaderSize;
        long entryTableEnd = entryTableOffset + ((long)sorted.Length * PackFormat.EntrySize);

        bool hasNameTable = nameTable.Length > 0;
        long nameTableOffset = hasNameTable ? entryTableEnd : 0;
        long tablesEnd = hasNameTable ? nameTableOffset + nameTable.Length : entryTableEnd;

        long dataSectionOffset = PackFormat.AlignUp(tablesEnd, PackFormat.DataSectionAlignment);

        var payloadOffsets = new long[sorted.Length];
        long cursor = dataSectionOffset;
        for (int i = 0; i < sorted.Length; i++)
        {
            payloadOffsets[i] = cursor;
            cursor = PackFormat.AlignUp(cursor + sorted[i].Stored.Length, PackFormat.PayloadAlignment);
        }

        long totalFileSize = cursor + PackFormat.DigestSize;

        PackFlags flags = PackFlags.EntriesSortedByAssetId | _bandFlags;
        if (hasNameTable) flags |= PackFlags.NameTablePresent;

        var header = new PackHeader(
            PackFormat.Magic,
            EngineInfo.PackFormatVersion,
            EngineInfo.MinimumReadablePackVersion,
            flags,
            (uint)sorted.Length,
            (ulong)entryTableOffset,
            (ulong)nameTableOffset,
            (ulong)nameTable.Length,
            _packSequence,
            EngineVersionWord,
            (ulong)dataSectionOffset,
            (ulong)totalFileSize);

        // The header sits outside the digest: the digest lives inside the header's
        // own TotalFileSize accounting, so covering it would make the value depend
        // on itself.
        Span<byte> headerBytes = stackalloc byte[PackFormat.HeaderSize];
        MemoryMarshal.Write(headerBytes, in header);
        stream.Write(headerBytes);

        var region = new RegionWriter(stream, entryTableOffset);

        Span<byte> entryBytes = stackalloc byte[PackFormat.EntrySize];
        uint nameCursor = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            PendingEntry pending = sorted[i];
            uint nameOffset = PackFormat.NameOffsetAbsent;
            ushort nameLength = 0;

            if (hasNameTable)
            {
                nameOffset = nameCursor;
                nameLength = (ushort)Encoding.UTF8.GetByteCount(pending.Path);

                // Cannot overflow: BuildNameTable measured the same records first
                // and refused a table that would not fit a 32-bit offset.
                nameCursor += (uint)(sizeof(ushort) + nameLength);
            }

            var entry = new PackEntry(
                pending.AssetId,
                (ulong)payloadOffsets[i],
                (ulong)pending.Stored.Length,
                pending.UncompressedSize,
                nameOffset,
                nameLength,
                pending.Kind,
                pending.Codec);

            MemoryMarshal.Write(entryBytes, in entry);
            region.Write(entryBytes);
        }

        if (hasNameTable) region.Write(nameTable);

        // Explicitly zero-filled rather than seeked over: a Stream seek past the
        // end leaves the gap holding whatever the filesystem gives back, which on
        // most filesystems is zeros and on none of them is a promise. It also has
        // to be written rather than skipped because the digest covers it.
        region.WriteZeros(dataSectionOffset - region.Position);

        for (int i = 0; i < sorted.Length; i++)
        {
            if (region.Position != payloadOffsets[i])
            {
                throw new InvalidOperationException(
                    $"Pack layout disagrees with what was written: entry {i} was placed at " +
                    $"{payloadOffsets[i]} but the writer is at {region.Position}.");
            }

            region.Write(sorted[i].Stored);
            region.WriteZeros(PackFormat.AlignUp(region.Position, PackFormat.PayloadAlignment) - region.Position);
        }

        if (region.Position + PackFormat.DigestSize != totalFileSize)
        {
            throw new InvalidOperationException(
                $"Pack layout disagrees with what was written: the header declares {totalFileSize} bytes " +
                $"but the writer ended at {region.Position + PackFormat.DigestSize}.");
        }

        Span<byte> digest = stackalloc byte[PackFormat.DigestSize];
        PackDigest.Write(digest, region.Digest());
        stream.Write(digest);
    }

    private static uint EngineVersionWord =>
        ((uint)EngineInfo.MajorVersion << 20) | ((uint)EngineInfo.MinorVersion << 10) | EngineInfo.RevisionVersion;

    // The sort is by id alone, so it is a total order with no ties (a tie is a
    // collision, which is fatal below) and List.Sort's instability cannot reach
    // the output. That is what makes the file a function of the entry SET rather
    // than of the order a cooker happened to walk a directory in.
    private PendingEntry[] SortAndCheckForCollisions()
    {
        PendingEntry[] sorted = [.. _entries];
        Array.Sort(sorted, static (a, b) => a.AssetId.CompareTo(b.AssetId));

        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].AssetId != sorted[i - 1].AssetId) continue;

            string first = sorted[i - 1].Path;
            string second = sorted[i].Path;

            // The two cases have different fixes, and a message that named only
            // the id would leave the cooker's user with nothing to act on. The
            // second is reachable in practice rather than only in theory: pack
            // identity is case-insensitive, so two spellings of one asset land
            // here rather than each getting an entry the other's spelling misses.
            throw new InvalidOperationException(string.Equals(first, second, StringComparison.Ordinal)
                ? $"Asset '{first}' was added to the pack twice (id {sorted[i].AssetId:X32})."
                : $"Asset id collision: '{first}' and '{second}' both resolve to id {sorted[i].AssetId:X32}. " +
                  "Pack identity is case-insensitive, matching the engine's asset caches, so two paths " +
                  "differing only in case are one asset; rename one of them.");
        }

        return sorted;
    }

    // Records in entry-table order, each a u16 UTF-8 byte count followed by the
    // bytes with no terminator. An entry's NameOffset addresses the count, so the
    // table can also be walked end to end without the entry table.
    private static byte[] BuildNameTable(PendingEntry[] sorted)
    {
        if (sorted.Length == 0) return [];

        long length = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            int nameBytes = Encoding.UTF8.GetByteCount(sorted[i].Path);
            if (nameBytes > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Asset path '{sorted[i].Path}' is {nameBytes} UTF-8 bytes, past the {ushort.MaxValue} " +
                    "an entry's name length can hold.");
            }

            length += sizeof(ushort) + nameBytes;
        }

        // NameOffsetAbsent is the largest u32, so it is not available as an offset.
        if (length > PackFormat.NameOffsetAbsent - 1)
        {
            throw new InvalidOperationException(
                $"The name table would be {length} bytes, past what a 32-bit name offset can address.");
        }

        var table = new byte[length];
        Span<byte> remaining = table;
        for (int i = 0; i < sorted.Length; i++)
        {
            int written = Encoding.UTF8.GetBytes(sorted[i].Path, remaining[sizeof(ushort)..]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(remaining, (ushort)written);
            remaining = remaining[(sizeof(ushort) + written)..];
        }

        return table;
    }

    private static byte[] Compress(ReadOnlySpan<byte> payload, PackCodec codec, string path)
    {
        switch (codec)
        {
            case PackCodec.None:
                return payload.ToArray();

            case PackCodec.Deflate:
            {
                var buffer = new MemoryStream();
                using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(payload);
                }

                // Deliberately no "compressed larger than the source, store it raw"
                // fallback: the caller chose the codec, and a writer that silently
                // wrote a different one would make the entry table disagree with
                // what was asked for with nothing reporting it.
                return buffer.ToArray();
            }

            case PackCodec.Zstandard:
                throw new NotSupportedException(
                    $"Codec {codec} is reserved and not implemented: Zstandard ships in-box in .NET 11 and this " +
                    $"solution targets .NET 10. Entry '{path}' must use None or Deflate.");

            default:
                throw new ArgumentOutOfRangeException(nameof(codec), codec, $"Unknown pack codec for entry '{path}'.");
        }
    }

    private readonly record struct PendingEntry(
        string Path,
        UInt128 AssetId,
        PackEntryKind Kind,
        PackCodec Codec,
        byte[] Stored,
        ulong UncompressedSize);

    // Everything past the header, written to the stream and fed to the digest in
    // one place: a second call site that wrote without hashing would produce a
    // file that fails its own verification, which reads as corruption.
    private sealed class RegionWriter(Stream stream, long startOffset)
    {
        private readonly PackDigest.Accumulator _digest = new();

        public long Position { get; private set; } = startOffset;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            stream.Write(bytes);
            _digest.Append(bytes);
            Position += bytes.Length;
        }

        public void WriteZeros(long count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            Span<byte> zeros = stackalloc byte[PackFormat.DataSectionAlignment];
            zeros.Clear();

            while (count > 0)
            {
                int chunk = (int)Math.Min(count, zeros.Length);
                Write(zeros[..chunk]);
                count -= chunk;
            }
        }

        public UInt128 Digest() => _digest.Finish();
    }
}
