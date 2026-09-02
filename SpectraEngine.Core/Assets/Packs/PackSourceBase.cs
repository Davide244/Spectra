using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Assets.Sources;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// Everything a <c>.spack</c> reader does that does not depend on where the bytes
/// came from: the mount-time validation, the id lookup, the tombstone rule and
/// the enumeration.
/// </summary>
/// <remarks>
/// <para><b>One mount sequence, not two.</b> <see cref="PackSource"/> reads
/// through a mapped view and <see cref="StreamPackSource"/> through
/// <see cref="System.IO.RandomAccess"/>, and the whole value of the fallback is
/// that it answers the same file the same way. A second copy of the validation
/// would drift the first time one of them was fixed, and the symptom would be a
/// pack one reader refuses and the other serves.</para>
/// <para><b>Mounting throws; every lookup after it degrades.</b> A truncated
/// pack, a bad digest or a pack demanding a newer reader is refused loudly and
/// never becomes a source, because none of its answers could be trusted. After
/// that a miss is a miss and an unreadable entry is a miss with a warning, which
/// is the <see cref="IContentSource"/> contract and the reason the engine's
/// degrade-don't-crash policy does not depend on which source answered.</para>
/// <para><b>Validation is at MOUNT, not at first read.</b> A pack that is going
/// to be refused should be refused while there is still a start-up log to say so
/// in, rather than in the middle of a frame that wanted one texture — and the
/// per-entry bounds pass is exactly what lets a read be a bare slice with no
/// checking of its own.</para>
/// </remarks>
public abstract class PackSourceBase : IContentSource, IMountPathSource, IDisposable
{
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Creates the source over <paramref name="handle"/>, which it owns.</summary>
    /// <remarks>
    /// The derived constructor must call <see cref="Mount"/> once its own storage
    /// fields are set, and must unmount the handle if that throws: a constructor
    /// that threw produced no object for anybody to dispose.
    /// </remarks>
    protected PackSourceBase(ILogger logger, string packPath, int priority, PackHandle handle)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(packPath);
        ArgumentNullException.ThrowIfNull(handle);

