using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;
using System.Diagnostics.CodeAnalysis;

namespace SpectraShade.Compiler.Analysis;

/// <summary>
/// Rewrites a shader whose model matrix is a uniform into the same shader with
/// that matrix arriving per instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>One source, two vertex stages, and the author writes neither of them
/// twice.</b> Marking a <c>cbuffer</c> field <c>[PerInstance]</c> says "this
/// uniform may also arrive per instance"; the compiler then emits the ordinary
/// stage AND an instanced twin, and the renderer picks whichever the draw needs.
/// A single draw keeps the uniform path exactly as it was, so nothing pays for
/// instancing it does not use.
/// </para>
/// <para>
/// <b>The alternative was a twin shader per material, and it does not scale.</b>
/// Duplicating a five-line depth pass is harmless; duplicating a
/// five-attachment G-buffer write is the drift hazard <c>CLAUDE.md</c> already
/// names, and every future shader that wanted instancing would owe the same
/// copy. Sharing the body through <c>import</c> was the other option and is not
/// available: imports parse and are never resolved.
/// </para>
/// <para>
/// <b>The rewrite is three edits and NO expression rewriting</b>, which is the
/// reason it needs no change in either code generator. The field leaves its
/// cbuffer, an identically named per-instance field joins the vertex input
/// struct, and the vertex body gains one leading local
/// (<c>mat4 uModel = input.uModel;</c>). That local is what keeps every existing
/// bare reference to the name resolving: without it, each one would have to be
/// found and rewritten into a member access, which is a visitor over every
/// expression node and a new place for the two variants to diverge.
/// </para>
/// </remarks>
public static class InstancedVariant
{
    /// <summary>
    /// Marks a <c>cbuffer</c> field as one that may instead arrive per instance.
    /// The same spelling as the vertex-input attribute, deliberately: it means
    /// the same thing in both places.
    /// </summary>
    public const string Attribute = VertexInputLayout.PerInstanceAttribute;

    /// <summary>
    /// The only type a per-instance uniform may have today. A world matrix is
    /// the case this exists for; anything else is refused rather than guessed
    /// at, because the location span and the buffer stride both depend on it.
    /// </summary>
    public const string SupportedType = "mat4";

    /// <summary>
    /// Builds the instanced twin of <paramref name="unit"/>, or returns false if
    /// it declares no per-instance uniform.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing for a shader that simply does not want
    /// this: most do not, and every one of them goes through here.
    /// </remarks>
    public static bool TryBuild(CompilationUnit unit, [NotNullWhen(true)] out CompilationUnit? instanced)
    {
        instanced = null;
        if (unit is null)
            return false;

        FieldDeclaration? marked = null;
        CBufferDeclaration? owner = null;
        foreach (SyntaxNode member in unit.Shader.Members)
        {
            if (member is not CBufferDeclaration cbuffer)
                continue;

            foreach (FieldDeclaration field in cbuffer.Fields)
            {
                if (!VertexInputLayout.IsPerInstance(field))
                    continue;

                // First one wins, and the analyzer refuses a second: two
                // per-instance uniforms would need a stride and a layout this
                // does not describe.
                marked = field;
                owner = cbuffer;
                break;
            }

            if (marked is not null)
                break;
        }

        if (marked is null || owner is null)
            return false;
        if (marked.Type.Name != SupportedType)
            return false;

        FunctionDeclaration? vertex = null;
        foreach (SyntaxNode member in unit.Shader.Members)
        {
            if (member is FunctionDeclaration f && f.HasAttribute("Vertex"))
            {
                vertex = f;
                break;
            }
        }

        if (vertex is null || vertex.Parameters.Count == 0)
            return false;

        var allStructs = new List<StructDeclaration>(unit.Structs);
        foreach (SyntaxNode member in unit.Shader.Members)
        {
            if (member is StructDeclaration s)
                allStructs.Add(s);
        }

        string inputTypeName = vertex.Parameters[0].Type.Name;
        StructDeclaration? inputStruct = allStructs.FirstOrDefault(s => s.Name == inputTypeName);
        if (inputStruct is null)
            return false;

        StructDeclaration rewrittenInput = WithInstanceField(inputStruct, marked);
        CBufferDeclaration rewrittenCbuffer = WithoutField(owner, marked);
        FunctionDeclaration rewrittenVertex = WithLeadingLocal(vertex, marked, vertex.Parameters[0].Name);

        instanced = Replace(unit, inputStruct, rewrittenInput, owner, rewrittenCbuffer, vertex, rewrittenVertex);
        return true;
    }

