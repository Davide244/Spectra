using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.CodeGen;

/// <summary>
/// Generates backend-specific shader code from a SpectraShade AST.
/// Each backend implements this to emit its own format.
/// </summary>
public interface ICodeGenerator
{
    GraphicsBackend Backend { get; }
    ShaderDataFormat OutputFormat { get; }

    /// <summary>
    /// Generates compiled output for the given shader.
    /// Returns a PipelineBlob containing the per-stage data.
    /// </summary>
    PipelineBlob Generate(CompilationUnit unit);
}

/// <summary>
/// How a compiled shader's instanced variant is attached to its blob.
/// </summary>
/// <remarks>
/// A separate helper rather than a second interface member: the variant is
/// produced by running the SAME generator over a rewritten AST, so no generator
/// needs to know the feature exists.
/// </remarks>
public static class InstancedBlob
{
    /// <summary>
    /// Returns <paramref name="blob"/> carrying the instanced vertex stage from
    /// <paramref name="instancedBlob"/>.
    /// </summary>
    public static PipelineBlob With(PipelineBlob blob, PipelineBlob instancedBlob) => new()
    {
        Backend = blob.Backend,
        Format = blob.Format,
        Stages = blob.Stages,
        VertexData = blob.VertexData,
        FragmentData = blob.FragmentData,
        GeometryData = blob.GeometryData,
        ComputeData = blob.ComputeData,
        VertexInputs = blob.VertexInputs,
        InstancedVertexData = instancedBlob.VertexData,
        InstancedVertexInputs = instancedBlob.VertexInputs,
    };
}
