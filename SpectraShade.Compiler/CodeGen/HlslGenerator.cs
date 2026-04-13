using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.CodeGen;

/// <summary>
/// Generates HLSL source from a SpectraShade AST.
/// Used for D3D11 and D3D12 backends.
///
/// TODO: Implement HLSL emission. Key differences from GLSL:
/// - Semantics instead of layout locations (POSITION, TEXCOORD, SV_Position, SV_Target)
/// - cbuffer instead of uniform
/// - Texture2D + SamplerState instead of sampler2D
/// - float4 instead of vec4, float4x4 instead of mat4
/// - Entry points can be named anything (not forced to "main")
/// - [numthreads(x,y,z)] for compute
/// </summary>
public sealed class HlslGenerator : ICodeGenerator
{
    private readonly GraphicsBackend _backend;

    public GraphicsBackend Backend => _backend;
    public ShaderDataFormat OutputFormat => ShaderDataFormat.SourceText;

    public HlslGenerator(GraphicsBackend backend)
    {
        if (backend is not (GraphicsBackend.D3D11 or GraphicsBackend.D3D12))
            throw new ArgumentException("HLSL generator requires D3D11 or D3D12 backend");
        _backend = backend;
    }

    public PipelineBlob Generate(CompilationUnit unit)
    {
        // TODO: Implement HLSL code generation
        throw new NotImplementedException("HLSL code generation is not yet implemented");
    }
}
