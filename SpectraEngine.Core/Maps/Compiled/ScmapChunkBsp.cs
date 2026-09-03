using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// One cell's <c>CBSP</c> blob, read in place: a flat solid-leaf BSP tree.
/// </summary>
/// <remarks>
/// <para><b>Queried flat, never rehydrated.</b> <c>BspNode</c> is a sealed class
/// with child references, so a 50k-part world's per-cell trees would be tens of
/// thousands of GC objects to allocate and chase at load; rebuilding them would
/// spend at load exactly what this shape exists to save. <see cref="Nodes"/> hands
/// straight to <see cref="FlatBspTree"/>.</para>
/// <para><b>Leaves occupy no slots.</b> Children are Quake-encoded, so the two
/// negative codes ARE the leaves, and a solid-leaf BSP is roughly half leaves. The
/// root's code is therefore a node index for any tree with a split at its root and
/// a leaf code for a tree that is one bare leaf, which is a legal and common
/// answer for a cell whose residents carve away to nothing.</para>
/// <para><b>Only the root is range-checked, deliberately.</b> Validating every
/// child index is an O(n) scan of storage meant to be paged in lazily, and it is
/// the same trade <c>.smodel</c> makes about index VALUES: the container's own
/// digest answers for the rest of the block. That trade assumes a pack this cook
/// produced, and it is the first thing to revisit if untrusted packs ever mount.</para>
/// </remarks>
public readonly ref struct ScmapChunkBsp
{
    /// <summary>
    /// Parses one cell's blob, validating its extent and its root before anything
    /// walks it.
    /// </summary>
    /// <param name="blob">The cell's slice of <c>CBSP</c>, exactly its declared length.</param>
    /// <param name="source">What to call the map in a message.</param>
    /// <param name="cell">The directory record this blob belongs to, for the message.</param>
    /// <exception cref="ScmapFormatException">The blob is not a well-formed flat tree.</exception>
    public ScmapChunkBsp(ReadOnlySpan<byte> blob, string source, scoped in ScmapChunkRecord cell)
    {
        string where = $"'{source}' chunk ({cell.X}, {cell.Y}, {cell.Z})";

        if (blob.Length < ScmapFormat.ChunkBspHeaderSize)
        {
            throw new ScmapFormatException(
                $"{where} has a {blob.Length}-byte BSP blob, short of the " +
                $"{ScmapFormat.ChunkBspHeaderSize}-byte header every one carries.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        RootIndex = BinaryPrimitives.ReadInt32LittleEndian(blob[4..]);

        long end = ScmapFormat.ChunkBspHeaderSize + ((long)count * ScmapFormat.FlatBspNodeSize);
        if (end > blob.Length)
        {
            throw new ScmapFormatException(
                $"{where} declares {count} BSP nodes, whose {ScmapFormat.FlatBspNodeSize}-byte records would " +
                $"end at byte {end} of a {blob.Length}-byte BSP blob.");
        }

        if (RootIndex < FlatBspNode.SolidLeaf || RootIndex >= count)
        {
            throw new ScmapFormatException(
                $"{where} names root {RootIndex}, which is neither a node index in [0, {count}) nor one of " +
                "the two leaf codes. A root out of range is a query that walks off the end of the block.");
        }

        Nodes = MemoryMarshal.Cast<byte, FlatBspNode>(
            blob.Slice(ScmapFormat.ChunkBspHeaderSize, (int)count * ScmapFormat.FlatBspNodeSize));
    }

    /// <summary>The internal nodes, in <see cref="BspFlattener"/> order.</summary>
    public ReadOnlySpan<FlatBspNode> Nodes { get; }

    /// <summary>The root's child code: a node index, or a leaf code for a bare-leaf tree.</summary>
    public int RootIndex { get; }

    /// <summary>
    /// True when <paramref name="point"/> lies inside solid space, walked straight
    /// off the mapped block.
    /// </summary>
    /// <remarks>
    /// The identical walk <see cref="FlatBspTree.ContainsPoint"/> performs, spelled
    /// here because that class takes a <c>ReadOnlyMemory</c> and a span into a
    /// mapped view is not one. Both call the same
    /// <c>System.Numerics.Plane.DotCoordinate</c> on the same value, which is what
    /// makes answer identity a structural property rather than an argument about
    /// float evaluation order.
    /// </remarks>
    public bool ContainsPoint(System.Numerics.Vector3 point)
    {
        int i = RootIndex;
        while (i >= 0)
        {
            ref readonly FlatBspNode node = ref Nodes[i];
            i = System.Numerics.Plane.DotCoordinate(node.Plane, point) >= 0f ? node.Front : node.Back;
        }

        return i == FlatBspNode.SolidLeaf;
    }

    /// <summary>
    /// The two sizes this format casts raw file bytes into, checked once against
    /// the runtime that is doing the casting.
    /// </summary>
    /// <remarks>
    /// <c>System.Numerics.Plane</c>'s field layout is overwhelmingly likely to be
    /// <c>{Vector3, float}</c> sequential and that is not a documented contract, so
    /// the constants are pinned rather than trusted. Called by the reader and by
    /// the writer, so a runtime that moved either would refuse the file rather than
    /// misread it.
    /// </remarks>
    /// <exception cref="ScmapFormatException">This runtime lays either struct out differently.</exception>
    public static void RequireNodeLayout()
    {
        int plane = Unsafe.SizeOf<System.Numerics.Plane>();
        int node = Unsafe.SizeOf<FlatBspNode>();

        if (plane == 16 && node == ScmapFormat.FlatBspNodeSize) return;

        throw new ScmapFormatException(
            $"This runtime lays out Plane as {plane} bytes and FlatBspNode as {node}, and the .scmap format " +
            $"casts file bytes into both at 16 and {ScmapFormat.FlatBspNodeSize}. Every compiled map's BSP " +
            "blob would be read at the wrong stride.");
    }
}
