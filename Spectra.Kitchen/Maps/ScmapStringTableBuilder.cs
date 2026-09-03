using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using SpectraEngine.Core.Maps.Compiled;

namespace Spectra.Kitchen.Maps;

/// <summary>
/// Accumulates the <c>STRT</c> section: one blob of UTF-8, addressed by index.
/// </summary>
/// <remarks>
/// <para><b>The list is the ORDER and the dictionary is only a lookup.</b> That
/// split is the whole class. Strings are emitted in first-reference order, and the
/// dictionary exists solely to answer "have I seen this one" in constant time;
/// nothing ever enumerates it. Emitting in dictionary order instead would leak the
/// runtime string hash seed into the file, and .NET randomises that seed per
/// PROCESS, so two runs of the same cook on the same input would produce different
/// bytes while every in-process test comparing two builds inside one test host
/// passed. That failure is reported as "CI says the map changed and nothing
/// changed", and it is the same determinism sin the keyvalue list and the cook
/// scheduler already refuse.</para>
/// <para><b>Index 0 is always the empty string</b>, so a record meaning "no name"
/// needs no sentinel value: zero already reads as nothing, and every offset stays
/// a plain index with no reserved value to remember.</para>
/// <para><b>Ordinal comparison, never case-insensitive.</b> A node name and a
/// target name are matched exactly by the entity runtime, so folding two spellings
/// into one entry here would silently rename a node and break the wiring that
/// points at it. Asset PATHS are case-insensitive identities elsewhere, and they
/// are normalised before they arrive rather than by being compared loosely here.</para>
/// </remarks>
public sealed class ScmapStringTableBuilder
{
    private readonly List<string> _strings = [string.Empty];
    private readonly Dictionary<string, uint> _lookup = new(StringComparer.Ordinal) { [string.Empty] = 0 };

    /// <summary>How many strings the table holds, the empty string at index 0 included.</summary>
    public int Count => _strings.Count;

    /// <summary>
    /// Returns the index of <paramref name="value"/>, appending it on first
    /// reference. A null or empty string is index 0.
    /// </summary>
    public uint Intern(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;

        if (_lookup.TryGetValue(value, out uint existing)) return existing;

        var index = (uint)_strings.Count;
        _strings.Add(value);
        _lookup[value] = index;
        return index;
    }

    /// <summary>The string at <paramref name="index"/>, for a diagnostic.</summary>
    public string At(int index) => _strings[index];

    /// <summary>
    /// Builds the section body: the count, the count+1 offset array, the blob
    /// length and the blob.
    /// </summary>
    /// <remarks>
    /// The offset array carries one entry past the last string, holding the blob
    /// length, so a reader gets every extent as a subtraction with no special case
    /// for the final string. Without that entry the last string's length has to
    /// come from the blob size instead, which is a second expression of one fact.
    /// </remarks>
    public byte[] Build()
    {
        int count = _strings.Count;
        var lengths = new int[count];

        long blobSize = 0;
        for (int i = 0; i < count; i++)
        {
            lengths[i] = Encoding.UTF8.GetByteCount(_strings[i]);
            blobSize += lengths[i];
        }

        if (blobSize > uint.MaxValue)
        {
            throw new InvalidOperationException(
                $"The compiled map's string blob would be {blobSize} bytes, past what a 32-bit offset can " +
                "address.");
        }

        long total = ScmapFormat.StringCountSize + ((long)(count + 1) * sizeof(uint)) + sizeof(uint) + blobSize;
        var body = new byte[total];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)count);

        int cursor = ScmapFormat.StringCountSize;
        uint offset = 0;
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[cursor..], offset);
            cursor += sizeof(uint);
            offset += (uint)lengths[i];
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span[cursor..], offset);
        cursor += sizeof(uint);

        BinaryPrimitives.WriteUInt32LittleEndian(span[cursor..], (uint)blobSize);
        cursor += sizeof(uint);

        for (int i = 0; i < count; i++)
        {
            cursor += Encoding.UTF8.GetBytes(_strings[i], span[cursor..]);
        }

        return body;
    }
}
