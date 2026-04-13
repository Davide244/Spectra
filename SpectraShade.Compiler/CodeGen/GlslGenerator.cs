using System.Globalization;
using System.Text;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.CodeGen;

/// <summary>
/// Generates GLSL 330 core source from a SpectraShade AST.
///
/// Transforms:
///   [Vertex] function params     → layout(location=N) in declarations + void main()
///   [Fragment] function params   → in declarations from vertex output struct + void main()
///   Position = expr              → gl_Position = expr
///   return result                → assigns to out variables
///   cbuffer fields               → uniform declarations
///   tex.Sample(uv)               → texture(tex, uv)
///   Math.Func(args)              → func(args) (lowercase GLSL builtins)
///   vec3(args)                   → vec3(args) (pass-through)
///   new Struct()                 → Struct() (GLSL constructor syntax)
/// </summary>
public sealed class GlslGenerator : ICodeGenerator
{
    public GraphicsBackend Backend => GraphicsBackend.OpenGL;
    public ShaderDataFormat OutputFormat => ShaderDataFormat.SourceText;

    private CompilationUnit _unit = null!;

    // Built-in Math.X → GLSL function name mapping
    private static readonly Dictionary<string, string> MathBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normalize"] = "normalize",
        ["Dot"] = "dot",
        ["Cross"] = "cross",
        ["Length"] = "length",
        ["Distance"] = "distance",
        ["Mix"] = "mix",
        ["Lerp"] = "mix",
        ["Clamp"] = "clamp",
        ["Min"] = "min",
        ["Max"] = "max",
        ["Abs"] = "abs",
        ["Floor"] = "floor",
        ["Ceil"] = "ceil",
        ["Fract"] = "fract",
        ["Mod"] = "mod",
        ["Pow"] = "pow",
        ["Sqrt"] = "sqrt",
        ["Sin"] = "sin",
        ["Cos"] = "cos",
        ["Tan"] = "tan",
        ["Asin"] = "asin",
        ["Acos"] = "acos",
        ["Atan"] = "atan",
        ["Reflect"] = "reflect",
        ["Refract"] = "refract",
        ["Step"] = "step",
        ["SmoothStep"] = "smoothstep",
        ["Sign"] = "sign",
        ["Exp"] = "exp",
        ["Log"] = "log",
        ["Exp2"] = "exp2",
        ["Log2"] = "log2",
        ["Inverse"] = "inverse",
        ["Transpose"] = "transpose",
        ["Determinant"] = "determinant",
    };

    public PipelineBlob Generate(CompilationUnit unit)
    {
        _unit = unit;
        var shader = unit.Shader;

        byte[]? vertexData = null;
        byte[]? fragmentData = null;
        var stages = ShaderStageFlags.None;

        var functions = shader.Members.OfType<FunctionDeclaration>().ToList();
        var cbuffers = shader.Members.OfType<CBufferDeclaration>().ToList();
        var samplers = shader.Members.OfType<SamplerDeclaration>().ToList();

        var vertexFunc = functions.FirstOrDefault(f => f.HasAttribute("Vertex"));
        var fragmentFunc = functions.FirstOrDefault(f => f.HasAttribute("Fragment"));
        var helperFunctions = functions.Where(f =>
            !f.HasAttribute("Vertex") && !f.HasAttribute("Fragment")
            && !f.HasAttribute("Geometry") && !f.HasAttribute("Compute")).ToList();

        // Resolve structs (from CompilationUnit and inside shader)
        var allStructs = new List<StructDeclaration>(unit.Structs);
        allStructs.AddRange(shader.Members.OfType<StructDeclaration>());

        if (vertexFunc is not null)
        {
            vertexData = Encoding.UTF8.GetBytes(
                EmitVertexStage(vertexFunc, cbuffers, samplers, helperFunctions, allStructs));
            stages |= ShaderStageFlags.Vertex;
        }

        if (fragmentFunc is not null)
        {
            fragmentData = Encoding.UTF8.GetBytes(
                EmitFragmentStage(fragmentFunc, vertexFunc, cbuffers, samplers, helperFunctions, allStructs));
            stages |= ShaderStageFlags.Fragment;
        }

        return new PipelineBlob
        {
            Backend = Backend,
            Format = OutputFormat,
            Stages = stages,
            VertexData = vertexData,
            FragmentData = fragmentData,
        };
    }

    private string EmitVertexStage(
        FunctionDeclaration func,
        List<CBufferDeclaration> cbuffers,
        List<SamplerDeclaration> samplers,
        List<FunctionDeclaration> helpers,
        List<StructDeclaration> structs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#version 330 core");
        sb.AppendLine();

        // Vertex inputs from function parameters → layout(location=N) in
        foreach (var param in func.Parameters)
        {
            var locAttr = param.Attributes.FirstOrDefault(a =>
                string.Equals(a.Name, "Location", StringComparison.OrdinalIgnoreCase));
            string layout = locAttr is not null
                ? $"layout(location = {EmitExpression(locAttr.Arguments[0])}) "
                : "";
            sb.AppendLine($"{layout}in {GlslType(param.Type.Name)} {param.Name};");
        }
        sb.AppendLine();

        // Vertex outputs from return struct fields → out declarations
        var returnStruct = FindStruct(func.ReturnType.Name, structs);
        if (returnStruct is not null)
        {
            foreach (var field in returnStruct.Fields)
            {
                if (HasAttribute(field.Attributes, "Position"))
                    continue; // Position is gl_Position, not an out variable
                sb.AppendLine($"out {GlslType(field.Type.Name)} v_{field.Name};");
            }
            sb.AppendLine();
        }

        // Uniforms from cbuffers
        EmitUniforms(sb, cbuffers);
        EmitSamplerUniforms(sb, samplers);

        // Helper functions
        foreach (var helper in helpers)
            EmitFunction(sb, helper);

        // Main function
        sb.AppendLine("void main()");
        sb.AppendLine("{");
        EmitVertexBody(sb, func, returnStruct, 1);
        sb.AppendLine("}");

        return sb.ToString();
    }

    private string EmitFragmentStage(
        FunctionDeclaration func,
        FunctionDeclaration? vertexFunc,
        List<CBufferDeclaration> cbuffers,
        List<SamplerDeclaration> samplers,
        List<FunctionDeclaration> helpers,
        List<StructDeclaration> structs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#version 330 core");
        sb.AppendLine();

        // Fragment inputs: matching vertex outputs
        StructDeclaration? inputStruct = null;
        string inputParamName = "input";
        if (func.Parameters.Count > 0)
        {
            inputStruct = FindStruct(func.Parameters[0].Type.Name, structs);
            inputParamName = func.Parameters[0].Name;
        }

        if (inputStruct is not null)
        {
            foreach (var field in inputStruct.Fields)
            {
                if (HasAttribute(field.Attributes, "Position"))
                    continue;
                sb.AppendLine($"in {GlslType(field.Type.Name)} v_{field.Name};");
            }
            sb.AppendLine();
        }

        // Fragment output
        // If return type is vec4 or a struct with [Target] attributes
        var returnStruct = FindStruct(func.ReturnType.Name, structs);
        if (returnStruct is not null)
        {
            foreach (var field in returnStruct.Fields)
            {
                var targetAttr = field.Attributes.FirstOrDefault(a =>
                    string.Equals(a.Name, "Target", StringComparison.OrdinalIgnoreCase));
                string layout = targetAttr is not null
                    ? $"layout(location = {EmitExpression(targetAttr.Arguments[0])}) "
                    : "";
                sb.AppendLine($"{layout}out {GlslType(field.Type.Name)} {field.Name};");
            }
        }
        else
        {
            // Simple return type: out vec4
            sb.AppendLine($"out {GlslType(func.ReturnType.Name)} fragColor;");
        }
        sb.AppendLine();

        // Uniforms
        EmitUniforms(sb, cbuffers);
        EmitSamplerUniforms(sb, samplers);

        // Helper functions
        foreach (var helper in helpers)
            EmitFunction(sb, helper);

        // Main function
        sb.AppendLine("void main()");
        sb.AppendLine("{");
        EmitFragmentBody(sb, func, inputStruct, inputParamName, returnStruct, 1);
        sb.AppendLine("}");

        return sb.ToString();
    }

    private void EmitVertexBody(StringBuilder sb, FunctionDeclaration func, StructDeclaration? returnStruct, int indent)
    {
        string pad = new(' ', indent * 4);

        foreach (var stmt in func.Body.Statements)
        {
            if (stmt is ReturnStatement ret && ret.Value is not null && returnStruct is not null)
            {
                // return result; → assign each struct field to out variable
                string resultName = GetReturnVarName(ret.Value);
                foreach (var field in returnStruct.Fields)
                {
                    if (HasAttribute(field.Attributes, "Position"))
                        sb.AppendLine($"{pad}gl_Position = {resultName}.{field.Name};");
                    else
                        sb.AppendLine($"{pad}v_{field.Name} = {resultName}.{field.Name};");
                }
            }
            else
            {
                EmitStatement(sb, stmt, indent, isVertex: true);
            }
        }
    }

    private void EmitFragmentBody(StringBuilder sb, FunctionDeclaration func, StructDeclaration? inputStruct, string inputParam, StructDeclaration? returnStruct, int indent)
    {
        string pad = new(' ', indent * 4);

        foreach (var stmt in func.Body.Statements)
        {
            if (stmt is ReturnStatement ret && ret.Value is not null)
            {
                if (returnStruct is not null)
                {
                    string resultName = GetReturnVarName(ret.Value);
                    foreach (var field in returnStruct.Fields)
                        sb.AppendLine($"{pad}{field.Name} = {resultName}.{field.Name};");
                }
                else
                {
                    // Simple return: out fragColor = expr
                    sb.AppendLine($"{pad}fragColor = {EmitExpression(ret.Value, inputStruct, inputParam)};");
                }
            }
            else
            {
                EmitStatement(sb, stmt, indent, inputStruct: inputStruct, inputParam: inputParam);
            }
        }
    }

    // ─── Shared emit ─────────────────────────────────────────

    private void EmitUniforms(StringBuilder sb, List<CBufferDeclaration> cbuffers)
    {
        foreach (var cbuffer in cbuffers)
        {
            sb.AppendLine($"// cbuffer {cbuffer.Name}");
            foreach (var field in cbuffer.Fields)
                sb.AppendLine($"uniform {GlslType(field.Type.Name)} {field.Name};");
            sb.AppendLine();
        }
    }

    private static void EmitSamplerUniforms(StringBuilder sb, List<SamplerDeclaration> samplers)
    {
        foreach (var sampler in samplers)
            sb.AppendLine($"uniform {GlslType(sampler.Type.Name)} {sampler.Name};");
        if (samplers.Count > 0)
            sb.AppendLine();
    }

    private void EmitFunction(StringBuilder sb, FunctionDeclaration func)
    {
        string ret = GlslType(func.ReturnType.Name);
        string parms = string.Join(", ", func.Parameters.Select(p => $"{GlslType(p.Type.Name)} {p.Name}"));
        sb.AppendLine($"{ret} {func.Name}({parms})");
        EmitBlock(sb, func.Body, 0);
        sb.AppendLine();
    }

    private void EmitBlock(StringBuilder sb, BlockStatement block, int indent, bool isVertex = false, StructDeclaration? inputStruct = null, string? inputParam = null)
    {
        string pad = new(' ', indent * 4);
        sb.AppendLine($"{pad}{{");
        foreach (var stmt in block.Statements)
            EmitStatement(sb, stmt, indent + 1, isVertex, inputStruct, inputParam);
        sb.AppendLine($"{pad}}}");
    }

    private void EmitStatement(StringBuilder sb, SyntaxNode node, int indent, bool isVertex = false, StructDeclaration? inputStruct = null, string? inputParam = null)
    {
        string pad = new(' ', indent * 4);

        switch (node)
        {
            case VariableDeclaration v:
                string varType = v.Type.Name == "var" ? "auto" : GlslType(v.Type.Name);
                // For GLSL 330, we need explicit types — resolve var to the initializer type if possible
                // For now, use the type as-is (analyzer would resolve var)
                if (v.Type.Name == "var" && v.Initializer is ConstructorExpression ctor)
                    varType = GlslType(ctor.Type.Name);
                else if (v.Type.Name == "var" && v.Initializer is NewExpression newExpr)
                    varType = v.Initializer is NewExpression ne ? ne.Type.Name : "auto";
                string init = v.Initializer is not null ? $" = {EmitExpression(v.Initializer, inputStruct, inputParam)}" : "";
                sb.AppendLine($"{pad}{varType} {v.Name}{init};");
                break;

            case ReturnStatement r:
                string val = r.Value is not null ? $" {EmitExpression(r.Value, inputStruct, inputParam)}" : "";
                sb.AppendLine($"{pad}return{val};");
                break;

            case ExpressionStatement e:
                sb.AppendLine($"{pad}{EmitExpression(e.Expression, inputStruct, inputParam, isVertex)};");
                break;

            case IfStatement i:
                sb.AppendLine($"{pad}if ({EmitExpression(i.Condition, inputStruct, inputParam)})");
                EmitStatementOrBlock(sb, i.ThenBranch, indent, isVertex, inputStruct, inputParam);
                if (i.ElseBranch is not null)
                {
                    sb.AppendLine($"{pad}else");
                    EmitStatementOrBlock(sb, i.ElseBranch, indent, isVertex, inputStruct, inputParam);
                }
                break;

            case ForStatement f:
                sb.Append($"{pad}for (");
                if (f.Initializer is VariableDeclaration fv)
                {
                    string fType = fv.Type.Name == "var" ? "float" : GlslType(fv.Type.Name);
                    string fInit = fv.Initializer is not null ? $" = {EmitExpression(fv.Initializer, inputStruct, inputParam)}" : "";
                    sb.Append($"{fType} {fv.Name}{fInit}");
                }
                sb.Append("; ");
                if (f.Condition is not null)
                    sb.Append(EmitExpression(f.Condition, inputStruct, inputParam));
                sb.Append("; ");
                if (f.Increment is not null)
                    sb.Append(EmitExpression(f.Increment, inputStruct, inputParam));
                sb.AppendLine(")");
                EmitStatementOrBlock(sb, f.Body, indent, isVertex, inputStruct, inputParam);
                break;

            case WhileStatement w:
                sb.AppendLine($"{pad}while ({EmitExpression(w.Condition, inputStruct, inputParam)})");
                EmitStatementOrBlock(sb, w.Body, indent, isVertex, inputStruct, inputParam);
                break;

            case BlockStatement b:
                EmitBlock(sb, b, indent, isVertex, inputStruct, inputParam);
                break;

            case DiscardStatement:
                sb.AppendLine($"{pad}discard;");
                break;

            case BreakStatement:
                sb.AppendLine($"{pad}break;");
                break;

            case ContinueStatement:
                sb.AppendLine($"{pad}continue;");
                break;
        }
    }

    private void EmitStatementOrBlock(StringBuilder sb, Statement stmt, int indent, bool isVertex = false, StructDeclaration? inputStruct = null, string? inputParam = null)
    {
        if (stmt is BlockStatement block)
            EmitBlock(sb, block, indent, isVertex, inputStruct, inputParam);
        else
            EmitStatement(sb, stmt, indent + 1, isVertex, inputStruct, inputParam);
    }

    // ─── Expression emit ─────────────────────────────────────

    private string EmitExpression(Expression expr, StructDeclaration? inputStruct = null, string? inputParam = null, bool isVertex = false)
    {
        switch (expr)
        {
            case IntLiteralExpression i:
                return i.Value.ToString();

            case FloatLiteralExpression f:
                return f.Value.ToString(CultureInfo.InvariantCulture);

            case BoolLiteralExpression b:
                return b.Value ? "true" : "false";

            case IdentifierExpression id:
                // Position → gl_Position in vertex stage
                if (id.Name == "Position" && isVertex)
                    return "gl_Position";
                return id.Name;

            case BinaryExpression bin:
                return $"({EmitExpression(bin.Left, inputStruct, inputParam, isVertex)} {MapOperator(bin.Operator)} {EmitExpression(bin.Right, inputStruct, inputParam, isVertex)})";

            case UnaryExpression un:
                return $"({MapOperator(un.Operator)}{EmitExpression(un.Operand, inputStruct, inputParam, isVertex)})";

            case ConstructorExpression ctor:
                string ctorArgs = string.Join(", ", ctor.Arguments.Select(a => EmitExpression(a, inputStruct, inputParam, isVertex)));
                return $"{GlslType(ctor.Type.Name)}({ctorArgs})";

            case NewExpression newExpr:
                // new Struct() → Struct() — GLSL doesn't have 'new', structs are constructed by name
                string newArgs = string.Join(", ", newExpr.Arguments.Select(a => EmitExpression(a, inputStruct, inputParam, isVertex)));
                return $"{newExpr.Type.Name}({newArgs})";

            case CallExpression call:
                return EmitCall(call, inputStruct, inputParam, isVertex);

            case MemberAccessExpression ma:
                return EmitMemberAccess(ma, inputStruct, inputParam, isVertex);

            case IndexExpression idx:
                return $"{EmitExpression(idx.Object, inputStruct, inputParam, isVertex)}[{EmitExpression(idx.Index, inputStruct, inputParam, isVertex)}]";

            case AssignmentExpression assign:
                string target = EmitExpression(assign.Target, inputStruct, inputParam, isVertex);
                string value = EmitExpression(assign.Value, inputStruct, inputParam, isVertex);
                return $"{target} {MapOperator(assign.Operator)} {value}";

            default:
                return "/* unknown */";
        }
    }

    private string EmitCall(CallExpression call, StructDeclaration? inputStruct, string? inputParam, bool isVertex)
    {
        // Math.Func(args) → func(args)
        if (call.Target is MemberAccessExpression ma && ma.Object is IdentifierExpression obj && obj.Name == "Math")
        {
            if (MathBuiltins.TryGetValue(ma.Member, out string? glslFunc))
            {
                string args = string.Join(", ", call.Arguments.Select(a => EmitExpression(a, inputStruct, inputParam, isVertex)));
                return $"{glslFunc}({args})";
            }
        }

        // tex.Sample(uv) → texture(tex, uv)
        if (call.Target is MemberAccessExpression sampleAccess && sampleAccess.Member == "Sample")
        {
            string texName = EmitExpression(sampleAccess.Object, inputStruct, inputParam, isVertex);
            string args = string.Join(", ", call.Arguments.Select(a => EmitExpression(a, inputStruct, inputParam, isVertex)));
            return $"texture({texName}, {args})";
        }

        // Regular function call
        string callTarget = EmitExpression(call.Target, inputStruct, inputParam, isVertex);
        string callArgs = string.Join(", ", call.Arguments.Select(a => EmitExpression(a, inputStruct, inputParam, isVertex)));
        return $"{callTarget}({callArgs})";
    }

    private string EmitMemberAccess(MemberAccessExpression ma, StructDeclaration? inputStruct, string? inputParam, bool isVertex)
    {
        // input.field → v_field (fragment reading vertex outputs)
        if (inputStruct is not null && inputParam is not null
            && ma.Object is IdentifierExpression id && id.Name == inputParam)
        {
            // Check if the field exists in the input struct and isn't [Position]
            var field = inputStruct.Fields.FirstOrDefault(f => f.Name == ma.Member);
            if (field is not null && !HasAttribute(field.Attributes, "Position"))
                return $"v_{ma.Member}";
        }

        // Swizzle or regular member access
        return $"{EmitExpression(ma.Object, inputStruct, inputParam, isVertex)}.{ma.Member}";
    }

    // ─── Helpers ─────────────────────────────────────────────

    private static string GetReturnVarName(Expression expr)
    {
        if (expr is IdentifierExpression id)
            return id.Name;
        return "/* complex return */";
    }

    private static StructDeclaration? FindStruct(string name, List<StructDeclaration> structs)
    {
        return structs.FirstOrDefault(s => s.Name == name);
    }

    private static bool HasAttribute(IReadOnlyList<AttributeSyntax> attrs, string name)
    {
        return attrs.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string MapOperator(TokenKind kind) => kind switch
    {
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.Equals => "==",
        TokenKind.NotEquals => "!=",
        TokenKind.Less => "<",
        TokenKind.LessEquals => "<=",
        TokenKind.Greater => ">",
        TokenKind.GreaterEquals => ">=",
        TokenKind.And => "&&",
        TokenKind.Or => "||",
        TokenKind.Not => "!",
        TokenKind.Assign => "=",
        TokenKind.PlusAssign => "+=",
        TokenKind.MinusAssign => "-=",
        TokenKind.StarAssign => "*=",
        TokenKind.SlashAssign => "/=",
        _ => "?"
    };

    private static string GlslType(string name) => name switch
    {
        "void" => "void",
        "bool" => "bool",
        "int" => "int",
        "uint" => "uint",
        "float" => "float",
        "double" => "double",
        "vec2" => "vec2",
        "vec3" => "vec3",
        "vec4" => "vec4",
        "ivec2" => "ivec2",
        "ivec3" => "ivec3",
        "ivec4" => "ivec4",
        "mat2" => "mat2",
        "mat3" => "mat3",
        "mat4" => "mat4",
        "sampler2D" => "sampler2D",
        "sampler3D" => "sampler3D",
        "samplerCube" => "samplerCube",
        _ => name,
    };
}
