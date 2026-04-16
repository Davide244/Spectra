using SpectraShade.Compiler.Lexing;

namespace SpectraShade.Compiler.Syntax;

/// <summary>
/// Recursive descent parser for SpectraShade.
///
/// Top-level syntax:
///   import "path.spectrashade";
///
///   struct FragmentInput { vec2 uv; vec3 normal; }
///
///   shader MyShader {
///       [Binding(0)] cbuffer Camera { mat4 view; mat4 projection; }
///       [Binding(1)] sampler2D albedoTex;
///
///       [Vertex]
///       FragmentInput Main([Location(0)] vec3 position, [Location(1)] vec2 uv) { ... }
///
///       [Fragment]
///       vec4 Main(FragmentInput input) { ... }
///   }
/// </summary>
public sealed class Parser
{
    private readonly List<Token> _tokens;
    private readonly List<Diagnostic> _diagnostics = [];
    private int _pos;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public Parser(List<Token> tokens)
    {
        _tokens = tokens;
        _pos = 0;
    }

    public CompilationUnit Parse()
    {
        var start = Current.Span;
        var imports = new List<ImportDirective>();
        var structs = new List<StructDeclaration>();

        // Parse imports and top-level structs before the shader block
        while (!Check(TokenKind.Shader) && !Check(TokenKind.EndOfFile))
        {
            if (Check(TokenKind.Import))
                imports.Add(ParseImport());
            else if (Check(TokenKind.Struct))
                structs.Add(ParseStruct());
            else
            {
                Error($"Expected 'import', 'struct', or 'shader', got '{Current.Text}'");
                Advance();
            }
        }

        var shader = ParseShader();

        // Structs can also appear after the shader block
        while (!Check(TokenKind.EndOfFile))
        {
            if (Check(TokenKind.Struct))
                structs.Add(ParseStruct());
            else
            {
                Error($"Unexpected token '{Current.Text}' after shader block");
                Advance();
            }
        }

        return new CompilationUnit(imports, structs, shader, Span(start));
    }

    // ─── Top-level ───────────────────────────────────────────

    private ImportDirective ParseImport()
    {
        var start = Current.Span;
        Expect(TokenKind.Import, "Expected 'import'");
        var pathToken = Expect(TokenKind.StringLiteral, "Expected import path string");
        Expect(TokenKind.Semicolon, "Expected ';'");

        // Strip quotes from path
        string path = pathToken.Text.Length >= 2
            ? pathToken.Text[1..^1]
            : pathToken.Text;

        return new ImportDirective(path, Span(start));
    }

    private ShaderDeclaration ParseShader()
    {
        var start = Current.Span;
        Expect(TokenKind.Shader, "Expected 'shader'");
        string name = Expect(TokenKind.Identifier, "Expected shader name").Text;
        Expect(TokenKind.LeftBrace, "Expected '{'");

        var members = new List<SyntaxNode>();
        while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
        {
            int before = _pos;
            var member = ParseShaderMember();
            if (member is not null)
                members.Add(member);
            if (_pos == before) Advance();
        }

        Expect(TokenKind.RightBrace, "Expected '}'");
        return new ShaderDeclaration(name, members, Span(start));
    }

    private SyntaxNode? ParseShaderMember()
    {
        // Parse leading attributes
        var attributes = ParseAttributes();

        // cbuffer
        if (Check(TokenKind.CBuffer))
            return ParseCBuffer(attributes);

        // Struct inside shader
        if (Check(TokenKind.Struct))
            return ParseStruct();

        // Sampler declaration: [Binding(N)] sampler2D name;
        if (IsSamplerType(Current.Kind))
            return ParseSamplerDeclaration(attributes);

        // Function (possibly a stage function with [Vertex]/[Fragment] attributes)
        if (IsTypeToken(Current.Kind) || Check(TokenKind.Void))
            return ParseFunction(attributes);

        Error($"Unexpected token '{Current.Text}' in shader body");
        Advance();
        return null;
    }

    // ─── Attributes ──────────────────────────────────────────

    private List<AttributeSyntax> ParseAttributes()
    {
        var attributes = new List<AttributeSyntax>();
        while (Check(TokenKind.LeftBracket))
            attributes.Add(ParseAttribute());
        return attributes;
    }

