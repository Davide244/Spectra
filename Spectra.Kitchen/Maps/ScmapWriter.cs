using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SpectraEngine.Core;
using SpectraEngine.Core.Maps.Compiled;

namespace Spectra.Kitchen.Maps;

/// <summary>
/// Emits one section's body into the file, having already declared how long it
/// will be.
/// </summary>
/// <param name="stream">Where the body goes. Write exactly the declared number of bytes.</param>
public delegate void ScmapSectionBodyWriter(Stream stream);

/// <summary>
/// Writes a <c>.scmap</c> container: the header, the section table and the section
/// bodies, with nothing above the container in it.
/// </summary>
/// <remarks>
/// <para><b>Writers live in this assembly and only here</b>, so no shipped game
/// binary carries map-baking code. The engine's side of the format is the format
/// types and the reader, in <c>SpectraEngine.Core.Maps.Compiled</c>, and they
/// share every constant: a size or an offset spelled twice is the failure this
/// whole family of formats already records.</para>
/// <para><b>A section declares its length and then writes it, and the writer
/// checks both statements.</b> That shape is not incidental: a chunk mesh blob is
/// the biggest thing in a compiled map and wants to be streamed out of the
/// compiled artifacts rather than materialised whole in a byte array first, so the
/// size has to be knowable before the bytes exist. Two statements can disagree, so
/// every section boundary is asserted twice, once that the stream is where the
/// layout said it would be and once that the body wrote what it declared, and both
/// refusals NAME THE SECTION. A file whose sections landed one byte off parses
/// into different numbers than it was written from, with an arbitrary symptom
/// somewhere else entirely.</para>
/// <para><b>Padding is written, never seeked over.</b> A seek past the end of a
/// stream leaves the gap holding whatever the filesystem gives back, which on most
/// filesystems is zeros and on none of them is a promise. Byte identity between
/// two cooks is the property this format is graded on, and an unwritten gap is how
/// it fails in a way that is very hard to bisect.</para>
/// <para><b>Two four-character codes are refused by name.</b> <c>RGNI</c> and
/// <c>BMDL</c> are reserved with no producer, and a cook that emitted either would
/// spend a code the format has already promised to something else. The refusal is
/// here rather than in a review comment because the reader treats both as unknown
/// sections and steps over them, so nothing downstream would ever notice.</para>
/// </remarks>
public sealed class ScmapWriter
{
    private readonly List<PendingSection> _sections = [];
    private readonly ScmapFlags _flags;
    private readonly UInt128 _sourceMapDigest;
    private readonly uint _mapFormatVersion;

    /// <summary>Creates a writer.</summary>
    /// <param name="flags">What optional content the cook put in the file.</param>
    /// <param name="sourceMapDigest">
    /// <c>XxHash128</c> of the source bundle's canonical enumeration. See
    /// <see cref="MapBundleDigest"/>.
    /// </param>
    /// <param name="mapFormatVersion">
    /// The authored map grammar the bake read. Informational: a load never gates
    /// on it, because the authored map is not present at runtime.
    /// </param>
    public ScmapWriter(ScmapFlags flags, UInt128 sourceMapDigest, uint mapFormatVersion)
    {
        _flags = flags;
        _sourceMapDigest = sourceMapDigest;
        _mapFormatVersion = mapFormatVersion;
    }

    /// <summary>How many sections have been added.</summary>
    public int Count => _sections.Count;

    /// <summary>Adds a section whose body is already in hand.</summary>
    /// <remarks>
    /// Expressed through the declaring overload rather than beside it, so there is
    /// one write path and the assert covers both ways in.
    /// </remarks>
    public void AddSection(uint kind, ReadOnlySpan<byte> body)
    {
        byte[] copy = body.ToArray();
        AddSection(kind, copy.Length, stream => stream.Write(copy));
    }

    /// <summary>
    /// Adds a section that declares its length now and writes its bytes later.
    /// </summary>
    /// <exception cref="ArgumentException">The code is reserved, or already added.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The declared size is negative.</exception>
    public void AddSection(uint kind, long bodySize, ScmapSectionBodyWriter write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegative(bodySize);

        if (kind == ScmapFormat.RegionIndexSection || kind == ScmapFormat.BrushModelSection)
        {
            throw new ArgumentException(
                $"Section '{ScmapFormat.DescribeFourCc(kind)}' is reserved and has no producer: " +
                "RGNI belongs to a streaming design nothing builds, and BMDL to a fused brush model whose " +
                "mechanism was overturned. Emitting either spends a code the format has promised elsewhere, " +
                "and a reader would step over it in silence.", nameof(kind));
        }

        for (int i = 0; i < _sections.Count; i++)
        {
            if (_sections[i].Kind != kind) continue;

            throw new ArgumentException(
                $"Section '{ScmapFormat.DescribeFourCc(kind)}' was added twice.", nameof(kind));
        }

        _sections.Add(new PendingSection(kind, bodySize, write));
    }

