using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Spectra.Kitchen.Models;

/// <summary>
/// One drawable range a cooked model will carry.
/// </summary>
/// <param name="IndexStart">First index of the range within the shared index buffer.</param>
/// <param name="IndexCount">How many indices the range covers.</param>
/// <param name="MaterialPath">
/// The content path of the material this range wears, or null when the cook found
/// none.
/// </param>
/// <remarks>
/// <b>A PATH, and the writer has no way to take an id, which is the point.</b>
/// <c>MaterialRef.Id</c> is per-process interning order: a file carrying one
/// loads correctly in the test that wrote it and mis-textures the world the day a
/// second asset interns first, with nothing reporting it. The standing invariant
/// is that an id is never written to disk, and the cheapest way to keep an
/// invariant is to make the wrong value inexpressible.
/// </remarks>
public readonly record struct SmodelSubmeshSpec(uint IndexStart, uint IndexCount, string? MaterialPath);

/// <summary>
/// Writes a <c>.smodel</c>: the 64-byte header and section table
/// <see cref="SmodelFormat"/> declares, then <c>VTXL</c>, <c>VBUF</c>,
/// <c>IBUF</c>, <c>SUBM</c> and <c>NAME</c>.
/// </summary>
/// <remarks>
/// <para><b>Every offset and every size comes from <see cref="SmodelFormat"/>,
/// never from a literal here.</b> A writer that computes a section start from
/// its own running cursor and a reader that recomputes it from a constant agree
/// exactly until one of them is edited, and then disagree as a read into the
/// middle of somebody else's bytes rather than as an exception. That is the
/// lesson <c>PackFormat</c> and <c>ShaderFileLayout</c> both already
/// record.</para>
/// <para><b>The bounds are COMPUTED here, from the geometry, rather than taken
/// from the caller.</b> A box that disagrees with the vertices it describes is
/// not an error anywhere: it culls a model that is on screen or fails to cull one
/// that is not, and the only symptom is geometry that flickers at the edge of the
/// frustum. Deriving them is a walk the writer is already making to check the
/// index ranges.</para>
/// <para><b>The index width is DERIVED from the vertex count</b>, sixteen bits
/// whenever they fit, because that halves the index buffer of every prop in a
/// game and costs one widening at load. The header flag is written from the same
/// decision that sized the buffer, so the two provably agree - the reader checks
/// that they do, and a disagreement there is a buffer read at the wrong element
/// size.</para>
/// <para><b>Padding is explicitly zero.</b> A managed array arrives zeroed so it
/// costs nothing, and it buys the thing the pack writer learned the hard way: an
/// unzeroed byte in a field nothing reads turns a byte-identity oracle red in a
/// way that is very hard to bisect.</para>
/// <para><b>What this writes is v1's SUBSET of the format, and the omissions are
/// free.</b> <c>LODS</c>, <c>SKEL</c>, <c>COLL</c> and <c>ANIM</c> are designed
/// and unwritten; the reader skips a FourCC it does not know and derives presence
/// from the section table, so a later cooker adding one needs no version
/// bump.</para>
/// </remarks>
public static class SmodelWriter
{
    /// <summary>
    /// The largest vertex count that still fits 16-bit indices, which is what
    /// decides <see cref="SmodelFlags.Index32"/>.
    /// </summary>
    public const int MaxVertexCountForIndex16 = ushort.MaxValue + 1;

