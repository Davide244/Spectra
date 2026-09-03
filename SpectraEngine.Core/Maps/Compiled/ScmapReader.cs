using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// Reads a compiled <c>.scmap</c> out of the bytes it sits in, with no stream, no
/// allocation per record and no copy of anything large.
/// </summary>
/// <remarks>
/// <para><b>A span in, and every table cast back out of it.</b> The bytes are
/// normally a memory-mapped view of a pack payload, so the reader must never take
/// ownership of them and must never index them without checking first: an
/// out-of-range read into a mapping is an access violation with no managed stack,
/// which is a crash nobody can attribute to a file. Every offset and every length
/// below is bounds-checked before it is used, and every failure is a
/// <see cref="ScmapFormatException"/> naming what was wrong and what was
/// expected.</para>
/// <para><b>An unknown section code is skipped, not refused</b>, which is what
/// lets a section designed today be written by a later cooker with no version
/// bump. Everything else is strict, because a compiled map is a build output that
/// can always be regenerated: the format version is an exact match, the geometry
/// version is an exact match, the compile constants are an exact match, and each
/// refusal says recook.</para>
/// <para><b>Three things are validated that a naive reader would trust.</b> The
/// chunk directory is checked for ascending cell order, because the whole point of
/// the sort is that a point lookup is a binary search and a binary search over an
/// unsorted directory silently answers "no such cell" rather than failing. Every
/// parent index is checked to precede its child, because that is what makes a
/// single forward pass legal and a backward edge is an infinite loop in a loader
/// rather than an exception here. And every chunk blob range is checked against
/// the section it addresses, which is the bound that turns a malformed file into a
/// refusal rather than into a read past the end of a mapping.</para>
/// </remarks>
public static class ScmapReader
{
    private const int StringSlot = 0;
    private const int AssetSlot = 1;
    private const int MetaSlot = 2;
    private const int NodeSlot = 3;
    private const int ChunkSlot = 4;
    private const int ChunkMeshSlot = 5;
    private const int ChunkBspSlot = 6;
    private const int BrushSourceSlot = 7;
    private const int KnownSectionCount = 8;