    /// <summary>Writes the file to <paramref name="path"/>.</summary>
    public void WriteToFile(string path)
    {
        using FileStream stream = File.Create(path);
        Write(stream);
    }

    /// <summary>
    /// Writes the file. Leaves the writer unchanged, so a second call produces
    /// byte-identical output.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The machine is big-endian.</exception>
    /// <exception cref="InvalidOperationException">
    /// A section did not land where the layout put it, or wrote a different number
    /// of bytes than it declared.
    /// </exception>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ScmapFormat.RequireLittleEndian();

        var sizes = new ScmapSectionSize[_sections.Count];
        for (int i = 0; i < _sections.Count; i++)
        {
            sizes[i] = new ScmapSectionSize(_sections[i].Kind, _sections[i].BodySize);
        }

        ScmapLayout layout = ScmapLayout.Compute(sizes);

        var header = new ScmapHeader(
            EngineInfo.CompiledMapFormatVersion,
            _flags,
            (uint)_sections.Count,
            _sourceMapDigest,
            EngineInfo.GeometryFormatVersion,
            _mapFormatVersion,
            ScmapFormat.StandardVertexLayoutId,
            EngineVersionWord,
            (ulong)layout.TotalSize);

        var region = new RegionStream(stream);

        Span<byte> headerBytes = stackalloc byte[ScmapFormat.HeaderSize];
        MemoryMarshal.Write(headerBytes, in header);
        region.Write(headerBytes);

        Span<byte> sectionBytes = stackalloc byte[ScmapFormat.SectionSize];
        for (int i = 0; i < _sections.Count; i++)
        {
            var record = new ScmapSection(
                layout.KindAt(i),
                (ulong)layout.OffsetAt(i),
                (ulong)layout.BodySizeAt(i));

            MemoryMarshal.Write(sectionBytes, in record);
            region.Write(sectionBytes);
        }

        // The table is a whole number of 32-byte records after a 64-byte header,
        // so this gap is always zero today. It is written anyway, because the
        // layout is what decides where the first section starts and a writer that
        // assumed the two agreed would be the second expression of that
        // arithmetic.
        long firstBody = _sections.Count > 0 ? layout.OffsetAt(0) : layout.TotalSize;
        region.WriteZeros(firstBody - region.Position);

        for (int i = 0; i < _sections.Count; i++)
        {
            PendingSection section = _sections[i];

            if (region.Position != layout.OffsetAt(i))
            {
                throw new InvalidOperationException(
                    $"Compiled map layout disagrees with what was written: section " +
                    $"'{ScmapFormat.DescribeFourCc(section.Kind)}' was placed at byte {layout.OffsetAt(i)} " +
                    $"and the writer is at byte {region.Position}.");
            }

            long start = region.Position;
            section.Write(region);
            long written = region.Position - start;

            if (written != section.BodySize)
            {
                throw new InvalidOperationException(
                    $"Section '{ScmapFormat.DescribeFourCc(section.Kind)}' declared {section.BodySize} bytes " +
                    $"and wrote {written}. The layout was computed from the declaration, so every later " +
                    "section is now placed somewhere the table does not say.");
            }

            region.WriteZeros(layout.PaddedSizeAt(i) - written);
        }

        if (region.Position != layout.TotalSize)
        {
            throw new InvalidOperationException(
                $"Compiled map layout disagrees with what was written: the header declares " +
                $"{layout.TotalSize} bytes and the writer ended at {region.Position}.");
        }
    }

    private static uint EngineVersionWord =>
        ((uint)EngineInfo.MajorVersion << 20) | ((uint)EngineInfo.MinorVersion << 10) | EngineInfo.RevisionVersion;

    private readonly record struct PendingSection(uint Kind, long BodySize, ScmapSectionBodyWriter Write);

    /// <summary>
    /// The stream a section body writes into: a forwarding wrapper that counts.
    /// </summary>
    /// <remarks>
    /// A wrapper rather than <c>stream.Position</c>, because the destination is
    /// not required to be seekable and a cook that worked against a file and threw
    /// against a pipe would be a difference nobody would find until CI. Counting
    /// here is also what lets the writer measure a body a producer wrote directly,
    /// which is the whole point of the declaring overload.
    /// </remarks>
    private sealed class RegionStream(Stream inner) : Stream
    {
        private static readonly byte[] Zeros = new byte[ScmapFormat.PayloadAlignment];

        private long _position;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public void WriteZeros(long count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            while (count > 0)
            {
                int chunk = (int)Math.Min(count, Zeros.Length);
                Write(Zeros.AsSpan(0, chunk));
                count -= chunk;
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _position += buffer.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void WriteByte(byte value)
        {
            Span<byte> one = [value];
            Write(one);
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
