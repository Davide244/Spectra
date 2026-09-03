using System;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// The 64 bytes at offset 0 of a <c>.scmap</c> file, exactly as they sit on disk.
/// </summary>
/// <remarks>
/// <para><b><c>Pack = 1</c> is what makes this struct the format rather than a
/// description of it.</b> Without it the CLR is free to insert padding, and the
/// bytes written from a struct value would stop matching the bytes a reader casts
/// back out of a mapped view; with it there is no padding at all, so no reserved
/// byte can pick up whatever was on the stack. Every one of the 64 bytes is a
/// declared field, and the layout tests pin both that size and the offset of each
/// field, because a field reordered by an edit compiles cleanly and produces a
/// file that parses into the wrong numbers.</para>
/// <para><b><see cref="FormatVersion"/> is an EXACT-match gate, not a floor.</b>
/// A compiled map is a build output that can always be regenerated, so the
/// refusal names both numbers and says recook. That is the opposite of
/// <c>PackHeader.MinReaderVersion</c>, and deliberately: a pack is mounted by
/// readers of many ages and carries a floor so a v2 that only appends stays
/// readable, while there is nothing in a <c>.scmap</c> to degrade to when the
/// bytes past the header mean something else.</para>
/// <para><b>Three version words rather than one</b>, because a stale compiled map
/// has three independent ways of being stale and a single number cannot say which.
/// <see cref="FormatVersion"/> is the container. <see cref="GeometryFormatVersion"/>
/// is what the CSG compiler emits, which has already changed once and will change
/// again. <see cref="VertexLayoutId"/> is which attributes a vertex carries, so a
/// mismatch is reportable as the precise attribute rather than as a generic bump.
/// <see cref="MapFormatVersion"/> is a fourth and is INFORMATIONAL: it records
/// which authored-map grammar the bake read, and a load never gates on it, because
/// the authored map is not present at runtime and cannot be re-read.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ScmapHeader
{
    /// <summary>Always <see cref="ScmapFormat.Magic"/>; four bytes reading <c>SCMP</c>.</summary>
    public readonly uint Magic;

    /// <summary>
    /// The version this map was compiled at. Must equal
    /// <c>EngineInfo.CompiledMapFormatVersion</c> exactly.
    /// </summary>
    public readonly ushort FormatVersion;

    /// <summary>
    /// Bytes in this header, so a reader that meets a grown header knows where the
    /// section table starts without deriving it.
    /// </summary>
    /// <remarks>
    /// Always <see cref="ScmapFormat.HeaderSize"/> in v1, and validated rather
    /// than trusted: a reader that seeks to a wrong declared offset reads a
    /// section table out of the middle of the header, which parses into plausible
    /// nonsense instead of failing.
    /// </remarks>
    public readonly ushort HeaderSize;

    /// <summary>Whole-file properties. See <see cref="ScmapFlags"/>.</summary>
    public readonly uint Flags;

    /// <summary>Number of records in the section table.</summary>
    public readonly uint SectionCount;

    /// <summary>
    /// <c>XxHash128</c> of the source <c>.smap</c> bundle's canonical enumeration:
    /// sorted bundle-relative paths, each path's UTF-8 bytes then its file bytes.
    /// </summary>
    /// <remarks>
    /// <b>The enumeration is canonical so the digest is not a fact about the
    /// cooking machine.</b> A directory walk returns files in whatever order the
    /// filesystem chose, which differs between machines and between filesystems on
    /// one machine, so hashing in walk order would make two identical bundles hash
    /// differently and every incremental cook re-bake every map. The path bytes go
    /// into the hash beside the file bytes for the ordinary reason: without them,
    /// renaming a script to a name that sorts the same way is invisible.
    /// </remarks>
    public readonly UInt128 SourceMapDigest;

    /// <summary>
    /// <c>EngineInfo.GeometryFormatVersion</c> at cook time. A mismatch refuses
    /// the load, because the symptom otherwise is a misinterpreted vertex buffer.
    /// </summary>
    public readonly uint GeometryFormatVersion;

    /// <summary>
    /// The authored map grammar the bake read, from the source document.
    /// Informational, never a load gate.
    /// </summary>
    public readonly uint MapFormatVersion;

    /// <summary>
    /// FNV-1a over the cooked vertex layout's <c>(semantic, component count)</c>
    /// pairs, so a geometry mismatch is reportable precisely.
    /// </summary>
    public readonly uint VertexLayoutId;

    /// <summary>
    /// <c>(Major &lt;&lt; 20) | (Minor &lt;&lt; 10) | Revision</c> of the engine
    /// that compiled this map. Informational, never a load gate: what gates a load
    /// is the three version words above, which are statements about the bytes.
    /// </summary>
    public readonly uint EngineVersion;

    /// <summary>Total bytes in the file, section padding included.</summary>
    public readonly ulong TotalSize;

    /// <summary>Reserved; written zero.</summary>
    public readonly ulong Reserved;

    /// <summary>Builds a header. Every field is assigned.</summary>
    public ScmapHeader(
        ushort formatVersion,
        ScmapFlags flags,
        uint sectionCount,
        UInt128 sourceMapDigest,
        uint geometryFormatVersion,
        uint mapFormatVersion,
        uint vertexLayoutId,
        uint engineVersion,
        ulong totalSize)
    {
        Magic = ScmapFormat.Magic;
        FormatVersion = formatVersion;
        HeaderSize = ScmapFormat.HeaderSize;
        Flags = (uint)flags;
        SectionCount = sectionCount;
        SourceMapDigest = sourceMapDigest;
        GeometryFormatVersion = geometryFormatVersion;
        MapFormatVersion = mapFormatVersion;
        VertexLayoutId = vertexLayoutId;
        EngineVersion = engineVersion;
        TotalSize = totalSize;
        Reserved = 0;
    }

    /// <summary>The file properties, as the enum rather than as the raw word.</summary>
    public ScmapFlags FileFlags => (ScmapFlags)Flags;
}
