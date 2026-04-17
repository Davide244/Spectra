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

    private static (CompilationUnit Unit, IReadOnlyList<Diagnostic> Diagnostics) Parse(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        var parser = new Parser(tokens);
        var unit = parser.Parse();
        return (unit, parser.Diagnostics);
    }
}
