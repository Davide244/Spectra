using System;
using System.Buffers;

namespace SpectraEngine.Core.Assets.Sources;

/// <summary>
/// A block of content bytes handed out by an <see cref="IContentSource"/>, owned
/// by the caller and released with <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para><b>Why a disposable wrapper rather than a <c>byte[]</c>.</b> Today the
/// bytes live in a pooled array and <see cref="Dispose"/> returns it; tomorrow
/// they will be a window into a memory-mapped archive, where the release is not
/// optional. Putting the disposal contract in place while the backing store is
/// still trivially forgiving is the whole point: every call site is written as
/// <c>using</c> now, so the archive can be swapped in underneath without
/// auditing them again.</para>
/// <para><b>The bytes are only readable through <see cref="Span"/></b>, never as
/// an array, because a pooled buffer is longer than the content in it and a
/// mapped view is not an array at all. A consumer that genuinely needs an
/// exactly-sized array (StbImageSharp's entry point does) copies one.</para>
/// <para><b>Single owner.</b> The span is valid until <see cref="Dispose"/>, and
/// disposing twice from two threads would return one buffer to the pool twice.
/// One blob belongs to one thread at a time, which is how every producer here
/// uses it: opened, read, disposed, on the same stack frame.</para>
/// </remarks>
public sealed class ContentBlob : IDisposable
{
    private byte[]? _buffer;

    private ContentBlob(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    /// <summary>Number of content bytes, which is never the backing array's length.</summary>
    public int Length { get; }

    /// <summary>The content bytes. Valid until <see cref="Dispose"/>.</summary>
    /// <exception cref="ObjectDisposedException">The blob was already disposed.</exception>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(ContentBlob));
            return buffer.AsSpan(0, Length);
        }
    }

    /// <summary>
    /// Rents a blob of <paramref name="length"/> bytes and hands the producer a
    /// writable view of exactly that many.
    /// </summary>
    /// <remarks>
    /// A pooled buffer arrives holding whatever the previous tenant left in it,
    /// so the producer must fill <paramref name="destination"/> completely; a
    /// short read leaves stale bytes where content should be, which decodes as a
    /// corrupt file rather than as a failure.
    /// </remarks>
    public static ContentBlob Rent(int length, out Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        destination = buffer.AsSpan(0, length);
        return new ContentBlob(buffer, length);
    }

    /// <summary>
    /// Rents a blob holding a copy of <paramref name="bytes"/> — for sources that
    /// already hold their content in memory.
    /// </summary>
    public static ContentBlob CopyOf(ReadOnlySpan<byte> bytes)
    {
        ContentBlob blob = Rent(bytes.Length, out Span<byte> destination);
        bytes.CopyTo(destination);
        return blob;
    }

    /// <summary>Releases the backing storage. Idempotent.</summary>
    public void Dispose()
    {
        byte[]? buffer = _buffer;
        if (buffer is null) return;

        _buffer = null;
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
