using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// The 64 bytes at offset 0 of a <c>.spack</c> file, exactly as they sit on disk.
/// </summary>
/// <remarks>
/// <para><b><c>Pack = 1</c> is what makes this struct the format rather than a
/// description of it.</b> Without it the CLR is free to insert padding, and the
/// bytes written from a struct value would stop matching the bytes a reader casts
/// back out of a mapped view; with it there is no padding at all, so no reserved
/// byte can pick up whatever was on the stack. Every one of the 64 bytes is a
/// declared field, and <c>PackFormatLayoutTests</c> pins both that size and the
/// offset of each field, because a field reordered by an edit compiles cleanly
/// and produces a file that parses into the wrong numbers.</para>
/// <para><b>Endianness is the machine's, and the machine is asserted to be
/// little-endian</b> (<see cref="PackFormat.RequireLittleEndian"/>). A struct
/// written and read as raw bytes is little-endian on every host this engine
/// targets and would silently be big-endian on one that is not, which is why the
/// assertion is a refusal rather than a comment.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct PackHeader
{
    /// <summary>Always <see cref="PackFormat.Magic"/>; four bytes reading <c>SPAK</c>.</summary>
    public readonly uint Magic;

    /// <summary>The version this pack was written at: <c>EngineInfo.PackFormatVersion</c>.</summary>
    public readonly ushort FormatVersion;

    /// <summary>
    /// The oldest reader that can still make sense of this pack. A reader refuses
    /// the file when this exceeds the version it implements.
    /// </summary>
    /// <remarks>
    /// It is a floor carried per pack rather than a global exact-match rule
    /// because the alternative refuses every pack the moment anything is added,
    /// including the overwhelming majority that carry nothing new.
    /// </remarks>
    public readonly ushort MinReaderVersion;

    /// <summary>Whole-pack properties. See <see cref="PackFlags"/>.</summary>
    public readonly uint Flags;

    /// <summary>Number of records in the entry table.</summary>
    public readonly uint EntryCount;

    /// <summary>
    /// Absolute offset of the entry table. 64 in v1, and written explicitly
    /// anyway so the header can grow without a version bump.
    /// </summary>
    public readonly ulong EntryTableOffset;

    /// <summary>Absolute offset of the name table; 0 when there is none.</summary>
    public readonly ulong NameTableOffset;

    /// <summary>Bytes in the name table; 0 when there is none.</summary>
    public readonly ulong NameTableLength;

    /// <summary>
    /// Monotonic ordering key among patch packs, so a mount order is decided by
    /// the packs rather than by whatever order a directory listing came back in.
    /// </summary>
    public readonly uint PackSequence;

    /// <summary>
    /// <c>(Major &lt;&lt; 20) | (Minor &lt;&lt; 10) | Revision</c> of the engine that
    /// wrote this pack. <b>Informational, never a load gate</b>: what gates a load
    /// is <see cref="MinReaderVersion"/>, which is a statement about the bytes,
    /// while this is a statement about the build and is here for bug reports.
    /// </summary>
    public readonly uint EngineVersion;

    /// <summary>
    /// Absolute offset of the first payload, aligned to
    /// <see cref="PackFormat.DataSectionAlignment"/>.
    /// </summary>
    public readonly ulong DataSectionOffset;

    /// <summary>
    /// Size of the whole file including the trailing digest, so a truncated pack
    /// is detectable from its own bytes without a stat call.
    /// </summary>
    public readonly ulong TotalFileSize;

    /// <summary>Builds a header. Every field is assigned; there are no reserved bytes.</summary>
    public PackHeader(
        uint magic,
        ushort formatVersion,
        ushort minReaderVersion,
        PackFlags flags,
        uint entryCount,
        ulong entryTableOffset,
        ulong nameTableOffset,
        ulong nameTableLength,
        uint packSequence,
        uint engineVersion,
        ulong dataSectionOffset,
        ulong totalFileSize)
    {
        Magic = magic;
        FormatVersion = formatVersion;
        MinReaderVersion = minReaderVersion;
        Flags = (uint)flags;
        EntryCount = entryCount;
        EntryTableOffset = entryTableOffset;
        NameTableOffset = nameTableOffset;
        NameTableLength = nameTableLength;
        PackSequence = packSequence;
        EngineVersion = engineVersion;
        DataSectionOffset = dataSectionOffset;
        TotalFileSize = totalFileSize;
    }

    /// <summary><see cref="Flags"/> as the enum it is.</summary>
    public PackFlags PackFlags => (PackFlags)Flags;

    /// <summary>Whether the entry table may be binary-searched.</summary>
    public bool EntriesSortedByAssetId => (PackFlags & PackFlags.EntriesSortedByAssetId) != 0;

    /// <summary>Whether a name table is present and non-empty.</summary>
    public bool HasNameTable =>
        (PackFlags & PackFlags.NameTablePresent) != 0 && NameTableOffset != 0 && NameTableLength != 0;
}
