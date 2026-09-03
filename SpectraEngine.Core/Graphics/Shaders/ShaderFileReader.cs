using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// Reads a .specshadecomp file. Can either load the full file or
/// extract only the blob for a specific backend.
/// </summary>
/// <remarks>
/// <para><b>There are two parsers here, over one layout.</b> A stream seeks; a
/// mounted pack hands out a span into a memory-mapped view that is already
/// there, and wrapping one in a <c>MemoryStream</c> to read it copies the whole
/// file to reach one blob, which is the copy the container exists to avoid.
/// Both take every offset from <see cref="ShaderFileLayout"/>, so the layout is
/// stated once, and <c>ShaderFileCodecTests</c> asserts the two agree byte for
/// byte on a multi-pipeline file - a divergence between them is a stage read
/// out of the middle of somebody else's bytes rather than an exception.</para>
/// <para><b>The span parser refuses a truncated file where the stream parser
/// returns what it found.</b> <c>BinaryReader.ReadBytes</c> short-reads at the
/// end of a stream and hands back a shorter array; the span parser cannot,
/// because it is looking at a fixed extent, so it throws. Both answers are
/// correct on a valid file, which is what the agreement test compares.</para>
/// </remarks>
public static class ShaderFileReader
{
    /// <summary>
    /// Reads the full compiled shader file from a stream.
    /// </summary>
    public static CompiledShaderFile Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        // Header
        Span<byte> magic = stackalloc byte[4];
        if (reader.Read(magic) != 4 || !magic.SequenceEqual(CompiledShaderFile.MagicBytes))
            throw new InvalidDataException("Not a valid .specshadecomp file (bad magic bytes)");

        ushort formatVersion = RequireSupportedVersion(reader.ReadUInt16());
        var stages = (ShaderStageFlags)reader.ReadByte();
        byte pipelineCount = reader.ReadByte();

        // Pipeline entry table
        var entries = new ShaderPipelineEntry[pipelineCount];
        for (int i = 0; i < pipelineCount; i++)
        {
            var backend = (GraphicsBackend)reader.ReadByte();
            var format = (ShaderDataFormat)reader.ReadByte();
            var entryStages = (ShaderStageFlags)reader.ReadByte();
            reader.ReadByte(); // reserved
            uint dataOffset = reader.ReadUInt32();
            uint dataSize = reader.ReadUInt32();
            entries[i] = new ShaderPipelineEntry(backend, format, entryStages, dataOffset, dataSize);
        }

        long dataSectionStart = ShaderFileLayout.DataSectionStart(pipelineCount);

        var pipelines = new List<PipelineBlob>(pipelineCount);
        for (int i = 0; i < pipelineCount; i++)
        {
            stream.Position = dataSectionStart + entries[i].DataOffset;
            var blob = DeserializePipelineBlob(reader, entries[i]);
            pipelines.Add(blob);
        }

