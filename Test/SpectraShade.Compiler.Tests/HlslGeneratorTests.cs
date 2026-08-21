using System.Text;
using System.Threading.Tasks;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Analysis;
using SpectraShade.Compiler.CodeGen;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.Tests;

public sealed class HlslGeneratorTests
{
    [Fact]
    public Task Vertex_stage_matches_snapshot() => VerifyStage(ShaderStage.Vertex);

    [Fact]
    public Task Fragment_stage_matches_snapshot() => VerifyStage(ShaderStage.Fragment);

    [Fact]
    public Task Geometry_stage_matches_snapshot()
    {
        var blob = Compile("GeometryExtrude.spectrashade");
        blob.GeometryData.ShouldNotBeNull();
        var text = Encoding.UTF8.GetString(blob.GeometryData!);
        return Verify(text, extension: "hlsl");
    }

    [Fact]
    public void For_loop_expression_initializer_is_kept()
    {
        var hlsl = CompileStageText("ForExpressionInitializer.spectrashade", ShaderStage.Fragment);

        // An assignment initializer on a pre-declared counter must survive
        // into the emitted for header (it used to emit `for (; ...)`) — in
        // the natural spelling and in the parenthesized legacy one (the
        // parens are unwrapped during parsing, so both emit identically).
        hlsl.ShouldContain("for (i = 0; (i < 4); i = (i + 1))", Case.Sensitive);
        hlsl.ShouldContain("for (j = 0; (j < 2); j = (j + 1))", Case.Sensitive);
    }

    [Fact]
    public void Vertex_stage_returning_bare_vec4_declares_SV_Position()
    {
        var hlsl = CompileStageText("BareVertexReturn.spectrashade", ShaderStage.Vertex);

        // A non-struct vertex return has no [Position] struct field to carry
        // the semantic, so the entry signature itself must declare it —
        // FXC/DXC reject a vertex entry with no SV_Position output.
        hlsl.ShouldContain("float4 main(VertexInput input) : SV_Position", Case.Sensitive);
    }

    private static SettingsTask VerifyStage(ShaderStage stage)
    {
        var blob = Compile("SimpleVertex.spectrashade");
        var data = stage switch
        {
            ShaderStage.Vertex => blob.VertexData,
            ShaderStage.Fragment => blob.FragmentData,
            _ => null,
        };
        data.ShouldNotBeNull();
        var text = Encoding.UTF8.GetString(data!);
        return Verify(text, extension: "hlsl");
    }

    // Compiles one stage to text for targeted string assertions. The generator
    // emits Environment.NewLine (via StringBuilder.AppendLine), so the result
    // is normalized to LF to keep multi-line ShouldContain checks OS-agnostic.
    private static string CompileStageText(string fixtureName, ShaderStage stage)
    {
        var blob = Compile(fixtureName);
        var data = stage switch
        {
            ShaderStage.Vertex => blob.VertexData,
            _ => blob.FragmentData,
        };
        data.ShouldNotBeNull();
        return Encoding.UTF8.GetString(data!).Replace("\r\n", "\n");
    }

    private static PipelineBlob Compile(string fixtureName)
    {
        var source = TestFixtures.Load(fixtureName);
        var tokens = new Lexer(source, fixtureName).Tokenize();
        var parser = new Parser(tokens);
        var unit = parser.Parse();
        parser.Diagnostics.ShouldBeEmpty();

        var analyzer = new SemanticAnalyzer();
        analyzer.Analyze(unit).ShouldBeTrue();
        analyzer.Diagnostics.ShouldBeEmpty();

        return new HlslGenerator(GraphicsBackend.D3D11).Generate(unit);
    }

    private enum ShaderStage { Vertex, Fragment }
}
