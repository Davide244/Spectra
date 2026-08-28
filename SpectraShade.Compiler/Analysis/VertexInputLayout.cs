using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.Analysis;

/// <summary>
/// Resolves a shader's vertex input struct into the locations and rates the
/// renderer has to build an input layout from.
/// </summary>
/// <remarks>
/// <b>One implementation, because three would drift.</b> The analyzer validates
/// this, the GLSL generator emits <c>layout(location = N)</c> from it and the
/// HLSL generator emits <c>TEXCOORD{N}</c> from it, and all three have to agree
/// about exactly the same three things: which location a field starts at, how
/// many it occupies, and whether it advances per instance. Those answers being
/// computed in one place is what stops a shader whose declared layout and
/// reported layout disagree, which is a class of bug with no symptom at compile
/// time at all.
/// </remarks>
public static class VertexInputLayout
{
    /// <summary>
    /// The attribute that marks a vertex input as advancing once per instance.
    /// </summary>
    public const string PerInstanceAttribute = "PerInstance";

    /// <summary>
    /// Floats per location and locations occupied, for every type that may
    /// appear as a vertex input. Anything absent is not one.
    /// </summary>
    /// <remarks>
    /// <b>A matrix is several locations, and that is the whole subtlety here.</b>
    /// One field in the source becomes four consecutive attributes in both
    /// targets, so the count is not "one per field" and the next field does not
    /// start at the next index.
    /// </remarks>
    public static bool TryDescribeType(string typeName, out uint componentCount, out uint locationSpan)
    {
        (componentCount, locationSpan) = typeName switch
        {
            "float" or "int" or "uint" or "bool" => (1u, 1u),
            "vec2" or "ivec2" or "uvec2" or "bvec2" => (2u, 1u),
            "vec3" or "ivec3" or "uvec3" or "bvec3" => (3u, 1u),
            "vec4" or "ivec4" or "uvec4" or "bvec4" => (4u, 1u),
            "mat2" => (2u, 2u),
            "mat3" => (3u, 3u),
            "mat4" => (4u, 4u),
            _ => (0u, 0u),
        };

        return locationSpan > 0;
    }

    /// <summary>
    /// Whether <paramref name="field"/> is declared per instance.
    /// </summary>
    public static bool IsPerInstance(FieldDeclaration field) =>
        field.Attributes.Any(a => string.Equals(a.Name, PerInstanceAttribute, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The location <paramref name="field"/> starts at: its <c>[Location(N)]</c>
    /// if it has a literal one, else <paramref name="fieldIndex"/>.
    /// </summary>
    /// <remarks>
    /// <b>The index fallback is preserved rather than fixed</b>, because every
    /// shader in the repo predates this file and several rely on it. It is
    /// wrong the moment a matrix is involved (a <c>mat4</c> at index 1 leaves
    /// index 2 already taken), which is why the analyzer refuses that
    /// combination outright instead of quietly producing an overlap here.
    /// </remarks>
    public static uint ResolveLocation(FieldDeclaration field, int fieldIndex)
    {
        AttributeSyntax? location = field.Attributes.FirstOrDefault(a =>
            string.Equals(a.Name, "Location", StringComparison.OrdinalIgnoreCase));

        if (location is not null && location.Arguments.Count > 0
            && location.Arguments[0] is IntLiteralExpression literal && literal.Value >= 0)
        {
            return (uint)literal.Value;
        }

        return (uint)fieldIndex;
    }

    /// <summary>Whether <paramref name="field"/> carries a literal <c>[Location(N)]</c>.</summary>
    public static bool HasExplicitLocation(FieldDeclaration field) =>
        field.Attributes.Any(a =>
            string.Equals(a.Name, "Location", StringComparison.OrdinalIgnoreCase)
            && a.Arguments.Count > 0 && a.Arguments[0] is IntLiteralExpression { Value: >= 0 });

    /// <summary>
    /// Describes the inputs of <paramref name="vertexFunc"/>'s first parameter
    /// struct. The form a code generator calls, since a generator knows its
    /// stage function rather than which struct happens to be the input.
    /// </summary>
    public static VertexInputElement[] DescribeFor(
        FunctionDeclaration? vertexFunc, IReadOnlyList<StructDeclaration> allStructs)
    {
        if (vertexFunc is null || vertexFunc.Parameters.Count == 0)
            return [];

        string name = vertexFunc.Parameters[0].Type.Name;
        return Describe(allStructs.FirstOrDefault(s => s.Name == name));
    }

    /// <summary>
    /// Describes every field of <paramref name="inputStruct"/> as a
    /// <see cref="VertexInputElement"/>, in declaration order. Fields whose type
    /// cannot be a vertex input are skipped; the analyzer is what reports them,
    /// so that a code generator running after a failed analysis still produces
    /// something rather than throwing.
    /// </summary>
    public static VertexInputElement[] Describe(StructDeclaration? inputStruct)
    {
        if (inputStruct is null)
            return [];

        var elements = new List<VertexInputElement>(inputStruct.Fields.Count);
        for (int i = 0; i < inputStruct.Fields.Count; i++)
        {
            FieldDeclaration field = inputStruct.Fields[i];
            if (!TryDescribeType(field.Type.Name, out uint components, out uint span))
                continue;

            elements.Add(new VertexInputElement(
                field.Name,
                ResolveLocation(field, i),
                span,
                components,
                IsPerInstance(field) ? VertexInputRate.PerInstance : VertexInputRate.PerVertex));
        }

        return [.. elements];
    }
}
