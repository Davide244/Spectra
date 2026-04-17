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
