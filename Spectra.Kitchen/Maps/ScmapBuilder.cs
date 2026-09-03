using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps.Compiled;

namespace Spectra.Kitchen.Maps;

/// <summary>
/// Assembles the five tables of a compiled map, claims the codes the sections that
/// do not exist yet will use, and hands the result to <see cref="ScmapWriter"/>.
/// </summary>
/// <remarks>
/// <para><b>Strings are interned at BUILD time in a canonical order, never as the
/// caller calls.</b> The specification asks for first-reference order during the
/// canonical node walk, and interning as each <c>Add</c> arrives would satisfy
/// that only while the cook happened to call in walk order: a bake that gathered
/// its materials before its nodes, or gathered them on a worker, would emit a
/// different string blob for the same map with nothing failing. Interning here
/// makes the order a property of the FILE rather than of a control flow, and the
/// order is fixed: the scene name, then the asset table in its own order, then
/// node names in pre-order.</para>
/// <para><b>A material becomes an asset by its PATH, and there is no other way
/// in.</b> <c>MaterialRef.Id</c> is per-process interning order and means nothing
/// outside the process that handed it out, so a cook that wrote one produces a
/// file that loads perfectly in the test that wrote it and mis-textures the whole
/// world the moment a second map interns first. The only entry point resolves the
/// ref back to its path and refuses a ref this process cannot name, which makes
/// the mistake unreachable rather than merely reviewed against.</para>
/// <para><b>The chunk directory is SORTED here.</b> A cook walks its cells out of
/// a dictionary, and the canonical order is what makes two cooks of one map
/// byte-identical and a point lookup a binary search. Nodes are never sorted:
/// sibling order is authored data, and traversal order is placement order is carve
/// order.</para>
/// <para><b>Five four-character codes are claimed as EMPTY sections.</b>
/// <c>ENTT</c>, <c>ECON</c>, <c>SCPT</c>, <c>LUAB</c> and <c>LUAS</c> carry
/// nothing until the milestones that fill them, and a reader steps over an unknown
/// code, so writing them now costs 32 bytes each and buys the guarantee that
/// nothing else takes the code. <c>RGNI</c> and <c>BMDL</c> are reserved with no
/// producer and <see cref="ScmapWriter"/> refuses them by name.</para>
/// </remarks>
public sealed class ScmapBuilder
{
    private readonly List<ScmapAssetSource> _assets = [];
    private readonly Dictionary<string, uint> _assetLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScmapNodeSource> _nodes = [];
    private readonly List<ScmapChunkSource> _chunks = [];
    private readonly List<ScmapSpawnSource> _spawns = [];
    private readonly List<ScmapBrushSourceEntry> _brushes = [];

    /// <summary>Creates a builder for a scene.</summary>
    /// <param name="sceneName">The scene's name, interned into <c>STRT</c>.</param>
    public ScmapBuilder(string sceneName)
    {
        ArgumentNullException.ThrowIfNull(sceneName);
        SceneName = sceneName;
    }

    /// <summary>The scene's name.</summary>
    public string SceneName { get; }

    /// <summary>How many assets the map references.</summary>
    public int AssetCount => _assets.Count;

    /// <summary>How many nodes have been added.</summary>
    public int NodeCount => _nodes.Count;

    /// <summary>How many cells the directory will carry.</summary>
    public int ChunkCount => _chunks.Count;

    /// <summary>How many authored brushes <c>BRSH</c> will carry.</summary>
    public int BrushSourceCount => _brushes.Count;

    /// <summary>Adds a spawn point.</summary>
    public void AddSpawn(ScmapSpawnSource spawn) => _spawns.Add(spawn);

