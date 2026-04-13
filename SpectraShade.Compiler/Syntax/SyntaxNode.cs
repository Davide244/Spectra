using SpectraShade.Compiler.Lexing;

namespace SpectraShade.Compiler.Syntax;

/// <summary>
/// Base class for all AST nodes.
/// </summary>
public abstract class SyntaxNode
{
    public SourceSpan Span { get; }

    protected SyntaxNode(SourceSpan span)
    {
        Span = span;
    }
}

// ─── Top-level ───────────────────────────────────────────────

/// <summary>
/// Root of the AST. One per .spectrashade file.
///
/// Contains imports, struct declarations (outside shader), and the shader block.
/// </summary>
public sealed class CompilationUnit : SyntaxNode
{
    public IReadOnlyList<ImportDirective> Imports { get; }
    public IReadOnlyList<StructDeclaration> Structs { get; }
    public ShaderDeclaration Shader { get; }

    public CompilationUnit(IReadOnlyList<ImportDirective> imports, IReadOnlyList<StructDeclaration> structs, ShaderDeclaration shader, SourceSpan span) : base(span)
    {
        Imports = imports;
        Structs = structs;
        Shader = shader;
    }
}

/// <summary>import "path.spectrashade";</summary>
public sealed class ImportDirective : SyntaxNode
{
    public string Path { get; }

    public ImportDirective(string path, SourceSpan span) : base(span)
    {
        Path = path;
    }
}

/// <summary>
/// shader Name { ... }
/// Contains cbuffers, sampler declarations, shared functions, and stage functions.
/// </summary>
public sealed class ShaderDeclaration : SyntaxNode
{
    public string Name { get; }
    public IReadOnlyList<SyntaxNode> Members { get; }

    public ShaderDeclaration(string name, IReadOnlyList<SyntaxNode> members, SourceSpan span) : base(span)
    {
        Name = name;
        Members = members;
    }
}

// ─── Attributes ──────────────────────────────────────────────

/// <summary>
/// [Name] or [Name(arg1, arg2)]
/// Examples: [Vertex], [Location(0)], [Binding(1)], [Target(0)]
/// </summary>
public sealed class AttributeSyntax : SyntaxNode
{
    public string Name { get; }
    public IReadOnlyList<Expression> Arguments { get; }

    public AttributeSyntax(string name, IReadOnlyList<Expression> arguments, SourceSpan span) : base(span)
    {
        Name = name;
        Arguments = arguments;
    }
}

// ─── Declarations ────────────────────────────────────────────

public sealed class StructDeclaration : SyntaxNode
{
    public string Name { get; }
    public IReadOnlyList<FieldDeclaration> Fields { get; }

    public StructDeclaration(string name, IReadOnlyList<FieldDeclaration> fields, SourceSpan span) : base(span)
    {
        Name = name;
        Fields = fields;
    }
}

public sealed class FieldDeclaration : SyntaxNode
{
    public IReadOnlyList<AttributeSyntax> Attributes { get; }
    public TypeSyntax Type { get; }
    public string Name { get; }

    public FieldDeclaration(IReadOnlyList<AttributeSyntax> attributes, TypeSyntax type, string name, SourceSpan span) : base(span)
    {
        Attributes = attributes;
        Type = type;
        Name = name;
    }
}

/// <summary>
/// [Binding(N)] cbuffer Name { fields... }
/// </summary>
public sealed class CBufferDeclaration : SyntaxNode
{
    public IReadOnlyList<AttributeSyntax> Attributes { get; }
    public string Name { get; }
    public IReadOnlyList<FieldDeclaration> Fields { get; }

    public CBufferDeclaration(IReadOnlyList<AttributeSyntax> attributes, string name, IReadOnlyList<FieldDeclaration> fields, SourceSpan span) : base(span)
    {
        Attributes = attributes;
        Name = name;
        Fields = fields;
    }
}

/// <summary>
/// [Binding(N)] sampler2D name;
/// A sampler/texture declaration at shader scope.
/// </summary>
public sealed class SamplerDeclaration : SyntaxNode
{
    public IReadOnlyList<AttributeSyntax> Attributes { get; }
    public TypeSyntax Type { get; }
    public string Name { get; }