    /// <summary>
    /// Validates <paramref name="file"/> and returns its tables as spans into it.
    /// </summary>
    /// <param name="file">The whole file, header included.</param>
    /// <param name="source">
    /// What to call the file in a message: a logical asset path, not a machine
    /// path, so the same failure reads the same way from a pack and from a loose
    /// cook directory.
    /// </param>
    /// <exception cref="ScmapFormatException">The file is not a readable <c>.scmap</c>.</exception>
    /// <exception cref="PlatformNotSupportedException">The machine is big-endian.</exception>
    public static ScmapDocument Read(ReadOnlySpan<byte> file, string source)
    {
        ScmapFormat.RequireLittleEndian();

        // Before a byte is read, because every chunk BSP blob in the file is about
        // to be cast at this stride and a runtime that laid either struct out
        // differently would misread all of them rather than fail.
        ScmapChunkBsp.RequireNodeLayout();

        if (file.Length < ScmapFormat.MinimumFileSize)
        {
            throw new ScmapFormatException(
                $"'{source}' is {file.Length} bytes, too short to hold a " +
                $"{ScmapFormat.HeaderSize}-byte .scmap header.");
        }

        ScmapHeader header = MemoryMarshal.Read<ScmapHeader>(file);

        if (header.Magic != ScmapFormat.Magic)
        {
            throw new ScmapFormatException(
                $"'{source}' is not a .scmap file: its first four bytes read " +
                $"'{ScmapFormat.DescribeFourCc(header.Magic)}', not 'SCMP'.");
        }

        if (header.FormatVersion != EngineInfo.CompiledMapFormatVersion)
        {
            // Exact, never a floor. A compiled map is a build output, so there is
            // nothing to carry forward and nothing to degrade to: the bytes past
            // the header only mean anything under the version that wrote them.
            throw new ScmapFormatException(
                $"'{source}' is .scmap format version {header.FormatVersion}, and this engine reads version " +
                $"{EngineInfo.CompiledMapFormatVersion}. Recook the map.");
        }

        if (header.HeaderSize != ScmapFormat.HeaderSize)
        {
            throw new ScmapFormatException(
                $"'{source}' declares a {header.HeaderSize}-byte header at format version " +
                $"{header.FormatVersion}, which this engine writes as {ScmapFormat.HeaderSize} bytes. " +
                "A wrong header size reads the section table out of the middle of the header.");
        }

        if (header.GeometryFormatVersion != EngineInfo.GeometryFormatVersion)
        {
            // The separate gate, and the one that actually bites: the container
            // can be unchanged while what a vertex buffer MEANS has moved under
            // it, and the symptom of missing that is a misinterpreted buffer,
            // which draws garbage on one backend and refuses an input layout on
            // another.
            throw new ScmapFormatException(
                $"'{source}' was cooked at geometry format version {header.GeometryFormatVersion}, and this " +
                $"engine reads version {EngineInfo.GeometryFormatVersion}. Recook the map.");
        }

        if (header.VertexLayoutId != ScmapFormat.StandardVertexLayoutId)
        {
            // Strictly narrower than the version above, and that is its value: it
            // says WHICH attribute moved rather than that something did.
            throw new ScmapFormatException(
                $"'{source}' was cooked for vertex layout {header.VertexLayoutId:X8}, and this engine's " +
                $"standard layout is {ScmapFormat.StandardVertexLayoutId:X8}. Recook the map.");
        }

        long tableEnd = ScmapFormat.SectionTableOffset + ((long)header.SectionCount * ScmapFormat.SectionSize);
        if (tableEnd > file.Length)
        {
            throw new ScmapFormatException(
                $"'{source}' declares {header.SectionCount} sections, whose {ScmapFormat.SectionSize}-byte " +
                $"table would end at byte {tableEnd} of a {file.Length}-byte file.");
        }

        Span<int> sectionOffset = stackalloc int[KnownSectionCount];
        Span<int> sectionLength = stackalloc int[KnownSectionCount];
        Span<bool> sectionPresent = stackalloc bool[KnownSectionCount];
        sectionPresent.Clear();

        int skipped = 0;
        for (uint i = 0; i < header.SectionCount; i++)
        {
            ScmapSection record = MemoryMarshal.Read<ScmapSection>(
                file[(ScmapFormat.SectionTableOffset + ((int)i * ScmapFormat.SectionSize))..]);

            // Bounds and alignment are checked for EVERY section, known or not. A
            // section this reader steps over is still a claim about where the
            // file's bytes are, and letting an unknown one describe an impossible
            // region would make the forward-compatibility mechanism a way to
            // smuggle a malformed file past the gate.
            RequireSectionInFile(source, record, file.Length);

            if ((record.Offset % ScmapFormat.PayloadAlignment) != 0)
            {
                throw new ScmapFormatException(
                    $"'{source}' section '{ScmapFormat.DescribeFourCc(record.Kind)}' starts at byte " +
                    $"{record.Offset}, which is not a multiple of {ScmapFormat.PayloadAlignment}. Payloads " +
                    "are reinterpreted in place, so an unaligned section start is a plane straddling a " +
                    "boundary.");
            }

            if ((record.SectionFlags & ScmapSectionFlags.Compressed) != 0)
            {
                throw new ScmapFormatException(
                    $"'{source}' section '{ScmapFormat.DescribeFourCc(record.Kind)}' is marked compressed. " +
                    "That flag is reserved and no cook sets it: compression and a mapped zero-copy read are " +
                    "mutually exclusive.");
            }

            int slot = KnownSlot(record.Kind);
            if (slot < 0)
            {
                skipped++;
                continue;
            }

            if (sectionPresent[slot])
            {
                throw new ScmapFormatException(
                    $"'{source}' carries section '{ScmapFormat.DescribeFourCc(record.Kind)}' more than once. " +
                    "A section names one region of the file, so a reader would have to choose, and choosing " +
                    "silently is how half a map comes from one copy and half from the other.");
            }

            sectionPresent[slot] = true;
            sectionOffset[slot] = (int)record.Offset;
            sectionLength[slot] = (int)record.Size;
        }

        RequireSection(source, sectionPresent, StringSlot, ScmapFormat.StringSection);
        RequireSection(source, sectionPresent, AssetSlot, ScmapFormat.AssetSection);
        RequireSection(source, sectionPresent, MetaSlot, ScmapFormat.MetaSection);
        RequireSection(source, sectionPresent, NodeSlot, ScmapFormat.NodeSection);
        RequireSection(source, sectionPresent, ChunkSlot, ScmapFormat.ChunkDirectorySection);

        // Two statements about one fact, checked against each other. The header
        // flag says the section is there and the table says where it is, and a file
        // where they disagree is one whose brush planes are either missing from a
        // loader that was told to expect them or present for one that was not - and
        // the second is the double-geometry hazard arriving through the back door.
        if (((header.FileFlags & ScmapFlags.HasBrushSource) != 0) != sectionPresent[BrushSourceSlot])
        {
            throw new ScmapFormatException(
                $"'{source}' has its HasBrushSource header flag " +
                $"{((header.FileFlags & ScmapFlags.HasBrushSource) != 0 ? "set" : "clear")} and a BRSH section " +
                $"{(sectionPresent[BrushSourceSlot] ? "present" : "absent")}. The flag says the section is " +
                "there and nothing else, so the two can only disagree in a file nothing this engine wrote.");
        }

        var strings = new ScmapStringTable(
            file.Slice(sectionOffset[StringSlot], sectionLength[StringSlot]),
            source);

        ReadOnlySpan<ScmapAssetEntry> assets = ReadAssets(
            source,
            file.Slice(sectionOffset[AssetSlot], sectionLength[AssetSlot]),
            strings);

        ScmapMeta meta = ReadMeta(
            source,
            file.Slice(sectionOffset[MetaSlot], sectionLength[MetaSlot]),
            strings,
            out ReadOnlySpan<ScmapSpawn> spawns);

        ReadOnlySpan<ScmapNodeRecord> nodes = ReadNodes(
            source,
            file.Slice(sectionOffset[NodeSlot], sectionLength[NodeSlot]),
            strings,
            out int invalidDeclaredStates);

        ReadOnlySpan<byte> meshBlob = sectionPresent[ChunkMeshSlot]
            ? file.Slice(sectionOffset[ChunkMeshSlot], sectionLength[ChunkMeshSlot])
            : default;

        ReadOnlySpan<byte> bspBlob = sectionPresent[ChunkBspSlot]
            ? file.Slice(sectionOffset[ChunkBspSlot], sectionLength[ChunkBspSlot])
            : default;

        ReadOnlySpan<ScmapChunkRecord> chunks = ReadChunks(
            source,
            file.Slice(sectionOffset[ChunkSlot], sectionLength[ChunkSlot]),
            meshBlob.Length,
            bspBlob.Length);

        ReadOnlySpan<byte> brushSource = sectionPresent[BrushSourceSlot]
            ? file.Slice(sectionOffset[BrushSourceSlot], sectionLength[BrushSourceSlot])
            : default;

        return new ScmapDocument(
            source,
            header,
            strings,
            assets,
            meta,
            spawns,
            nodes,
            chunks,
            meshBlob,
            bspBlob,
            sectionPresent[ChunkBspSlot] ? sectionOffset[ChunkBspSlot] : 0,
            brushSource,
            sectionPresent[BrushSourceSlot],
            skipped,
            invalidDeclaredStates);
    }

