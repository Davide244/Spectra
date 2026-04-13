using System;
using System.IO;

namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// Writes a <see cref="CompiledShaderFile"/> to the .specshadecomp binary format.
///
/// File layout:
///   [Header]
///     4 bytes  - Magic "SSCO"
///     2 bytes  - Format version (uint16)
///     1 byte   - Stage flags (ShaderStageFlags)
///     1 byte   - Pipeline count (uint8)
///   [Pipeline Entry Table] (repeated per pipeline)
///     1 byte   - Backend (GraphicsBackend)
///     1 byte   - Data format (ShaderDataFormat)
///     1 byte   - Stage flags for this entry
///     1 byte   - Reserved/padding
///     4 bytes  - Data offset from start of data section (uint32)
///     4 bytes  - Data size in bytes (uint32)
///   [Data Section]
///     Per pipeline blob:
///       For each stage present (ordered: vertex, fragment, geometry, compute):
///         4 bytes  - Stage data length (uint32)
///         N bytes  - Stage data
/// </summary>
public static class ShaderFileWriter
{
    private const int HeaderSize = 8;
    private const int EntrySize = 12;

    public static void Write(Stream stream, CompiledShaderFile file)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Header
        writer.Write(CompiledShaderFile.MagicBytes);
        writer.Write(file.FormatVersion);
        writer.Write((byte)file.Stages);
        writer.Write((byte)file.Pipelines.Count);

        // Build blobs first so we know offsets and sizes
        var blobs = new byte[file.Pipelines.Count][];
        for (int i = 0; i < file.Pipelines.Count; i++)
        {
            blobs[i] = SerializePipelineBlob(file.Pipelines[i]);
        }

        // Pipeline entry table
        uint dataOffset = 0;
        for (int i = 0; i < file.Pipelines.Count; i++)
        {
            var pipeline = file.Pipelines[i];
            writer.Write((byte)pipeline.Backend);
            writer.Write((byte)pipeline.Format);
            writer.Write((byte)pipeline.Stages);
            writer.Write((byte)0); // reserved
            writer.Write(dataOffset);
            writer.Write((uint)blobs[i].Length);
            dataOffset += (uint)blobs[i].Length;
        }

        // Data section
        for (int i = 0; i < blobs.Length; i++)
        {
            writer.Write(blobs[i]);
        }
    }

    public static void WriteToFile(string path, CompiledShaderFile file)
    {
        using var stream = File.Create(path);
        Write(stream, file);
    }

    private static byte[] SerializePipelineBlob(PipelineBlob blob)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        WriteStageIfPresent(writer, blob.Stages, ShaderStageFlags.Vertex, blob.VertexData);
        WriteStageIfPresent(writer, blob.Stages, ShaderStageFlags.Fragment, blob.FragmentData);
        WriteStageIfPresent(writer, blob.Stages, ShaderStageFlags.Geometry, blob.GeometryData);
        WriteStageIfPresent(writer, blob.Stages, ShaderStageFlags.Compute, blob.ComputeData);

        return ms.ToArray();
    }

    private static void WriteStageIfPresent(BinaryWriter writer, ShaderStageFlags stages, ShaderStageFlags flag, byte[]? data)
    {
        if (!stages.HasFlag(flag))
            return;

        if (data is null)
            throw new InvalidOperationException($"Stage {flag} is declared but has no data");

        writer.Write((uint)data.Length);
        writer.Write(data);
    }
}