    /// <summary>
    /// Adds an asset reference, or returns the index one already has.
    /// </summary>
    /// <remarks>
    /// Keyed case-insensitively on the normalised path, matching the pack's asset
    /// identity and the engine's own caches: two spellings of one path are one
    /// asset, and letting them be two would put the same texture in the file twice
    /// under indices that compare unequal.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The path is already present under a different kind.</exception>
    public uint AddAsset(ScmapAssetSource asset)
    {
        string normalized = ContentRoot.NormalizeRelativePath(asset.ContentPath);

        if (_assetLookup.TryGetValue(normalized, out uint existing))
        {
            ScmapAssetSource had = _assets[(int)existing];
            if (had.Kind != asset.Kind)
            {
                throw new InvalidOperationException(
                    $"Asset '{normalized}' was added as {had.Kind} and again as {asset.Kind}. One path is one " +
                    "asset, so a reader would have to choose which kind it is, and choosing silently is how " +
                    "a material gets loaded as a model.");
            }

            return existing;
        }

        var index = (uint)_assets.Count;
        _assets.Add(asset with { ContentPath = normalized });
        _assetLookup[normalized] = index;
        return index;
    }

    /// <summary>
    /// Adds a material reference by resolving it back to the path it was interned
    /// from.
    /// </summary>
    /// <remarks>
    /// The only way a material reaches the asset table, deliberately. See the
    /// remarks on this class: the id is a per-process number and writing it is the
    /// shorter, wronger code.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The reference is the engine default (which names no path) or was never
    /// interned in this process.
    /// </exception>
    public uint AddMaterial(MaterialRef material, ulong contentHash = 0)
    {
        if (material.IsDefault)
        {
            throw new InvalidOperationException(
                "The default material names no path, so it has no asset-table row. A surface that wears it " +
                "names no asset at all, which is what the engine's fallback to the default material already " +
                "means at load.");
        }

        if (!MaterialRegistry.TryGetPath(material, out string path))
        {
            throw new InvalidOperationException(
                $"Material id {material.Id} was never interned in this process, so there is no path to write. " +
                "An id is per-process interning order and means nothing in a file.");
        }

        return AddAsset(new ScmapAssetSource(PackEntryKind.Material, path, contentHash));
    }

    /// <summary>Adds a node. Returns the index it was placed at.</summary>
    /// <remarks>
    /// Call in pre-order. The parent index is validated against what has been
    /// added so far, which is the same invariant a forward-pass loader relies on:
    /// a parent always precedes its child.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The parent index is not a node already added, or the payload kind or
    /// declared state has no meaning.
    /// </exception>
    public int AddNode(ScmapNodeSource node)
    {
        int index = _nodes.Count;
        string name = node.Name ?? string.Empty;

        if (node.ParentIndex < -1 || node.ParentIndex >= index)
        {
            throw new InvalidOperationException(
                $"Node {index} ('{name}') names parent {node.ParentIndex}. Records are pre-order, so a parent " +
                "index is -1 or a node already added; anything else is a graph a forward-pass loader walks " +
                "forever.");
        }

        if (node.PayloadKind == ScmapPayloadKind.RetiredBrushModel)
        {
            throw new InvalidOperationException(
                $"Node {index} ('{name}') declares payload kind 3, which is retired and carries no meaning. " +
                "An entity-owned brush is a part brush wearing the entity-owned flag; the value is burned " +
                "rather than reused, because an enum value in a shipped format must never mean two things.");
        }

        if (!Enum.IsDefined(node.PayloadKind))
        {
            throw new InvalidOperationException(
                $"Node {index} ('{name}') declares payload kind {(ushort)node.PayloadKind}, which this format " +
                "has no meaning for.");
        }

        if (node.DeclaredState == ScmapNodeState.Invalid)
        {
            throw new InvalidOperationException(
                $"Node {index} ('{name}') declares state 3, which is the unused encoding of a two-bit field " +
                "rather than a fourth state. A reader tolerates it as a per-node defect; a cook is the loud " +
                "gate and refuses it.");
        }

        _nodes.Add(node with { Name = name });
        return index;
    }