    private AttributeSyntax ParseAttribute()
    {
        var start = Current.Span;
        Expect(TokenKind.LeftBracket, "Expected '['");
        string name = Expect(TokenKind.Identifier, "Expected attribute name").Text;

        var args = new List<Expression>();
        if (Match(TokenKind.LeftParen))
        {
            while (!Check(TokenKind.RightParen) && !Check(TokenKind.EndOfFile))
            {
                int before = _pos;
                args.Add(ParseExpression());
                if (!Check(TokenKind.RightParen))
                    Expect(TokenKind.Comma, "Expected ',' between attribute arguments");
                if (_pos == before) Advance();
            }
            Expect(TokenKind.RightParen, "Expected ')'");
        }

        Expect(TokenKind.RightBracket, "Expected ']'");
        return new AttributeSyntax(name, args, Span(start));
    }

    // ─── Declarations ────────────────────────────────────────

    private StructDeclaration ParseStruct()
    {
        var start = Current.Span;
        Expect(TokenKind.Struct, "Expected 'struct'");
        string name = Expect(TokenKind.Identifier, "Expected struct name").Text;
        Expect(TokenKind.LeftBrace, "Expected '{'");

        var fields = new List<FieldDeclaration>();
        while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
        {
            int before = _pos;
            var fieldStart = Current.Span;
            var fieldAttrs = ParseAttributes();
            var type = ParseType();
            string fieldName = Expect(TokenKind.Identifier, "Expected field name").Text;
            Expect(TokenKind.Semicolon, "Expected ';'");
            fields.Add(new FieldDeclaration(fieldAttrs, type, fieldName, Span(fieldStart)));
            if (_pos == before) Advance();
        }

        Expect(TokenKind.RightBrace, "Expected '}'");
        return new StructDeclaration(name, fields, Span(start));
    }

    private CBufferDeclaration ParseCBuffer(List<AttributeSyntax> attributes)
    {
        var start = Current.Span;
        Expect(TokenKind.CBuffer, "Expected 'cbuffer'");
        string name = Expect(TokenKind.Identifier, "Expected cbuffer name").Text;
        Expect(TokenKind.LeftBrace, "Expected '{'");

        var fields = new List<FieldDeclaration>();
        while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
        {
            int before = _pos;
            var fieldStart = Current.Span;
            var fieldAttrs = ParseAttributes();
            var type = ParseType();
            string fieldName = Expect(TokenKind.Identifier, "Expected field name").Text;
            Expect(TokenKind.Semicolon, "Expected ';'");
            fields.Add(new FieldDeclaration(fieldAttrs, type, fieldName, Span(fieldStart)));
            if (_pos == before) Advance();
        }

        Expect(TokenKind.RightBrace, "Expected '}'");
        return new CBufferDeclaration(attributes, name, fields, Span(start));
    }

    private SamplerDeclaration ParseSamplerDeclaration(List<AttributeSyntax> attributes)
    {
        var start = Current.Span;
        var type = ParseType();
        string name = Expect(TokenKind.Identifier, "Expected sampler name").Text;
        Expect(TokenKind.Semicolon, "Expected ';'");
        return new SamplerDeclaration(attributes, type, name, Span(start));
    }

    private FunctionDeclaration ParseFunction(List<AttributeSyntax> attributes)
    {
        var start = Current.Span;
        var returnType = ParseType();
        string name = Expect(TokenKind.Identifier, "Expected function name").Text;
        Expect(TokenKind.LeftParen, "Expected '('");

        var parameters = new List<ParameterSyntax>();
        while (!Check(TokenKind.RightParen) && !Check(TokenKind.EndOfFile))
        {
            int before = _pos;
            var paramStart = Current.Span;
            var paramAttrs = ParseAttributes();
            var paramType = ParseType();
            string paramName = Expect(TokenKind.Identifier, "Expected parameter name").Text;
            parameters.Add(new ParameterSyntax(paramAttrs, paramType, paramName, Span(paramStart)));

            if (!Check(TokenKind.RightParen))
                Expect(TokenKind.Comma, "Expected ',' between parameters");
            if (_pos == before) Advance();
        }

        Expect(TokenKind.RightParen, "Expected ')'");
        var body = ParseBlock();

        return new FunctionDeclaration(attributes, returnType, name, parameters, body, Span(start));
    }

    // ─── Statements ──────────────────────────────────────────

    private BlockStatement ParseBlock()
    {
        var start = Current.Span;
        Expect(TokenKind.LeftBrace, "Expected '{'");

        var statements = new List<SyntaxNode>();
        while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
        {
            int before = _pos;
            var stmt = ParseStatement();
            if (stmt is not null)
                statements.Add(stmt);
            if (_pos == before) Advance();
        }

        Expect(TokenKind.RightBrace, "Expected '}'");
        return new BlockStatement(statements, Span(start));
    }

