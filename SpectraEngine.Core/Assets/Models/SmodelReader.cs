using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// Reads a cooked <c>.smodel</c> out of the bytes it sits in, with no stream, no
/// allocation per record and no copy of anything large.
/// </summary>
/// <remarks>
/// <para><b>A span in, and every table cast back out of it.</b> The bytes are
/// normally a memory-mapped view of a pack payload, so the reader must never take
/// ownership of them and must never index them without checking first: an
/// out-of-range read into a mapping is an access violation with no managed stack,
/// which is a crash nobody can attribute to a file. Every offset and every length
/// below is therefore bounds-checked before it is used, and every failure is a
/// <see cref="SmodelFormatException"/> naming what was wrong and what was
/// expected.</para>
/// <para><b>An unknown section FourCC is skipped, not refused.</b> That is the
/// most important structural decision in the format: it is what lets a section
/// designed today be written by a later cooker with no version bump, and it is
/// the same stance the map codec takes for unknown JSON members. Everything
/// <em>else</em> about the file is strict, because a cooked artifact is a build
/// output that can always be regenerated.</para>
/// <para><b>What this reader does not do is walk the indices.</b> Checking that
/// every index addresses a real vertex is O(indices) and would spend, on load,
/// exactly the time the zero-copy layout exists to save. The submesh ranges are
/// checked against the index buffer, which is the bound that turns a malformed
/// file into a wrong picture rather than into a read past the end.</para>
/// </remarks>
public static class SmodelReader
{
    private const int VertexLayoutSlot = 0;
    private const int VertexBufferSlot = 1;
    private const int IndexBufferSlot = 2;
    private const int SubmeshSlot = 3;
    private const int LodSlot = 4;
    private const int SkeletonSlot = 5;
    private const int CollisionSlot = 6;
    private const int NameSlot = 7;
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
    /// <exception cref="SmodelFormatException">The file is not a readable <c>.smodel</c>.</exception>
    /// <exception cref="PlatformNotSupportedException">The machine is big-endian.</exception>
    public static SmodelModel Read(ReadOnlySpan<byte> file, string source)
    {
        SmodelFormat.RequireLittleEndian();

        if (file.Length < SmodelFormat.MinimumFileSize)
        {
            throw new SmodelFormatException(
                $"'{source}' is {file.Length} bytes, too short to hold a " +
                $"{SmodelFormat.HeaderSize}-byte .smodel header.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(file);
        if (magic != SmodelFormat.Magic)
        {
            throw new SmodelFormatException(
                $"'{source}' is not a .smodel file: its first four bytes read " +
                $"'{SmodelFormat.DescribeFourCc(magic)}', not 'SMDL'.");
        }

        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(file[0x04..]);
        if (formatVersion != EngineInfo.ModelFormatVersion)
        {
            // Cooked, so it versions the strict way: exact match, and a message
            // that says recook. There is nothing to carry forward and nothing to
            // degrade to, because the bytes past the header only mean anything
            // under the version that wrote them.
            throw new SmodelFormatException(
                $"'{source}' is .smodel format version {formatVersion}, and this engine reads version " +
                $"{EngineInfo.ModelFormatVersion}. Recook the model.");
        }

        var flags = (SmodelFlags)BinaryPrimitives.ReadUInt16LittleEndian(file[0x06..]);

        uint geometryFormatVersion = BinaryPrimitives.ReadUInt32LittleEndian(file[0x08..]);
        if (geometryFormatVersion != EngineInfo.GeometryFormatVersion)
        {
            // The separate gate, and the one that actually bites: the container
            // can be unchanged while what a vertex buffer MEANS has moved under
            // it, and the symptom of missing that is a misinterpreted buffer,
            // which draws garbage on one backend and refuses an input layout on
            // another.
            throw new SmodelFormatException(
                $"'{source}' was cooked at geometry format version {geometryFormatVersion}, and this " +
                $"engine reads version {EngineInfo.GeometryFormatVersion}. Recook the model.");
        }

        uint sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(file[0x0C..]);

        Vector3 boundsMin = ReadVector3(file[0x10..]);
        Vector3 boundsMax = ReadVector3(file[0x1C..]);
        uint vertexLayoutId = BinaryPrimitives.ReadUInt32LittleEndian(file[0x28..]);

        long tableEnd = SmodelFormat.SectionTableOffset + ((long)sectionCount * SmodelFormat.SectionSize);
        if (tableEnd > file.Length)
        {
            throw new SmodelFormatException(
                $"'{source}' declares {sectionCount} sections, whose {SmodelFormat.SectionSize}-byte table " +
                $"would end at byte {tableEnd} of a {file.Length}-byte file.");
        }

        Span<int> sectionOffset = stackalloc int[KnownSectionCount];
        Span<int> sectionLength = stackalloc int[KnownSectionCount];
        Span<bool> sectionPresent = stackalloc bool[KnownSectionCount];
        sectionPresent.Clear();

        int skipped = 0;
        for (uint i = 0; i < sectionCount; i++)
        {
            ReadOnlySpan<byte> record =
                file.Slice(SmodelFormat.SectionTableOffset + ((int)i * SmodelFormat.SectionSize), SmodelFormat.SectionSize);

            uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(record);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]);

            // Bounds and alignment are checked for EVERY section, known or not.
            // A section this reader will step over is still a claim about where
            // the file's bytes are, and letting an unknown one describe an
            // impossible region would make the forward-compatibility mechanism a
            // way to smuggle a malformed file past the gate.
            RequireSectionInFile(source, fourCc, offset, length, file.Length);

            if ((offset % SmodelFormat.PayloadAlignment) != 0)
            {
                throw new SmodelFormatException(
                    $"'{source}' section '{SmodelFormat.DescribeFourCc(fourCc)}' starts at byte {offset}, " +
                    $"which is not a multiple of {SmodelFormat.PayloadAlignment}. Payloads are reinterpreted " +
                    "in place, so an unaligned section start is a plane straddling a boundary.");
            }

            int slot = KnownSlot(fourCc);
            if (slot < 0)
            {
                skipped++;
                continue;
            }

            if (sectionPresent[slot])
            {
                throw new SmodelFormatException(
                    $"'{source}' carries section '{SmodelFormat.DescribeFourCc(fourCc)}' more than once. " +
                    "A section names one region of the file, so a reader would have to choose, and choosing " +
                    "silently is how half a model comes from one copy and half from the other.");
            }

            sectionPresent[slot] = true;
            sectionOffset[slot] = (int)offset;
            sectionLength[slot] = (int)length;
        }

        RequireSection(source, sectionPresent, VertexLayoutSlot, SmodelFormat.VertexLayoutSection);
        RequireSection(source, sectionPresent, VertexBufferSlot, SmodelFormat.VertexBufferSection);
        RequireSection(source, sectionPresent, IndexBufferSlot, SmodelFormat.IndexBufferSection);
        RequireSection(source, sectionPresent, SubmeshSlot, SmodelFormat.SubmeshSection);

        ReadOnlySpan<byte> names = sectionPresent[NameSlot]
            ? file.Slice(sectionOffset[NameSlot], sectionLength[NameSlot])
            : default;

        ReadOnlySpan<SmodelVertexAttribute> attributes = ReadVertexLayout(
            source,
            file.Slice(sectionOffset[VertexLayoutSlot], sectionLength[VertexLayoutSlot]),
            vertexLayoutId,
            out uint strideFloats);

        ReadOnlySpan<float> vertices = ReadVertexBuffer(
            source,
            file.Slice(sectionOffset[VertexBufferSlot], sectionLength[VertexBufferSlot]),
            strideFloats);

        bool index32 = (flags & SmodelFlags.Index32) != 0;
        ReadOnlySpan<byte> indexBytes = file.Slice(sectionOffset[IndexBufferSlot], sectionLength[IndexBufferSlot]);
        int indexWidth = index32 ? sizeof(uint) : sizeof(ushort);
        RequireWholeRecords(source, SmodelFormat.IndexBufferSection, indexBytes.Length, indexWidth);

        ReadOnlySpan<ushort> indices16 = index32 ? default : MemoryMarshal.Cast<byte, ushort>(indexBytes);
        ReadOnlySpan<uint> indices32 = index32 ? MemoryMarshal.Cast<byte, uint>(indexBytes) : default;
        int indexCount = indexBytes.Length / indexWidth;

        ReadOnlySpan<SmodelSubmesh> submeshes = ReadSubmeshes(
            source,
            file.Slice(sectionOffset[SubmeshSlot], sectionLength[SubmeshSlot]),
            indexCount,
            names);

        ReadOnlySpan<SmodelLod> lods = sectionPresent[LodSlot]
            ? ReadLods(source, file.Slice(sectionOffset[LodSlot], sectionLength[LodSlot]), submeshes.Length)
            : default;

        ReadOnlySpan<SmodelJoint> joints = sectionPresent[SkeletonSlot]
            ? ReadSkeleton(source, file.Slice(sectionOffset[SkeletonSlot], sectionLength[SkeletonSlot]), names)
            : default;

        ReadOnlySpan<SmodelCollisionHull> hulls = default;
        ReadOnlySpan<Plane> planes = default;
        if (sectionPresent[CollisionSlot])
        {
            ReadCollision(
                source,
                file.Slice(sectionOffset[CollisionSlot], sectionLength[CollisionSlot]),
                out hulls,
                out planes);
        }

        RequireFlagMatchesSection(source, flags, SmodelFlags.HasSkeleton, sectionPresent[SkeletonSlot], "SKEL");
        RequireFlagMatchesSection(source, flags, SmodelFlags.HasCollision, sectionPresent[CollisionSlot], "COLL");

        return new SmodelModel(
            source,
            flags,
            vertexLayoutId,
            boundsMin,
            boundsMax,
            attributes,
            strideFloats,
            vertices,
            indices16,
            indices32,
            submeshes,
            lods,
            joints,
            hulls,
            planes,
            names,
            skipped);
    }