    /// <summary>Adds a cell to the chunk directory.</summary>
    /// <remarks>
    /// Order is free here and canonical in the file: the directory is sorted at
    /// build time, because a cook walks its cells out of a dictionary and two
    /// cooks of one map must produce one file.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The cell is already in the directory, its submeshes are not in ascending
    /// asset order, or a submesh's arrays do not describe whole vertices and whole
    /// triangles.
    /// </exception>
    public void AddChunk(ScmapChunkSource chunk)
    {
        for (int i = 0; i < _chunks.Count; i++)
        {
            if (_chunks[i].Coord != chunk.Coord) continue;

            throw new InvalidOperationException(
                $"Cell ({chunk.Coord.X}, {chunk.Coord.Y}, {chunk.Coord.Z}) is already in the chunk directory. " +
                "One cell owns one entry, and a duplicate makes a binary search answer whichever it lands on.");
        }

        ValidateSubmeshes(chunk);

        if (chunk.BspNodes is { Length: > 0 } &&
            (chunk.BspRootIndex < FlatBspNode.SolidLeaf || chunk.BspRootIndex >= chunk.BspNodes.Length))
        {
            throw new InvalidOperationException(
                $"Cell ({chunk.Coord.X}, {chunk.Coord.Y}, {chunk.Coord.Z}) names BSP root " +
                $"{chunk.BspRootIndex} over {chunk.BspNodes.Length} nodes, which is neither an index into them " +
                "nor a leaf code. A root out of range is a query that walks off the end of the block.");
        }

        _chunks.Add(chunk);
    }

    /// <summary>Adds one authored brush's planes and faces to <c>BRSH</c>.</summary>
    /// <remarks>
    /// Call in node pre-order, which is what makes the section a pure function of
    /// the map. The node index is not validated against the node list here because
    /// a bake adds nodes and brushes in one pass; <see cref="Write"/> checks every
    /// one before a byte is emitted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The face count does not match the plane count.</exception>
    public void AddBrushSource(ScmapBrushSourceEntry brush)
    {
        ArgumentNullException.ThrowIfNull(brush.Planes);
        ArgumentNullException.ThrowIfNull(brush.Faces);

        if (brush.Planes.Length != brush.Faces.Length)
        {
            throw new InvalidOperationException(
                $"Brush on node {brush.NodeIndex} has {brush.Planes.Length} planes and {brush.Faces.Length} " +
                "faces. One face per plane is the invariant the whole per-face material path rests on, so a " +
                "mismatch is an indexing bug rather than a surface that renders wrongly.");
        }

        _brushes.Add(brush);
    }

    private static void ValidateSubmeshes(ScmapChunkSource chunk)
    {
        if (chunk.Submeshes is not { Length: > 0 }) return;

        for (int i = 0; i < chunk.Submeshes.Length; i++)
        {
            ScmapSubmeshSource submesh = chunk.Submeshes[i];
            ArgumentNullException.ThrowIfNull(submesh.Vertices);
            ArgumentNullException.ThrowIfNull(submesh.Indices);

            if (i > 0 && chunk.Submeshes[i - 1].AssetIndex >= submesh.AssetIndex)
            {
                // Ascending asset index is a total order over a VALUE key, which is
                // the whole reason two compiles of one cell emit one file. Ascending
                // material id would not be: an id is per-process interning order.
                throw new InvalidOperationException(
                    $"Cell ({chunk.Coord.X}, {chunk.Coord.Y}, {chunk.Coord.Z}) has submeshes out of ascending " +
                    $"asset order at {i}: asset {chunk.Submeshes[i - 1].AssetIndex} is followed by asset " +
                    $"{submesh.AssetIndex}.");
            }

            if (submesh.Vertices.Length % ScmapFormat.StandardVertexStrideFloats != 0)
            {
                throw new InvalidOperationException(
                    $"Cell ({chunk.Coord.X}, {chunk.Coord.Y}, {chunk.Coord.Z}) submesh {i} carries " +
                    $"{submesh.Vertices.Length} floats, which is not a whole number of " +
                    $"{ScmapFormat.StandardVertexStrideFloats}-float vertices.");
            }

            if (submesh.Indices.Length % 3 != 0)
            {
                throw new InvalidOperationException(
                    $"Cell ({chunk.Coord.X}, {chunk.Coord.Y}, {chunk.Coord.Z}) submesh {i} carries " +
                    $"{submesh.Indices.Length} indices, which is not a whole number of triangles.");
            }
        }
    }