    private static int KnownSlot(uint fourCc) => fourCc switch
    {
        ScmapFormat.StringSection => StringSlot,
        ScmapFormat.AssetSection => AssetSlot,
        ScmapFormat.MetaSection => MetaSlot,
        ScmapFormat.NodeSection => NodeSlot,
        ScmapFormat.ChunkDirectorySection => ChunkSlot,
        ScmapFormat.ChunkMeshSection => ChunkMeshSlot,
        ScmapFormat.ChunkBspSection => ChunkBspSlot,
        ScmapFormat.BrushSourceSection => BrushSourceSlot,

        // ENTT, ECON, SCPT, LUAB, LUAS and NBND land here on purpose: they are
        // claimed and empty until the milestones that fill them, and stepping over
        // a section this build has no consumer for is exactly what the skip rule is
        // for. RGNI and BMDL land here forever.
        _ => -1,
    };

    private static ReadOnlySpan<ScmapAssetEntry> ReadAssets(
        string source,
        ReadOnlySpan<byte> section,
        ScmapStringTable strings)
    {
        if (section.Length < ScmapFormat.AssetCountSize)
        {
            throw new ScmapFormatException(
                $"'{source}' has a {section.Length}-byte ASTB section, too short to hold its own entry count.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(section);
        long end = ScmapFormat.AssetCountSize + ((long)count * ScmapFormat.AssetEntrySize);
        if (end > section.Length)
        {
            throw new ScmapFormatException(
                $"'{source}' declares {count} assets, whose {ScmapFormat.AssetEntrySize}-byte records would " +
                $"end at byte {end} of a {section.Length}-byte ASTB section.");
        }

        ReadOnlySpan<ScmapAssetEntry> entries = MemoryMarshal.Cast<byte, ScmapAssetEntry>(
            section.Slice(ScmapFormat.AssetCountSize, (int)count * ScmapFormat.AssetEntrySize));

        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].PathString >= (uint)strings.Count)
            {
                throw new ScmapFormatException(
                    $"'{source}' asset {i} names string {entries[i].PathString} of a {strings.Count}-string " +
                    "table. An asset that cannot name its own path is one every material referencing it " +
                    "would resolve to the placeholder, which renders as magenta rather than as an error.");
            }
        }

