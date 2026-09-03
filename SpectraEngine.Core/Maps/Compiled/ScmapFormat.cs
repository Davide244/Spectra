using System;
using SpectraEngine.Core.Assets.Models;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// The fixed byte geometry of a <c>.scmap</c> file, stated once for the cook that
/// writes one in <c>Spectra.Kitchen</c> and the reader here.
/// </summary>
/// <remarks>
/// <para><b>Two expressions of one layout diverge</b>, which is the lesson
/// <see cref="PackFormat"/>, <see cref="SmodelFormat"/> and <c>SentDef</c> all
/// already record: a writer that computes a section start from its own running
/// cursor and a reader that recomputes it from a literal agree exactly until one
/// of them is edited, and then disagree as a read into the middle of somebody
/// else's bytes rather than as an exception. Both sides take their arithmetic
/// from here, and the writer's layout pass and write pass both take their padding
/// from <c>ScmapLayout.PaddedSectionSize</c>, which is the one function that
/// knows what a section costs.</para>
/// <para><b>The section table sits at a fixed offset, like <c>.smodel</c>'s and
/// unlike <c>.spack</c>'s.</b> A pack carries its table offset as a header field
/// so a v2 header can grow without a version bump, because a pack is mounted by
/// readers of many ages. A compiled map versions the strict way: a reader seeing
/// a version it does not implement refuses the file outright and says recook, so
/// a v2 that moved the table would already be unreachable by this code and an
/// explicit offset would buy nothing. <see cref="ScmapHeader.HeaderSize"/> is
/// written into the file anyway, because a wrong one is the difference between a
/// refusal and a table read out of the middle of the header.</para>
/// <para><b>Reserved four-character codes are named here and never emitted.</b>
/// <c>RGNI</c> is the region index the streaming design reserved and nothing
/// builds, and <c>BMDL</c> is the retired brush-model section whose only producer
/// was overturned. Naming them costs nothing, and it is what stops a code being
/// spent twice: a reader treats both as unknown sections and steps over them,
/// which is the same forward-compatibility rule that lets <c>ENTT</c> and
/// <c>SCPT</c> be filled by a later cooker with no version bump.</para>
/// </remarks>
public static class ScmapFormat
{
    /// <summary>
    /// File magic, <c>"SCMP"</c>. Stored as a little-endian <see cref="uint"/>,
    /// so the first four bytes on disk read <c>S C M P</c> in a hex dump.
    /// </summary>
    /// <remarks>
    /// The four-byte abbreviation is <c>SCMP</c>; the extension is always spelled
    /// <c>.scmap</c>. They are deliberately different lengths and neither is a
    /// typo for the other.
    /// </remarks>
    public const uint Magic = 'S' | ('C' << 8) | ('M' << 16) | ((uint)'P' << 24);

    /// <summary>
    /// The extension a compiled map is written and resolved under, including the
    /// dot.
    /// </summary>
    /// <remarks>
    /// Here rather than in the cooker, for the reason
    /// <see cref="PackFormat.FileExtension"/> already records: a cook that writes
    /// one spelling and a boot that resolves another is an error nowhere, the
    /// runtime simply finds no compiled map while every log line reads healthy.
    /// </remarks>
    public const string FileExtension = ".scmap";

    /// <summary>Bytes in the header, which lives at offset 0.</summary>
    public const int HeaderSize = 64;

    /// <summary>Absolute offset of the first section-table record.</summary>
    public const int SectionTableOffset = HeaderSize;

    /// <summary>Bytes in one section-table record, fixed stride.</summary>
    public const int SectionSize = 32;

    /// <summary>
    /// The smallest legal file: a header and nothing else. Such a file is still
    /// refused, but by the required-section check rather than by a length check,
    /// because the two failures want to say different things.
    /// </summary>
    public const int MinimumFileSize = HeaderSize;

