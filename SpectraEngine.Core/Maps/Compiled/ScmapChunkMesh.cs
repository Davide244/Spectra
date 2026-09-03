using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// One 24-byte <c>CMSH</c> submesh directory record: where one cell's geometry
/// for one material sits inside that cell's blob.
/// </summary>
/// <remarks>
/// <para><b><see cref="AssetIndex"/> is an index into <c>ASTB</c> and NEVER a
/// <c>MaterialRef.Id</c>.</b> The registry hands out ids in per-process interning
/// order, so a cook that wrote one produces a file that loads perfectly in the
/// test that wrote it and mis-textures the entire world the moment a second map
/// interns first. The wrong version is also shorter code, which is why it is
/// written down where the field is.</para>
/// <para><b>The arrays are self-contained and zero-based.</b> Indices are based at
/// this submesh's own first vertex, not at the cell's, so each entry hands
/// straight to <c>Renderer.CreateMesh</c> with no slicing and no offset
/// arithmetic. That mirrors the artifact the compile produces, and the contrast
/// with <c>.smodel</c> - one buffer, submeshes as index ranges - is deliberate on
/// both sides: an LOD switch has to be a draw-range change, while a chunk submesh
/// is created and destroyed per cell as the world recompiles.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapSubmeshEntry
{
    /// <summary>
    /// Index into <c>ASTB</c> of the material every triangle here wears, or
    /// <see cref="ScmapFormat.NoAssetIndex"/> when the surfaces name none.
    /// </summary>
    public readonly uint AssetIndex;

    /// <summary>Vertices in this submesh.</summary>
    public readonly uint VertexCount;

    /// <summary>Indices in this submesh, always a multiple of three.</summary>
    public readonly uint IndexCount;

    /// <summary>Reserved; written zero.</summary>
    public readonly uint Reserved;

    /// <summary>Byte offset of the vertex array from the start of this cell's blob. 16-byte aligned.</summary>
    public readonly uint VertexOffset;

    /// <summary>Byte offset of the index array from the start of this cell's blob. 16-byte aligned.</summary>
    public readonly uint IndexOffset;

    /// <summary>Builds one directory record. Every field is assigned.</summary>
    public ScmapSubmeshEntry(
        uint assetIndex,
        uint vertexCount,
        uint indexCount,
        uint vertexOffset,
        uint indexOffset)
    {
        AssetIndex = assetIndex;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        Reserved = 0;
        VertexOffset = vertexOffset;
        IndexOffset = indexOffset;
    }

    /// <summary>Whether this submesh names a row of the asset table at all.</summary>
    public bool NamesAsset => AssetIndex != ScmapFormat.NoAssetIndex;
}

