using SpectraEngine.Core.Assets.Packs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Spectra.Kitchen.Packs;

/// <summary>
/// A <c>.spack</c>'s header and tables, read out into arrays: what a tool needs
/// in order to talk ABOUT a pack rather than to read content out of one.
/// </summary>
/// <remarks>
/// <para><b>It is not a second reader, and the line is worth stating.</b>
/// <see cref="PackSource"/> answers "give me the bytes at this path" and keeps
/// its tables private, in place, behind a binary search - which is right for the
/// thing a frame calls and useless to <c>scook inspect</c>, which wants to print
/// every row, and to <see cref="PackVerifier"/>, which wants to make its own
/// claim about the ORDER of those rows. Both of those are questions about the
/// table itself.</para>
/// <para><b>Every offset comes from <see cref="PackFormat"/> and every record
/// through <see cref="PackEntryTable"/>.</b> That is what keeps this one
/// expression of the layout rather than two: nothing here recomputes a region
/// the header already states, and a header that grows moves this reader with it
/// for free. The bounds checks below are this reader's own reads being kept
/// inside the file, not a copy of the mount validation - a length taken from a
/// corrupt header would otherwise ask for a multi-gigabyte array before anything
/// had a chance to refuse the file.</para>
/// <para><b>Validation stays where it is.</b> A pack that is going to be refused
/// is refused by <see cref="PackSourceBase"/>'s mount, which is the sequence a
/// shipped game runs; a verifier that validated here as well would be proving
/// its own arithmetic rather than the file.</para>
/// </remarks>
public sealed class PackContents
{
    private readonly PackEntry[] _entries;
    private readonly byte[] _nameTable;

    private PackContents(
        string path, long fileLength, in PackHeader header, PackEntry[] entries, byte[] nameTable, UInt128 digest)
    {
        Path = path;
        FileLength = fileLength;
        Header = header;
        StoredDigest = digest;
        _entries = entries;
        _nameTable = nameTable;
    }

    /// <summary>
    /// The file these tables came from, resolved to a full path.
    /// </summary>
    /// <remarks>
    /// Full rather than as it was given, because it labels every diagnostic and
    /// every report this feeds: a line naming a relative path means something
    /// different in each directory it is read from, and CI logs are read from
    /// somewhere else by definition.
    /// </remarks>
    public string Path { get; }

    /// <summary>Bytes in the file.</summary>
    public long FileLength { get; }

    /// <summary>The header, as it sits on disk.</summary>
    public PackHeader Header { get; }

    /// <summary>The trailing digest the file declares, unverified.</summary>
    /// <remarks>
    /// Read rather than checked: whether it MATCHES is the mount's claim, and
    /// this is here so <c>inspect</c> can print the value that a bug report will
    /// be quoting.
    /// </remarks>
    public UInt128 StoredDigest { get; }

    /// <summary>The entry table, in table order.</summary>
    public IReadOnlyList<PackEntry> Entries => _entries;

    /// <summary>Whether the pack carries a name table at all.</summary>
    public bool HasNameTable => _nameTable.Length > 0;

    /// <summary>
    /// The name of entry <paramref name="index"/>, or the empty string when the
    /// pack carries none for it.
    /// </summary>
    public string NameOf(int index) => PackEntryTable.ReadName(_nameTable, in _entries[index]);

    /// <summary>Reads the tables of the pack at <paramref name="path"/>.</summary>
    /// <exception cref="PackMountException">
    /// The file is too short, is not a pack, or points a region outside itself.
    /// </exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static PackContents Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        PackFormat.RequireLittleEndian();

        path = System.IO.Path.GetFullPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        long length = stream.Length;
        PackFormat.RequireMinimumFileSize(path, length);

        Span<byte> headerBytes = stackalloc byte[PackFormat.HeaderSize];
        stream.ReadExactly(headerBytes);
        PackHeader header = MemoryMarshal.Read<PackHeader>(headerBytes);

        if (header.Magic != PackFormat.Magic)
        {
            throw new PackMountException(
                $"'{path}' is not a .spack file: its first four bytes are 0x{header.Magic:X8}, " +
                $"not 0x{PackFormat.Magic:X8} ('SPAK').");
        }

        int tableBytes = RequireInside(
            path, header.EntryTableOffset, (long)header.EntryCount * PackFormat.EntrySize, length, "entry table");

        var entries = new PackEntry[tableBytes / PackFormat.EntrySize];
        stream.Position = (long)header.EntryTableOffset;
        stream.ReadExactly(MemoryMarshal.AsBytes(entries.AsSpan()));

        byte[] names = [];
        if (header.HasNameTable)
        {
            names = new byte[RequireInside(
                path, header.NameTableOffset, (long)header.NameTableLength, length, "name table")];

            stream.Position = (long)header.NameTableOffset;
            stream.ReadExactly(names);
        }

        Span<byte> digestBytes = stackalloc byte[PackFormat.DigestSize];
        stream.Position = length - PackFormat.DigestSize;
        stream.ReadExactly(digestBytes);

        return new PackContents(path, length, in header, entries, names, PackDigest.Read(digestBytes));
    }

    // A length this reader is about to allocate and read, so a header claiming
    // one that does not fit inside the file is refused BEFORE the array is asked
    // for rather than after: an EntryCount of 2^31 is one edited field and would
    // otherwise be an out-of-memory exception naming nothing. The int cap is the
    // same one the mount applies, and for the same reason: both regions are
    // addressed as a single span.
    private static int RequireInside(string path, ulong offset, long bytes, long fileLength, string what)
    {
        if (bytes is >= 0 and <= int.MaxValue &&
            offset >= (ulong)PackFormat.HeaderSize &&
            (long)offset + bytes <= fileLength)
        {
            return (int)bytes;
        }

        throw new PackMountException(
            $"'{path}' puts its {what} at {offset} for {bytes} bytes, which is not inside the " +
            $"{fileLength}-byte file.");
    }
}