    /// <summary>
    /// Alignment every section starts on, asserted at load rather than assumed.
    /// </summary>
    /// <remarks>
    /// Chunk meshes and flat BSP nodes are reinterpreted in place out of a mapped
    /// view as <c>float</c>, <c>uint</c> and <c>System.Numerics.Plane</c>, and a
    /// <c>Plane</c> may not straddle a 16-byte boundary. It is the same number
    /// <see cref="PackFormat.PayloadAlignment"/> and
    /// <see cref="SmodelFormat.PayloadAlignment"/> carry, for the same reason, and
    /// the three compose: a pack payload starts 16-byte aligned, so a section
    /// 16-byte aligned within the file is 16-byte aligned in the mapping too.
    /// </remarks>
    public const int PayloadAlignment = 16;

    /// <summary>Bytes in one <see cref="ScmapAssetEntry"/> record.</summary>
    public const int AssetEntrySize = 16;

    /// <summary>Bytes in one <see cref="ScmapNodeRecord"/> record.</summary>
    public const int NodeRecordSize = 80;

    /// <summary>Bytes in one <see cref="ScmapChunkRecord"/> record.</summary>
    public const int ChunkRecordSize = 64;

    /// <summary>Bytes in one <see cref="ScmapSpawn"/> record.</summary>
    public const int SpawnRecordSize = 32;

    /// <summary>Bytes of fixed preamble in <c>META</c>, before the spawn array.</summary>
    /// <remarks>
    /// 48 rather than the 32 the declared fields need, so the spawn array starts
    /// 16-byte aligned within a section that is itself 16-byte aligned, which is
    /// what lets it be cast in place. The sixteen bytes between are reserved and
    /// zero-filled.
    /// </remarks>
    public const int MetaPreambleSize = 48;

    /// <summary>
    /// Bytes of fixed preamble in <c>STRT</c>: the string count. The offset array
    /// and the blob size follow, then the blob.
    /// </summary>
    public const int StringCountSize = 4;

    /// <summary>Bytes of fixed preamble in <c>ASTB</c>: the entry count.</summary>
    public const int AssetCountSize = 4;

    /// <summary>
    /// Bytes of fixed preamble in <c>NODE</c>: the node count, padded to the
    /// payload alignment so the 80-byte records can be cast in place.
    /// </summary>
    public const int NodePreambleSize = 16;

    /// <summary>
    /// Bytes of fixed preamble in <c>CHDR</c>: the chunk count, padded to the
    /// payload alignment for the same reason as <see cref="NodePreambleSize"/>.
    /// </summary>
    public const int ChunkPreambleSize = 16;

    /// <summary>Section <c>STRT</c>: the string blob every string index addresses.</summary>
    public const uint StringSection = 'S' | ('T' << 8) | ('R' << 16) | ((uint)'T' << 24);

    /// <summary>Section <c>ASTB</c>: the asset table every material and model reference resolves through.</summary>
    public const uint AssetSection = 'A' | ('S' << 8) | ('T' << 16) | ((uint)'B' << 24);

    /// <summary>Section <c>META</c>: scene metadata and the compile constants a load validates.</summary>
    public const uint MetaSection = 'M' | ('E' << 8) | ('T' << 16) | ((uint)'A' << 24);

    /// <summary>Section <c>NODE</c>: the node graph, pre-order, one 80-byte record each.</summary>
    public const uint NodeSection = 'N' | ('O' << 8) | ('D' << 16) | ((uint)'E' << 24);

    /// <summary>Section <c>CHDR</c>: the chunk directory, sorted by <c>ChunkCoord.CompareTo</c>.</summary>
    public const uint ChunkDirectorySection = 'C' | ('H' << 8) | ('D' << 16) | ((uint)'R' << 24);

    /// <summary>Section <c>CMSH</c>: the per-cell mesh blobs the directory points into.</summary>
    public const uint ChunkMeshSection = 'C' | ('M' << 8) | ('S' << 16) | ((uint)'H' << 24);