    public SamplerDeclaration(IReadOnlyList<AttributeSyntax> attributes, TypeSyntax type, string name, SourceSpan span) : base(span)
    {
        Attributes = attributes;
        Type = type;
        Name = name;
    }
}

/// <summary>
/// A function declaration, potentially with attributes like [Vertex] or [Fragment].
/// Stage functions have attributes; shared helper functions don't.
///
/// [Vertex]
/// FragmentInput Main([Location(0)] vec3 position, [Location(1)] vec2 uv) { ... }
/// </summary>
public sealed class FunctionDeclaration : SyntaxNode
{
    public IReadOnlyList<AttributeSyntax> Attributes { get; }
    public TypeSyntax ReturnType { get; }
    public string Name { get; }
    public IReadOnlyList<ParameterSyntax> Parameters { get; }
    public BlockStatement Body { get; }

    public FunctionDeclaration(IReadOnlyList<AttributeSyntax> attributes, TypeSyntax returnType, string name, IReadOnlyList<ParameterSyntax> parameters, BlockStatement body, SourceSpan span) : base(span)
    {
        Attributes = attributes;
        ReturnType = returnType;
        Name = name;
        Parameters = parameters;
        Body = body;
    }

    public bool HasAttribute(string name) =>
        Attributes.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class ParameterSyntax : SyntaxNode
{
    public IReadOnlyList<AttributeSyntax> Attributes { get; }
    public TypeSyntax Type { get; }
    public string Name { get; }

    public ParameterSyntax(IReadOnlyList<AttributeSyntax> attributes, TypeSyntax type, string name, SourceSpan span) : base(span)
    {
        Attributes = attributes;
        Type = type;
        Name = name;
    }
}

// ─── Types ───────────────────────────────────────────────────

public sealed class TypeSyntax : SyntaxNode
{
    public string Name { get; }
    public bool IsArray { get; }
    public Expression? ArraySize { get; }

    public TypeSyntax(string name, bool isArray, Expression? arraySize, SourceSpan span) : base(span)
    {
        Name = name;
        IsArray = isArray;
        ArraySize = arraySize;
    }
}

// ─── Statements ──────────────────────────────────────────────

public abstract class Statement : SyntaxNode
{
    protected Statement(SourceSpan span) : base(span) { }
}

public sealed class BlockStatement : Statement
{
    public IReadOnlyList<SyntaxNode> Statements { get; }

    public BlockStatement(IReadOnlyList<SyntaxNode> statements, SourceSpan span) : base(span)
    {
        Statements = statements;
    }
}

public sealed class VariableDeclaration : SyntaxNode
{
    public TypeSyntax Type { get; }
    public string Name { get; }
    public Expression? Initializer { get; }

    public VariableDeclaration(TypeSyntax type, string name, Expression? initializer, SourceSpan span) : base(span)
    {
        Type = type;
        Name = name;
        Initializer = initializer;
    }
}

public sealed class ReturnStatement : Statement
{
    public Expression? Value { get; }

    public ReturnStatement(Expression? value, SourceSpan span) : base(span)
    {
        Value = value;
    }
}

public sealed class IfStatement : Statement
{
    public Expression Condition { get; }
    public Statement ThenBranch { get; }
    public Statement? ElseBranch { get; }

    public IfStatement(Expression condition, Statement thenBranch, Statement? elseBranch, SourceSpan span) : base(span)
    {
        Condition = condition;
        ThenBranch = thenBranch;
        ElseBranch = elseBranch;
    }
}

public sealed class ForStatement : Statement
{
    public SyntaxNode? Initializer { get; }
    public Expression? Condition { get; }
    public Expression? Increment { get; }
    public Statement Body { get; }

    public ForStatement(SyntaxNode? initializer, Expression? condition, Expression? increment, Statement body, SourceSpan span) : base(span)
    {
        Initializer = initializer;
        Condition = condition;
        Increment = increment;
        Body = body;
    }
}

public sealed class WhileStatement : Statement
{
    public Expression Condition { get; }
    public Statement Body { get; }