    private SyntaxNode? ParseStatement()
    {
        if (Check(TokenKind.LeftBrace))
            return ParseBlock();

        if (Check(TokenKind.If))
            return ParseIfStatement();

        if (Check(TokenKind.For))
            return ParseForStatement();

        if (Check(TokenKind.While))
            return ParseWhileStatement();

        if (Check(TokenKind.Return))
            return ParseReturnStatement();

        if (Check(TokenKind.Discard))
        {
            var span = Current.Span;
            Advance();
            Expect(TokenKind.Semicolon, "Expected ';'");
            return new DiscardStatement(Span(span));
        }

        if (Check(TokenKind.Break))
        {
            var span = Current.Span;
            Advance();
            Expect(TokenKind.Semicolon, "Expected ';'");
            return new BreakStatement(Span(span));
        }

        if (Check(TokenKind.Continue))
        {
            var span = Current.Span;
            Advance();
            Expect(TokenKind.Semicolon, "Expected ';'");
            return new ContinueStatement(Span(span));
        }

        // var name = expr;
        if (Check(TokenKind.Var))
            return ParseVarDeclaration();

        // Type name ... (variable declaration)
        if (IsTypeToken(Current.Kind) && _pos + 1 < _tokens.Count && _tokens[_pos + 1].Kind == TokenKind.Identifier)
        {
            // Disambiguate: is this "Type name" (declaration) or "Type(" (constructor in expression)?
            // Look ahead: if token after identifier is = or ; it's a declaration
            if (_pos + 2 < _tokens.Count)
            {
                var afterIdent = _tokens[_pos + 2].Kind;
                if (afterIdent is TokenKind.Assign or TokenKind.Semicolon)
                    return ParseTypedVarDeclaration();
            }
        }

        return ParseExpressionStatement();
    }

    private VariableDeclaration ParseVarDeclaration()
    {
        var start = Current.Span;
        Expect(TokenKind.Var, "Expected 'var'");
        string name = Expect(TokenKind.Identifier, "Expected variable name").Text;
        Expect(TokenKind.Assign, "Expected '=' after var declaration");
        var init = ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';'");

        // "var" is represented as a special type name that the analyzer resolves
        var varType = new TypeSyntax("var", false, null, Span(start));
        return new VariableDeclaration(varType, name, init, Span(start));
    }

    private VariableDeclaration ParseTypedVarDeclaration()
    {
        var start = Current.Span;
        var type = ParseType();
        string name = Expect(TokenKind.Identifier, "Expected variable name").Text;
        Expression? init = null;
        if (Match(TokenKind.Assign))
            init = ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';'");
        return new VariableDeclaration(type, name, init, Span(start));
    }