    /// <summary>
    /// The first location past everything <paramref name="inputStruct"/> already
    /// claims, accounting for multi-location types.
    /// </summary>
    /// <remarks>
    /// Computed rather than fixed, so a shader with more or fewer vertex inputs
    /// than the standard three still places its instance matrix somewhere free.
    /// </remarks>
    public static uint NextFreeLocation(StructDeclaration inputStruct)
    {
        uint next = 0;
        for (int i = 0; i < inputStruct.Fields.Count; i++)
        {
            FieldDeclaration field = inputStruct.Fields[i];
            if (!VertexInputLayout.TryDescribeType(field.Type.Name, out _, out uint span))
                continue;

            uint end = VertexInputLayout.ResolveLocation(field, i) + span;
            if (end > next)
                next = end;
        }
        return next;
    }

    private static StructDeclaration WithInstanceField(StructDeclaration inputStruct, FieldDeclaration marked)
    {
        SourceSpan span = marked.Span;
        uint location = NextFreeLocation(inputStruct);

        var attributes = new List<AttributeSyntax>
        {
            new("Location", [new IntLiteralExpression((int)location, span)], span),
            new(VertexInputLayout.PerInstanceAttribute, [], span),
        };

        // The SAME name as the uniform it replaces. That is what lets the
        // leading local below shadow it without either generator noticing that
        // anything moved.
        var field = new FieldDeclaration(attributes, marked.Type, marked.Name, span);

        var fields = new List<FieldDeclaration>(inputStruct.Fields) { field };
        return new StructDeclaration(inputStruct.Name, fields, inputStruct.Span);
    }

    private static CBufferDeclaration WithoutField(CBufferDeclaration cbuffer, FieldDeclaration marked)
    {
        var fields = new List<FieldDeclaration>(cbuffer.Fields.Count);
        foreach (FieldDeclaration field in cbuffer.Fields)
        {
            if (!ReferenceEquals(field, marked))
                fields.Add(field);
        }

        return new CBufferDeclaration(cbuffer.Attributes, cbuffer.Name, fields, cbuffer.Span);
    }

    private static FunctionDeclaration WithLeadingLocal(
        FunctionDeclaration vertex, FieldDeclaration marked, string inputParameterName)
    {
        SourceSpan span = marked.Span;

        // mat4 <name> = <input>.<name>;
        var initializer = new MemberAccessExpression(
            new IdentifierExpression(inputParameterName, span), marked.Name, span);
        var local = new VariableDeclaration(marked.Type, marked.Name, initializer, span);

        var statements = new List<SyntaxNode>(vertex.Body.Statements.Count + 1) { local };
        statements.AddRange(vertex.Body.Statements);

        var body = new BlockStatement(statements, vertex.Body.Span);
        return new FunctionDeclaration(
            vertex.Attributes, vertex.ReturnType, vertex.Name, vertex.Parameters, body, vertex.Span);
    }

    // Rebuilds the unit with three nodes swapped and everything else shared by
    // reference. Safe because every syntax node is immutable.
    private static CompilationUnit Replace(
        CompilationUnit unit,
        StructDeclaration oldInput, StructDeclaration newInput,
        CBufferDeclaration oldCbuffer, CBufferDeclaration newCbuffer,
        FunctionDeclaration oldVertex, FunctionDeclaration newVertex)
    {
        var structs = new List<StructDeclaration>(unit.Structs.Count);
        foreach (StructDeclaration s in unit.Structs)
            structs.Add(ReferenceEquals(s, oldInput) ? newInput : s);

        var members = new List<SyntaxNode>(unit.Shader.Members.Count);
        foreach (SyntaxNode member in unit.Shader.Members)
        {
            members.Add(member switch
            {
                _ when ReferenceEquals(member, oldInput) => newInput,
                _ when ReferenceEquals(member, oldCbuffer) => newCbuffer,
                _ when ReferenceEquals(member, oldVertex) => newVertex,
                _ => member,
            });
        }

        var shader = new ShaderDeclaration(unit.Shader.Name, members, unit.Shader.Span);
        return new CompilationUnit(unit.Imports, structs, shader, unit.Span);
    }
}
