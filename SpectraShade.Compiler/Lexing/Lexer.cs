using System;
using System.Collections.Generic;

namespace SpectraShade.Compiler.Lexing;

/// <summary>
/// Hand-written lexer for SpectraShade. Produces a token stream from source text.
/// </summary>
public sealed class Lexer
{
    private readonly string _source;
    private readonly string _fileName;
    private int _pos;
    private int _line;
    private int _column;

    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        // Top-level
        ["shader"] = TokenKind.Shader,
        ["import"] = TokenKind.Import,
        ["struct"] = TokenKind.Struct,
        ["cbuffer"] = TokenKind.CBuffer,

        // Qualifiers
        ["in"] = TokenKind.In,
        ["out"] = TokenKind.Out,
        ["const"] = TokenKind.Const,

        // Type inference
        ["var"] = TokenKind.Var,

        // Object
        ["new"] = TokenKind.New,

        // Control flow
        ["if"] = TokenKind.If,
        ["else"] = TokenKind.Else,
        ["for"] = TokenKind.For,
        ["while"] = TokenKind.While,
        ["return"] = TokenKind.Return,
        ["break"] = TokenKind.Break,
        ["continue"] = TokenKind.Continue,
        ["discard"] = TokenKind.Discard,

        // Scalar types
        ["void"] = TokenKind.Void,
        ["bool"] = TokenKind.Bool,
        ["int"] = TokenKind.Int,
        ["uint"] = TokenKind.UInt,
        ["float"] = TokenKind.Float,
        ["double"] = TokenKind.Double,

        // Vector types
        ["vec2"] = TokenKind.Vec2,
        ["vec3"] = TokenKind.Vec3,
        ["vec4"] = TokenKind.Vec4,
        ["ivec2"] = TokenKind.IVec2,
        ["ivec3"] = TokenKind.IVec3,
        ["ivec4"] = TokenKind.IVec4,
        ["uvec2"] = TokenKind.UVec2,
        ["uvec3"] = TokenKind.UVec3,
        ["uvec4"] = TokenKind.UVec4,
        ["bvec2"] = TokenKind.BVec2,
        ["bvec3"] = TokenKind.BVec3,
        ["bvec4"] = TokenKind.BVec4,

        // Matrix types
        ["mat2"] = TokenKind.Mat2,
        ["mat3"] = TokenKind.Mat3,
        ["mat4"] = TokenKind.Mat4,

        // Sampler types
        ["sampler2D"] = TokenKind.Sampler2D,
        ["sampler2DArray"] = TokenKind.Sampler2DArray,
        ["sampler3D"] = TokenKind.Sampler3D,
        ["samplerCube"] = TokenKind.SamplerCube,

