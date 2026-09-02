using System;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// The fixed byte geometry of a <c>.spack</c> file, stated once for the writer
/// in <c>Spectra.Kitchen</c> and the reader here.
/// </summary>
/// <remarks>
/// <para><b>Two expressions of one layout diverge</b>, which is the lesson
/// <see cref="Graphics.Shaders.ShaderFileLayout"/> already records: a writer that
/// computes the data section from its own running cursor and a reader that
/// recomputes it from <c>64 + 48 * count</c> agree exactly until the header
/// grows, and then disagree as a seek into the middle of somebody else's bytes
/// rather than as an exception. Both sides take their arithmetic from here.</para>
/// <para><b>The header carries <c>EntryTableOffset</c> and
/// <c>DataSectionOffset</c> explicitly even though v1 could derive both.</b> That
/// is what lets the header grow without a version bump: a reader that seeks to
/// the offset it was told, rather than to the offset it computed, keeps working
/// when a v2 header is 128 bytes.</para>
/// </remarks>
public static class PackFormat
{
    /// <summary>
    /// File magic, <c>"SPAK"</c>. Stored as a little-endian <see cref="uint"/>,
    /// so the first four bytes on disk read <c>S P A K</c> in a hex dump.
    /// </summary>
    /// <remarks>
    /// The four-byte abbreviation is <c>SPAK</c>; the extension is always spelled
    /// <c>.spack</c>. They are deliberately different lengths and neither is a
    /// typo for the other.
    /// </remarks>
    public const uint Magic = 'S' | ('P' << 8) | ('A' << 16) | ((uint)'K' << 24);

    /// <summary>Bytes in the header, which lives at offset 0.</summary>
    public const int HeaderSize = 64;

    /// <summary>Bytes in one entry-table record, fixed stride.</summary>
    public const int EntrySize = 48;

    /// <summary>Bytes in the trailing content digest.</summary>
    public const int DigestSize = 16;

    /// <summary>
    /// Alignment every payload starts on, and the whole reason the data section
    /// exists as a distinct region: a mapped payload is reinterpreted in place as
    /// <c>Vector4</c>, <c>Matrix4x4</c> or a flat BSP node, and none of those may
    /// straddle a 16-byte boundary.
    /// </summary>
    public const int PayloadAlignment = 16;

    /// <summary>
    /// Alignment the data section itself starts on: prefetch friendliness, and so
    /// a block-level patcher diffs on 4K boundaries rather than on a boundary that
    /// moves whenever the name table changes length.
    /// </summary>
    public const int DataSectionAlignment = 4096;

    /// <summary>
    /// What <see cref="PackEntry.NameOffset"/> holds when an entry has no name
    /// table record. Zero cannot serve, because zero is the first record's
    /// legitimate offset.
    /// </summary>
    public const uint NameOffsetAbsent = 0xFFFFFFFFu;

    /// <summary>
    /// Rounds <paramref name="value"/> up to the next multiple of
    /// <paramref name="alignment"/>, which must be a power of two.
    /// </summary>
    public static long AlignUp(long value, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        if ((alignment & (alignment - 1)) != 0)
            throw new ArgumentException($"Alignment {alignment} is not a power of two.", nameof(alignment));

        long mask = alignment - 1L;
        return (value + mask) & ~mask;
    }

    /// <summary>
    /// Refuses to read or write a pack on a big-endian machine.
    /// </summary>
    /// <remarks>
    /// The whole zero-copy premise is <c>MemoryMarshal.Cast</c> over raw mapped
    /// bytes, which is endianness-native by construction. A byte-swapping reader
    /// would have to copy every vertex, i.e. do the one thing this format exists
    /// to avoid, so the honest answer is to refuse loudly rather than to pretend.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The machine is big-endian.</exception>
    public static void RequireLittleEndian()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "The .spack container is little-endian only: every header, entry and payload is " +
                "reinterpreted in place, so a big-endian host would have to copy and byte-swap all of it.");
        }
    }
}
