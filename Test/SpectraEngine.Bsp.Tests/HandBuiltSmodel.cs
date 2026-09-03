using System.Buffers.Binary;
using System.Text;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// A writer of <c>.smodel</c> bytes built from the format specification rather
/// than from the engine's own types.
/// </summary>
/// <remarks>
/// <para><b>Deliberately hand-written, and it does not touch <c>SmodelFormat</c>,
/// <c>SmodelReader</c> or any of the record structs.</b> There is no cook rule
/// yet, and a reader verified only against its own writer proves the two agree
/// rather than that either is right: a field reordered in a struct would move in
/// both at once and every test would stay green. Every offset, width and
/// constant below is a literal taken from <c>docs/formats-and-pipeline.md</c>
/// section 2.3, so this file disagreeing with the reader is what a layout
/// regression looks like.</para>
/// <para>It is also why the FNV-1a is spelled out again here rather than
/// imported: the layout id is a value the reader recomputes and compares, so
/// borrowing the engine's implementation to produce it would make that check
/// compare a function with itself.</para>
/// <para>Every field is public and overridable because most of what this fixture
/// is for is building files that are <em>wrong</em> in one specific way.</para>
/// </remarks>
internal sealed class HandBuiltSmodel
{
    public const int HeaderSize = 64;
    public const int SectionTableOffset = 64;
    public const int SectionSize = 24;
    public const int PayloadAlignment = 16;
    public const uint NameOffsetAbsent = 0xFFFFFFFFu;

    // Every field below exists to be assigned by a caller building a file that
    // is wrong in one specific way, so a project that links this fixture and only
    // builds VALID files legitimately assigns none of them. CS0649 is a report
    // about this file from that project's point of view and says nothing about
    // either.
#pragma warning disable CS0649
    public uint Magic = FourCc("SMDL");
    public ushort FormatVersion = 1;
    public ushort Flags;
    public uint GeometryFormatVersion = 1;
    public float[] Bounds = [-1f, -2f, -3f, 4f, 5f, 6f];

    /// <summary>Written instead of the layout hashed from the VTXL payload.</summary>
    public uint? VertexLayoutIdOverride;

    /// <summary>Written instead of the real number of table records.</summary>
    public uint? SectionCountOverride;
#pragma warning restore CS0649

    private readonly List<Entry> _sections = [];
    private uint _layoutId = FnvOffsetBasis;

    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;

    private readonly record struct Entry(uint FourCc, byte[]? Payload, ulong Offset, ulong Length);

    /// <summary>Appends a section whose payload this fixture lays out and aligns.</summary>
    public HandBuiltSmodel Section(string fourCc, byte[] payload)
    {
        _sections.Add(new Entry(FourCc(fourCc), payload, 0, 0));
        return this;
    }

    /// <summary>
    /// Appends a table record naming a region this fixture does not write, which
    /// is how a section reaching past the file or landing off the alignment is
    /// expressed.
    /// </summary>
    public HandBuiltSmodel SectionAt(string fourCc, ulong offset, ulong length)
    {
        _sections.Add(new Entry(FourCc(fourCc), null, offset, length));
        return this;
    }

    // ------------------------------------------------------------------
    // Payload builders, each spelling out its record from the spec.
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>VTXL</c>: <c>u32 attributeCount</c>, <c>u32 strideFloats</c>, then eight
    /// bytes per attribute. Records the layout id the header should carry.
    /// </summary>
    public HandBuiltSmodel VertexLayout(
        uint strideFloats,
        params (byte Semantic, byte ComponentType, byte ComponentCount, ushort ByteOffset)[] attributes)
    {
        var buffer = new Buf();
        buffer.U32((uint)attributes.Length);
        buffer.U32(strideFloats);

        uint hash = FnvOffsetBasis;
        foreach (var attribute in attributes)
        {
            buffer.U8(attribute.Semantic);
            buffer.U8(attribute.ComponentType);
            buffer.U8(attribute.ComponentCount);
            buffer.U8(0);                       // per-attribute flags
            buffer.U16(attribute.ByteOffset);
            buffer.U16(0);                      // reserved

            hash = (hash ^ attribute.Semantic) * FnvPrime;
            hash = (hash ^ attribute.ComponentCount) * FnvPrime;
        }

        _layoutId = hash;
        return Section("VTXL", buffer.ToArray());
    }