        return entries;
    }

    private static ScmapMeta ReadMeta(
        string source,
        ReadOnlySpan<byte> section,
        ScmapStringTable strings,
        out ReadOnlySpan<ScmapSpawn> spawns)
    {
        if (section.Length < ScmapFormat.MetaPreambleSize)
        {
            throw new ScmapFormatException(
                $"'{source}' has a {section.Length}-byte META section, short of the " +
                $"{ScmapFormat.MetaPreambleSize}-byte preamble every compiled map carries.");
        }

        ScmapMeta meta = MemoryMarshal.Read<ScmapMeta>(section);

        RequireCompileConstant(source, "cell size", meta.CellSize, ScmapFormat.EngineCellSize,
            "every point and ray query would be routed against a directory built for another lattice, " +
            "which reads as sporadic collision bugs rather than as a version problem");

        RequireCompileConstant(source, "weld band", meta.WeldBand, ScmapFormat.EngineWeldBand,
            "cell borders would be welded across a different band, which reads as seams exactly where two " +
            "cells meet");

        RequireCompileConstant(source, "snap grid", meta.SnapGrid, ScmapFormat.EngineSnapGrid,
            "vertices were quantised to another lattice, which reads as hairline cracks rather than as a " +
            "version problem");

        if (meta.SceneNameString >= (uint)strings.Count)
        {
            throw new ScmapFormatException(
                $"'{source}' names its scene with string {meta.SceneNameString} of a {strings.Count}-string " +
                "table.");
        }

        long spawnEnd = ScmapFormat.MetaPreambleSize + ((long)meta.SpawnCount * ScmapFormat.SpawnRecordSize);
        if (spawnEnd > section.Length)
        {
            throw new ScmapFormatException(
                $"'{source}' declares {meta.SpawnCount} spawns, whose records would end at byte {spawnEnd} " +
                $"of a {section.Length}-byte META section.");
        }

        spawns = MemoryMarshal.Cast<byte, ScmapSpawn>(
            section.Slice(ScmapFormat.MetaPreambleSize, (int)meta.SpawnCount * ScmapFormat.SpawnRecordSize));

        return meta;
    }

    private static ReadOnlySpan<ScmapNodeRecord> ReadNodes(
        string source,
        ReadOnlySpan<byte> section,
        ScmapStringTable strings,
        out int invalidDeclaredStates)
    {
        if (section.Length < ScmapFormat.NodePreambleSize)
        {
            throw new ScmapFormatException(
                $"'{source}' has a {section.Length}-byte NODE section, short of the " +
                $"{ScmapFormat.NodePreambleSize}-byte preamble that carries its node count.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(section);
        long end = ScmapFormat.NodePreambleSize + ((long)count * ScmapFormat.NodeRecordSize);
        if (end > section.Length)
        {
            throw new ScmapFormatException(
                $"'{source}' declares {count} nodes, whose {ScmapFormat.NodeRecordSize}-byte records would " +
                $"end at byte {end} of a {section.Length}-byte NODE section.");
        }

        ReadOnlySpan<ScmapNodeRecord> nodes = MemoryMarshal.Cast<byte, ScmapNodeRecord>(
            section.Slice(ScmapFormat.NodePreambleSize, (int)count * ScmapFormat.NodeRecordSize));

        invalidDeclaredStates = 0;

        for (int i = 0; i < nodes.Length; i++)
        {
            ref readonly ScmapNodeRecord node = ref nodes[i];

            if (node.NameString >= (uint)strings.Count)
            {
                throw new ScmapFormatException(
                    $"'{source}' node {i} names string {node.NameString} of a {strings.Count}-string table. " +
                    "A node's name is its target name, so a name that cannot be read is entity wiring that " +
                    "silently resolves to nothing.");
            }

            if (node.ParentIndex < -1 || node.ParentIndex >= i)
            {
                throw new ScmapFormatException(
                    $"'{source}' node {i} ('{strings.GetStringOrEmpty((int)node.NameString)}') names parent " +
                    $"{node.ParentIndex}. Records are pre-order, so a parent index is -1 or strictly less " +
                    "than the child's own; anything else is a cycle a forward-pass loader walks forever.");
            }

            if (node.PayloadKind == ScmapPayloadKind.RetiredBrushModel)
            {
                // Named rather than guessed at. The value held the fused
                // entity-local brush model, whose mechanism was overturned, and
                // guessing that it meant a part brush is how a door silently
                // becomes a wall.
                throw new ScmapFormatException(
                    $"'{source}' node {i} ('{strings.GetStringOrEmpty((int)node.NameString)}') declares " +
                    "payload kind 3, which is retired and carries no meaning. An entity-owned brush is a " +
                    "part brush wearing the entity-owned flag. Recook the map.");
            }

            if (!Enum.IsDefined(node.PayloadKind))
            {
                throw new ScmapFormatException(
                    $"'{source}' node {i} ('{strings.GetStringOrEmpty((int)node.NameString)}') declares " +
                    $"payload kind {node.PayloadKindRaw}, which this engine has no meaning for at .scmap " +
                    $"format version {EngineInfo.CompiledMapFormatVersion}. Recook the map.");
            }

            if (node.DeclaredState == ScmapNodeState.Invalid) invalidDeclaredStates++;
        }

        return nodes;
    }

    private static ReadOnlySpan<ScmapChunkRecord> ReadChunks(
        string source,
        ReadOnlySpan<byte> section,
        int meshBlobLength,
        int bspBlobLength)
    {
        if (section.Length < ScmapFormat.ChunkPreambleSize)
        {
            throw new ScmapFormatException(
                $"'{source}' has a {section.Length}-byte CHDR section, short of the " +
                $"{ScmapFormat.ChunkPreambleSize}-byte preamble that carries its chunk count.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(section);
        long end = ScmapFormat.ChunkPreambleSize + ((long)count * ScmapFormat.ChunkRecordSize);
        if (end > section.Length)
        {
            throw new ScmapFormatException(
                $"'{source}' declares {count} chunks, whose {ScmapFormat.ChunkRecordSize}-byte records would " +
                $"end at byte {end} of a {section.Length}-byte CHDR section.");
        }

        ReadOnlySpan<ScmapChunkRecord> chunks = MemoryMarshal.Cast<byte, ScmapChunkRecord>(
            section.Slice(ScmapFormat.ChunkPreambleSize, (int)count * ScmapFormat.ChunkRecordSize));

        for (int i = 0; i < chunks.Length; i++)
        {
            ref readonly ScmapChunkRecord cell = ref chunks[i];

            if (i > 0 && Compare(in chunks[i - 1], in cell) >= 0)
            {
                // Not tidiness. The directory is sorted so a point lookup is a
                // binary search, and a binary search over an unsorted directory
                // answers "no such cell" for a cell that is right there, which
                // reads as a player falling through a floor they can see.
                throw new ScmapFormatException(
                    $"'{source}' chunk directory is not in ascending cell order at record {i}: " +
                    $"({chunks[i - 1].X}, {chunks[i - 1].Y}, {chunks[i - 1].Z}) is followed by " +
                    $"({cell.X}, {cell.Y}, {cell.Z}).");
            }

            RequireBlobRange(source, cell, "mesh", cell.MeshOffset, cell.MeshSize, meshBlobLength, "CMSH");
            RequireBlobRange(source, cell, "BSP", cell.BspOffset, cell.BspSize, bspBlobLength, "CBSP");
        }

        return chunks;
    }

    private static int Compare(in ScmapChunkRecord a, in ScmapChunkRecord b)
    {
        int c = a.X.CompareTo(b.X);
        if (c != 0) return c;
        c = a.Y.CompareTo(b.Y);
        return c != 0 ? c : a.Z.CompareTo(b.Z);
    }

    private static void RequireBlobRange(
        string source,
        in ScmapChunkRecord cell,
        string what,
        uint offset,
        uint size,
        int blobLength,
        string sectionName)
    {
        // A zero size is legal and common: a resident-only cell owns no render
        // geometry, so the compile produces no artifact for it and the directory
        // mirrors the compile.
        if (size == 0) return;

        if ((long)offset + size > blobLength)
        {
            throw new ScmapFormatException(
                $"'{source}' chunk ({cell.X}, {cell.Y}, {cell.Z}) claims a {size}-byte {what} blob at offset " +
                $"{offset} of a {blobLength}-byte {sectionName} section.");
        }

        if ((offset % ScmapFormat.PayloadAlignment) != 0)
        {
            throw new ScmapFormatException(
                $"'{source}' chunk ({cell.X}, {cell.Y}, {cell.Z}) places its {what} blob at offset {offset}, " +
                $"which is not a multiple of {ScmapFormat.PayloadAlignment}.");
        }
    }

    private static void RequireSectionInFile(string source, in ScmapSection record, int fileLength)
    {
        // Subtraction rather than addition, because offset + size is exactly the
        // arithmetic a corrupt file makes wrap: two values near ulong.MaxValue sum
        // to something small and pass a naive bound.
        if (record.Offset > (ulong)fileLength || record.Size > (ulong)fileLength - record.Offset)
        {
            throw new ScmapFormatException(
                $"'{source}' section '{ScmapFormat.DescribeFourCc(record.Kind)}' claims {record.Size} bytes " +
                $"at offset {record.Offset}, which runs past the {fileLength}-byte file.");
        }

        if (record.UncompressedSize != record.Size)
        {
            throw new ScmapFormatException(
                $"'{source}' section '{ScmapFormat.DescribeFourCc(record.Kind)}' declares {record.Size} " +
                $"stored bytes and {record.UncompressedSize} decoded ones while claiming no codec.");
        }
    }

    private static void RequireSection(string source, ReadOnlySpan<bool> present, int slot, uint fourCc)
    {
        if (present[slot]) return;

        throw new ScmapFormatException(
            $"'{source}' has no '{ScmapFormat.DescribeFourCc(fourCc)}' section, which every .scmap must " +
            "carry.");
    }

    private static void RequireCompileConstant(
        string source,
        string what,
        float stored,
        float engine,
        string consequence)
    {
        // Exact, and deliberately not a tolerance: both sides are compile-time
        // constants, so a difference of any size is a different build rather than
        // rounding. The comparison also refuses a NaN, which no tolerance would.
        if (stored == engine) return;

        throw new ScmapFormatException(
            $"'{source}' was compiled with a {what} of {stored} and this engine's is {engine}. " +
            $"Loaded anyway, {consequence}. Recook the map.");
    }
}