    public WhileStatement(Expression condition, Statement body, SourceSpan span) : base(span)
    {
        Condition = condition;
        Body = body;
    }
}

public sealed class ExpressionStatement : Statement
{
    public Expression Expression { get; }

    public ExpressionStatement(Expression expression, SourceSpan span) : base(span)
    {
        Expression = expression;
    }
}

public sealed class DiscardStatement : Statement
{
    public DiscardStatement(SourceSpan span) : base(span) { }
}

public sealed class BreakStatement : Statement
{
    public BreakStatement(SourceSpan span) : base(span) { }
}

public sealed class ContinueStatement : Statement
{
    public ContinueStatement(SourceSpan span) : base(span) { }
}

// ─── Expressions ─────────────────────────────────────────────

public abstract class Expression : SyntaxNode
{
    protected Expression(SourceSpan span) : base(span) { }
}

public sealed class BinaryExpression : Expression
{
    public Expression Left { get; }
    public TokenKind Operator { get; }
    public Expression Right { get; }

    public BinaryExpression(Expression left, TokenKind op, Expression right, SourceSpan span) : base(span)
    {
        Left = left;
        Operator = op;
        Right = right;
    }
}

public sealed class UnaryExpression : Expression
{
    public TokenKind Operator { get; }
    public Expression Operand { get; }

    public UnaryExpression(TokenKind op, Expression operand, SourceSpan span) : base(span)
    {
        Operator = op;
        Operand = operand;
    }
}

public sealed class CallExpression : Expression
{
    public Expression Target { get; }
    public IReadOnlyList<Expression> Arguments { get; }

    public CallExpression(Expression target, IReadOnlyList<Expression> arguments, SourceSpan span) : base(span)
    {
        Target = target;
        Arguments = arguments;
    }
}

/// <summary>Built-in type constructor: vec3(1.0, 0.0, 0.0)</summary>
public sealed class ConstructorExpression : Expression
{
    public TypeSyntax Type { get; }
    public IReadOnlyList<Expression> Arguments { get; }

    public ConstructorExpression(TypeSyntax type, IReadOnlyList<Expression> arguments, SourceSpan span) : base(span)
    {
        Type = type;
        Arguments = arguments;
    }
}

/// <summary>new StructName() — for user-defined structs</summary>
public sealed class NewExpression : Expression
{
    public TypeSyntax Type { get; }
    public IReadOnlyList<Expression> Arguments { get; }

    public NewExpression(TypeSyntax type, IReadOnlyList<Expression> arguments, SourceSpan span) : base(span)
    {
        Type = type;
        Arguments = arguments;
    }
}

public sealed class MemberAccessExpression : Expression
{
    public Expression Object { get; }
    public string Member { get; }

    public MemberAccessExpression(Expression obj, string member, SourceSpan span) : base(span)
    {
        Object = obj;
        Member = member;
    }
}

public sealed class IndexExpression : Expression
{
    public Expression Object { get; }
    public Expression Index { get; }

    public IndexExpression(Expression obj, Expression index, SourceSpan span) : base(span)
    {
        Object = obj;
        Index = index;
    }
}

public sealed class AssignmentExpression : Expression
{
    public Expression Target { get; }
    public TokenKind Operator { get; }
    public Expression Value { get; }

    public AssignmentExpression(Expression target, TokenKind op, Expression value, SourceSpan span) : base(span)
    {
        Target = target;
        Operator = op;
        Value = value;
    }
}

public sealed class IdentifierExpression : Expression
{
    public string Name { get; }

    public IdentifierExpression(string name, SourceSpan span) : base(span)
    {
        Name = name;
    }
}

public sealed class IntLiteralExpression : Expression
{
    public int Value { get; }

    public IntLiteralExpression(int value, SourceSpan span) : base(span)
    {
        Value = value;
    }
}

public sealed class FloatLiteralExpression : Expression
{
    public float Value { get; }

    public FloatLiteralExpression(float value, SourceSpan span) : base(span)
    {
        Value = value;
    }
}

public sealed class BoolLiteralExpression : Expression
{
    public bool Value { get; }

    public BoolLiteralExpression(bool value, SourceSpan span) : base(span)
    {
        Value = value;
    }
}