        _logger = logger;
        PackPath = packPath;
        Priority = priority;
        Handle = handle;
    }

    /// <summary>Path of the mounted file, as it was given.</summary>
    public string PackPath { get; }

    /// <inheritdoc/>
    public int Priority { get; }

    /// <summary>
    /// The pack's refcounted lifetime. Public because the hazard is: anything
    /// holding a span into this pack has to be able to say so.
    /// </summary>
    public PackHandle Handle { get; }

    /// <summary>The validated header, as it sits on disk.</summary>
    public PackHeader Header { get; private set; }

    /// <summary>Records in the entry table, tombstones included.</summary>
    /// <remarks>
    /// Stored rather than read off <see cref="Entries"/>, which for a mapped pack
    /// is a slice of a view that an unmount may already have taken away.
    /// </remarks>
    public int EntryCount { get; private set; }

    /// <summary>Records that delete the path they name rather than serving it.</summary>
    public int TombstoneCount { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
    public bool IsUnmounted => _disposed;

    /// <summary>The logger every degradation is reported through.</summary>
    protected ILogger Logger => _logger;

    /// <summary>Bytes in the file, which the header is checked against.</summary>
    protected abstract long FileLength { get; }

    /// <summary>The entry table, in place. Empty until <see cref="LoadTables"/> has run.</summary>
    protected abstract ReadOnlySpan<PackEntry> Entries { get; }

    /// <summary>The name table, in place. Empty when the pack carries none.</summary>
    protected abstract ReadOnlySpan<byte> NameTable { get; }

    /// <summary>Copies <paramref name="destination"/>.Length bytes from <paramref name="offset"/>.</summary>
    protected abstract void ReadRaw(long offset, Span<byte> destination);

    /// <summary>
    /// Makes <see cref="Entries"/> and <see cref="NameTable"/> answer: a slice for
    /// a mapped pack, a read for a streamed one. The regions are already known to
    /// be inside the file when this runs.
    /// </summary>
    protected abstract void LoadTables(in PackHeader header);

    /// <summary>The digest of one region, which may be larger than a span can address.</summary>
    protected abstract UInt128 ComputeDigest(long offset, long length);

    /// <summary>Opens one entry's payload, decompressing it when the codec says to.</summary>
    protected abstract bool TryReadPayload(in PackEntry entry, [NotNullWhen(true)] out ContentBlob? blob);

    /// <summary>
    /// Validates the file and makes it answerable. The order is the whole point,
    /// so it is stated once, here:
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>the file is long enough to hold a header and a digest;</item>
    /// <item>the magic is <c>SPAK</c>;</item>
    /// <item>the format version is non-zero and not below the reader floor the
    /// pack itself declares, which is a self-consistency check no writer can
    /// legitimately fail;</item>
    /// <item>that declared floor does not exceed the version this engine
    /// implements, naming both numbers;</item>
    /// <item>the sorted-entries flag is set, because binary-searching an unsorted
    /// table misses entries silently rather than failing;</item>
    /// <item><see cref="PackHeader.TotalFileSize"/> equals the real length, which
    /// is how truncation is caught with no stat call;</item>
    /// <item>every declared region — entry table, name table, data section — lies
    /// inside the file and is aligned as the in-place cast requires;</item>
    /// <item>every entry: ids strictly ascending, payload windows inside the data
    /// region, sizes a blob can address, name records inside the name table;</item>
    /// <item>the trailing content digest matches, which is last because it is the
    /// only check that reads the whole file.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="PackMountException">The pack is refused.</exception>
    protected void Mount()
    {
        PackFormat.RequireLittleEndian();

        long length = FileLength;
        PackFormat.RequireMinimumFileSize(PackPath, length);

        Span<byte> headerBytes = stackalloc byte[PackFormat.HeaderSize];
        ReadRaw(0, headerBytes);
        PackHeader header = MemoryMarshal.Read<PackHeader>(headerBytes);

        ValidateHeader(in header, length);
        ValidateRegions(in header, length);

        LoadTables(in header);
        Header = header;

        TombstoneCount = ValidateEntries(in header, length);
        EntryCount = (int)header.EntryCount;
        ValidateDigest(in header, length);

        _logger.LogInformation(
            "Mounted pack {Pack} [priority {Priority}]: {Entries} entries ({Tombstones} tombstones), " +
            "format v{Format}, {Bytes} bytes, digest verified.",
            PackPath, Priority, EntryCount, TombstoneCount, header.FormatVersion, length);
    }

    /// <inheritdoc/>
    public bool TryOpen(string path, [NotNullWhen(true)] out ContentBlob? blob)
    {
        blob = null;
        if (!TryFindLive(path, out PackEntry entry) || entry.IsTombstone) return false;

        try
        {
            return TryReadPayload(in entry, out blob);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            // Present but unreadable degrades exactly as a miss does, or the
            // engine's fallback would depend on which source answered. This line
            // is the only place the difference between the two is recorded.
            _logger.LogWarning("Could not read content '{Path}' from {Source}: {Message}", path, this, ex.Message);
            blob = null;
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Exists(string path) => TryFindLive(path, out PackEntry entry) && !entry.IsTombstone;

    /// <summary>
    /// Whether the pack carries a deletion for <paramref name="path"/>: the entry
    /// exists and says the path does not.
    /// </summary>
    public bool IsTombstone(string path) => TryFindLive(path, out PackEntry entry) && entry.IsTombstone;

    /// <inheritdoc/>
    /// <remarks>Always false: a pack cannot be watched, so it is simply not watched.</remarks>
    public bool TryGetWatchPath(string path, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        return false;
    }

    /// <inheritdoc/>
    public void TryEnumerate(string prefix, string extension, List<string> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (_disposed) return;

        string? normalizedPrefix = NormalizePrefix(prefix);
        ReadOnlySpan<PackEntry> entries = Entries;
        ReadOnlySpan<byte> names = NameTable;

        for (int i = 0; i < entries.Length; i++)
        {
            ref readonly PackEntry entry = ref entries[i];
            if (entry.IsTombstone || !entry.HasName) continue;

            string name = PackEntryTable.ReadName(names, in entry);
            if (!Matches(name, normalizedPrefix, extension)) continue;

            results.Add(name);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Tombstones are included, because a deletion is a decision this pack makes
    /// about a logical path and the mount stack cannot flatten what it cannot see.
    /// </remarks>
    public void EnumerateMountPaths(List<MountPath> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (_disposed) return;

        ReadOnlySpan<PackEntry> entries = Entries;
        ReadOnlySpan<byte> names = NameTable;

        for (int i = 0; i < entries.Length; i++)
        {
            ref readonly PackEntry entry = ref entries[i];
            if (!entry.HasName) continue;

            results.Add(new MountPath(PackEntryTable.ReadName(names, in entry), entry.IsTombstone));
        }
    }

    /// <summary>
    /// Unmounts the pack. The mapping survives until the last blob holding a
    /// reference is disposed, which may be after this returns.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Handle.RequestUnmount();
    }

    /// <inheritdoc/>
    public override string ToString() => $"pack @ {PackPath}";

    /// <summary>
    /// Inflates <paramref name="stored"/> into <paramref name="destination"/>,
    /// which must be exactly the entry's uncompressed size.
    /// </summary>
    /// <remarks>
    /// The compressed bytes are copied into a pooled array first because
    /// <see cref="DeflateStream"/> reads from a <see cref="Stream"/> and there is
    /// no span-taking deflate decoder in the box. That copy is affordable here and
    /// nowhere else: a compressed entry has already forfeited the zero-copy read
    /// the container exists for, which is why <see cref="PackCodec.None"/> is the
    /// default for everything cooked.
    /// </remarks>
    protected static void Inflate(ReadOnlySpan<byte> stored, Span<byte> destination, string what)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(stored.Length);
        try
        {
            stored.CopyTo(rented);

            using var source = new MemoryStream(rented, 0, stored.Length, writable: false);
            using var deflate = new DeflateStream(source, CompressionMode.Decompress);

            deflate.ReadExactly(destination);

            // An entry that inflates to more than it declared is corruption the
            // digest would normally have caught, and reading only the declared
            // length would quietly hand back a truncated asset.
            if (deflate.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"Entry '{what}' inflates to more than the {destination.Length} bytes it declares.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // A lookup on a disposed source is a miss rather than a throw: an unmount can
    // race a decode that was already queued, and the caller is in the middle of
    // choosing between real content and a fallback either way.
    private bool TryFindLive(string path, out PackEntry entry)
    {
        entry = default;
        if (_disposed || string.IsNullOrEmpty(path)) return false;

        string normalized;
        try
        {
            normalized = ContentRoot.NormalizeRelativePath(path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        ReadOnlySpan<PackEntry> entries = Entries;
        if (!PackEntryTable.TryFind(entries, PackAssetId.FromNormalized(normalized), out int index)) return false;

        entry = entries[index];
        return true;
    }

    private void ValidateHeader(in PackHeader header, long length)
    {
        if (header.Magic != PackFormat.Magic)
        {
            throw new PackMountException(
                $"'{PackPath}' is not a .spack file: its first four bytes are 0x{header.Magic:X8}, " +
                $"not 0x{PackFormat.Magic:X8} ('SPAK').");
        }

        if (header.FormatVersion == 0)
        {
            throw new PackMountException(
                $"'{PackPath}' declares format version 0, which no writer emits.");
        }

        if (header.MinReaderVersion > header.FormatVersion)
        {
            throw new PackMountException(
                $"'{PackPath}' is self-inconsistent: it was written at format version {header.FormatVersion} " +
                $"but demands a reader implementing version {header.MinReaderVersion}.");
        }

        if (header.MinReaderVersion > EngineInfo.PackFormatVersion)
        {
            throw new PackMountException(
                $"'{PackPath}' demands a reader implementing pack format version {header.MinReaderVersion}; " +
                $"this engine implements version {EngineInfo.PackFormatVersion}. Recook the pack, or update.");
        }

        if (!header.EntriesSortedByAssetId)
        {
            throw new PackMountException(
                $"'{PackPath}' does not set {nameof(PackFlags.EntriesSortedByAssetId)}, which v1 requires. " +
                "Binary-searching an unsorted table misses entries silently, which presents as content that " +
                "is intermittently absent rather than as a corrupt file.");
        }

        if (header.TotalFileSize != (ulong)length)
        {
            throw new PackMountException(
                $"'{PackPath}' declares {header.TotalFileSize} bytes and is {length}: " +
                (header.TotalFileSize > (ulong)length ? "the file is truncated." : "the file has trailing bytes."));
        }
    }

    private void ValidateRegions(in PackHeader header, long length)
    {
        long tail = length - PackFormat.DigestSize;

        if (header.EntryTableOffset < PackFormat.HeaderSize || header.EntryTableOffset > (ulong)tail)
        {
            throw new PackMountException(
                $"'{PackPath}' puts its entry table at {header.EntryTableOffset}, outside the file's " +
                $"[{PackFormat.HeaderSize}, {tail}] body.");
        }

        // The table is cast in place, so its start must be as aligned as the ids
        // inside it need to be.
        if (header.EntryTableOffset % PackFormat.PayloadAlignment != 0)
        {
            throw new PackMountException(
                $"'{PackPath}' puts its entry table at {header.EntryTableOffset}, which is not a multiple of " +
                $"{PackFormat.PayloadAlignment}; the table is reinterpreted in place and every asset id must " +
                "stay aligned.");
        }

        // The table is addressed as one span, whose length is an int.
        long tableBytes = (long)header.EntryCount * PackFormat.EntrySize;
        if (tableBytes > int.MaxValue)
        {
            throw new PackMountException(
                $"'{PackPath}' declares {header.EntryCount} entries, whose table is {tableBytes} bytes, past the " +
                $"{int.MaxValue} a single span can address.");
        }

        if ((long)header.EntryTableOffset + tableBytes > tail)
        {
            throw new PackMountException(
                $"'{PackPath}' declares {header.EntryCount} entries ending at " +
                $"{(long)header.EntryTableOffset + tableBytes}, past the {tail} its body holds.");
        }

        if (header.HasNameTable)
        {
            if (header.NameTableLength > int.MaxValue)
            {
                throw new PackMountException(
                    $"'{PackPath}' declares a {header.NameTableLength}-byte name table, past the {int.MaxValue} " +
                    "a single span can address.");
            }

            if (header.NameTableOffset < PackFormat.HeaderSize ||
                header.NameTableOffset + header.NameTableLength > (ulong)tail)
            {
                throw new PackMountException(
                    $"'{PackPath}' puts a {header.NameTableLength}-byte name table at {header.NameTableOffset}, " +
                    $"outside the file's [{PackFormat.HeaderSize}, {tail}] body.");
            }
        }

        if (header.DataSectionOffset > (ulong)tail)
        {
            throw new PackMountException(
                $"'{PackPath}' puts its data section at {header.DataSectionOffset}, past the {tail} its body holds.");
        }

        if (header.DataSectionOffset % PackFormat.PayloadAlignment != 0)
        {
            throw new PackMountException(
                $"'{PackPath}' puts its data section at {header.DataSectionOffset}, which is not a multiple of " +
                $"{PackFormat.PayloadAlignment}; a payload is reinterpreted in place and may not straddle it.");
        }
    }

    private int ValidateEntries(in PackHeader header, long length)
    {
        ReadOnlySpan<PackEntry> entries = Entries;
        ReadOnlySpan<byte> names = NameTable;

        if (entries.Length != (int)header.EntryCount)
        {
            throw new PackMountException(
                $"'{PackPath}' declares {header.EntryCount} entries and its table holds {entries.Length}.");
        }

        ulong tail = (ulong)(length - PackFormat.DigestSize);
        int tombstones = 0;

        for (int i = 0; i < entries.Length; i++)
        {
            ref readonly PackEntry entry = ref entries[i];

            // Strictly ascending, so this checks the sorted flag's claim rather
            // than trusting it, and rejects duplicate ids in the same pass: two
            // entries with one id make a binary search's answer depend on where
            // it happened to land.
            if (i > 0 && entry.AssetId <= entries[i - 1].AssetId)
            {
                throw new PackMountException(
                    $"'{PackPath}' claims a sorted entry table, but entry {i} (id {entry.AssetId:X32}) does not " +
                    $"sit above entry {i - 1} (id {entries[i - 1].AssetId:X32}).");
            }

            if (entry.StoredSize > int.MaxValue || entry.UncompressedSize > int.MaxValue)
            {
                throw new PackMountException(
                    $"'{PackPath}' entry {i} is {entry.UncompressedSize} bytes uncompressed " +
                    $"({entry.StoredSize} stored), past the {int.MaxValue} one content blob can address.");
            }

            if (entry.PayloadOffset < header.DataSectionOffset ||
                entry.PayloadOffset > tail ||
                entry.StoredSize > tail - entry.PayloadOffset)
            {
                throw new PackMountException(
                    $"'{PackPath}' entry {i} claims {entry.StoredSize} bytes at {entry.PayloadOffset}, outside " +
                    $"the data section [{header.DataSectionOffset}, {tail}].");
            }

            if (entry.PayloadOffset % PackFormat.PayloadAlignment != 0)
            {
                throw new PackMountException(
                    $"'{PackPath}' entry {i} starts at {entry.PayloadOffset}, which is not a multiple of " +
                    $"{PackFormat.PayloadAlignment}.");
            }

            if (entry.EntryCodec == PackCodec.None && entry.StoredSize != entry.UncompressedSize)
            {
                throw new PackMountException(
                    $"'{PackPath}' entry {i} is stored uncompressed and declares {entry.StoredSize} stored " +
                    $"against {entry.UncompressedSize} uncompressed.");
            }

            if (entry.IsTombstone)
            {
                tombstones++;
                if (entry.StoredSize != 0)
                {
                    throw new PackMountException(
                        $"'{PackPath}' entry {i} is a tombstone carrying {entry.StoredSize} bytes; a deletion has " +
                        "no payload.");
                }
            }

            ValidateName(in header, names, in entry, i);
        }

        return tombstones;
    }

    private void ValidateName(in PackHeader header, ReadOnlySpan<byte> names, in PackEntry entry, int index)
    {
        if (!entry.HasName)
        {
            if (entry.NameLength != 0)
            {
                throw new PackMountException(
                    $"'{PackPath}' entry {index} has no name record and declares a {entry.NameLength}-byte name.");
            }

            return;
        }

        if (!header.HasNameTable)
        {
            throw new PackMountException(
                $"'{PackPath}' entry {index} points at name offset {entry.NameOffset} in a pack that carries no " +
                "name table.");
        }

        long record = (long)entry.NameOffset;
        long recordEnd = record + sizeof(ushort) + entry.NameLength;
        if (recordEnd > (long)header.NameTableLength)
        {
            throw new PackMountException(
                $"'{PackPath}' entry {index} names a record ending at {recordEnd} in a " +
                $"{header.NameTableLength}-byte name table.");
        }

        // The record's own prefix is what the table can be walked end to end with,
        // and the entry's copy is what a reader that skipped the table would use;
        // a disagreement makes those two walks return different names.
        ushort prefix = BinaryPrimitives.ReadUInt16LittleEndian(names[(int)record..]);
        if (prefix != entry.NameLength)
        {
            throw new PackMountException(
                $"'{PackPath}' entry {index} declares a {entry.NameLength}-byte name and its record says {prefix}.");
        }
    }

    private void ValidateDigest(in PackHeader header, long length)
    {
        Span<byte> stored = stackalloc byte[PackFormat.DigestSize];
        ReadRaw(length - PackFormat.DigestSize, stored);

        long from = (long)header.EntryTableOffset;
        UInt128 computed = ComputeDigest(from, length - PackFormat.DigestSize - from);
        UInt128 declared = PackDigest.Read(stored);

        if (computed == declared) return;

        throw new PackMountException(
            $"'{PackPath}' fails its content digest: the bytes from {from} to end of file hash to " +
            $"{computed:X32} and the file declares {declared:X32}. The pack is corrupt.");
    }

    private static string? NormalizePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return null;

        try
        {
            return ContentRoot.NormalizeRelativePath(prefix);
        }
        catch (ArgumentException)
        {
            // A prefix that cannot be normalised cannot match a normalised name,
            // which is the same answer as an empty result set.
            return " ";
        }
    }

    private static bool Matches(string name, string? prefix, string extension)
    {
        if (prefix is not null &&
            !(name.Length > prefix.Length &&
              name[prefix.Length] == '/' &&
              name.AsSpan(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return string.IsNullOrEmpty(extension) || name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }
}