    /// <summary>Section <c>CBSP</c>: the per-cell flat BSP blobs the directory points into.</summary>
    public const uint ChunkBspSection = 'C' | ('B' << 8) | ('S' << 16) | ((uint)'P' << 24);

    /// <summary>Section <c>ENTT</c>: entity records, one per entity-bearing node.</summary>
    public const uint EntitySection = 'E' | ('N' << 8) | ('T' << 16) | ((uint)'T' << 24);

    /// <summary>Section <c>ECON</c>: entity output connections.</summary>
    public const uint EntityConnectionSection = 'E' | ('C' << 8) | ('O' << 16) | ((uint)'N' << 24);

    /// <summary>Section <c>SCPT</c>: script records.</summary>
    public const uint ScriptSection = 'S' | ('C' << 8) | ('P' << 16) | ((uint)'T' << 24);

    /// <summary>Section <c>LUAB</c>: compiled Luau bytecode, a cache rather than the ground truth.</summary>
    public const uint ScriptBytecodeSection = 'L' | ('U' << 8) | ('A' << 16) | ((uint)'B' << 24);

    /// <summary>Section <c>LUAS</c>: Luau source, which is the ground truth.</summary>
    public const uint ScriptSourceSection = 'L' | ('U' << 8) | ('A' << 16) | ((uint)'S' << 24);

    /// <summary>Section <c>BRSH</c>: authored brush planes kept for runtime re-carving.</summary>
    public const uint BrushSourceSection = 'B' | ('R' << 8) | ('S' << 16) | ((uint)'H' << 24);

    /// <summary>Section <c>NBND</c>: optional per-node local bounds.</summary>
    public const uint NodeBoundsSection = 'N' | ('B' << 8) | ('N' << 16) | ((uint)'D' << 24);

    /// <summary>
    /// Section <c>RGNI</c>, reserved and never written: the region index map
    /// streaming would need. Reserving the code is nearly free; building the
    /// streamer is a separate hard design, and the chunk grid is a compile
    /// partition rather than a residency one.
    /// </summary>
    public const uint RegionIndexSection = 'R' | ('G' << 8) | ('N' << 16) | ((uint)'I' << 24);

    /// <summary>
    /// Section <c>BMDL</c>, reserved and never written: it held the fused
    /// entity-local brush model, whose mechanism was overturned. An entity-owned
    /// brush is a part brush whose owner happens to be an entity, and the only
    /// surviving distinction rides
    /// <see cref="ScmapPayloadFlags.IsEntityOwned"/>. The code is burned rather
    /// than reused, for the same reason <c>PayloadKind</c> 3 is.
    /// </summary>
    public const uint BrushModelSection = 'B' | ('M' << 8) | ('D' << 16) | ((uint)'L' << 24);

    /// <summary>
    /// The vertex layout every cooked chunk mesh is in: the engine's standard
    /// interleaved position, normal, uv0, all float32.
    /// </summary>
    /// <remarks>
    /// Expressed as <see cref="SmodelVertexAttribute"/> rather than as
    /// <c>VertexAttribute</c> because the identity being stamped is over
    /// <c>(semantic, component count)</c> pairs and <c>VertexAttribute</c> carries
    /// a shader LOCATION rather than a semantic. Two formats naming one geometry
    /// shape must hash it the same way, or a model and a map cooked from the same
    /// layout would report different layout ids and one of the two gates would be
    /// reporting nonsense.
    /// </remarks>
    public static ReadOnlySpan<SmodelVertexAttribute> StandardVertexLayout => _standardVertexLayout;

    private static readonly SmodelVertexAttribute[] _standardVertexLayout =
    [
        new(SmodelSemantic.Position, SmodelComponentType.Float32, componentCount: 3, byteOffset: 0),
        new(SmodelSemantic.Normal, SmodelComponentType.Float32, componentCount: 3, byteOffset: 12),
        new(SmodelSemantic.Uv0, SmodelComponentType.Float32, componentCount: 2, byteOffset: 24),
    ];