    /// <summary>
    /// Refuses a name offset that is not a whole record inside the name blob.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="SmodelModel.GetName"/> so the offset a submesh was
    /// validated with and the offset a caller reads with cannot be checked two
    /// different ways.
    /// </remarks>
    internal static void RequireNameRecord(string source, ReadOnlySpan<byte> names, uint nameOffset, string what)
    {
        if (nameOffset == SmodelFormat.NameOffsetAbsent) return;

        long start = nameOffset;
        if (start + sizeof(ushort) > names.Length)
        {
            throw new SmodelFormatException(
                $"'{source}' has {what} at byte {nameOffset} of a {names.Length}-byte NAME section, " +
                "which cannot hold even a length prefix there.");
        }

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(names[(int)start..]);
        if (start + sizeof(ushort) + length > names.Length)
        {
            throw new SmodelFormatException(
                $"'{source}' has {what} at byte {nameOffset} declaring {length} bytes, which runs past " +
                $"the {names.Length}-byte NAME section.");
        }
    }

    private static int KnownSlot(uint fourCc) => fourCc switch
    {
        SmodelFormat.VertexLayoutSection => VertexLayoutSlot,
        SmodelFormat.VertexBufferSection => VertexBufferSlot,
        SmodelFormat.IndexBufferSection => IndexBufferSlot,
        SmodelFormat.SubmeshSection => SubmeshSlot,
        SmodelFormat.LodSection => LodSlot,
        SmodelFormat.SkeletonSection => SkeletonSlot,
        SmodelFormat.CollisionSection => CollisionSlot,
        SmodelFormat.NameSection => NameSlot,

        // ANIM lands here on purpose. It is reserved and never written, so a file
        // carrying one is a file from a future this reader does not implement, and
        // stepping over it is exactly what the skip rule is for.
        _ => -1,
    };

