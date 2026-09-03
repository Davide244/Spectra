using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// One 16-byte <c>BRSH</c> record: an authored brush's planes, by node.
/// </summary>
/// <remarks>
/// <b>The link runs from the brush to the node and never back.</b> A node record's
/// <c>PayloadIndex</c> is left zero for every brush, so there is one expression of
/// the association and a loader builds whatever map it wants in one pass over this
/// table. Two directions would be two things to keep in step, and the symptom of a
/// disagreement is a brush re-carved under somebody else's transform.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapBrushRecord
{
    /// <summary>Index into <c>NODE</c> of the node this brush hangs on.</summary>
    public readonly uint NodeIndex;

    /// <summary>How many planes this brush has, which is also how many faces.</summary>
    public readonly uint PlaneCount;

    /// <summary>
    /// Index of this brush's first plane in the section's plane array, which is
    /// also the index of its first face.
    /// </summary>
    /// <remarks>
    /// One start for both arrays, because one <c>FaceSurface</c> per plane is the
    /// invariant the whole per-face material path rests on. Two starts would let a
    /// file express a face count that disagreed with its plane count, which is an
    /// indexing bug rather than a rendering one.
    /// </remarks>
    public readonly uint PlaneStart;

    /// <summary>Reserved; written zero.</summary>
    public readonly uint Reserved;

    /// <summary>Builds one brush record. Every field is assigned.</summary>
    public ScmapBrushRecord(uint nodeIndex, uint planeCount, uint planeStart)
    {
        NodeIndex = nodeIndex;
        PlaneCount = planeCount;
        PlaneStart = planeStart;
        Reserved = 0;
    }
}

/// <summary>
/// One 48-byte <c>BRSH</c> face record: what one authored brush plane wears.
/// </summary>
/// <remarks>
/// <para><b><see cref="AssetIndex"/> is an index into <c>ASTB</c></b>, for the
/// reason <see cref="ScmapSubmeshEntry.AssetIndex"/> is: a <c>MaterialRef.Id</c>
/// is per-process interning order and means nothing in a file.</para>
/// <para><b>Zero axes mean world-aligned</b>, exactly as they do on
/// <c>FaceSurface</c>, so the projection is derived from the face normal by the
/// dominant-axis rule rather than being stored. Writing a derived pair instead
/// would freeze into the file an answer the engine recomputes, and the two would
/// drift the day the rule changed.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapFaceRecord
{
    /// <summary>
    /// Index into <c>ASTB</c> of this face's material, or
    /// <see cref="ScmapFormat.NoAssetIndex"/> when it names none.
    /// </summary>
    public readonly uint AssetIndex;

    /// <summary>Brush-local U axis, or zero for world-aligned.</summary>
    public readonly Vector3 UAxis;

    /// <summary>Brush-local V axis, or zero for world-aligned.</summary>
    public readonly Vector3 VAxis;

    /// <summary>U offset, in repeats.</summary>
    public readonly float UOffset;

    /// <summary>V offset, in repeats.</summary>
    public readonly float VOffset;

    /// <summary>World units per U repeat.</summary>
    public readonly float UScale;

    /// <summary>World units per V repeat.</summary>
    public readonly float VScale;

    /// <summary>Reserved; written zero.</summary>
    public readonly uint Reserved;

    /// <summary>Builds one face record. Every field is assigned.</summary>
    public ScmapFaceRecord(
        uint assetIndex,
        Vector3 uAxis,
        Vector3 vAxis,
        float uOffset,
        float vOffset,
        float uScale,
        float vScale)
    {
        AssetIndex = assetIndex;
        UAxis = uAxis;
        VAxis = vAxis;
        UOffset = uOffset;
        VOffset = vOffset;
        UScale = uScale;
        VScale = vScale;
        Reserved = 0;
    }
}