/// <summary>
/// One cell's <c>CMSH</c> blob, read in place: a directory plus the vertex and
/// index arrays it addresses.
/// </summary>
/// <remarks>
/// <para><b>A <c>ref struct</c> for the reason <see cref="ScmapDocument"/> is
/// one</b>: the bytes are normally a memory-mapped view of a pack payload, and
/// unmapping a view while a span into it is alive is an access violation with no
/// managed stack.</para>
/// <para><b>Every array is 16-byte aligned within the blob</b>, and the blob
/// itself starts at a 16-byte offset inside a section that is itself 16-byte
/// aligned, so the three compose and
/// <c>MemoryMarshal.Cast&lt;byte, float&gt;</c> over the mapped view is legal all
/// the way down. The alignment is asserted here rather than assumed, because a
/// blob one byte out is not an exception anywhere: it is a vertex array read from
/// the middle of somebody else's.</para>
/// <para><b>Submeshes are in ASCENDING asset index, and that is checked.</b> It is
/// a total order over a value key, which is what makes two compiles of one cell
/// emit the same submeshes in the same order; the check is here because a
/// directory out of that order is a claim about the FILE rather than about the
/// writer, and only the first survives a file edited afterwards.</para>
/// </remarks>
public readonly ref struct ScmapChunkMesh
{
    private readonly ReadOnlySpan<byte> _blob;

    /// <summary>
    /// Parses one cell's blob, validating every range before anything indexes with
    /// one.
    /// </summary>
    /// <param name="blob">The cell's slice of <c>CMSH</c>, exactly its declared length.</param>
    /// <param name="source">What to call the map in a message.</param>
    /// <param name="cell">The directory record this blob belongs to, for the message.</param>
    /// <exception cref="ScmapFormatException">The blob is not a well-formed chunk mesh.</exception>
    public ScmapChunkMesh(ReadOnlySpan<byte> blob, string source, scoped in ScmapChunkRecord cell)
    {
        string where = $"'{source}' chunk ({cell.X}, {cell.Y}, {cell.Z})";
        _blob = blob;

        if (blob.Length < ScmapFormat.ChunkMeshHeaderSize)
        {
            throw new ScmapFormatException(
                $"{where} has a {blob.Length}-byte mesh blob, short of the " +
                $"{ScmapFormat.ChunkMeshHeaderSize}-byte header every one carries.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        VertexStrideFloats = BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]);

        if (VertexStrideFloats == 0)
        {
            throw new ScmapFormatException(
                $"{where} declares a zero-float vertex stride, which makes every vertex count meaningless.");
        }

        long directoryEnd =
            ScmapFormat.ChunkMeshHeaderSize + ((long)count * ScmapFormat.ChunkSubmeshEntrySize);

        if (directoryEnd > blob.Length)
        {
            throw new ScmapFormatException(
                $"{where} declares {count} submeshes, whose {ScmapFormat.ChunkSubmeshEntrySize}-byte records " +
                $"would end at byte {directoryEnd} of a {blob.Length}-byte mesh blob.");
        }

        Submeshes = MemoryMarshal.Cast<byte, ScmapSubmeshEntry>(
            blob.Slice(ScmapFormat.ChunkMeshHeaderSize, (int)count * ScmapFormat.ChunkSubmeshEntrySize));

        for (int i = 0; i < Submeshes.Length; i++)
        {
            ScmapSubmeshEntry entry = Submeshes[i];

            if (i > 0 && Submeshes[i - 1].AssetIndex >= entry.AssetIndex)
            {
                // Not tidiness. Ascending asset index is what makes two compiles
                // of one cell emit one file, and a duplicate index is two
                // submeshes claiming one material, which draws the same surface
                // twice and z-fights.
                throw new ScmapFormatException(
                    $"{where} has submeshes out of ascending asset order at record {i}: asset " +
                    $"{Submeshes[i - 1].AssetIndex} is followed by asset {entry.AssetIndex}.");
            }

            if (entry.IndexCount % 3 != 0)
            {
                throw new ScmapFormatException(
                    $"{where} submesh {i} declares {entry.IndexCount} indices, which is not a whole number of " +
                    "triangles.");
            }

            RequireArray(where, i, "vertex", entry.VertexOffset, (long)entry.VertexCount * VertexStrideFloats * sizeof(float), blob.Length);
            RequireArray(where, i, "index", entry.IndexOffset, (long)entry.IndexCount * sizeof(uint), blob.Length);
        }
    }

    /// <summary>Floats per vertex, which this engine writes as eight.</summary>
    public uint VertexStrideFloats { get; }

    /// <summary>The submesh directory, in ascending asset index.</summary>
    public ReadOnlySpan<ScmapSubmeshEntry> Submeshes { get; }

    /// <summary>
    /// Submesh <paramref name="index"/>'s interleaved vertex data, cast in place.
    /// </summary>
    public ReadOnlySpan<float> Vertices(int index)
    {
        ScmapSubmeshEntry entry = Submeshes[index];
        return MemoryMarshal.Cast<byte, float>(
            _blob.Slice((int)entry.VertexOffset, (int)entry.VertexCount * (int)VertexStrideFloats * sizeof(float)));
    }

    /// <summary>
    /// Submesh <paramref name="index"/>'s index data, cast in place. Zero-based at
    /// this submesh's own first vertex.
    /// </summary>
    public ReadOnlySpan<uint> Indices(int index)
    {
        ScmapSubmeshEntry entry = Submeshes[index];
        return MemoryMarshal.Cast<byte, uint>(
            _blob.Slice((int)entry.IndexOffset, (int)entry.IndexCount * sizeof(uint)));
    }

    /// <summary>Triangles across every submesh of this cell.</summary>
    public int TriangleCount
    {
        get
        {
            int triangles = 0;
            for (int i = 0; i < Submeshes.Length; i++) triangles += (int)(Submeshes[i].IndexCount / 3);
            return triangles;
        }
    }

    private static void RequireArray(
        string where, int index, string what, uint offset, long bytes, int blobLength)
    {
        // Subtraction rather than addition, because offset + length is exactly the
        // arithmetic a corrupt file makes wrap.
        if (offset > (ulong)blobLength || bytes > blobLength - offset)
        {
            throw new ScmapFormatException(
                $"{where} submesh {index} claims a {bytes}-byte {what} array at offset {offset} of a " +
                $"{blobLength}-byte mesh blob.");
        }

        if ((offset % ScmapFormat.PayloadAlignment) != 0)
        {
            throw new ScmapFormatException(
                $"{where} submesh {index} places its {what} array at offset {offset}, which is not a multiple " +
                $"of {ScmapFormat.PayloadAlignment}. The array is reinterpreted in place, so an unaligned " +
                "start is a read out of the middle of the array before it.");
        }
    }
}
