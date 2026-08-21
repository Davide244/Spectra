using System.Collections.Generic;
using System.Linq;
using SpectraShade.Compiler;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Parses_simple_vertex_shader_without_diagnostics()
    {
        var (unit, diagnostics) = Parse(TestFixtures.Load("SimpleVertex.spectrashade"));

        diagnostics.ShouldBeEmpty();
        unit.Shader.Name.ShouldBe("SimpleVertex");
    }

    [Fact]
    public void Lifts_top_level_structs_onto_the_unit()
    {
        var (unit, _) = Parse(TestFixtures.Load("SimpleVertex.spectrashade"));

        unit.Structs.Select(s => s.Name)
            .ShouldBe(new[] { "VertexInput", "VertexOutput", "FragmentInput" });
    }

    [Fact]
    public void Surfaces_vertex_and_fragment_entry_functions()
    {
        var (unit, _) = Parse(TestFixtures.Load("SimpleVertex.spectrashade"));

        var functions = unit.Shader.Members.OfType<FunctionDeclaration>().ToList();
        functions.ShouldContain(f => f.Name == "VertexMain" && f.HasAttribute("Vertex"));
        functions.ShouldContain(f => f.Name == "FragmentMain" && f.HasAttribute("Fragment"));
    }

    [Fact]
    public void Braceless_if_with_declaration_body_reports_diagnostic_instead_of_throwing()
    {
        // A declaration as the sole body of a brace-less if. VariableDeclaration
        // is not a Statement, so this used to crash the parser with an
        // InvalidCastException — escaping the diagnostics contract — instead
        // of reporting an error.
        var source = """
            shader Crash {
                [Vertex]
                vec4 VertexMain([Location(0)] vec3 position) {
                    if (position.x > 0.0) float y = 1.0;
                    return vec4(position, 1.0);
                }

                [Fragment]
                vec4 FragmentMain() {
                    return vec4(1.0, 1.0, 1.0, 1.0);
                }
            }
            """;

        var (_, diagnostics) = Parse(source);

        diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("brace-less"));
    }

    [Fact]
    public void Braceless_loop_with_declaration_body_reports_diagnostic_instead_of_throwing()
    {
        // Same crash shape through the for and while body paths.
        var source = """
            shader Crash {
                [Vertex]
                vec4 VertexMain([Location(0)] vec3 position) {
                    for (var i = 0; i < 4; i = i + 1) float a = 1.0;
                    while (position.x > 0.0) float b = 2.0;
                    return vec4(position, 1.0);
                }

                [Fragment]
                vec4 FragmentMain() {
                    return vec4(1.0, 1.0, 1.0, 1.0);
                }
            }
            """;

        var (_, diagnostics) = Parse(source);

        diagnostics.Count(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("brace-less")).ShouldBe(2);
    }

    private static (CompilationUnit Unit, IReadOnlyList<Diagnostic> Diagnostics) Parse(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        var parser = new Parser(tokens);
        var unit = parser.Parse();
        return (unit, parser.Diagnostics);
    }
}
