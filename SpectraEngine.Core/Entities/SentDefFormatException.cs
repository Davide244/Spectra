using System;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// A <c>.sentdef</c> image was refused, reported against the byte that caused
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every refusal names what was wrong AND what was expected.</b> This file is
/// a binary produced by a tool and read by another, often on a different
/// machine and a different engine build, so the person holding the failure
/// usually has neither end in front of them: "not a .sentdef" is a support
/// thread, "expected the magic 'SENT' and found 0x00000000" is a truncated copy
/// somebody can see in the file size.
/// </para>
/// <para>
/// <b>The offset is in bytes, like <c>MapFormatException</c>'s</b>, and for a
/// stronger reason: there is nothing else to name. A map is text with node names
/// in it; this is a table of integers, so the offset is the only coordinate a
/// hex editor and a reader can agree on.
/// </para>
/// <para>
/// <b>Reading throws and every other operation degrades</b>, the same split the
/// pack container makes at mount: a definition table that cannot be parsed makes
/// none of its answers trustworthy, whereas a class this build simply has not
/// heard of is an ordinary miss that a placed entity survives as authored data.
/// </para>
/// </remarks>
public sealed class SentDefFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong, and what was expected instead.</param>
    /// <param name="byteOffset">Where in the image the offending bytes start.</param>
    /// <param name="inner">The failure underneath, when there was one.</param>
    public SentDefFormatException(string message, long byteOffset, Exception? inner = null)
        : base($"{message} (at byte {byteOffset})", inner) => ByteOffset = byteOffset;

    /// <summary>Byte offset into the image where the offending bytes start.</summary>
    public long ByteOffset { get; }
}
