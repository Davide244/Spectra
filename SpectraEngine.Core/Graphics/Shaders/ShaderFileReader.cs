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
}