    /// <summary>Builds the whole file.</summary>
    /// <param name="sourceMapDigest">
    /// <c>XxHash128</c> of the source bundle's canonical enumeration. See
    /// <see cref="MapBundleDigest"/>.
    /// </param>
    /// <param name="mapFormatVersion">The authored map grammar the bake read.</param>
    /// <param name="flags">What optional content the cook put in the file.</param>
    public byte[] Build(UInt128 sourceMapDigest, uint mapFormatVersion, ScmapFlags flags = ScmapFlags.None)
    {
        using var buffer = new MemoryStream();
        Write(buffer, sourceMapDigest, mapFormatVersion, flags);
        return buffer.ToArray();
    }

    /// <summary>Builds the whole file into <paramref name="stream"/>.</summary>
    public void Write(Stream stream, UInt128 sourceMapDigest, uint mapFormatVersion, ScmapFlags flags = ScmapFlags.None)
    {
        var strings = new ScmapStringTableBuilder();

        // The canonical interning order, and the only place it is stated. Every
        // string in the file gets its index here rather than wherever a caller
        // happened to mention it first.
        uint sceneNameString = strings.Intern(SceneName);

        var assetPathStrings = new uint[_assets.Count];
        for (int i = 0; i < _assets.Count; i++) assetPathStrings[i] = strings.Intern(_assets[i].ContentPath);

        var nodeNameStrings = new uint[_nodes.Count];
        for (int i = 0; i < _nodes.Count; i++) nodeNameStrings[i] = strings.Intern(_nodes[i].Name);

        byte[] stringBody = strings.Build();
        byte[] assetBody = BuildAssets(assetPathStrings);
        byte[] metaBody = BuildMeta(sceneNameString);
        byte[] nodeBody = BuildNodes(nodeNameStrings);
        byte[] chunkBody = BuildChunks(out byte[] meshBody, out byte[] bspBody);
        byte[]? brushBody = BuildBrushSource();

        // Derived rather than taken. The flag says a BRSH section is present and
        // nothing else, and the reader cross-checks the two, so a caller allowed to
        // set it independently could produce a file whose header and table disagree
        // about whether a level's brush planes exist.
        ScmapFlags fileFlags = brushBody is null
            ? flags & ~ScmapFlags.HasBrushSource
            : flags | ScmapFlags.HasBrushSource;

        var writer = new ScmapWriter(fileFlags, sourceMapDigest, mapFormatVersion);

        writer.AddSection(ScmapFormat.StringSection, stringBody);
        writer.AddSection(ScmapFormat.AssetSection, assetBody);
        writer.AddSection(ScmapFormat.MetaSection, metaBody);
        writer.AddSection(ScmapFormat.NodeSection, nodeBody);
        writer.AddSection(ScmapFormat.ChunkDirectorySection, chunkBody);
        writer.AddSection(ScmapFormat.ChunkMeshSection, meshBody);
        writer.AddSection(ScmapFormat.ChunkBspSection, bspBody);

        // Claimed and empty: the four entity and script codes are filled by the
        // milestones that own them, and a reader steps over a code it does not
        // know, so writing them now costs 32 bytes each and buys the guarantee that
        // nothing else takes the code.
        writer.AddSection(ScmapFormat.EntitySection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.EntityConnectionSection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.ScriptSection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.ScriptBytecodeSection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.ScriptSourceSection, ReadOnlySpan<byte>.Empty);

        // Last, so the sections a load always reads sit at the front of the file.
        // An absent BRSH is an ABSENT section rather than an empty one, because the
        // header flag beside it is a claim about presence and an empty section is
        // still present.
        if (brushBody is not null) writer.AddSection(ScmapFormat.BrushSourceSection, brushBody);

        writer.Write(stream);
    }