/// <summary>
/// The <c>BRSH</c> section, read in place: authored brush planes and their faces.
/// </summary>
/// <remarks>
/// <para><b>THE DOUBLE-GEOMETRY HAZARD LIVES HERE, and this is its guard.</b> When
/// baked chunks and this section are both present, a loader that helpfully
/// re-carves what it finds produces a world where every wall is drawn twice, with
/// z-fighting that every graphics programmer's instinct attributes to depth
/// precision or a pipeline state bug rather than to a map loader.
/// <see cref="IsReCarvable"/> is the one predicate a loader may consult, and it is
/// the negation of <see cref="ScmapNodeRecord.BakedIntoChunks"/>.</para>
/// <para><b>The cooked name is <c>BakedIntoChunks</c> and the engine name is
/// <c>SceneNode.IsStaticWorldBrush</c>, and neither may take the other's
/// spelling.</b> The engine's predicate means "admitted to the carve"; the cooked
/// flag means "already baked, do not re-carve". They differ by exactly the mistake
/// above: a loader reading "static world brush" as "belongs in the carve" carves
/// it again.</para>
/// <para><b>A part brush is here whatever the cook was asked for.</b> Its planes
/// live nowhere else - a part is never baked into a chunk, its mesh is built at
/// runtime from its own <c>Brush</c> - so a map that dropped them would ship a
/// level whose parts are invisible with nothing reporting it.
/// <c>--keep-brush-source</c> adds the WORLD brushes on top, and those are exactly
/// the ones that must never be carved again.</para>
/// </remarks>
public readonly ref struct ScmapBrushSource
{
    /// <summary>
    /// Parses the section, validating every range before anything indexes with
    /// one.
    /// </summary>
    /// <param name="section">The whole <c>BRSH</c> section.</param>
    /// <param name="source">What to call the map in a message.</param>
    /// <param name="nodeCount">How many <c>NODE</c> records exist, to bound the back-references.</param>
    /// <exception cref="ScmapFormatException">The section is not a well-formed brush table.</exception>
    public ScmapBrushSource(ReadOnlySpan<byte> section, string source, int nodeCount)
    {
        if (section.Length < ScmapFormat.BrushSourceHeaderSize)
        {
            throw new ScmapFormatException(
                $"'{source}' has a {section.Length}-byte BRSH section, short of the " +
                $"{ScmapFormat.BrushSourceHeaderSize}-byte preamble that carries its counts.");
        }

        uint brushCount = BinaryPrimitives.ReadUInt32LittleEndian(section);
        uint planeCount = BinaryPrimitives.ReadUInt32LittleEndian(section[4..]);

        long brushBytes = (long)brushCount * ScmapFormat.BrushSourceRecordSize;
        long planeStart = ScmapFormat.AlignUp(ScmapFormat.BrushSourceHeaderSize + brushBytes, ScmapFormat.PayloadAlignment);
        long planeBytes = (long)planeCount * ScmapFormat.PlaneSize;
        long faceStart = ScmapFormat.AlignUp(planeStart + planeBytes, ScmapFormat.PayloadAlignment);
        long faceBytes = (long)planeCount * ScmapFormat.BrushFaceRecordSize;

        if (faceStart + faceBytes > section.Length)
        {
            throw new ScmapFormatException(
                $"'{source}' declares {brushCount} brushes over {planeCount} planes, whose records would end " +
                $"at byte {faceStart + faceBytes} of a {section.Length}-byte BRSH section.");
        }

        Brushes = MemoryMarshal.Cast<byte, ScmapBrushRecord>(
            section.Slice(ScmapFormat.BrushSourceHeaderSize, (int)brushBytes));

        Planes = MemoryMarshal.Cast<byte, Plane>(section.Slice((int)planeStart, (int)planeBytes));
        Faces = MemoryMarshal.Cast<byte, ScmapFaceRecord>(section.Slice((int)faceStart, (int)faceBytes));

        for (int i = 0; i < Brushes.Length; i++)
        {
            ScmapBrushRecord brush = Brushes[i];

            if (brush.NodeIndex >= (uint)nodeCount)
            {
                throw new ScmapFormatException(
                    $"'{source}' brush {i} names node {brush.NodeIndex} of a {nodeCount}-node map. A brush " +
                    "that cannot name its node has no transform, so it would be carved at the origin.");
            }

            if (brush.PlaneCount < 4)
            {
                // Fewer than four half-spaces cannot bound a volume, so a brush
                // built from them is unbounded rather than small: the carve would
                // subtract half the world.
                throw new ScmapFormatException(
                    $"'{source}' brush {i} declares {brush.PlaneCount} planes, and fewer than four half-spaces " +
                    "bound no volume at all.");
            }

            if ((long)brush.PlaneStart + brush.PlaneCount > planeCount)
            {
                throw new ScmapFormatException(
                    $"'{source}' brush {i} claims planes [{brush.PlaneStart}, " +
                    $"{brush.PlaneStart + brush.PlaneCount}) of a {planeCount}-plane table.");
            }
        }
    }

    /// <summary>One record per authored brush the cook kept, in node pre-order.</summary>
    public ReadOnlySpan<ScmapBrushRecord> Brushes { get; }

    /// <summary>Every brush's planes, concatenated. Brush-local, exactly as authored.</summary>
    public ReadOnlySpan<Plane> Planes { get; }

    /// <summary>Every brush's faces, concatenated and index-aligned with <see cref="Planes"/>.</summary>
    public ReadOnlySpan<ScmapFaceRecord> Faces { get; }

    /// <summary>
    /// Whether a loader may carve this node's brush into the live world.
    /// </summary>
    /// <remarks>
    /// <b>The whole double-geometry guard, in one predicate.</b> A node whose
    /// geometry was baked into the chunks is already in the world; carving it again
    /// draws every one of its walls twice. This is the ONLY question a loader gets
    /// to ask about a <c>BRSH</c> entry before deciding to carve, and answering it
    /// from anything else - the section being present, the cook flag, the engine's
    /// own admission predicate - is how the two answers drift apart.
    /// </remarks>
    public static bool IsReCarvable(in ScmapNodeRecord node) => !node.BakedIntoChunks;
}
