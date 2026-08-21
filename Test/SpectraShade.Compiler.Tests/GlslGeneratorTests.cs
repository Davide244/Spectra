using System.Text;
using System.Threading.Tasks;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Analysis;
using SpectraShade.Compiler.CodeGen;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.Tests;

public sealed class GlslGeneratorTests
{
    [Fact]
    public Task Vertex_stage_matches_snapshot() =>
        VerifyStage("SimpleVertex.spectrashade", ShaderStage.Vertex);

    [Fact]
    public Task Fragment_stage_matches_snapshot() =>
        VerifyStage("SimpleVertex.spectrashade", ShaderStage.Fragment);

    [Fact]
    public Task Nested_return_vertex_stage_matches_snapshot() =>
        VerifyStage("NestedReturns.spectrashade", ShaderStage.Vertex);

    [Fact]
    public Task Nested_return_fragment_stage_matches_snapshot() =>
        VerifyStage("NestedReturns.spectrashade", ShaderStage.Fragment);

    [Fact]
    public Task Geometry_shader_vertex_stage_matches_snapshot() =>
        VerifyStage("GeometryExtrude.spectrashade", ShaderStage.Vertex);

    [Fact]
    public Task Geometry_shader_geometry_stage_matches_snapshot() =>
        VerifyStage("GeometryExtrude.spectrashade", ShaderStage.Geometry);

    [Fact]
    public Task Geometry_shader_fragment_stage_matches_snapshot() =>
        VerifyStage("GeometryExtrude.spectrashade", ShaderStage.Fragment);

    [Fact]
    public void Braceless_if_return_in_fragment_stage_stays_guarded()
    {
        var glsl = CompileStageText("BracelessReturns.spectrashade", ShaderStage.Fragment);

        // `if (c) return X;` lowers the single return into two statements
        // (output assignment + bare return), so the body must come out braced —
        // an unbraced emission would leak the bare `return;` out of the if.
        glsl.ShouldContain(
            "    if ((v_uv.x > 0.5))\n" +
            "    {\n" +
            "        fragColor = vec4(1, 0, 0, 1);\n" +
            "        return;\n" +
            "    }\n", Case.Sensitive);

        // The fall-through return after the if must still emit its own
        // assignment (with the leak, execution never reached it).
        glsl.ShouldContain(
            "    fragColor = vec4(0, 1, 0, 1);\n" +
            "    return;\n", Case.Sensitive);
    }

    [Fact]
    public void Braceless_if_return_in_vertex_stage_stays_guarded()
    {
        var glsl = CompileStageText("BracelessReturns.spectrashade", ShaderStage.Vertex);

        // A vertex struct return lowers to gl_Position + one assignment per
        // varying + bare return — all of it must stay inside the if.
        glsl.ShouldContain(
            "    if ((a_position.x < 0.5))\n" +
            "    {\n" +
            "        gl_Position = result.position;\n" +
            "        v_uv = result.uv;\n" +
            "        return;\n" +
            "    }\n", Case.Sensitive);

        // The statements after the if must still be emitted at function scope.
        glsl.ShouldContain(
            "    result.uv = vec2(0, 0);\n" +
            "    gl_Position = result.position;\n" +
            "    v_uv = result.uv;\n" +
            "    return;\n", Case.Sensitive);
    }

    [Fact]
    public void Vertex_stage_returning_bare_vec4_writes_gl_Position()
    {
        var glsl = CompileStageText("BareVertexReturn.spectrashade", ShaderStage.Vertex);

        // A non-struct vertex return is the clip-space position itself; it
        // must route to gl_Position instead of being silently dropped.
        glsl.ShouldContain(
            "void main()\n" +
            "{\n" +
            "    gl_Position = vec4(a_position, 1);\n" +
            "    return;\n" +
            "}\n", Case.Sensitive);
    }

    [Fact]
    public void For_loop_expression_initializer_is_kept()
    {
        var glsl = CompileStageText("ForExpressionInitializer.spectrashade", ShaderStage.Fragment);

        // An assignment initializer on a pre-declared counter must survive
        // into the emitted for header (it used to emit `for (; ...)`) — in
        // the natural spelling and in the parenthesized legacy one (the
        // parens are unwrapped during parsing, so both emit identically).
        glsl.ShouldContain("for (i = 0; (i < 4); i = (i + 1))", Case.Sensitive);
        glsl.ShouldContain("for (j = 0; (j < 2); j = (j + 1))", Case.Sensitive);
    }

    [Fact]
    public void Fragment_local_named_fragColor_is_escaped_off_the_stage_output()
    {
        var glsl = CompileStageText("FragColorShadow.spectrashade", ShaderStage.Fragment);

        // The generator owns the invented `fragColor` output; a user local of
        // the same name must be escaped, or it silently shadows the output
        // and the stage never writes it (the shader still compiles cleanly).
        glsl.ShouldContain("out vec4 fragColor;\n", Case.Sensitive);
        glsl.ShouldContain("    vec4 _ss_fragColor = vec4(v_uv, 0, 1);\n", Case.Sensitive);
        glsl.ShouldContain(
            "    fragColor = _ss_fragColor;\n" +
            "    return;\n", Case.Sensitive);
    }

    private static SettingsTask VerifyStage(string fixtureName, ShaderStage stage)
    {
        var blob = Compile(fixtureName);
        var (data, extension) = stage switch
        {
            ShaderStage.Vertex => (blob.VertexData, "vert"),
            ShaderStage.Geometry => (blob.GeometryData, "geom"),
            _ => (blob.FragmentData, "frag"),
        };
        data.ShouldNotBeNull();
        var text = Encoding.UTF8.GetString(data!);
        return Verify(text, extension: extension);
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
            ShaderStage.Geometry => blob.GeometryData,
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

        return new GlslGenerator().Generate(unit);
    }

    private enum ShaderStage { Vertex, Geometry, Fragment }
}
