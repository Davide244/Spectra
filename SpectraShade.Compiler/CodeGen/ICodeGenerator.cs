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
