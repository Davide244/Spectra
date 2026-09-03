using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// The <c>STRT</c> section, read in place: an offset array over one UTF-8 blob.
/// </summary>
/// <remarks>
/// <para><b>Offsets rather than length prefixes, and no NUL terminators.</b> A
/// consumer of this table wants a <c>ReadOnlySpan&lt;byte&gt;</c> it can compare
/// or decode without walking anything, and index 0 is the empty string so that a
/// record meaning "no name" needs no sentinel value: zero already reads as
/// nothing.</para>
/// <para><b>The count+1 offset array is what makes a length a subtraction.</b> The
/// last entry is the blob length, so every string's extent is
/// <c>offsets[i + 1] - offsets[i]</c> with no special case for the final one, and
/// the reader validates the array is non-decreasing and ends exactly at the blob
/// length. Without the second half of that check a truncated blob is a read past
/// the end of a mapped view, which is an access violation with no managed stack
/// rather than an exception.</para>
/// <para><b>Emission order is FIRST-REFERENCE order during the canonical node
/// walk</b>, which the writer owns and this reader cannot verify. It matters
/// because dictionary iteration order would leak the runtime string hash seed into
/// the file, and the failure that causes is a cook whose bytes differ between two
/// runs of the same tool on the same input, which somebody reports as "CI says the
/// map changed and nothing changed".</para>
/// </remarks>
public readonly ref struct ScmapStringTable
{
    private readonly ReadOnlySpan<uint> _offsets;
    private readonly ReadOnlySpan<byte> _blob;

    /// <summary>
    /// Parses the section, validating every offset before anything indexes with
    /// one.
    /// </summary>
    /// <exception cref="ScmapFormatException">The section is not a well-formed string table.</exception>
    public ScmapStringTable(ReadOnlySpan<byte> section, string source)
    {
        if (section.Length < ScmapFormat.StringCountSize)
        {
            throw new ScmapFormatException(
                $"'{source}' has a {section.Length}-byte STRT section, too short to hold its own string count.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(section);
        if (count == 0)
        {
            throw new ScmapFormatException(
                $"'{source}' declares an empty STRT section. Index 0 is always the empty string, so a " +
                "well-formed table carries at least one entry.");
        }

        long offsetBytes = (long)(count + 1) * sizeof(uint);
        long declared = ScmapFormat.StringCountSize + offsetBytes + sizeof(uint);
        if (declared > section.Length)
        {
            throw new ScmapFormatException(
                $"'{source}' declares {count} strings, whose offset array and blob length would end at byte " +
                $"{declared} of a {section.Length}-byte STRT section.");
        }

        _offsets = MemoryMarshal.Cast<byte, uint>(
            section.Slice(ScmapFormat.StringCountSize, (int)offsetBytes));

        uint blobSize = BinaryPrimitives.ReadUInt32LittleEndian(section[(int)(ScmapFormat.StringCountSize + offsetBytes)..]);
        long blobEnd = declared + blobSize;
        if (blobEnd > section.Length)
        {
            throw new ScmapFormatException(
                $"'{source}' declares a {blobSize}-byte string blob ending at byte {blobEnd} of a " +
                $"{section.Length}-byte STRT section.");
        }

        _blob = section.Slice((int)declared, (int)blobSize);

        if (_offsets[0] != 0)
        {
            throw new ScmapFormatException(
                $"'{source}' starts its STRT offset array at {_offsets[0]} rather than 0.");
        }

        for (int i = 1; i <= (int)count; i++)
        {
            if (_offsets[i] < _offsets[i - 1])
            {
                throw new ScmapFormatException(
                    $"'{source}' has a STRT offset array that goes backwards at index {i}: " +
                    $"{_offsets[i]} after {_offsets[i - 1]}. Every string's length is the difference " +
                    "between two neighbours, so a decreasing pair is a negative length.");
            }
        }

        if (_offsets[(int)count] != blobSize)
        {
            throw new ScmapFormatException(
                $"'{source}' has a STRT offset array ending at {_offsets[(int)count]} over a {blobSize}-byte " +
                "blob. The last offset is the blob length, which is what makes every string's extent a " +
                "subtraction with no special case for the final one.");
        }

        Count = (int)count;
    }

    /// <summary>How many strings the table holds, the empty string at index 0 included.</summary>
    public int Count { get; }

    /// <summary>The UTF-8 bytes of one string, without decoding them.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is not in the table.</exception>
    public ReadOnlySpan<byte> GetUtf8(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

        int start = (int)_offsets[index];
        return _blob.Slice(start, (int)_offsets[index + 1] - start);
    }

    /// <summary>One string, decoded. Allocates, so it is for names and messages rather than for a hot path.</summary>
    public string GetString(int index) => Encoding.UTF8.GetString(GetUtf8(index));

    /// <summary>
    /// One string, decoded, or the empty string when the index is out of range.
    /// </summary>
    /// <remarks>
    /// For a message about a record that is itself being refused: a node whose
    /// payload kind is illegal may well also carry a name index that is, and a
    /// second exception thrown while composing the first one's text would replace
    /// a precise refusal with an index-out-of-range nobody can act on.
    /// </remarks>
    public string GetStringOrEmpty(int index) =>
        index >= 0 && index < Count ? GetString(index) : string.Empty;
}