    /// <summary>Floats per vertex in <see cref="StandardVertexLayout"/>.</summary>
    public const uint StandardVertexStrideFloats = 8;

    /// <summary>
    /// The layout identity a header stamps: FNV-1a over each attribute's
    /// <c>(semantic, component count)</c> pair, in declaration order.
    /// </summary>
    /// <remarks>
    /// <para>One implementation, borrowed from <see cref="SmodelFormat"/>. A
    /// second copy of the same hash is exactly the kind that gets corrected in one
    /// place and not the other, and the symptom would be two cooked artifacts
    /// disagreeing about a layout they were both built from.</para>
    /// <para>It is the precise report rather than the whole gate:
    /// <c>EngineInfo.GeometryFormatVersion</c> catches a wholesale change in what
    /// compiled geometry means, and this value says which attribute moved.</para>
    /// </remarks>
    public static uint StandardVertexLayoutId => SmodelFormat.ComputeVertexLayoutId(StandardVertexLayout);

    /// <summary>
    /// Rounds <paramref name="value"/> up to the next multiple of
    /// <paramref name="alignment"/>, which must be a power of two.
    /// </summary>
    /// <remarks>
    /// One implementation, borrowed from the container this format ships inside: a
    /// second copy of alignment arithmetic is exactly the kind that gets fixed in
    /// one place and not in the other.
    /// </remarks>
    public static long AlignUp(long value, int alignment) => PackFormat.AlignUp(value, alignment);

    /// <summary>
    /// Renders a FourCC as the four characters it reads as, for a message.
    /// </summary>
    /// <remarks>
    /// Borrowed from <see cref="SmodelFormat.DescribeFourCc"/>, which already maps
    /// a non-printable byte to <c>?</c> rather than emitting it raw: an unknown
    /// section's code arrives from a file that may be arbitrary bytes, and a
    /// control character in an exception message is how a log line stops being
    /// greppable.
    /// </remarks>
    public static string DescribeFourCc(uint fourCc) => SmodelFormat.DescribeFourCc(fourCc);

    /// <summary>
    /// The cell size a compiled map must have been baked on, which is the one the
    /// running engine chunks with.
    /// </summary>
    /// <remarks>
    /// Named here so the load validation and the writer read one constant. A
    /// runtime built with a different cell size would mis-route every point and
    /// ray query against a directory built for another lattice, and the failure
    /// looks like sporadic collision bugs rather than like a version problem.
    /// </remarks>
    public static float EngineCellSize => ChunkCoord.CellSize;

    /// <summary>
    /// The vertex snap grid a compiled map must have been baked on. Same argument
    /// as <see cref="EngineCellSize"/>: a map welded on another grid has hairline
    /// cracks rather than an error.
    /// </summary>
    public static float EngineSnapGrid => VertexSnapper.GridSize;

    /// <summary>
    /// The cross-cell weld band a compiled map must have been baked on. Validated
    /// with the other two: a map welded across a different band has seams exactly
    /// where two cells meet, which is a picture rather than an error.
    /// </summary>
    public static float EngineWeldBand => ChunkGrid.WeldBand;

    /// <summary>
    /// Refuses to read or write a <c>.scmap</c> on a big-endian machine.
    /// </summary>
    /// <remarks>
    /// The whole zero-copy premise is <c>MemoryMarshal.Cast</c> over raw mapped
    /// bytes, which is endianness-native by construction. A byte-swapping reader
    /// would have to copy every vertex and every BSP plane, that is, do the one
    /// thing this format exists to avoid, so the honest answer is to refuse loudly
    /// rather than to pretend.
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">The machine is big-endian.</exception>
    public static void RequireLittleEndian()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "The .scmap format is little-endian only: its node, chunk, mesh and BSP payloads are " +
                "reinterpreted in place, so a big-endian host would have to copy and byte-swap all of it.");
        }
    }
}
