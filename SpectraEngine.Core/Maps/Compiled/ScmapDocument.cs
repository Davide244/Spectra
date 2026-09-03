using System;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// A validated <c>.scmap</c>, as spans into the bytes it sits in.
/// </summary>
/// <remarks>
/// <para><b>A <c>ref struct</c>, so a document provably cannot outlive the
/// mapping its spans point into.</b> The bytes are normally a memory-mapped view
/// of a pack payload, and unmapping a view while a span into it is alive is an
/// access violation with no managed stack: a crash nobody can attribute to a file.
/// The compiler enforces here what a comment could only ask for.</para>
/// <para><b>This is the document, not the scene.</b> Turning these records into a
/// <c>SceneNode</c> graph, GPU meshes and flat BSP trees is the runtime loader's
/// job; everything here is a bounds-checked view of what the cook wrote, so the
/// loader and the cook's own verifier read one parser rather than two.</para>
/// </remarks>
public readonly ref struct ScmapDocument
{
    internal ScmapDocument(
        string source,
        ScmapHeader header,
        ScmapStringTable strings,
        ReadOnlySpan<ScmapAssetEntry> assets,
        ScmapMeta meta,
        ReadOnlySpan<ScmapSpawn> spawns,
        ReadOnlySpan<ScmapNodeRecord> nodes,
        ReadOnlySpan<ScmapChunkRecord> chunks,
        ReadOnlySpan<byte> chunkMeshBlob,
        ReadOnlySpan<byte> chunkBspBlob,
        int skippedSectionCount,
        int invalidDeclaredStateCount)
    {
        Source = source;
        Header = header;
        Strings = strings;
        Assets = assets;
        Meta = meta;
        Spawns = spawns;
        Nodes = nodes;
        Chunks = chunks;
        ChunkMeshBlob = chunkMeshBlob;
        ChunkBspBlob = chunkBspBlob;
        SkippedSectionCount = skippedSectionCount;
        InvalidDeclaredStateCount = invalidDeclaredStateCount;
    }

    /// <summary>What to call this map in a message: a logical asset path, not a machine path.</summary>
    public string Source { get; }

    /// <summary>The file header, already validated.</summary>
    public ScmapHeader Header { get; }

    /// <summary>The <c>STRT</c> section.</summary>
    public ScmapStringTable Strings { get; }

    /// <summary>The <c>ASTB</c> section, in the order a load must intern it.</summary>
    public ReadOnlySpan<ScmapAssetEntry> Assets { get; }

    /// <summary>The <c>META</c> preamble.</summary>
    public ScmapMeta Meta { get; }

    /// <summary>The spawn records following the <c>META</c> preamble.</summary>
    public ReadOnlySpan<ScmapSpawn> Spawns { get; }

    /// <summary>The <c>NODE</c> section, in pre-order.</summary>
    public ReadOnlySpan<ScmapNodeRecord> Nodes { get; }

    /// <summary>The <c>CHDR</c> section, sorted by cell coordinate.</summary>
    public ReadOnlySpan<ScmapChunkRecord> Chunks { get; }

    /// <summary>
    /// The whole <c>CMSH</c> section, which every chunk record's mesh offset is
    /// relative to. Empty when no cell owns render geometry.
    /// </summary>
    public ReadOnlySpan<byte> ChunkMeshBlob { get; }

    /// <summary>
    /// The whole <c>CBSP</c> section, which every chunk record's BSP offset is
    /// relative to. Empty when no cell has a tree.
    /// </summary>
    public ReadOnlySpan<byte> ChunkBspBlob { get; }

    /// <summary>
    /// How many section records this reader stepped over because it did not know
    /// the code.
    /// </summary>
    /// <remarks>
    /// Reported rather than swallowed: skipping is the forward-compatibility
    /// mechanism, and a host that wants to say "this map carries data this build
    /// does not use" needs a number to say it with.
    /// </remarks>
    public int SkippedSectionCount { get; }

    /// <summary>
    /// How many node records declared the unused encoding of the two-bit state
    /// field.
    /// </summary>
    /// <remarks>
    /// A per-node load defect rather than a refusal, because one bad node is not a
    /// reason to refuse a level. Counted so a host can report it: silently reading
    /// such a node as <c>Inherit</c> is how a level ships with a node nobody knows
    /// is wrong.
    /// </remarks>
    public int InvalidDeclaredStateCount { get; }

    /// <summary>The name of node <paramref name="index"/>, decoded.</summary>
    public string NodeName(int index) => Strings.GetStringOrEmpty((int)Nodes[index].NameString);

    /// <summary>The content path of asset <paramref name="index"/>, decoded.</summary>
    public string AssetPath(int index) => Strings.GetStringOrEmpty((int)Assets[index].PathString);

    /// <summary>The scene's name, decoded.</summary>
    public string SceneName => Strings.GetStringOrEmpty((int)Meta.SceneNameString);
}
