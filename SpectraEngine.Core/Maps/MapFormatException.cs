using System;

namespace SpectraEngine.Core.Maps;

/// <summary>
/// A map document could not be read, reported against the place in the file
/// that caused it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node name and the byte offset are the whole point of this type.</b>
/// A <c>.smap</c> bundle is text a person edits by hand, merges in git and
/// generates from scripts, so the first genuinely bad map will not be one the
/// editor wrote. <c>Brush</c>'s constructor rejects duplicate and
/// unbounded plane sets with an <see cref="ArgumentException"/> raised deep
/// inside CSG code that has never heard of a file; surfaced raw, that says
/// "Planes 2 and 5 are near-coplanar duplicates" about a map with four hundred
/// brushes in it. The reader catches those and re-throws as this, naming the
/// node and the offset, which is the difference between a two-minute fix and an
/// afternoon.
/// </para>
/// <para>
/// <b>The offset is in bytes, not lines</b>, because that is what
/// <see cref="System.Text.Json.Utf8JsonReader"/> actually knows
/// (<c>TokenStartIndex</c>) and converting to a line number means a second scan
/// of the document to produce a number that can still be wrong after a merge.
/// Every editor can go to a byte offset; none can go to a line that moved.
/// </para>
/// </remarks>
public sealed class MapFormatException : Exception
{
    public MapFormatException(string message, string? nodeName, long byteOffset, Exception? inner = null)
        : base(Describe(message, nodeName, byteOffset), inner)
    {
        NodeName = nodeName;
        ByteOffset = byteOffset;
    }

    /// <summary>Name of the node being read when this failed, or null outside a node.</summary>
    public string? NodeName { get; }

    /// <summary>Byte offset into the document where the offending token starts.</summary>
    public long ByteOffset { get; }

    private static string Describe(string message, string? nodeName, long byteOffset) =>
        nodeName is null
            ? $"{message} (at byte {byteOffset})"
            : $"{message} (node '{nodeName}', at byte {byteOffset})";
}