    private static Vector3 ReadVector3(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));

    private static void RequireSectionInFile(string source, uint fourCc, ulong offset, ulong length, int fileLength)
    {
        // Subtraction rather than addition, because offset + length is exactly the
        // arithmetic a hostile or corrupt file makes wrap: two values near
        // ulong.MaxValue sum to something small and pass a naive bound.
        if (offset > (ulong)fileLength || length > (ulong)fileLength - offset)
        {
            throw new SmodelFormatException(
                $"'{source}' section '{SmodelFormat.DescribeFourCc(fourCc)}' claims {length} bytes at " +
                $"offset {offset}, which runs past the {fileLength}-byte file.");
        }
    }

    private static void RequireSection(string source, ReadOnlySpan<bool> present, int slot, uint fourCc)
    {
        if (present[slot]) return;

        throw new SmodelFormatException(
            $"'{source}' has no '{SmodelFormat.DescribeFourCc(fourCc)}' section, which every .smodel " +
            "must carry.");
    }

    private static void RequireWholeRecords(string source, uint fourCc, int length, int recordSize)
    {
        if (length % recordSize == 0) return;

        // MemoryMarshal.Cast truncates a partial trailing element in silence, so
        // without this the last record of a corrupt section simply disappears and
        // every count downstream is one short with nothing reporting it.
        throw new SmodelFormatException(
            $"'{source}' section '{SmodelFormat.DescribeFourCc(fourCc)}' is {length} bytes, which is not " +
            $"a whole number of {recordSize}-byte records.");
    }

    private static void RequireFlagMatchesSection(
        string source,
        SmodelFlags flags,
        SmodelFlags flag,
        bool present,
        string sectionName)
    {
        bool declared = (flags & flag) != 0;
        if (declared == present) return;

        throw new SmodelFormatException(
            $"'{source}' sets header flag {flag} to {declared} while its section table " +
            $"{(present ? "does" : "does not")} carry '{sectionName}'. The table is the truth and the flag " +
            "is a summary of it, so a disagreement is a writer edited in one place and not the other.");
    }

    private static ReadOnlySpan<SmodelVertexAttribute> ReadVertexLayout(
        string source,
        ReadOnlySpan<byte> section,
        uint stampedLayoutId,
        out uint strideFloats)
    {
        if (section.Length < SmodelFormat.VertexLayoutPreambleSize)
        {
            throw new SmodelFormatException(
                $"'{source}' section 'VTXL' is {section.Length} bytes, too short to hold its " +
                $"{SmodelFormat.VertexLayoutPreambleSize}-byte attribute count and stride.");
        }

        uint attributeCount = BinaryPrimitives.ReadUInt32LittleEndian(section);
        strideFloats = BinaryPrimitives.ReadUInt32LittleEndian(section[4..]);

        long expected = SmodelFormat.VertexLayoutPreambleSize
            + ((long)attributeCount * SmodelFormat.VertexAttributeSize);
        if (expected != section.Length)
        {
            throw new SmodelFormatException(
                $"'{source}' section 'VTXL' declares {attributeCount} attributes, which need {expected} " +
                $"bytes, but the section is {section.Length}.");
        }

        if (attributeCount == 0)
        {
            throw new SmodelFormatException(
                $"'{source}' section 'VTXL' declares no attributes, so nothing in VBUF has a meaning.");
        }

        if (strideFloats == 0)
        {
            throw new SmodelFormatException(
                $"'{source}' section 'VTXL' declares a stride of zero floats, which makes every vertex " +
                "the same vertex and the vertex count infinite.");
        }

        long strideBytes = (long)strideFloats * sizeof(float);
        if (strideBytes > ushort.MaxValue)
        {
            // Bounded by the format's own arithmetic rather than by a taste
            // limit: an attribute states its ByteOffset in a u16, so a vertex
            // that did not fit in one could not address its own components. The
            // check is also what keeps the stride-to-bytes multiplication in
            // ReadVertexBuffer well inside an int, where an unchecked one would
            // wrap and pass every length test that follows.
            throw new SmodelFormatException(
                $"'{source}' section 'VTXL' declares a stride of {strideFloats} floats ({strideBytes} " +
                $"bytes), and an attribute's byte offset is a u16, so a vertex cannot exceed " +
                $"{ushort.MaxValue} bytes.");
        }

        ReadOnlySpan<SmodelVertexAttribute> attributes =
            MemoryMarshal.Cast<byte, SmodelVertexAttribute>(section[SmodelFormat.VertexLayoutPreambleSize..]);

        uint computed = SmodelFormat.ComputeVertexLayoutId(attributes);
        if (computed != stampedLayoutId)
        {
            throw new SmodelFormatException(
                $"'{source}' stamps vertex layout id 0x{stampedLayoutId:X8} in its header, but its VTXL " +
                $"section hashes to 0x{computed:X8}. One of the two was written by a cooker the other " +
                "half of was not.");
        }

        return attributes;
    }

    private static ReadOnlySpan<float> ReadVertexBuffer(string source, ReadOnlySpan<byte> section, uint strideFloats)
    {
        // ReadVertexLayout has already refused a stride too large for a u16 byte
        // offset, so this multiplication is bounded far inside an int.
        int strideBytes = (int)strideFloats * sizeof(float);
        RequireWholeRecords(source, SmodelFormat.VertexBufferSection, section.Length, strideBytes);
        return MemoryMarshal.Cast<byte, float>(section);
    }

    private static ReadOnlySpan<SmodelSubmesh> ReadSubmeshes(
        string source,
        ReadOnlySpan<byte> section,
        int indexCount,
        ReadOnlySpan<byte> names)
    {
        RequireWholeRecords(source, SmodelFormat.SubmeshSection, section.Length, SmodelFormat.SubmeshSize);

        if (section.Length == 0)
        {
            throw new SmodelFormatException(
                $"'{source}' section 'SUBM' is empty, so the model has no drawable range at all.");
        }

        ReadOnlySpan<SmodelSubmesh> submeshes = MemoryMarshal.Cast<byte, SmodelSubmesh>(section);

        for (int i = 0; i < submeshes.Length; i++)
        {
            SmodelSubmesh submesh = submeshes[i];
            long end = (long)submesh.IndexStart + submesh.IndexCount;
            if (end > indexCount)
            {
                throw new SmodelFormatException(
                    $"'{source}' submesh {i} covers indices {submesh.IndexStart} to {end}, past the " +
                    $"{indexCount} indices in IBUF.");
            }

            RequireNameRecord(source, names, submesh.MaterialNameOffset, $"a material name for submesh {i}");
        }

        return submeshes;
    }

    private static ReadOnlySpan<SmodelLod> ReadLods(string source, ReadOnlySpan<byte> section, int submeshCount)
    {
        RequireWholeRecords(source, SmodelFormat.LodSection, section.Length, SmodelFormat.LodSize);
        ReadOnlySpan<SmodelLod> lods = MemoryMarshal.Cast<byte, SmodelLod>(section);

        for (int i = 0; i < lods.Length; i++)
        {
            long end = (long)lods[i].FirstSubmesh + lods[i].SubmeshCount;
            if (end > submeshCount)
            {
                throw new SmodelFormatException(
                    $"'{source}' LOD {i} covers submeshes {lods[i].FirstSubmesh} to {end}, past the " +
                    $"{submeshCount} in SUBM.");
            }
        }

        return lods;
    }

    private static ReadOnlySpan<SmodelJoint> ReadSkeleton(
        string source,
        ReadOnlySpan<byte> section,
        ReadOnlySpan<byte> names)
    {
        RequireWholeRecords(source, SmodelFormat.SkeletonSection, section.Length, SmodelFormat.JointSize);
        ReadOnlySpan<SmodelJoint> joints = MemoryMarshal.Cast<byte, SmodelJoint>(section);

        for (int i = 0; i < joints.Length; i++)
        {
            RequireNameRecord(source, names, joints[i].NameOffset, $"a name for joint {i}");

            int parent = joints[i].ParentIndex;
            if (parent >= i || parent < SmodelJoint.NoParent)
            {
                // The whole point of the ordering rule: with it, a hierarchy walk
                // is one forward loop. Without it, a forward reference reads a
                // parent matrix that has not been computed yet, which for a fresh
                // array is identity, so the pose is wrong in a way that still
                // looks like a pose.
                string name = joints[i].HasName
                    ? $"'{ReadNameFor(source, names, joints[i].NameOffset)}'"
                    : "unnamed";

                throw new SmodelFormatException(
                    $"'{source}' joint {i} ({name}) declares parent {parent}, which is not less than its " +
                    $"own index {i}. Every joint's parent must precede it, so a hierarchy can be built in " +
                    "one forward pass.");
            }
        }

        return joints;
    }

    private static void ReadCollision(
        string source,
        ReadOnlySpan<byte> section,
        out ReadOnlySpan<SmodelCollisionHull> hulls,
        out ReadOnlySpan<Plane> planes)
    {
        if (section.Length < SmodelFormat.CollisionPreambleSize)
        {
            throw new SmodelFormatException(
                $"'{source}' section 'COLL' is {section.Length} bytes, too short to hold its hull count.");
        }

        uint hullCount = BinaryPrimitives.ReadUInt32LittleEndian(section);
        long hullTableEnd = SmodelFormat.CollisionPreambleSize
            + ((long)hullCount * SmodelFormat.CollisionHullSize);
        if (hullTableEnd > section.Length)
        {
            throw new SmodelFormatException(
                $"'{source}' section 'COLL' declares {hullCount} hulls, whose table would end at byte " +
                $"{hullTableEnd} of a {section.Length}-byte section.");
        }

        // The plane array is realigned inside the section rather than packed
        // against the hull table, because the whole reason it is a flat array of
        // System.Numerics.Plane is that it can be cast in place, and a hull table
        // of any odd length would otherwise leave the first plane straddling a
        // 16-byte boundary.
        long planesStart = SmodelFormat.AlignUp(hullTableEnd, SmodelFormat.PayloadAlignment);
        if (planesStart > section.Length)
        {
            throw new SmodelFormatException(
                $"'{source}' section 'COLL' is {section.Length} bytes, which ends inside the padding " +
                $"before its plane array at byte {planesStart}.");
        }

        int planeBytes = section.Length - (int)planesStart;
        RequireWholeRecords(source, SmodelFormat.CollisionSection, planeBytes, SmodelFormat.CollisionPlaneSize);

        hulls = MemoryMarshal.Cast<byte, SmodelCollisionHull>(
            section.Slice(SmodelFormat.CollisionPreambleSize, (int)hullCount * SmodelFormat.CollisionHullSize));
        planes = MemoryMarshal.Cast<byte, Plane>(section[(int)planesStart..]);

        for (int i = 0; i < hulls.Length; i++)
        {
            if (hulls[i].PlaneCount < SmodelFormat.MinimumHullPlanes)
            {
                throw new SmodelFormatException(
                    $"'{source}' collision hull {i} has {hulls[i].PlaneCount} planes, and Brush needs at " +
                    $"least {SmodelFormat.MinimumHullPlanes} to bound a volume. A hull that cannot become " +
                    "a Brush is collision that silently is not there.");
            }

            long end = (long)hulls[i].PlaneStart + hulls[i].PlaneCount;
            if (end > planes.Length)
            {
                throw new SmodelFormatException(
                    $"'{source}' collision hull {i} covers planes {hulls[i].PlaneStart} to {end}, past the " +
                    $"{planes.Length} in COLL.");
            }
        }
    }

    private static string ReadNameFor(string source, ReadOnlySpan<byte> names, uint nameOffset)
    {
        RequireNameRecord(source, names, nameOffset, "a name");
        ReadOnlySpan<byte> record = names[(int)nameOffset..];
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(record);
        return System.Text.Encoding.UTF8.GetString(record.Slice(sizeof(ushort), length));
    }
}
