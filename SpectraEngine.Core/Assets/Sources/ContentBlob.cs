using SpectraEngine.Core.Assets.Packs;
using System;
using System.Buffers;
using System.Threading;

namespace SpectraEngine.Core.Assets.Sources;

/// <summary>
/// A block of content bytes handed out by an <see cref="IContentSource"/>, owned
/// by the caller and released with <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para><b>Why a disposable wrapper rather than a <c>byte[]</c>.</b> The bytes
/// may live in a pooled array, where <see cref="Dispose"/> returns it, or they
/// may be a window straight into a memory-mapped pack, where the release is not
/// optional: unmapping a view under a live span is an access violation rather
/// than an exception. Putting the disposal contract in place while the backing
/// store was still trivially forgiving is what let the mapped store be swapped in
/// underneath without auditing every call site again.</para>
/// <para><b>A blob from a pack holds a <see cref="PackHandle"/> reference, and
/// the reference travels with the blob.</b> That is the whole lifetime design in
/// one sentence: a blob opened on the thread pool and consumed on the render
/// thread outlives the stack frame that opened it, so the thing keeping its bytes
/// alive has to be the blob rather than the call. A pooled blob from a pack holds
/// one too, because one rule ("a blob from a pack holds a reference") is a rule,
/// while two rules that differ by codec is how a call site ends up correct only
/// against the codec it was tested with.</para>
/// <para><b>The bytes are only readable through <see cref="Span"/></b>, never as
/// an array, because a pooled buffer is longer than the content in it and a
/// mapped view is not an array at all. A consumer that genuinely needs an
/// exactly-sized array (StbImageSharp's entry point does) copies one.</para>
/// <para><b>Single owner.</b> The span is valid until <see cref="Dispose"/>, and
/// disposing twice from two threads would return one buffer to the pool twice and
/// drop one pack reference twice. One blob belongs to one thread at a time, which
/// is how every producer here uses it; the disposal flag is interlocked anyway,
/// because the consequence of losing that race on a mapped blob is a process that
/// dies with no managed stack.</para>
/// </remarks>
public sealed class ContentBlob : IDisposable
{
    private readonly ulong _offset;
    private byte[]? _buffer;
    private PackHandle? _handle;
    private int _disposed;

    private ContentBlob(byte[]? buffer, PackHandle? handle, ulong offset, int length)
    {
        _buffer = buffer;
        _handle = handle;
        _offset = offset;
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
            // Checked before either store is touched: on a mapped blob the store
            // is address space the last release may already have unmapped, and
            // reading it after that is not an exception anybody can catch.
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ContentBlob));

            byte[]? buffer = _buffer;
            if (buffer is not null) return buffer.AsSpan(0, Length);

            PackHandle handle = _handle ?? throw new ObjectDisposedException(nameof(ContentBlob));
            return handle.Slice(_offset, Length);
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
        return new ContentBlob(buffer, handle: null, offset: 0, length);
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

    /// <summary>
    /// A window straight into a mounted pack's mapped view: no copy, no decode,
    /// nothing between the caller and the file's own bytes.
    /// </summary>
    /// <remarks>
    /// The caller must already have taken the reference this blob then owns and
    /// releases; taking it here would leave a failure between the two impossible
    /// to unwind, since a half-built blob has no owner to dispose it.
    /// </remarks>
    internal static ContentBlob OverPack(PackHandle handle, ulong offset, int length)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return new ContentBlob(buffer: null, handle, offset, length);
    }

    /// <summary>
    /// Rents a blob a pack decompresses into, holding a reference to that pack for
    /// as long as the blob lives.
    /// </summary>
    /// <remarks>
    /// The reference is not what keeps the inflated bytes valid — a pooled array
    /// outlives any mount — it is what keeps the compressed bytes valid while they
    /// are being read, and what keeps the rule uniform afterwards.
    /// </remarks>
    internal static ContentBlob RentUnderPack(PackHandle handle, int length, out Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        destination = buffer.AsSpan(0, length);
        return new ContentBlob(buffer, handle, offset: 0, length);
    }

    /// <summary>Releases the backing storage and any pack reference. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        byte[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);

        PackHandle? handle = _handle;
        _handle = null;
        handle?.Release();
    }
}
