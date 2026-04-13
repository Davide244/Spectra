namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// One entry in the pipeline table of a .specshadecomp file.
/// Points to a blob containing compiled shader data for a specific backend.
/// </summary>
public readonly struct ShaderPipelineEntry
{
    /// <summary>Which graphics backend this entry targets.</summary>
    public GraphicsBackend Backend { get; }

    /// <summary>Data format of the blob (source text, SPIR-V, DXIL).</summary>
    public ShaderDataFormat Format { get; }

    /// <summary>Which shader stages are present in this blob.</summary>
    public ShaderStageFlags Stages { get; }

    /// <summary>Byte offset from the start of the data section to this blob.</summary>
    public uint DataOffset { get; }

    /// <summary>Total byte size of this blob.</summary>
    public uint DataSize { get; }

    public ShaderPipelineEntry(GraphicsBackend backend, ShaderDataFormat format, ShaderStageFlags stages, uint dataOffset, uint dataSize)
    {
        Backend = backend;
        Format = format;
        Stages = stages;
        DataOffset = dataOffset;
        DataSize = dataSize;
    }
}
