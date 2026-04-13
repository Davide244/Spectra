using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// In-memory representation of a .specshadecomp file.
/// Contains compiled shader data for one or more graphics backends.
/// </summary>
public sealed class CompiledShaderFile
{
    /// <summary>Magic bytes: "SSCO" (SpectraShade Compiled Object).</summary>
    public static ReadOnlySpan<byte> MagicBytes => "SSCO"u8;

    /// <summary>Format version of this file. Must match EngineInfo.ShaderFormatVersion to be loadable.</summary>
    public ushort FormatVersion { get; init; }

    /// <summary>Shader stages present across all pipeline entries.</summary>
    public ShaderStageFlags Stages { get; init; }

    /// <summary>Per-backend pipeline entries with their compiled data.</summary>
    public required IReadOnlyList<PipelineBlob> Pipelines { get; init; }

    /// <summary>
    /// Finds the compiled data for a specific backend.
    /// Returns null if this file wasn't compiled for that backend.
    /// </summary>
    public PipelineBlob? GetPipeline(GraphicsBackend backend)
    {
        for (int i = 0; i < Pipelines.Count; i++)
        {
            if (Pipelines[i].Backend == backend)
                return Pipelines[i];
        }
        return null;
    }
}

/// <summary>
/// Compiled shader data for a single graphics backend.
/// </summary>
public sealed class PipelineBlob
{
    public required GraphicsBackend Backend { get; init; }
    public required ShaderDataFormat Format { get; init; }
    public required ShaderStageFlags Stages { get; init; }

    /// <summary>Vertex stage data. Null if this stage wasn't compiled.</summary>
    public byte[]? VertexData { get; init; }

    /// <summary>Fragment/pixel stage data. Null if this stage wasn't compiled.</summary>
    public byte[]? FragmentData { get; init; }

    /// <summary>Geometry stage data. Null if this stage wasn't compiled.</summary>
    public byte[]? GeometryData { get; init; }

    /// <summary>Compute stage data. Null if this stage wasn't compiled.</summary>
    public byte[]? ComputeData { get; init; }
}
