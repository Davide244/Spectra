using System;
using System.Buffers;
using System.Runtime.InteropServices;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// One cell's flat BSP nodes as a <see cref="ReadOnlyMemory{T}"/> over the
/// compiled map's own bytes, so a <see cref="FlatBspTree"/> queries the mapping
/// rather than a copy of it.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> <see cref="FlatBspTree"/> takes a
/// <see cref="ReadOnlyMemory{T}"/> because the block "is a plain array today and
/// a memory-mapped view of a cooked map later", and later is now. A span into a
/// mapping is not a <c>Memory</c>, there is no <c>MemoryMarshal.Cast</c> for one,
/// and the alternative - copying every cell's nodes into a managed array at load
/// - spends exactly what the zero-copy layout was designed to save, on the one
/// structure a level keeps for its whole life.</para>
/// <para><b>Every access re-asks the blob, and that is the safety property, not
/// an oversight.</b> The obvious implementation pins a pointer once; a pointer
/// into a released mapping is an access violation with no managed stack, and a
/// pointer into a POOLED array (which is what a loose-file blob is) is worse
/// still, because the array is collectable and movable and the failure is silent
/// rather than immediate. <see cref="ContentBlob.Span"/> throws
/// <see cref="ObjectDisposedException"/> once its blob is released, so a
/// use-after-free through this manager is an exception naming the type instead of
/// a crash naming nothing.</para>
/// <para><b>Pinning is refused rather than implemented.</b> Nothing in the query
/// path pins - <see cref="FlatBspTree"/> only ever takes the span - and a
/// <see cref="MemoryHandle"/> handed out here would be a promise about an address
/// that this type deliberately does not make. A refusal that names the reason is
/// better than a handle that is correct for a mapped blob and quietly wrong for a
/// pooled one.</para>
/// </remarks>
internal sealed class MappedBspNodes : MemoryManager<FlatBspNode>
{
    private readonly ContentBlob _file;
    private readonly int _byteOffset;
    private readonly int _count;

    /// <param name="file">The whole compiled map, whose lifetime owns these bytes.</param>
    /// <param name="byteOffset">Where this cell's node array starts, from the first byte of the file.</param>
    /// <param name="count">How many nodes are there.</param>
    public MappedBspNodes(ContentBlob file, int byteOffset, int count)
    {
        _file = file;
        _byteOffset = byteOffset;
        _count = count;
    }

    /// <inheritdoc/>
    public override Span<FlatBspNode> GetSpan()
    {
        ReadOnlySpan<byte> bytes = _file.Span.Slice(_byteOffset, _count * ScmapFormat.FlatBspNodeSize);

        // The one place read-only bytes become a writable span, and it goes no
        // further: MemoryManager's contract is stated in Span<T>, while everything
        // this type is handed to takes the ReadOnlyMemory it exposes. Nothing in
        // the engine writes a BSP node, and a mapped view is mapped read-only, so
        // an attempt to would fault at the page rather than corrupt a file.
        return MemoryMarshal.Cast<byte, FlatBspNode>(
            MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(bytes), bytes.Length));
    }

    /// <inheritdoc/>
    public override MemoryHandle Pin(int elementIndex = 0) =>
        throw new NotSupportedException(
            "A compiled map's BSP nodes cannot be pinned. Their lifetime is the map's ContentBlob, which is " +
            "either a window into a memory-mapped file (already at a fixed address, so pinning says nothing) " +
            "or a pooled array (which the blob may return at any time, so a pin would outlive its own " +
            "promise). Read the span instead.");

    /// <inheritdoc/>
    public override void Unpin()
    {
    }

    /// <summary>Nothing to release: the bytes belong to the map's blob.</summary>
    protected override void Dispose(bool disposing)
    {
    }
}
