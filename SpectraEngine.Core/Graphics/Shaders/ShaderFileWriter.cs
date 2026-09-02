using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// Writes a <see cref="CompiledShaderFile"/> to the .specshadecomp binary format.
///
/// Every multi-byte field is little-endian. Region sizes come from
/// <see cref="ShaderFileLayout"/>.
///
/// File layout:
///   [Header] (ShaderFileLayout.HeaderSize)
///     4 bytes  - Magic "SSCO"
///     2 bytes  - Format version (uint16)
///     1 byte   - Stage flags (ShaderStageFlags)
///     1 byte   - Pipeline count (uint8)
///   [Pipeline Entry Table] (ShaderFileLayout.EntrySize, repeated per pipeline)
///     1 byte   - Backend (GraphicsBackend)
///     1 byte   - Data format (ShaderDataFormat)
///     1 byte   - Stage flags for this entry
///     1 byte   - Reserved/padding
///     4 bytes  - Data offset from start of data section (uint32)
///     4 bytes  - Data size in bytes (uint32)
///   [Data Section] (begins at ShaderFileLayout.DataSectionStart)
///     Per pipeline blob:
///       For each stage present (ordered: vertex, fragment, geometry, compute):
///         4 bytes  - Stage data length (uint32)
///         N bytes  - Stage data
///       [Vertex Input Table]  - what the ordinary vertex stage declares
///       1 byte   - Instanced variant present (0 or 1)
///       Present only when that byte is 1:
///         4 bytes  - Instanced vertex stage length (uint32)
///         N bytes  - Instanced vertex stage data
///         [Vertex Input Table] - what the instanced vertex stage declares
///
///   [Vertex Input Table]
///     4 bytes  - Element count (uint32)
///     Per element:
///       4 bytes  - Location (uint32)
///       4 bytes  - Location span (uint32)
///       4 bytes  - Component count (uint32)
///       1 byte   - Rate (VertexInputRate)
///       2 bytes  - Name length in UTF-8 bytes (uint16)
///       N bytes  - Name, UTF-8, no terminator
///
/// The stage flags in the entry are what tell a reader where the stage sections
/// stop and the vertex input table starts, so the tables can only be appended
/// after them.
/// </summary>
public static class ShaderFileWriter
{
    public static void Write(Stream stream, CompiledShaderFile file)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        Span<byte> header = stackalloc byte[ShaderFileLayout.HeaderSize];
        CompiledShaderFile.MagicBytes.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], file.FormatVersion);
        header[6] = (byte)file.Stages;
        header[7] = (byte)file.Pipelines.Count;
        writer.Write(header);

        // Build blobs first so we know offsets and sizes
        var blobs = new byte[file.Pipelines.Count][];
        for (int i = 0; i < file.Pipelines.Count; i++)
        {
            blobs[i] = SerializePipelineBlob(file.Pipelines[i]);
        }

        // Pipeline entry table
        Span<byte> entry = stackalloc byte[ShaderFileLayout.EntrySize];
        uint dataOffset = 0;
        for (int i = 0; i < file.Pipelines.Count; i++)
        {
            var pipeline = file.Pipelines[i];
            entry[0] = (byte)pipeline.Backend;
            entry[1] = (byte)pipeline.Format;
            entry[2] = (byte)pipeline.Stages;
            entry[3] = 0; // reserved
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], dataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)blobs[i].Length);
            writer.Write(entry);
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

        WriteVertexInputs(writer, blob.VertexInputs);

        // The instanced inputs describe the instanced stage, so they ride inside
        // its presence flag. A blob carrying the inputs without the stage is
        // refused rather than written, because writing it drops them and a
        // dropped vertex layout is a wrong input layout rather than an error.
        if (blob.InstancedVertexData is null)
        {
            if (blob.InstancedVertexInputs.Count > 0)
                throw new InvalidOperationException(
                    "Instanced vertex inputs are declared but there is no instanced vertex stage to carry them");

            writer.Write((byte)0);
        }
        else
        {
            writer.Write((byte)1);
            writer.Write((uint)blob.InstancedVertexData.Length);
            writer.Write(blob.InstancedVertexData);
            WriteVertexInputs(writer, blob.InstancedVertexInputs);
        }

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

    private static void WriteVertexInputs(BinaryWriter writer, IReadOnlyList<VertexInputElement> inputs)
    {
        writer.Write((uint)inputs.Count);

        Span<byte> record = stackalloc byte[ShaderFileLayout.VertexInputRecordSize];
        for (int i = 0; i < inputs.Count; i++)
        {
            VertexInputElement element = inputs[i];

            int nameLength = Encoding.UTF8.GetByteCount(element.Name);
            if (nameLength > ushort.MaxValue)
                throw new InvalidOperationException($"Vertex input name '{element.Name}' is too long to encode");

            BinaryPrimitives.WriteUInt32LittleEndian(record, element.Location);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], element.LocationSpan);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], element.ComponentCount);
            record[12] = (byte)element.Rate;
            BinaryPrimitives.WriteUInt16LittleEndian(record[13..], (ushort)nameLength);

            writer.Write(record);
            writer.Write(Encoding.UTF8.GetBytes(element.Name));
        }
    }
}