    /// <summary>
    /// Writes one cooked model.
    /// </summary>
    /// <param name="vertices">
    /// The whole model's interleaved vertices in
    /// <see cref="SmodelStandardLayout"/>: eight floats each.
    /// </param>
    /// <param name="indices">The whole model's index buffer, three per triangle.</param>
    /// <param name="submeshes">Ranges into <paramref name="indices"/>, in draw order.</param>
    /// <exception cref="ArgumentException">
    /// The inputs describe a file this format cannot hold. Thrown rather than
    /// clamped or repaired, because every one of these is a cooker bug and a
    /// repaired one ships a model that is merely wrong.
    /// </exception>
    public static byte[] Write(
        ReadOnlySpan<float> vertices,
        ReadOnlySpan<uint> indices,
        IReadOnlyList<SmodelSubmeshSpec> submeshes)
    {
        ArgumentNullException.ThrowIfNull(submeshes);
        SmodelFormat.RequireLittleEndian();

        int stride = (int)SmodelStandardLayout.StrideFloats;
        if (vertices.Length == 0 || vertices.Length % stride != 0)
        {
            throw new ArgumentException(
                $"{vertices.Length} floats is not a whole number of {stride}-float vertices.",
                nameof(vertices));
        }

        int vertexCount = vertices.Length / stride;
        if (indices.Length == 0 || indices.Length % 3 != 0)
        {
            throw new ArgumentException(
                $"{indices.Length} indices is not a whole number of triangles.", nameof(indices));
        }

        if (submeshes.Count == 0)
        {
            throw new ArgumentException(
                "A .smodel needs at least one submesh, or the model has no drawable range at all.",
                nameof(submeshes));
        }

        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] < (uint)vertexCount) continue;

            throw new ArgumentException(
                $"Index {i} names vertex {indices[i]} and the model has {vertexCount}.", nameof(indices));
        }

        bool index32 = vertexCount > MaxVertexCountForIndex16;

        var records = new SmodelSubmesh[submeshes.Count];
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var names = new NameBlob();

        for (int i = 0; i < submeshes.Count; i++)
        {
            SmodelSubmeshSpec spec = submeshes[i];
            long end = (long)spec.IndexStart + spec.IndexCount;
            if (end > indices.Length)
            {
                throw new ArgumentException(
                    $"Submesh {i} covers indices {spec.IndexStart} to {end} and the model has " +
                    $"{indices.Length}.",
                    nameof(submeshes));
            }

            if (spec.IndexCount == 0 || spec.IndexCount % 3 != 0)
            {
                throw new ArgumentException(
                    $"Submesh {i} covers {spec.IndexCount} indices, which is not a whole number of " +
                    "triangles.",
                    nameof(submeshes));
            }

            Bounds(vertices, indices, stride, (int)spec.IndexStart, (int)spec.IndexCount,
                out Vector3 submeshMin, out Vector3 submeshMax);

            min = Vector3.Min(min, submeshMin);
            max = Vector3.Max(max, submeshMax);

            records[i] = new SmodelSubmesh(
                spec.IndexStart, spec.IndexCount, names.Offset(spec.MaterialPath), submeshMin, submeshMax);
        }

        byte[] vertexLayout = BuildVertexLayout();
        byte[] vertexBuffer = MemoryMarshal.AsBytes(vertices).ToArray();
        byte[] indexBuffer = BuildIndexBuffer(indices, index32);
        byte[] submeshTable = MemoryMarshal.AsBytes<SmodelSubmesh>(records).ToArray();
        byte[] nameBlob = names.ToArray();

        var sections = new List<(uint FourCc, byte[] Payload)>(5)
        {
            (SmodelFormat.VertexLayoutSection, vertexLayout),
            (SmodelFormat.VertexBufferSection, vertexBuffer),
            (SmodelFormat.IndexBufferSection, indexBuffer),
            (SmodelFormat.SubmeshSection, submeshTable),
        };

        // Only when something names one. The reader treats NAME as optional and a
        // zero-length section would be a claim about a region that carries
        // nothing, which is a thing to reason about later for no benefit now.
        if (nameBlob.Length > 0) sections.Add((SmodelFormat.NameSection, nameBlob));

        return Assemble(sections, index32, min, max);
    }

    // Lays the sections out on their alignment and writes the whole file. The
    // layout pass and the write pass are one loop rather than two, because two
    // passes over the same list that must agree on every size INCLUDING padding
    // is exactly the shape that produces a one-byte disagreement and an
    // arbitrary symptom three sections later.
    private static byte[] Assemble(
        List<(uint FourCc, byte[] Payload)> sections, bool index32, Vector3 min, Vector3 max)
    {
        long tableEnd = SmodelFormat.SectionTableOffset + ((long)sections.Count * SmodelFormat.SectionSize);
        var offsets = new long[sections.Count];

        long at = tableEnd;
        for (int i = 0; i < sections.Count; i++)
        {
            at = SmodelFormat.AlignUp(at, SmodelFormat.PayloadAlignment);
            offsets[i] = at;
            at += sections[i].Payload.Length;
        }

        // The file ENDS at the last payload rather than on the alignment. Every
        // section START obeys the rule, which is what the in-place casts need; a
        // padded tail would be bytes no reader ever looks at, and it would make
        // this writer's output differ from a byte-for-byte transcription of the
        // format specification for no reason anybody could see.
        var file = new byte[at];

        BinaryPrimitives.WriteUInt32LittleEndian(file, SmodelFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x04), EngineInfo.ModelFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(
            file.AsSpan(0x06), (ushort)(index32 ? SmodelFlags.Index32 : SmodelFlags.None));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x08), EngineInfo.GeometryFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), (uint)sections.Count);
        WriteVector3(file.AsSpan(0x10), min);
        WriteVector3(file.AsSpan(0x1C), max);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x28), SmodelStandardLayout.LayoutId);

        // 0x2C to 0x40 stays zero: it is the header's reserved tail, and the
        // reader refuses any file whose format version this build does not know
        // long before it could look at those bytes.

        for (int i = 0; i < sections.Count; i++)
        {
            Span<byte> record = file.AsSpan(
                SmodelFormat.SectionTableOffset + (i * SmodelFormat.SectionSize), SmodelFormat.SectionSize);

            BinaryPrimitives.WriteUInt32LittleEndian(record, sections[i].FourCc);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], 0);
            BinaryPrimitives.WriteUInt64LittleEndian(record[8..], (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(record[16..], (ulong)sections[i].Payload.Length);

            sections[i].Payload.CopyTo(file.AsSpan((int)offsets[i]));
        }

        return file;
    }

    private static byte[] BuildVertexLayout()
    {
        ReadOnlySpan<SmodelVertexAttribute> attributes = SmodelStandardLayout.Attributes;

        var section = new byte[
            SmodelFormat.VertexLayoutPreambleSize + (attributes.Length * SmodelFormat.VertexAttributeSize)];

        BinaryPrimitives.WriteUInt32LittleEndian(section, (uint)attributes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(4), SmodelStandardLayout.StrideFloats);
        MemoryMarshal.AsBytes(attributes).CopyTo(section.AsSpan(SmodelFormat.VertexLayoutPreambleSize));
        return section;
    }

    private static byte[] BuildIndexBuffer(ReadOnlySpan<uint> indices, bool index32)
    {
        if (index32) return MemoryMarshal.AsBytes(indices).ToArray();

        var narrowed = new byte[indices.Length * sizeof(ushort)];
        for (int i = 0; i < indices.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(narrowed.AsSpan(i * sizeof(ushort)), (ushort)indices[i]);

        return narrowed;
    }

    private static void Bounds(
        ReadOnlySpan<float> vertices,
        ReadOnlySpan<uint> indices,
        int stride,
        int start,
        int count,
        out Vector3 min,
        out Vector3 max)
    {
        min = new Vector3(float.PositiveInfinity);
        max = new Vector3(float.NegativeInfinity);

        for (int i = 0; i < count; i++)
        {
            int at = (int)indices[start + i] * stride;
            var position = new Vector3(vertices[at], vertices[at + 1], vertices[at + 2]);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
    }

    private static void WriteVector3(Span<byte> destination, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination, value.X);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..], value.Z);
    }

    // The NAME section: u16 length plus UTF-8 per record, mirroring the pack's
    // own name table. One record per DISTINCT string, so two submeshes wearing
    // one material share it - which is the whole reason a material reference is a
    // path rather than an inline description.
    private sealed class NameBlob
    {
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);

        public uint Offset(string? name)
        {
            if (string.IsNullOrEmpty(name)) return SmodelFormat.NameOffsetAbsent;
            if (_offsets.TryGetValue(name, out uint existing)) return existing;

            byte[] utf8 = Encoding.UTF8.GetBytes(name);
            if (utf8.Length > ushort.MaxValue)
            {
                throw new ArgumentException(
                    $"A name record is length-prefixed with a u16, and '{name}' is {utf8.Length} bytes.",
                    nameof(name));
            }

            var offset = (uint)_bytes.Count;

            // Ordinal, and the dictionary is keyed ordinally too: two paths that
            // differ only in case are two assets, because asset identity is the
            // normalized path and nothing upstream folds case. Deduplicating them
            // here would silently give one submesh the other's material.
            _offsets[name] = offset;

            _bytes.Add((byte)(utf8.Length & 0xFF));
            _bytes.Add((byte)(utf8.Length >> 8));
            _bytes.AddRange(utf8);
            return offset;
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
