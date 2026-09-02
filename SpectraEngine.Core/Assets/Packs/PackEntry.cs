using System;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// One 48-byte entry-table record, exactly as it sits on disk.
/// </summary>
/// <remarks>
/// <para><b>The table is reinterpreted as <c>ReadOnlySpan&lt;PackEntry&gt;</c>
/// straight off the mapped view</b>, so lookup is a binary search with no parse
/// and no allocation. That is only legal because <c>Pack = 1</c> makes the struct
/// exactly 48 bytes with no padding, and because 48 divides the 16-byte alignment
/// the table starts on, which keeps every <see cref="AssetId"/> 16-byte aligned.
/// <c>PackFormatLayoutTests</c> pins the size and every field offset.</para>
/// <para><b>Sorted ascending by <see cref="AssetId"/> as an unsigned 128-bit
/// value</b>, which is what <see cref="UInt128"/>'s own comparison already means.
/// Signed comparison would order the top half of the space below the bottom half
/// and a binary search would then miss roughly half of every pack, intermittently,
/// as a content miss rather than as a fault.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct PackEntry
{
    /// <summary>
    /// <c>XxHash128</c> of the normalized content-relative SOURCE path. See
    /// <see cref="PackAssetId"/> for why identity is the source path.
    /// </summary>
    public readonly UInt128 AssetId;

    /// <summary>
    /// Absolute file offset of the payload, aligned to
    /// <see cref="PackFormat.PayloadAlignment"/>.
    /// </summary>
    public readonly ulong PayloadOffset;

    /// <summary>Bytes on disk, i.e. after <see cref="Codec"/> was applied.</summary>
    public readonly ulong StoredSize;

    /// <summary>
    /// Bytes after decompression, equal to <see cref="StoredSize"/> when
    /// <see cref="Codec"/> is <see cref="PackCodec.None"/>. Carried rather than
    /// derived so a reader can size its destination buffer in one allocation
    /// before it starts decompressing.
    /// </summary>
    public readonly ulong UncompressedSize;

    /// <summary>
    /// Byte offset of this entry's record within the name table, or
    /// <see cref="PackFormat.NameOffsetAbsent"/>. The offset addresses the
    /// record's own <c>u16</c> length prefix, not the text after it, so the table
    /// can also be walked end to end without the entry table.
    /// </summary>
    public readonly uint NameOffset;

    /// <summary>
    /// Length of the name in UTF-8 bytes, which must equal the <c>u16</c> prefix
    /// the record itself carries.
    /// </summary>
    public readonly ushort NameLength;

    /// <summary>See <see cref="PackEntryKind"/>.</summary>
    public readonly byte Kind;

    /// <summary>See <see cref="PackCodec"/>.</summary>
    public readonly byte Codec;

    /// <summary>Builds an entry. Every field is assigned; there are no reserved bytes.</summary>
    public PackEntry(
        UInt128 assetId,
        ulong payloadOffset,
        ulong storedSize,
        ulong uncompressedSize,
        uint nameOffset,
        ushort nameLength,
        PackEntryKind kind,
        PackCodec codec)
    {
        AssetId = assetId;
        PayloadOffset = payloadOffset;
        StoredSize = storedSize;
        UncompressedSize = uncompressedSize;
        NameOffset = nameOffset;
        NameLength = nameLength;
        Kind = (byte)kind;
        Codec = (byte)codec;
    }

    /// <summary><see cref="Kind"/> as the enum it is.</summary>
    public PackEntryKind EntryKind => (PackEntryKind)Kind;

    /// <summary><see cref="Codec"/> as the enum it is.</summary>
    public PackCodec EntryCodec => (PackCodec)Codec;

    /// <summary>Whether this entry deletes the path it names rather than serving it.</summary>
    public bool IsTombstone => Kind == (byte)PackEntryKind.Tombstone;

    /// <summary>Whether this entry has a name-table record.</summary>
    public bool HasName => NameOffset != PackFormat.NameOffsetAbsent;
}