    /// <summary><c>VBUF</c>: interleaved floats, nothing else.</summary>
    public HandBuiltSmodel VertexBuffer(params float[] floats)
    {
        var buffer = new Buf();
        foreach (float value in floats) buffer.F32(value);
        return Section("VBUF", buffer.ToArray());
    }

    /// <summary><c>IBUF</c> at sixteen bits, which is what the header flag must not say.</summary>
    public HandBuiltSmodel Indices16(params ushort[] indices)
    {
        var buffer = new Buf();
        foreach (ushort value in indices) buffer.U16(value);
        return Section("IBUF", buffer.ToArray());
    }

    /// <summary><c>IBUF</c> at thirty-two bits, which the header flag must say.</summary>
    public HandBuiltSmodel Indices32(params uint[] indices)
    {
        var buffer = new Buf();
        foreach (uint value in indices) buffer.U32(value);
        return Section("IBUF", buffer.ToArray());
    }

    /// <summary>
    /// <c>SUBM</c>: <c>{u32 IndexStart, u32 IndexCount, u32 MaterialNameOffset,
    /// u32 Flags, f32[6] Bounds}</c>.
    /// </summary>
    public HandBuiltSmodel Submeshes(params (uint Start, uint Count, uint MaterialName)[] submeshes)
    {
        var withBounds = new (uint, uint, uint, float[])[submeshes.Length];
        for (int i = 0; i < submeshes.Length; i++)
        {
            withBounds[i] = (
                submeshes[i].Start, submeshes[i].Count, submeshes[i].MaterialName,
                [-1f, -1f, -1f, 1f, 1f, 1f]);
        }

        return Submeshes(withBounds);
    }

    /// <summary>
    /// The same record with its bounds stated, which a caller comparing this
    /// fixture's bytes against a real writer's needs.
    /// </summary>
    public HandBuiltSmodel Submeshes(
        params (uint Start, uint Count, uint MaterialName, float[] Bounds)[] submeshes)
    {
        var buffer = new Buf();
        foreach (var submesh in submeshes)
        {
            buffer.U32(submesh.Start);
            buffer.U32(submesh.Count);
            buffer.U32(submesh.MaterialName);
            buffer.U32(0);
            foreach (float value in submesh.Bounds) buffer.F32(value);
        }

        return Section("SUBM", buffer.ToArray());
    }

    /// <summary><c>LODS</c>: <c>{f32 ScreenHeightThreshold, u32 FirstSubmesh, u32 SubmeshCount}</c>.</summary>
    public HandBuiltSmodel Lods(params (float Threshold, uint FirstSubmesh, uint SubmeshCount)[] lods)
    {
        var buffer = new Buf();
        foreach (var lod in lods)
        {
            buffer.F32(lod.Threshold);
            buffer.U32(lod.FirstSubmesh);
            buffer.U32(lod.SubmeshCount);
        }

        return Section("LODS", buffer.ToArray());
    }

    /// <summary><c>SKEL</c>: <c>{u32 NameOffset, i32 ParentIndex, f32[12] InverseBind}</c>.</summary>
    public HandBuiltSmodel Skeleton(params (uint NameOffset, int Parent, float[] InverseBind)[] joints)
    {
        var buffer = new Buf();
        foreach (var joint in joints)
        {
            buffer.U32(joint.NameOffset);
            buffer.I32(joint.Parent);
            foreach (float value in joint.InverseBind) buffer.F32(value);
        }

        return Section("SKEL", buffer.ToArray());
    }

