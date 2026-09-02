using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// Reading the entry and name tables in place: the cast, the binary search and
/// the name record.
/// </summary>
/// <remarks>
/// Shared by both sources so the mapped reader and the stream fallback cannot
/// answer the same file differently. The only difference between them is where
/// the span came from.
/// </remarks>
public static class PackEntryTable
{
    /// <summary>
    /// The entry table's bytes reinterpreted as records, with no parse and no
    /// allocation.
    /// </summary>
    /// <remarks>
    /// Legal because <c>Pack = 1</c> makes <see cref="PackEntry"/> exactly 48
    /// bytes with no padding, and because both the table's start and its stride
    /// are multiples of 16, which keeps every <see cref="PackEntry.AssetId"/>
    /// 16-byte aligned.
    /// </remarks>
    public static ReadOnlySpan<PackEntry> Cast(ReadOnlySpan<byte> tableBytes) =>
        MemoryMarshal.Cast<byte, PackEntry>(tableBytes);

    /// <summary>
    /// Finds <paramref name="assetId"/> in a table sorted ascending as an
    /// unsigned 128-bit value. Allocation-free.
    /// </summary>
    /// <remarks>
    /// <b>Unsigned comparison, which is what <see cref="UInt128"/>'s own operators
    /// already mean.</b> A signed comparison would order the top half of the id
    /// space below the bottom half, and this search would then miss roughly half
    /// of every pack — intermittently, as content that is absent for some assets
    /// and present for others, rather than as a fault.
    /// </remarks>
    public static bool TryFind(ReadOnlySpan<PackEntry> entries, UInt128 assetId, out int index)
    {
        int low = 0;
        int high = entries.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            UInt128 candidate = entries[middle].AssetId;

            if (candidate == assetId)
            {
                index = middle;
                return true;
            }

            if (candidate < assetId) low = middle + 1;
            else high = middle - 1;
        }

        index = -1;
        return false;
    }

    /// <summary>
    /// The name of <paramref name="entry"/>, or the empty string when the pack
    /// carries no name table or the entry has no record in it.
    /// </summary>
    /// <remarks>
    /// Read through the record's own <c>u16</c> prefix rather than through the
    /// entry's copy of the length; the two are checked to agree at mount, so by
    /// here either will do and the record's is the one the table can be walked
    /// end to end with.
    /// </remarks>
    public static string ReadName(ReadOnlySpan<byte> nameTable, in PackEntry entry)
    {
        if (!entry.HasName || nameTable.IsEmpty) return string.Empty;

        ReadOnlySpan<byte> record = nameTable[(int)entry.NameOffset..];
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(record);
        return Encoding.UTF8.GetString(record.Slice(sizeof(ushort), length));
    }
}