        // Booleans
        ["true"] = TokenKind.BoolLiteral,
        ["false"] = TokenKind.BoolLiteral,
    };

    public Lexer(string source, string fileName = "<source>")
    {
        _source = source;
        _fileName = fileName;
        _pos = 0;
        _line = 1;
        _column = 1;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
                break;
        }

        return tokens;
    }

    private Token NextToken()
    {
        SkipWhitespaceAndComments();

        if (_pos >= _source.Length)
            return MakeToken(TokenKind.EndOfFile, "", _pos, _pos);

        int start = _pos;
        char c = Current;

        // String literals
        if (c == '"')
            return ReadString(start);

        // Numbers
        if (char.IsAsciiDigit(c))
            return ReadNumber(start);

        // Identifiers and keywords
        if (char.IsAsciiLetter(c) || c == '_')
            return ReadIdentifierOrKeyword(start);

        // Operators and punctuation
        return ReadOperatorOrPunctuation(start);
    }

    private Token ReadString(int start)
    {
        Advance(); // skip opening quote
        while (_pos < _source.Length && Current != '"' && Current != '\n')
            Advance();

        if (_pos < _source.Length && Current == '"')
            Advance(); // skip closing quote

        // Text includes quotes
        string text = _source[start.._pos];
        return MakeToken(TokenKind.StringLiteral, text, start, _pos);
    }

    private Token ReadNumber(int start)
    {
        bool isFloat = false;

        while (_pos < _source.Length && (char.IsAsciiDigit(Current) || Current == '.' || Current == 'f'))
        {
            if (Current == '.')
            {
                if (isFloat) break;
                isFloat = true;
            }
            if (Current == 'f')
            {
                isFloat = true;
                Advance();
                break;
            }
            Advance();
        }

        string text = _source[start.._pos];
        return MakeToken(isFloat ? TokenKind.FloatLiteral : TokenKind.IntLiteral, text, start, _pos);
    }

    private Token ReadIdentifierOrKeyword(int start)
    {
        while (_pos < _source.Length && (char.IsAsciiLetterOrDigit(Current) || Current == '_'))
            Advance();

        string text = _source[start.._pos];
        var kind = Keywords.GetValueOrDefault(text, TokenKind.Identifier);
        return MakeToken(kind, text, start, _pos);
    }

    private Token ReadOperatorOrPunctuation(int start)
    {
        char c = Current;
        Advance();

        switch (c)
        {
            case '(': return MakeToken(TokenKind.LeftParen, "(", start, _pos);
            case ')': return MakeToken(TokenKind.RightParen, ")", start, _pos);
            case '{': return MakeToken(TokenKind.LeftBrace, "{", start, _pos);
            case '}': return MakeToken(TokenKind.RightBrace, "}", start, _pos);
            case '[': return MakeToken(TokenKind.LeftBracket, "[", start, _pos);
            case ']': return MakeToken(TokenKind.RightBracket, "]", start, _pos);
            case ';': return MakeToken(TokenKind.Semicolon, ";", start, _pos);
            case ',': return MakeToken(TokenKind.Comma, ",", start, _pos);
            case '.': return MakeToken(TokenKind.Dot, ".", start, _pos);
            case ':': return MakeToken(TokenKind.Colon, ":", start, _pos);
            case '~': return MakeToken(TokenKind.BitwiseNot, "~", start, _pos);
            case '%': return MatchNext('=') ? MakeToken(TokenKind.PercentAssign, "%=", start, _pos) : MakeToken(TokenKind.Percent, "%", start, _pos);

            case '+':
                if (MatchNext('=')) return MakeToken(TokenKind.PlusAssign, "+=", start, _pos);
                return MakeToken(TokenKind.Plus, "+", start, _pos);
            case '-':
                if (MatchNext('=')) return MakeToken(TokenKind.MinusAssign, "-=", start, _pos);
                if (MatchNext('>')) return MakeToken(TokenKind.Arrow, "->", start, _pos);
                return MakeToken(TokenKind.Minus, "-", start, _pos);
            case '*':
                if (MatchNext('=')) return MakeToken(TokenKind.StarAssign, "*=", start, _pos);
                return MakeToken(TokenKind.Star, "*", start, _pos);
            case '/':
                if (MatchNext('=')) return MakeToken(TokenKind.SlashAssign, "/=", start, _pos);
                return MakeToken(TokenKind.Slash, "/", start, _pos);

            case '=':
                if (MatchNext('=')) return MakeToken(TokenKind.Equals, "==", start, _pos);
                return MakeToken(TokenKind.Assign, "=", start, _pos);
            case '!':
                if (MatchNext('=')) return MakeToken(TokenKind.NotEquals, "!=", start, _pos);
                return MakeToken(TokenKind.Not, "!", start, _pos);
            case '<':
                if (MatchNext('=')) return MakeToken(TokenKind.LessEquals, "<=", start, _pos);
                if (MatchNext('<')) return MakeToken(TokenKind.ShiftLeft, "<<", start, _pos);
                return MakeToken(TokenKind.Less, "<", start, _pos);
            case '>':
                if (MatchNext('=')) return MakeToken(TokenKind.GreaterEquals, ">=", start, _pos);
                if (MatchNext('>')) return MakeToken(TokenKind.ShiftRight, ">>", start, _pos);
                return MakeToken(TokenKind.Greater, ">", start, _pos);
            case '&':
                if (MatchNext('&')) return MakeToken(TokenKind.And, "&&", start, _pos);
                return MakeToken(TokenKind.BitwiseAnd, "&", start, _pos);
            case '|':
                if (MatchNext('|')) return MakeToken(TokenKind.Or, "||", start, _pos);
                return MakeToken(TokenKind.BitwiseOr, "|", start, _pos);
            case '^':
                return MakeToken(TokenKind.BitwiseXor, "^", start, _pos);

            default:
                return MakeToken(TokenKind.Invalid, c.ToString(), start, _pos);
        }
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            if (char.IsWhiteSpace(Current))
            {
                Advance();
            }
            else if (_pos + 1 < _source.Length && Current == '/' && _source[_pos + 1] == '/')
            {
                while (_pos < _source.Length && Current != '\n')
                    Advance();
            }
            else if (_pos + 1 < _source.Length && Current == '/' && _source[_pos + 1] == '*')
            {
                Advance();
                Advance();
                while (_pos + 1 < _source.Length && !(Current == '*' && _source[_pos + 1] == '/'))
                    Advance();
                if (_pos + 1 < _source.Length)
                {
                    Advance();
                    Advance();
                }
            }
            else
            {
                break;
            }
        }
    }

    private char Current => _source[_pos];

    private void Advance()
    {
        if (_pos < _source.Length)
        {
            if (_source[_pos] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
            _pos++;
        }
    }

    private bool MatchNext(char expected)
    {
        if (_pos < _source.Length && _source[_pos] == expected)
        {
            Advance();
            return true;
        }
        return false;
    }

    private Token MakeToken(TokenKind kind, string text, int startOffset, int endOffset)
    {
        var start = new SourceLocation(_fileName, _line, _column - (endOffset - startOffset), startOffset);
        var end = new SourceLocation(_fileName, _line, _column, endOffset);
        return new Token(kind, text, new SourceSpan(start, end));
    }
}