    /// <summary>
    /// <c>COLL</c>: <c>u32 hullCount</c>, the hull table, padding to the next
    /// sixteen-byte boundary, then the flat plane array.
    /// </summary>
    public HandBuiltSmodel Collision(
        (uint PlaneStart, uint PlaneCount)[] hulls,
        (float Nx, float Ny, float Nz, float D)[] planes)
    {
        var buffer = new Buf();
        buffer.U32((uint)hulls.Length);
        foreach (var hull in hulls)
        {
            buffer.U32(hull.PlaneStart);
            buffer.U32(hull.PlaneCount);
        }

        buffer.AlignTo(PayloadAlignment);
        foreach (var plane in planes)
        {
            buffer.F32(plane.Nx);
            buffer.F32(plane.Ny);
            buffer.F32(plane.Nz);
            buffer.F32(plane.D);
        }

        return Section("COLL", buffer.ToArray());
    }

    /// <summary>
    /// <c>NAME</c>: <c>u16</c>-prefixed UTF-8 records, back to back. Returns each
    /// record's offset, because a submesh or a joint has to name one.
    /// </summary>
    public HandBuiltSmodel Names(out uint[] offsets, params string[] names)
    {
        var buffer = new Buf();
        offsets = new uint[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            offsets[i] = (uint)buffer.Length;
            byte[] utf8 = Encoding.UTF8.GetBytes(names[i]);
            buffer.U16((ushort)utf8.Length);
            buffer.Bytes(utf8);
        }

        return Section("NAME", buffer.ToArray());
    }

    /// <summary>Lays the whole file out and returns its bytes.</summary>
    public byte[] Build()
    {
        int cursor = SectionTableOffset + (_sections.Count * SectionSize);
        var placed = new Entry[_sections.Count];
        for (int i = 0; i < _sections.Count; i++)
        {
            Entry entry = _sections[i];
            if (entry.Payload is null)
            {
                placed[i] = entry;
                continue;
            }

            cursor = AlignUp(cursor, PayloadAlignment);
            placed[i] = entry with { Offset = (ulong)cursor, Length = (ulong)entry.Payload.Length };
            cursor += entry.Payload.Length;
        }

        byte[] file = new byte[Math.Max(cursor, HeaderSize)];
        Span<byte> span = file;

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x04..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x06..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x08..], GeometryFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..], SectionCountOverride ?? (uint)_sections.Count);
        for (int i = 0; i < 6; i++)
            BinaryPrimitives.WriteSingleLittleEndian(span[(0x10 + (i * 4))..], Bounds[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x28..], VertexLayoutIdOverride ?? _layoutId);

        for (int i = 0; i < placed.Length; i++)
        {
            Span<byte> record = span.Slice(SectionTableOffset + (i * SectionSize), SectionSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record, placed[i].FourCc);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], 0);
            BinaryPrimitives.WriteUInt64LittleEndian(record[8..], placed[i].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(record[16..], placed[i].Length);

            placed[i].Payload?.CopyTo(span[(int)placed[i].Offset..]);
        }

        return file;
    }

    private static uint FourCc(string text) =>
        text[0] | ((uint)text[1] << 8) | ((uint)text[2] << 16) | ((uint)text[3] << 24);

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    /// <summary>A growable little-endian byte writer, so a payload reads as its spec does.</summary>
    private sealed class Buf
    {
        private readonly List<byte> _bytes = [];

        public int Length => _bytes.Count;

        public void U8(byte value) => _bytes.Add(value);

        public void U16(ushort value)
        {
            Span<byte> scratch = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(scratch, value);
            _bytes.AddRange(scratch);
        }

        public void U32(uint value)
        {
            Span<byte> scratch = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, value);
            _bytes.AddRange(scratch);
        }

        public void I32(int value)
        {
            Span<byte> scratch = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(scratch, value);
            _bytes.AddRange(scratch);
        }

        public void F32(float value)
        {
            Span<byte> scratch = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(scratch, value);
            _bytes.AddRange(scratch);
        }

        public void Bytes(ReadOnlySpan<byte> value) => _bytes.AddRange(value);

        public void AlignTo(int alignment)
        {
            while (_bytes.Count % alignment != 0) _bytes.Add(0);
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
