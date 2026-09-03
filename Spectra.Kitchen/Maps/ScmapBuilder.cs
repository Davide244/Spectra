using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Packs;
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
    /// The cell is already in the directory, or it points at a chunk blob this
    /// stage does not write.
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

        if (chunk.MeshSize != 0 || chunk.BspSize != 0)
        {
            // Loud rather than tolerated. A directory pointing into an empty CMSH
            // is a cell whose geometry silently does not exist, which renders as
            // a hole in the world rather than as an error.
            throw new InvalidOperationException(
                $"Cell ({chunk.Coord.X}, {chunk.Coord.Y}, {chunk.Coord.Z}) declares a {chunk.MeshSize}-byte " +
                $"mesh blob and a {chunk.BspSize}-byte BSP blob, and this builder writes the directory with " +
                "the CMSH and CBSP sections empty. The blobs arrive with the map bake.");
        }

        _chunks.Add(chunk);
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
        byte[] chunkBody = BuildChunks();

        var writer = new ScmapWriter(flags, sourceMapDigest, mapFormatVersion);

        writer.AddSection(ScmapFormat.StringSection, stringBody);
        writer.AddSection(ScmapFormat.AssetSection, assetBody);
        writer.AddSection(ScmapFormat.MetaSection, metaBody);
        writer.AddSection(ScmapFormat.NodeSection, nodeBody);
        writer.AddSection(ScmapFormat.ChunkDirectorySection, chunkBody);

        // Claimed and empty. CMSH and CBSP are filled by the map bake; the four
        // entity and script codes by the milestones that own them.
        writer.AddSection(ScmapFormat.ChunkMeshSection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.ChunkBspSection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.EntitySection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.EntityConnectionSection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.ScriptSection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.ScriptBytecodeSection, ReadOnlySpan<byte>.Empty);
        writer.AddSection(ScmapFormat.ScriptSourceSection, ReadOnlySpan<byte>.Empty);

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

    private byte[] BuildChunks()
    {
        ScmapChunkSource[] sorted = [.. _chunks];
        Array.Sort(sorted, static (a, b) => a.Coord.CompareTo(b.Coord));

        var body = new byte[ScmapFormat.ChunkPreambleSize + (sorted.Length * ScmapFormat.ChunkRecordSize)];
        Span<byte> span = body;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)sorted.Length);

        for (int i = 0; i < sorted.Length; i++)
        {
            ScmapChunkSource source = sorted[i];
            var record = new ScmapChunkRecord(
                source.Coord.X,
                source.Coord.Y,
                source.Coord.Z,
                source.RenderBounds.Min,
                source.RenderBounds.Max,
                source.MeshOffset,
                source.MeshSize,
                source.BspOffset,
                source.BspSize);

            MemoryMarshal.Write(span[(ScmapFormat.ChunkPreambleSize + (i * ScmapFormat.ChunkRecordSize))..], in record);
        }

        return body;
    }
}
