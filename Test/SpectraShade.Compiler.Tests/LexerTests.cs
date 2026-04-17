using System.Linq;
using SpectraShade.Compiler.Lexing;

namespace SpectraShade.Compiler.Tests;

public sealed class LexerTests
{
    [Fact]
    public void Produces_keyword_identifier_brace_and_eof()
    {
        var tokens = new Lexer("shader Test { }").Tokenize();

        tokens.Select(t => t.Kind).ShouldBe(new[]
        {
            TokenKind.Shader,
            TokenKind.Identifier,
            TokenKind.LeftBrace,
            TokenKind.RightBrace,
            TokenKind.EndOfFile,
        });
    }

    [Fact]
    public void Tracks_line_and_column_across_newlines()
    {
        var tokens = new Lexer("struct\nFoo").Tokenize();

        var foo = tokens.Single(t => t.Text == "Foo");
        foo.Span.Start.Line.ShouldBe(2);
        foo.Span.Start.Column.ShouldBe(1);
    }

    [Theory]
    [InlineData("vec2", TokenKind.Vec2)]
    [InlineData("vec3", TokenKind.Vec3)]
    [InlineData("vec4", TokenKind.Vec4)]
    [InlineData("mat4", TokenKind.Mat4)]
    [InlineData("sampler2D", TokenKind.Sampler2D)]
    public void Recognizes_builtin_type_keywords(string source, TokenKind expected)
    {
        var tokens = new Lexer(source).Tokenize();
        tokens[0].Kind.ShouldBe(expected);
    }
}