        return new CompiledShaderFile
        {
            FormatVersion = formatVersion,
            Stages = stages,
            Pipelines = pipelines,
        };
    }

    /// <summary>
    /// Reads only the pipeline blob for a specific backend, skipping all others.
    /// Returns null if the file doesn't contain data for that backend.
    /// </summary>
    public static PipelineBlob? ReadPipeline(Stream stream, GraphicsBackend backend)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        // Header
        Span<byte> magic = stackalloc byte[4];
        if (reader.Read(magic) != 4 || !magic.SequenceEqual(CompiledShaderFile.MagicBytes))
            throw new InvalidDataException("Not a valid .specshadecomp file (bad magic bytes)");

        RequireSupportedVersion(reader.ReadUInt16());
        reader.ReadByte();   // stages
        byte pipelineCount = reader.ReadByte();

        // Scan entry table for the requested backend
        ShaderPipelineEntry? target = null;
        for (int i = 0; i < pipelineCount; i++)
        {
            var entryBackend = (GraphicsBackend)reader.ReadByte();
            var format = (ShaderDataFormat)reader.ReadByte();
            var entryStages = (ShaderStageFlags)reader.ReadByte();
            reader.ReadByte(); // reserved
            uint dataOffset = reader.ReadUInt32();
            uint dataSize = reader.ReadUInt32();

            if (entryBackend == backend)
            {
                target = new ShaderPipelineEntry(entryBackend, format, entryStages, dataOffset, dataSize);
                break;
            }
        }

        if (target is null)
            return null;

        // The scan above stops at the matching entry, so the stream is parked
        // mid-table and only the layout can say where the data section begins.
        stream.Position = ShaderFileLayout.DataSectionStart(pipelineCount) + target.Value.DataOffset;

        return DeserializePipelineBlob(reader, target.Value);
    }

    /// <summary>
    /// Reads only the pipeline blob for <paramref name="backend"/> out of a
    /// whole .specshadecomp file that is already in memory. Returns null when
    /// the file carries no data for that backend.
    /// </summary>
    /// <remarks>
    /// The span twin of <see cref="ReadPipeline(Stream, GraphicsBackend)"/>, and
    /// the one a mounted pack uses: a <c>ContentBlob</c> over a mapped view is a
    /// span already, so this reads the two blobs it needs (the entry table and
    /// one pipeline) straight out of the file's own bytes. Only the stage
    /// payloads and the input names are copied, because
    /// <see cref="PipelineBlob"/> outlives the mapping.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// The bytes are not a .specshadecomp file this engine reads, or are
    /// truncated.
    /// </exception>
    public static PipelineBlob? ReadPipeline(ReadOnlySpan<byte> file, GraphicsBackend backend)
    {
        var cursor = new SpanCursor(file);
        int pipelineCount = ReadFileHeader(ref cursor);

        ShaderPipelineEntry? target = null;
        for (int i = 0; i < pipelineCount; i++)
        {
            ShaderPipelineEntry entry = ReadEntry(ref cursor);
            if (entry.Backend != backend) continue;

            target = entry;
            break;
        }

        if (target is null)
            return null;

        // The scan above stops at the matching entry, so the cursor is parked
        // mid-table and only the layout can say where the data section begins.
        cursor.Seek(ShaderFileLayout.DataSectionStart(pipelineCount) + target.Value.DataOffset);

        return DeserializePipelineBlob(ref cursor, target.Value);
    }

    /// <summary>
    /// Every backend a .specshadecomp file carries a blob for, in table order,
    /// without deserialising any of them.
    /// </summary>
    /// <remarks>
    /// The question a cooked-pack verify asks: which backends did this shader
    /// actually come out for. Reading the entry table alone answers it without
    /// paying for the stage payloads, and it shares the header parse above, so
    /// a verify cannot disagree with a load about what is in the file.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// The bytes are not a .specshadecomp file this engine reads, or are
    /// truncated.
    /// </exception>
    public static GraphicsBackend[] ReadBackends(ReadOnlySpan<byte> file)
    {
        var cursor = new SpanCursor(file);
        int pipelineCount = ReadFileHeader(ref cursor);

        var backends = new GraphicsBackend[pipelineCount];
        for (int i = 0; i < pipelineCount; i++)
            backends[i] = ReadEntry(ref cursor).Backend;

        return backends;
    }

    public static CompiledShaderFile ReadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static PipelineBlob? ReadPipelineFromFile(string path, GraphicsBackend backend)
    {
        using var stream = File.OpenRead(path);
        return ReadPipeline(stream, backend);
    }

    /// <summary>
    /// Refuses a file this engine's format does not describe.
    /// </summary>
    /// <remarks>
    /// A compiled shader is a build output, so it versions the way every cooked
    /// artifact does: an exact match, and a message that says recook. There is
    /// nothing to carry forward and nothing to degrade to, because the bytes
    /// past the header only mean anything under the version that wrote them.
    /// </remarks>
    private static ushort RequireSupportedVersion(ushort formatVersion)
    {
        if (formatVersion != EngineInfo.ShaderFormatVersion)
            throw new InvalidDataException(
                $"Compiled shader format version {formatVersion} cannot be read by this engine, "
                + $"which reads version {EngineInfo.ShaderFormatVersion}. Recompile (recook) the shader.");

        return formatVersion;
    }

    private static PipelineBlob DeserializePipelineBlob(BinaryReader reader, ShaderPipelineEntry entry)
    {
        byte[]? vertexData = null;
        byte[]? fragmentData = null;
        byte[]? geometryData = null;
        byte[]? computeData = null;

        if (entry.Stages.HasFlag(ShaderStageFlags.Vertex))
            vertexData = ReadStageData(reader);

        if (entry.Stages.HasFlag(ShaderStageFlags.Fragment))
            fragmentData = ReadStageData(reader);

        if (entry.Stages.HasFlag(ShaderStageFlags.Geometry))
            geometryData = ReadStageData(reader);

        if (entry.Stages.HasFlag(ShaderStageFlags.Compute))
            computeData = ReadStageData(reader);

        VertexInputElement[] vertexInputs = ReadVertexInputs(reader, entry.DataSize);

        byte[]? instancedVertexData = null;
        VertexInputElement[] instancedVertexInputs = [];
        if (reader.ReadByte() != 0)
        {
            instancedVertexData = ReadStageData(reader);
            instancedVertexInputs = ReadVertexInputs(reader, entry.DataSize);
        }

        return new PipelineBlob
        {
            Backend = entry.Backend,
            Format = entry.Format,
            Stages = entry.Stages,
            VertexData = vertexData,
            FragmentData = fragmentData,
            GeometryData = geometryData,
            ComputeData = computeData,
            VertexInputs = vertexInputs,
            InstancedVertexData = instancedVertexData,
            InstancedVertexInputs = instancedVertexInputs,
        };
    }

    private static byte[] ReadStageData(BinaryReader reader)
    {
        uint length = reader.ReadUInt32();
        return reader.ReadBytes((int)length);
    }

    private static VertexInputElement[] ReadVertexInputs(BinaryReader reader, uint blobSize)
    {
        uint count = reader.ReadUInt32();

        // The blob's declared size bounds the table: every element costs at
        // least a fixed record, so a larger count is a corrupt file rather than
        // an allocation to attempt.
        if (count > blobSize / ShaderFileLayout.VertexInputRecordSize)
            throw new InvalidDataException(
                $"Vertex input table declares {count} elements, more than the {blobSize}-byte pipeline blob can hold");

        var inputs = new VertexInputElement[count];
        Span<byte> record = stackalloc byte[ShaderFileLayout.VertexInputRecordSize];

        for (int i = 0; i < inputs.Length; i++)
        {
            if (reader.Read(record) != record.Length)
                throw new InvalidDataException("Truncated vertex input record");

            uint location = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint locationSpan = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            uint componentCount = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            var rate = (VertexInputRate)record[12];
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[13..]);

            byte[] nameBytes = reader.ReadBytes(nameLength);
            if (nameBytes.Length != nameLength)
                throw new InvalidDataException("Truncated vertex input name");

            inputs[i] = new VertexInputElement(
                Encoding.UTF8.GetString(nameBytes), location, locationSpan, componentCount, rate);
        }

        return inputs;
    }

    // ---- the span half ---------------------------------------------------

    // Header and version, returning the pipeline count every later offset is
    // measured from. Shared by both span entry points so a backend listing and a
    // blob load cannot disagree about what the file is.
    private static int ReadFileHeader(ref SpanCursor cursor)
    {
        if (!cursor.Take(4).SequenceEqual(CompiledShaderFile.MagicBytes))
            throw new InvalidDataException("Not a valid .specshadecomp file (bad magic bytes)");

        RequireSupportedVersion(cursor.U16());
        cursor.U8();  // stages, restated per entry
        return cursor.U8();
    }

    private static ShaderPipelineEntry ReadEntry(ref SpanCursor cursor)
    {
        var backend = (GraphicsBackend)cursor.U8();
        var format = (ShaderDataFormat)cursor.U8();
        var stages = (ShaderStageFlags)cursor.U8();
        cursor.U8();  // reserved
        uint dataOffset = cursor.U32();
        uint dataSize = cursor.U32();
        return new ShaderPipelineEntry(backend, format, stages, dataOffset, dataSize);
    }

    private static PipelineBlob DeserializePipelineBlob(ref SpanCursor cursor, ShaderPipelineEntry entry)
    {
        byte[]? vertexData = null;
        byte[]? fragmentData = null;
        byte[]? geometryData = null;
        byte[]? computeData = null;

        if (entry.Stages.HasFlag(ShaderStageFlags.Vertex))
            vertexData = ReadStageData(ref cursor);

        if (entry.Stages.HasFlag(ShaderStageFlags.Fragment))
            fragmentData = ReadStageData(ref cursor);

        if (entry.Stages.HasFlag(ShaderStageFlags.Geometry))
            geometryData = ReadStageData(ref cursor);

        if (entry.Stages.HasFlag(ShaderStageFlags.Compute))
            computeData = ReadStageData(ref cursor);

        VertexInputElement[] vertexInputs = ReadVertexInputs(ref cursor, entry.DataSize);

        byte[]? instancedVertexData = null;
        VertexInputElement[] instancedVertexInputs = [];
        if (cursor.U8() != 0)
        {
            instancedVertexData = ReadStageData(ref cursor);
            instancedVertexInputs = ReadVertexInputs(ref cursor, entry.DataSize);
        }

        return new PipelineBlob
        {
            Backend = entry.Backend,
            Format = entry.Format,
            Stages = entry.Stages,
            VertexData = vertexData,
            FragmentData = fragmentData,
            GeometryData = geometryData,
            ComputeData = computeData,
            VertexInputs = vertexInputs,
            InstancedVertexData = instancedVertexData,
            InstancedVertexInputs = instancedVertexInputs,
        };
    }

    private static byte[] ReadStageData(ref SpanCursor cursor) => cursor.Take(cursor.U32()).ToArray();

    private static VertexInputElement[] ReadVertexInputs(ref SpanCursor cursor, uint blobSize)
    {
        uint count = cursor.U32();

        // The blob's declared size bounds the table exactly as it does on the
        // stream path: every element costs at least a fixed record, so a larger
        // count is a corrupt file rather than an allocation to attempt.
        if (count > blobSize / ShaderFileLayout.VertexInputRecordSize)
            throw new InvalidDataException(
                $"Vertex input table declares {count} elements, more than the {blobSize}-byte pipeline blob can hold");

        var inputs = new VertexInputElement[count];
        for (int i = 0; i < inputs.Length; i++)
        {
            ReadOnlySpan<byte> record = cursor.Take(ShaderFileLayout.VertexInputRecordSize);

            uint location = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint locationSpan = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            uint componentCount = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            var rate = (VertexInputRate)record[12];
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[13..]);

            inputs[i] = new VertexInputElement(
                Encoding.UTF8.GetString(cursor.Take(nameLength)),
                location,
                locationSpan,
                componentCount,
                rate);
        }

        return inputs;
    }

    /// <summary>
    /// A read position inside a whole .specshadecomp file, bounds-checked.
    /// </summary>
    /// <remarks>
    /// A <c>ref struct</c> because the span it walks may be a window into a
    /// mapped pack view, which must never be captured on the heap: the mapping
    /// is released when the <c>ContentBlob</c> is disposed, and reading it
    /// afterwards is an access violation with no managed stack rather than an
    /// exception anyone can catch.
    /// </remarks>
    private ref struct SpanCursor
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _at;

        public SpanCursor(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _at = 0;
        }

        public void Seek(long position)
        {
            if (position < 0 || position > _bytes.Length)
                throw new InvalidDataException(
                    $"Compiled shader file seeks to {position}, outside its {_bytes.Length} bytes.");

            _at = (int)position;
        }

        // Takes a uint length so a corrupt 32-bit size cannot wrap into a small
        // positive int: anything past int.MaxValue is larger than any span and
        // fails the bounds test below rather than silently reading a short slice.
        public ReadOnlySpan<byte> Take(uint count) =>
            count > int.MaxValue ? throw Truncated(count) : Take((int)count);

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _bytes.Length - _at < count) throw Truncated((uint)count);

            ReadOnlySpan<byte> slice = _bytes.Slice(_at, count);
            _at += count;
            return slice;
        }

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        private InvalidDataException Truncated(uint count) =>
            new($"Compiled shader file is truncated: {count} bytes wanted at offset {_at} of {_bytes.Length}.");
    }
}