    private IfStatement ParseIfStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.If, "Expected 'if'");
        Expect(TokenKind.LeftParen, "Expected '('");
        var condition = ParseExpression();
        Expect(TokenKind.RightParen, "Expected ')'");
        var thenBranch = (Statement)(ParseStatement() ?? new BlockStatement([], Span(start)));
        Statement? elseBranch = null;
        if (Match(TokenKind.Else))
            elseBranch = (Statement)(ParseStatement() ?? new BlockStatement([], Span(start)));
        return new IfStatement(condition, thenBranch, elseBranch, Span(start));
    }

    private ForStatement ParseForStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.For, "Expected 'for'");
        Expect(TokenKind.LeftParen, "Expected '('");

        SyntaxNode? init = null;
        if (!Check(TokenKind.Semicolon))
        {
            if (Check(TokenKind.Var))
                init = ParseVarDeclaration();
            else if (IsTypeToken(Current.Kind))
            {
                var type = ParseType();
                string name = Expect(TokenKind.Identifier, "Expected name").Text;
                Expression? initExpr = null;
                if (Match(TokenKind.Assign))
                    initExpr = ParseExpression();
                init = new VariableDeclaration(type, name, initExpr, Span(start));
                Expect(TokenKind.Semicolon, "Expected ';'");
            }
            else
            {
                init = new ExpressionStatement(ParseExpression(), Span(start));
                Expect(TokenKind.Semicolon, "Expected ';'");
            }
        }
        else
        {
            Expect(TokenKind.Semicolon, "Expected ';'");
        }

        Expression? condition = null;
        if (!Check(TokenKind.Semicolon))
            condition = ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';'");

        Expression? increment = null;
        if (!Check(TokenKind.RightParen))
            increment = ParseExpression();
        Expect(TokenKind.RightParen, "Expected ')'");

        var body = (Statement)(ParseStatement() ?? new BlockStatement([], Span(start)));
        return new ForStatement(init, condition, increment, body, Span(start));
    }

    private WhileStatement ParseWhileStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.While, "Expected 'while'");
        Expect(TokenKind.LeftParen, "Expected '('");
        var condition = ParseExpression();
        Expect(TokenKind.RightParen, "Expected ')'");
        var body = (Statement)(ParseStatement() ?? new BlockStatement([], Span(start)));
        return new WhileStatement(condition, body, Span(start));
    }

    private ReturnStatement ParseReturnStatement()
    {
        var start = Current.Span;
        Expect(TokenKind.Return, "Expected 'return'");
        Expression? value = null;
        if (!Check(TokenKind.Semicolon))
            value = ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';'");
        return new ReturnStatement(value, Span(start));
    }

    private ExpressionStatement ParseExpressionStatement()
    {
        var start = Current.Span;
        var expr = ParseExpression();
        Expect(TokenKind.Semicolon, "Expected ';'");
        return new ExpressionStatement(expr, Span(start));
    }

    // ─── Expressions (precedence climbing) ───────────────────

    private Expression ParseExpression() => ParseAssignment();

    private Expression ParseAssignment()
    {
        var expr = ParseOr();

        if (IsAssignmentOp(Current.Kind))
        {
            var op = Current.Kind;
            Advance();
            var right = ParseAssignment();
            return new AssignmentExpression(expr, op, right, new SourceSpan(expr.Span.Start, right.Span.End));
        }

        return expr;
    }

    private Expression ParseOr() => ParseBinaryLeft(ParseAnd, TokenKind.Or);
    private Expression ParseAnd() => ParseBinaryLeft(ParseEquality, TokenKind.And);
    private Expression ParseEquality() => ParseBinaryLeft(ParseComparison, TokenKind.Equals, TokenKind.NotEquals);
    private Expression ParseComparison() => ParseBinaryLeft(ParseAdditive, TokenKind.Less, TokenKind.LessEquals, TokenKind.Greater, TokenKind.GreaterEquals);
    private Expression ParseAdditive() => ParseBinaryLeft(ParseMultiplicative, TokenKind.Plus, TokenKind.Minus);
    private Expression ParseMultiplicative() => ParseBinaryLeft(ParseUnary, TokenKind.Star, TokenKind.Slash, TokenKind.Percent);

    private Expression ParseBinaryLeft(Func<Expression> parseOperand, params TokenKind[] ops)
    {
        var left = parseOperand();

        while (ops.Contains(Current.Kind))
        {
            var op = Current.Kind;
            Advance();
            var right = parseOperand();
            left = new BinaryExpression(left, op, right, new SourceSpan(left.Span.Start, right.Span.End));
        }

        return left;
    }

    private Expression ParseUnary()
    {
        if (Current.Kind is TokenKind.Minus or TokenKind.Not or TokenKind.BitwiseNot)
        {
            var start = Current.Span;
            var op = Current.Kind;
            Advance();
            var operand = ParseUnary();
            return new UnaryExpression(op, operand, Span(start));
        }

        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        var expr = ParsePrimary();

        while (true)
        {
            if (Check(TokenKind.Dot))
            {
                Advance();
                string member = Expect(TokenKind.Identifier, "Expected member name").Text;
                expr = new MemberAccessExpression(expr, member, new SourceSpan(expr.Span.Start, Current.Span.End));
            }
            else if (Check(TokenKind.LeftBracket))
            {
                Advance();
                var index = ParseExpression();
                Expect(TokenKind.RightBracket, "Expected ']'");
                expr = new IndexExpression(expr, index, new SourceSpan(expr.Span.Start, Current.Span.End));
            }
            else if (Check(TokenKind.LeftParen))
            {
                expr = ParseCallArguments(expr);
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private Expression ParsePrimary()
    {
        var start = Current.Span;

        // new Type(args) — user-defined struct constructor
        if (Check(TokenKind.New))
        {
            Advance();
            var type = ParseType();
            var args = ParseArgumentList();
            return new NewExpression(type, args, Span(start));
        }

        // Built-in type constructor: vec3(...), mat4(...)
        if (IsBuiltinType(Current.Kind))
        {
            var type = ParseType();
            if (Check(TokenKind.LeftParen))
            {
                var args = ParseArgumentList();
                return new ConstructorExpression(type, args, Span(start));
            }
            return new IdentifierExpression(type.Name, Span(start));
        }

        if (Check(TokenKind.IntLiteral))
        {
            var token = Current;
            Advance();
            return new IntLiteralExpression(int.Parse(token.Text), Span(start));
        }

        if (Check(TokenKind.FloatLiteral))
        {
            var token = Current;
            Advance();
            return new FloatLiteralExpression(float.Parse(token.Text.TrimEnd('f'), System.Globalization.CultureInfo.InvariantCulture), Span(start));
        }

        if (Check(TokenKind.BoolLiteral))
        {
            var token = Current;
            Advance();
            return new BoolLiteralExpression(token.Text == "true", Span(start));
        }

        if (Check(TokenKind.Identifier))
        {
            string name = Current.Text;
            Advance();
            return new IdentifierExpression(name, Span(start));
        }

        if (Match(TokenKind.LeftParen))
        {
            var expr = ParseExpression();
            Expect(TokenKind.RightParen, "Expected ')'");
            return expr;
        }

        Error($"Expected expression, got '{Current.Text}'");
        Advance();
        return new IdentifierExpression("<error>", Span(start));
    }

    private Expression ParseCallArguments(Expression target)
    {
        var args = ParseArgumentList();
        return new CallExpression(target, args, new SourceSpan(target.Span.Start, Current.Span.End));
    }

    private List<Expression> ParseArgumentList()
    {
        Expect(TokenKind.LeftParen, "Expected '('");
        var args = new List<Expression>();
        while (!Check(TokenKind.RightParen) && !Check(TokenKind.EndOfFile))
        {
            int before = _pos;
            args.Add(ParseExpression());
            if (!Check(TokenKind.RightParen))
                Expect(TokenKind.Comma, "Expected ',' between arguments");
            if (_pos == before) Advance();
        }
        Expect(TokenKind.RightParen, "Expected ')'");
        return args;
    }

    // ─── Type parsing ────────────────────────────────────────

    private TypeSyntax ParseType()
    {
        var start = Current.Span;
        string name;

        if (IsTypeToken(Current.Kind))
        {
            name = Current.Text;
            Advance();
        }
        else
        {
            name = Expect(TokenKind.Identifier, "Expected type name").Text;
        }

        bool isArray = false;
        Expression? arraySize = null;
        if (Match(TokenKind.LeftBracket))
        {
            isArray = true;
            if (!Check(TokenKind.RightBracket))
                arraySize = ParseExpression();
            Expect(TokenKind.RightBracket, "Expected ']'");
        }

        return new TypeSyntax(name, isArray, arraySize, Span(start));
    }

    // ─── Helpers ─────────────────────────────────────────────

    private Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

    private void Advance()
    {
        if (_pos < _tokens.Count - 1)
            _pos++;
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private bool Match(TokenKind kind)
    {
        if (Current.Kind == kind)
        {
            Advance();
            return true;
        }
        return false;
    }

    private Token Expect(TokenKind kind, string message)
    {
        if (Current.Kind == kind)
        {
            var token = Current;
            Advance();
            return token;
        }

        Error(message);
        return Current;
    }

    private void Error(string message)
    {
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, Current.Span));
    }

    private SourceSpan Span(SourceSpan start) => new(start.Start, Current.Span.End);

    private static bool IsAssignmentOp(TokenKind kind) =>
        kind is TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign or TokenKind.SlashAssign;

    private static bool IsSamplerType(TokenKind kind) =>
        kind is TokenKind.Sampler2D or TokenKind.Sampler2DArray or TokenKind.Sampler3D or TokenKind.SamplerCube;

    private static bool IsBuiltinType(TokenKind kind) =>
        kind is TokenKind.Vec2 or TokenKind.Vec3 or TokenKind.Vec4
            or TokenKind.IVec2 or TokenKind.IVec3 or TokenKind.IVec4
            or TokenKind.UVec2 or TokenKind.UVec3 or TokenKind.UVec4
            or TokenKind.BVec2 or TokenKind.BVec3 or TokenKind.BVec4
            or TokenKind.Mat2 or TokenKind.Mat3 or TokenKind.Mat4
            or TokenKind.Sampler2D or TokenKind.Sampler2DArray or TokenKind.Sampler3D or TokenKind.SamplerCube;

    private static bool IsTypeToken(TokenKind kind) =>
        kind is TokenKind.Void or TokenKind.Bool or TokenKind.Int or TokenKind.UInt
            or TokenKind.Float or TokenKind.Double or TokenKind.Identifier
            || IsBuiltinType(kind);
}
