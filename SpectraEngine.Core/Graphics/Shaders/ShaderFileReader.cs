using System;
using System.Collections.Generic;
using System.IO;

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
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Header
        Span<byte> magic = stackalloc byte[4];
        if (reader.Read(magic) != 4 || !magic.SequenceEqual(CompiledShaderFile.MagicBytes))
            throw new InvalidDataException("Not a valid .specshadecomp file (bad magic bytes)");

        ushort formatVersion = reader.ReadUInt16();
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

        // Data section starts here
        long dataSectionStart = stream.Position;

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
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Header
        Span<byte> magic = stackalloc byte[4];
        if (reader.Read(magic) != 4 || !magic.SequenceEqual(CompiledShaderFile.MagicBytes))
            throw new InvalidDataException("Not a valid .specshadecomp file (bad magic bytes)");

        reader.ReadUInt16(); // format version
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

        // Jump to the data section start (after full entry table)
        long dataSectionStart = 8 + (pipelineCount * 12L);
        stream.Position = dataSectionStart + target.Value.DataOffset;

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

        return new PipelineBlob
        {
            Backend = entry.Backend,
            Format = entry.Format,
            Stages = entry.Stages,
            VertexData = vertexData,
            FragmentData = fragmentData,
            GeometryData = geometryData,
            ComputeData = computeData,
        };
    }

    private static byte[] ReadStageData(BinaryReader reader)
    {
        uint length = reader.ReadUInt32();
        return reader.ReadBytes((int)length);
    }
}
