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
        int chunkBspBlobFileOffset,
        ReadOnlySpan<byte> brushSourceSection,
        bool hasBrushSource,
        int skippedSectionCount,
        int invalidDeclaredStateCount)
    {
        BrushSourceSection = brushSourceSection;
        HasBrushSource = hasBrushSource;
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
        ChunkBspBlobFileOffset = chunkBspBlobFileOffset;
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
    /// Where <see cref="ChunkBspBlob"/> starts, counted from the first byte of
    /// the file. Zero when there is no <c>CBSP</c> section.
    /// </summary>
    /// <remarks>
    /// <b>A span cannot say where it came from, and a runtime load needs to
    /// know.</b> Every other table here is consumed inside the call that parses
    /// it - a chunk mesh goes straight to the GPU and the bytes are done with -
    /// but a per-cell BSP is QUERIED for the life of the level, off the mapping
    /// itself. The thing that owns that mapping is a <c>ContentBlob</c>, which
    /// hands out its span afresh on every access precisely so a released blob
    /// throws rather than reads freed address space, and re-deriving a cell's node
    /// array from that span needs a byte offset into the whole file. Recovering
    /// one by differencing two spans' addresses would work and would be a pointer
    /// comparison standing in for a number the reader already had.
    /// </remarks>
    public int ChunkBspBlobFileOffset { get; }

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

    /// <summary>
    /// The whole <c>BRSH</c> section, or empty when the cook kept no brush source.
    /// </summary>
    public ReadOnlySpan<byte> BrushSourceSection { get; }

    /// <summary>Whether this map carries authored brush planes at all.</summary>
    /// <remarks>
    /// <b>Presence, never permission.</b> A map with parts in it carries this
    /// section whatever the cook was asked for, because a part brush's planes live
    /// nowhere else; whether any particular brush may be carved is
    /// <c>ScmapBrushSource.IsReCarvable</c>, and reading this flag as licence to
    /// rebuild the static world is the double-geometry hazard.
    /// </remarks>
    public bool HasBrushSource { get; }

    /// <summary>Chunk <paramref name="index"/>'s mesh blob, parsed in place.</summary>
    /// <remarks>
    /// Call only when the record's <c>MeshSize</c> is non-zero: a cell with no
    /// owned render geometry has no blob at all, which is legal and common rather
    /// than an error.
    /// </remarks>
    /// <exception cref="ScmapFormatException">The blob is not a well-formed chunk mesh.</exception>
    public ScmapChunkMesh ChunkMesh(int index)
    {
        ScmapChunkRecord cell = Chunks[index];
        return new ScmapChunkMesh(
            ChunkMeshBlob.Slice((int)cell.MeshOffset, (int)cell.MeshSize), Source, in cell);
    }

    /// <summary>Chunk <paramref name="index"/>'s flat BSP blob, parsed in place.</summary>
    /// <exception cref="ScmapFormatException">The blob is not a well-formed flat tree.</exception>
    public ScmapChunkBsp ChunkBsp(int index)
    {
        ScmapChunkRecord cell = Chunks[index];
        return new ScmapChunkBsp(
            ChunkBspBlob.Slice((int)cell.BspOffset, (int)cell.BspSize), Source, in cell);
    }

    /// <summary>The <c>BRSH</c> section, parsed in place.</summary>
    /// <exception cref="ScmapFormatException">The section is not a well-formed brush table.</exception>
    public ScmapBrushSource BrushSource() => new(BrushSourceSection, Source, Nodes.Length);

    /// <summary>Triangles across every cell that owns render geometry.</summary>
    /// <remarks>
    /// <b>The measurement the double-geometry guard is graded on.</b> A
    /// <c>--keep-brush-source</c> cook must draw exactly what the same map cooked
    /// without it draws, and a loader that re-carved would double this number.
    /// </remarks>
    public int TriangleCount
    {
        get
        {
            int triangles = 0;
            for (int i = 0; i < Chunks.Length; i++)
            {
                if (Chunks[i].MeshSize == 0) continue;
                triangles += ChunkMesh(i).TriangleCount;
            }

            return triangles;
        }
    }

    /// <summary>The name of node <paramref name="index"/>, decoded.</summary>
    public string NodeName(int index) => Strings.GetStringOrEmpty((int)Nodes[index].NameString);

    /// <summary>The content path of asset <paramref name="index"/>, decoded.</summary>
    public string AssetPath(int index) => Strings.GetStringOrEmpty((int)Assets[index].PathString);

    /// <summary>The scene's name, decoded.</summary>
    public string SceneName => Strings.GetStringOrEmpty((int)Meta.SceneNameString);
}
