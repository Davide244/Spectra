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

    /// <summary>
    /// The vertex inputs the shader declares, in the order its input struct
    /// declares them. Empty for a shader with no vertex stage.
    /// </summary>
    /// <remarks>
    /// <b>Reported rather than agreed.</b> Before this existed, the only record
    /// of a shader's vertex layout was a comment in the HLSL generator and a
    /// matching comment in <c>D3D11Mesh.CreateInputLayout</c>. That held while
    /// the engine had exactly one layout; per-instance inputs end it, because
    /// the rate and the multi-location span of a matrix are facts about the
    /// shader that no mesh knows. See <see cref="VertexInputElement"/>.
    /// <para>
    /// It is identical across backends, since it describes the source rather
    /// than the target, and is carried per blob anyway because the blob is what
    /// a renderer is handed.
    /// </para>
    /// </remarks>
    public IReadOnlyList<VertexInputElement> VertexInputs { get; init; } = [];

    /// <summary>
    /// A second vertex stage for the same shader, with its per-instance uniform
    /// arriving as a vertex input instead. Null unless the source marked a
    /// <c>cbuffer</c> field <c>[PerInstance]</c>.
    /// </summary>
    /// <remarks>
    /// <b>Same fragment stage, same materials, same everything else.</b> Only
    /// the vertex stage differs, so a renderer builds the instanced program from
    /// this plus <see cref="FragmentData"/> and the author never wrote a second
    /// shader. See <c>InstancedVariant</c> for the rewrite.
    /// </remarks>
    public byte[]? InstancedVertexData { get; init; }

    /// <summary>
    /// The vertex inputs <see cref="InstancedVertexData"/> declares, which is
    /// <see cref="VertexInputs"/> plus the per-instance matrix. Empty when there
    /// is no instanced variant.
    /// </summary>
    public IReadOnlyList<VertexInputElement> InstancedVertexInputs { get; init; } = [];
}