    private byte[] BuildAssets(uint[] pathStrings)
    {
        var body = new byte[ScmapFormat.AssetCountSize + (_assets.Count * ScmapFormat.AssetEntrySize)];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)_assets.Count);

        for (int i = 0; i < _assets.Count; i++)
        {
            var entry = new ScmapAssetEntry(_assets[i].Kind, pathStrings[i], _assets[i].ContentHash);
            MemoryMarshal.Write(span[(ScmapFormat.AssetCountSize + (i * ScmapFormat.AssetEntrySize))..], in entry);
        }

        return body;
    }

    private byte[] BuildMeta(uint sceneNameString)
    {
        var body = new byte[ScmapFormat.MetaPreambleSize + (_spawns.Count * ScmapFormat.SpawnRecordSize)];
        Span<byte> span = body;

        var meta = new ScmapMeta(sceneNameString, (uint)_spawns.Count);
        MemoryMarshal.Write(span, in meta);

        for (int i = 0; i < _spawns.Count; i++)
        {
            var record = new ScmapSpawn(_spawns[i].Position, _spawns[i].Rotation);
            MemoryMarshal.Write(span[(ScmapFormat.MetaPreambleSize + (i * ScmapFormat.SpawnRecordSize))..], in record);
        }

        return body;
    }

    private byte[] BuildNodes(uint[] nameStrings)
    {
        var body = new byte[ScmapFormat.NodePreambleSize + (_nodes.Count * ScmapFormat.NodeRecordSize)];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)_nodes.Count);

        for (int i = 0; i < _nodes.Count; i++)
        {
            ScmapNodeSource source = _nodes[i];
            var record = new ScmapNodeRecord(
                source.Id,
                nameStrings[i],
                source.ParentIndex,
                source.LocalTransform.Position,
                source.LocalTransform.Rotation,
                source.LocalTransform.Scale,
                source.PayloadKind,
                source.PayloadFlags,
                source.DeclaredRealm,
                source.DeclaredState,
                source.PayloadIndex);

            MemoryMarshal.Write(span[(ScmapFormat.NodePreambleSize + (i * ScmapFormat.NodeRecordSize))..], in record);
        }

        return body;
    }

    /// <summary>
    /// Sorts the directory and lays the two blob sections out in that same order.
    /// </summary>
    /// <remarks>
    /// <para><b>One pass, so the directory and the blobs cannot disagree about
    /// order.</b> A second pass over a second ordering is exactly how a cell ends
    /// up pointing at its neighbour's geometry, and nothing downstream can tell:
    /// the file parses, every offset is in range, and the level renders somebody
    /// else's walls.</para>
    /// <para><b>Every blob's own length is a multiple of the payload alignment</b>,
    /// because each array inside it is padded up after itself, so the blobs tile
    /// with no gap and a cell's declared size is the same number whether you count
    /// its content or its footprint. The padding goes through
    /// <see cref="ScmapLayout.PaddedSectionSize"/> and nothing else - one function
    /// decides what a padded run costs, at every scale in this format.</para>
    /// </remarks>
    private byte[] BuildChunks(out byte[] meshBody, out byte[] bspBody)
    {
        ScmapChunkSource[] sorted = [.. _chunks];
        Array.Sort(sorted, static (a, b) => a.Coord.CompareTo(b.Coord));

        using var meshes = new MemoryStream();
        using var bsps = new MemoryStream();

        var body = new byte[ScmapFormat.ChunkPreambleSize + (sorted.Length * ScmapFormat.ChunkRecordSize)];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)sorted.Length);

        for (int i = 0; i < sorted.Length; i++)
        {
            ScmapChunkSource source = sorted[i];

            var meshOffset = (uint)meshes.Length;
            uint meshSize = WriteChunkMesh(meshes, source);

            var bspOffset = (uint)bsps.Length;
            uint bspSize = WriteChunkBsp(bsps, source);

            var record = new ScmapChunkRecord(
                source.Coord.X,
                source.Coord.Y,
                source.Coord.Z,
                source.RenderBounds.Min,
                source.RenderBounds.Max,
                meshSize == 0 ? 0 : meshOffset,
                meshSize,
                bspSize == 0 ? 0 : bspOffset,
                bspSize);

            MemoryMarshal.Write(span[(ScmapFormat.ChunkPreambleSize + (i * ScmapFormat.ChunkRecordSize))..], in record);
        }

        meshBody = meshes.ToArray();
        bspBody = bsps.ToArray();
        return body;
    }

    // One cell's CMSH blob, or nothing at all for a cell that owns no render
    // geometry - which is legal and common, and is what the compile itself does
    // for a resident-only cell.
    private static uint WriteChunkMesh(MemoryStream blobs, in ScmapChunkSource cell)
    {
        if (cell.Submeshes is not { Length: > 0 }) return 0;

        ScmapSubmeshSource[] submeshes = cell.Submeshes;
        long start = blobs.Length;

        long directory = ScmapLayout.PaddedSectionSize(
            ScmapFormat.ChunkMeshHeaderSize + ((long)submeshes.Length * ScmapFormat.ChunkSubmeshEntrySize));

        // The arrays are placed BEFORE any of them is written, because a directory
        // record has to carry an offset the writer has not reached yet. Both passes
        // walk the same list in the same order and both take their padding from the
        // one function, which is what keeps them in step.
        var entries = new ScmapSubmeshEntry[submeshes.Length];
        long cursor = directory;
        for (int i = 0; i < submeshes.Length; i++)
        {
            ScmapSubmeshSource submesh = submeshes[i];

            long vertexOffset = cursor;
            cursor += ScmapLayout.PaddedSectionSize((long)submesh.Vertices.Length * sizeof(float));

            long indexOffset = cursor;
            cursor += ScmapLayout.PaddedSectionSize((long)submesh.Indices.Length * sizeof(uint));

            entries[i] = new ScmapSubmeshEntry(
                submesh.AssetIndex,
                (uint)(submesh.Vertices.Length / ScmapFormat.StandardVertexStrideFloats),
                (uint)submesh.Indices.Length,
                (uint)vertexOffset,
                (uint)indexOffset);
        }

        Span<byte> header = stackalloc byte[ScmapFormat.ChunkMeshHeaderSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)submeshes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], ScmapFormat.StandardVertexStrideFloats);
        blobs.Write(header);

        Span<byte> entry = stackalloc byte[ScmapFormat.ChunkSubmeshEntrySize];
        for (int i = 0; i < entries.Length; i++)
        {
            MemoryMarshal.Write(entry, in entries[i]);
            blobs.Write(entry);
        }

        WriteZeros(blobs, start + directory - blobs.Length);

        for (int i = 0; i < submeshes.Length; i++)
        {
            ScmapSubmeshSource submesh = submeshes[i];

            blobs.Write(MemoryMarshal.AsBytes<float>(submesh.Vertices));
            WriteZeros(blobs, start + entries[i].IndexOffset - blobs.Length);

            blobs.Write(MemoryMarshal.AsBytes<uint>(submesh.Indices));
            WriteZeros(blobs, ScmapLayout.PaddedSectionSize(blobs.Length - start) - (blobs.Length - start));
        }

        return (uint)(blobs.Length - start);
    }

    // One cell's CBSP blob. A null node array means the cell has no tree at all;
    // an EMPTY one is a tree that is a single bare leaf, and it still gets a blob
    // so that the root's leaf code survives - solid and empty are different
    // answers, and a missing blob could not tell them apart.
    private static uint WriteChunkBsp(MemoryStream blobs, in ScmapChunkSource cell)
    {
        if (cell.BspNodes is null) return 0;

        long start = blobs.Length;

        Span<byte> header = stackalloc byte[ScmapFormat.ChunkBspHeaderSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)cell.BspNodes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], cell.BspRootIndex);
        blobs.Write(header);

        blobs.Write(MemoryMarshal.AsBytes<FlatBspNode>(cell.BspNodes));
        WriteZeros(blobs, ScmapLayout.PaddedSectionSize(blobs.Length - start) - (blobs.Length - start));

        return (uint)(blobs.Length - start);
    }

    // The BRSH section, or null when this cook kept no brush source at all. Null
    // rather than an empty body, because the header flag beside it claims the
    // section is PRESENT and an empty section is present.
    private byte[]? BuildBrushSource()
    {
        if (_brushes.Count == 0) return null;

        int planes = 0;
        foreach (ScmapBrushSourceEntry brush in _brushes)
        {
            if (brush.NodeIndex < 0 || brush.NodeIndex >= _nodes.Count)
            {
                throw new InvalidOperationException(
                    $"A kept brush names node {brush.NodeIndex} of a {_nodes.Count}-node map. A brush that " +
                    "cannot name its node has no transform, so a load would carve it at the origin.");
            }

            planes += brush.Planes.Length;
        }

        long records = (long)_brushes.Count * ScmapFormat.BrushSourceRecordSize;
        long planeStart = ScmapLayout.PaddedSectionSize(ScmapFormat.BrushSourceHeaderSize + records);
        long faceStart = ScmapLayout.PaddedSectionSize(planeStart + ((long)planes * ScmapFormat.PlaneSize));
        long total = faceStart + ((long)planes * ScmapFormat.BrushFaceRecordSize);

        var body = new byte[total];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)_brushes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)planes);

        int plane = 0;
        for (int i = 0; i < _brushes.Count; i++)
        {
            ScmapBrushSourceEntry brush = _brushes[i];
            var record = new ScmapBrushRecord((uint)brush.NodeIndex, (uint)brush.Planes.Length, (uint)plane);
            MemoryMarshal.Write(
                span[(ScmapFormat.BrushSourceHeaderSize + (i * ScmapFormat.BrushSourceRecordSize))..], in record);

            for (int f = 0; f < brush.Planes.Length; f++, plane++)
            {
                MemoryMarshal.Write(
                    span[(int)(planeStart + ((long)plane * ScmapFormat.PlaneSize))..], in brush.Planes[f]);

                ScmapFaceSource face = brush.Faces[f];
                var faceRecord = new ScmapFaceRecord(
                    face.AssetIndex,
                    face.UAxis,
                    face.VAxis,
                    face.UOffset,
                    face.VOffset,
                    face.UScale,
                    face.VScale);

                MemoryMarshal.Write(
                    span[(int)(faceStart + ((long)plane * ScmapFormat.BrushFaceRecordSize))..], in faceRecord);
            }
        }

        return body;
    }

    // Padding is written, never seeked over: a seek past the end of a stream leaves
    // the gap holding whatever the filesystem gives back, which on most filesystems
    // is zeros and on none of them is a promise.
    private static void WriteZeros(MemoryStream blobs, long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Span<byte> zeros = stackalloc byte[ScmapFormat.PayloadAlignment];
        zeros.Clear();

        while (count > 0)
        {
            int chunk = (int)Math.Min(count, zeros.Length);
            blobs.Write(zeros[..chunk]);
            count -= chunk;
        }
    }
}
