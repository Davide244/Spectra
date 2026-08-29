using System;

namespace SpectraEngine.Core.Projects;

/// <summary>
/// A project manifest could not be read, reported against the place in the file
/// that caused it.
/// </summary>
/// <remarks>
/// Separate from the map's exception rather than shared, because the two name
/// different things: a map failure points at a node, and a project failure
/// points at a member. A single type carrying both would have one of them null
/// on every throw, which is a shape that teaches callers to check nothing.
/// </remarks>
public sealed class ProjectFormatException : Exception
{
    public ProjectFormatException(string message, long byteOffset, Exception? inner = null)
        : base($"{message} (at byte {byteOffset})", inner) => ByteOffset = byteOffset;

    /// <summary>Byte offset into the document where the offending token starts.</summary>
    public long ByteOffset { get; }
}
