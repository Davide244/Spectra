using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.CodeGen;

/// <summary>
/// Generates SPIR-V bytecode from a SpectraShade AST.
/// Used for the Vulkan backend.
///
/// TODO: Implement SPIR-V emission. Options:
/// 1. Direct SPIR-V bytecode generation (hard but no external deps)
/// 2. Generate GLSL via GlslGenerator, then invoke glslangValidator/shaderc
///    at compile time to produce SPIR-V (practical, widely used)
/// 3. Use SPIRV-Cross for reflection after generation
///
/// Option 2 is recommended for initial implementation.
/// </summary>
public sealed class SpirVGenerator : ICodeGenerator
{
    public GraphicsBackend Backend => GraphicsBackend.Vulkan;
    public ShaderDataFormat OutputFormat => ShaderDataFormat.SpirV;

    public PipelineBlob Generate(CompilationUnit unit)
    {
        // TODO: Implement SPIR-V generation
        throw new NotImplementedException("SPIR-V code generation is not yet implemented");
    }
}
