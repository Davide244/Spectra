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

    // Current emit context — set before emitting each stage, used by EmitExpression/EmitStatement
    private bool _isVertex;
    private bool _isGeometry;
    private bool _isCompute;
    private StructDeclaration? _inputStruct;
    private string? _inputParam;
    private StructDeclaration? _geometryOutputStruct;

    // GLSL reserved words (used or reserved-for-future-use) that must not appear
    // as user identifiers. Names matching this set are prefixed with `_ss_` at every
    // emission site. Source: GLSL 4.6 spec §3.6.
    private static readonly HashSet<string> GlslReservedWords = new(StringComparer.Ordinal)
    {
        "input", "output", "common", "partition", "active", "asm", "class", "union",
        "enum", "typedef", "template", "this", "resource", "goto", "inline", "noinline",
        "public", "static", "extern", "external", "interface", "long", "short", "half",
        "fixed", "unsigned", "superp", "hvec2", "hvec3", "hvec4", "fvec2", "fvec3", "fvec4",
        "filter", "sizeof", "cast", "namespace", "using", "sampler3DRect",
        "attribute", "varying", "subroutine", "patch", "sample", "coherent", "volatile",
        "restrict", "readonly", "writeonly", "noperspective", "centroid", "precise",
    };

    private static string EscapeId(string name)
        => GlslReservedWords.Contains(name) ? "_ss_" + name : name;

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
        var geometryFunc = functions.FirstOrDefault(f => f.HasAttribute("Geometry"));
        var computeFunc = functions.FirstOrDefault(f => f.HasAttribute("Compute"));
        var helperFunctions = functions.Where(f =>
            !f.HasAttribute("Vertex") && !f.HasAttribute("Fragment")
            && !f.HasAttribute("Geometry") && !f.HasAttribute("Compute")).ToList();

        // Resolve structs (from CompilationUnit and inside shader)
        var allStructs = new List<StructDeclaration>(unit.Structs);
        allStructs.AddRange(shader.Members.OfType<StructDeclaration>());

        byte[]? geometryData = null;
        byte[]? computeData = null;

        if (vertexFunc is not null)
        {
            vertexData = Encoding.UTF8.GetBytes(
                EmitVertexStage(vertexFunc, cbuffers, samplers, helperFunctions, allStructs));
            stages |= ShaderStageFlags.Vertex;
        }

        if (geometryFunc is not null)
        {
            geometryData = Encoding.UTF8.GetBytes(
                EmitGeometryStage(geometryFunc, vertexFunc, fragmentFunc, cbuffers, samplers, helperFunctions, allStructs));
            stages |= ShaderStageFlags.Geometry;
        }

        if (fragmentFunc is not null)
        {
            fragmentData = Encoding.UTF8.GetBytes(
                EmitFragmentStage(fragmentFunc, vertexFunc, cbuffers, samplers, helperFunctions, allStructs));
            stages |= ShaderStageFlags.Fragment;
        }

        if (computeFunc is not null)
        {
            computeData = Encoding.UTF8.GetBytes(
                EmitComputeStage(computeFunc, cbuffers, samplers, helperFunctions, allStructs));
            stages |= ShaderStageFlags.Compute;
        }

        return new PipelineBlob
        {
            Backend = Backend,
            Format = OutputFormat,
            Stages = stages,
            VertexData = vertexData,
            FragmentData = fragmentData,
            GeometryData = geometryData,
            ComputeData = computeData,
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

        EmitStructs(sb, structs);

        // Vertex inputs: the input struct's fields become attribute declarations.
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
                var locAttr = field.Attributes.FirstOrDefault(a =>
                    string.Equals(a.Name, "Location", StringComparison.OrdinalIgnoreCase));
                string layout = locAttr is not null
                    ? $"layout(location = {EmitExpression(locAttr.Arguments[0])}) "
                    : "";
                sb.AppendLine($"{layout}in {GlslType(field.Type.Name)} a_{field.Name};");
            }
            sb.AppendLine();
        }

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
        EmitVertexBody(sb, func, returnStruct, inputStruct, inputParamName, 1);
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

        EmitStructs(sb, structs);

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

        // Depth testing hints
        if (func.HasAttribute("EarlyDepthStencil"))
            sb.AppendLine("layout(early_fragment_tests) in;");

        var depthWriteAttr = func.Attributes.FirstOrDefault(a =>
            string.Equals(a.Name, "DepthWrite", StringComparison.OrdinalIgnoreCase));
        if (depthWriteAttr is not null)
        {
            string depthCondition = "depth_any";
            if (depthWriteAttr.Arguments.Count > 0 && depthWriteAttr.Arguments[0] is IdentifierExpression depthId)
            {
                depthCondition = depthId.Name switch
                {
                    "Less" => "depth_less",
                    "Greater" => "depth_greater",
                    "Unchanged" => "depth_unchanged",
                    _ => "depth_any",
                };
            }
            sb.AppendLine($"layout({depthCondition}) out float gl_FragDepth;");
        }

        if (func.HasAttribute("EarlyDepthStencil") || depthWriteAttr is not null)
            sb.AppendLine();

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
                sb.AppendLine($"{layout}out {GlslType(field.Type.Name)} {EscapeId(field.Name)};");
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

    private string EmitGeometryStage(
        FunctionDeclaration func,
        FunctionDeclaration? vertexFunc,
        FunctionDeclaration? fragmentFunc,
        List<CBufferDeclaration> cbuffers,
        List<SamplerDeclaration> samplers,
        List<FunctionDeclaration> helpers,
        List<StructDeclaration> structs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#version 330 core");
        sb.AppendLine();

        // Input primitive layout
        string inputPrimitive = "triangles";
        var inputPrimAttr = func.Attributes.FirstOrDefault(a =>
            string.Equals(a.Name, "InputPrimitive", StringComparison.OrdinalIgnoreCase));
        if (inputPrimAttr is not null && inputPrimAttr.Arguments.Count > 0
            && inputPrimAttr.Arguments[0] is IdentifierExpression inputPrimId)
        {
            inputPrimitive = inputPrimId.Name switch
            {
                "Points" => "points",
                "Lines" => "lines",
                "LinesAdjacency" => "lines_adjacency",
                "Triangles" => "triangles",
                "TrianglesAdjacency" => "triangles_adjacency",
                _ => "triangles",
            };
        }
        sb.AppendLine($"layout({inputPrimitive}) in;");

        // Output primitive layout + max vertices
        string outputPrimitive = "triangle_strip";
        var outputPrimAttr = func.Attributes.FirstOrDefault(a =>
            string.Equals(a.Name, "OutputPrimitive", StringComparison.OrdinalIgnoreCase));
        if (outputPrimAttr is not null && outputPrimAttr.Arguments.Count > 0
            && outputPrimAttr.Arguments[0] is IdentifierExpression outputPrimId)
        {
            outputPrimitive = outputPrimId.Name switch
            {
                "Points" => "points",
                "LineStrip" => "line_strip",
                "TriangleStrip" => "triangle_strip",
                _ => "triangle_strip",
            };
        }

        var maxVertAttr = func.Attributes.FirstOrDefault(a =>
            string.Equals(a.Name, "MaxVertexCount", StringComparison.OrdinalIgnoreCase));
        string maxVerts = maxVertAttr is not null && maxVertAttr.Arguments.Count > 0
            ? EmitExpression(maxVertAttr.Arguments[0])
            : "3";
        sb.AppendLine($"layout({outputPrimitive}, max_vertices = {maxVerts}) out;");
        sb.AppendLine();

        // Geometry inputs: vertex output struct fields as in arrays
        StructDeclaration? inputStruct = null;
        string inputParamName = "vertices";
        if (func.Parameters.Count > 0)
        {
            inputStruct = FindStruct(func.Parameters[0].Type.Name, structs);
            inputParamName = func.Parameters[0].Name;
        }

        if (inputStruct is not null)
        {
            sb.AppendLine($"in VS_OUT {{");
            foreach (var field in inputStruct.Fields)
            {
                if (HasAttribute(field.Attributes, "Position"))
                    continue;
                sb.AppendLine($"    {GlslType(field.Type.Name)} {EscapeId(field.Name)};");
            }
            sb.AppendLine($"}} gs_in[];");
            sb.AppendLine();
        }

        // Geometry outputs: fragment input struct fields
        StructDeclaration? outputStruct = null;
        if (fragmentFunc is not null && fragmentFunc.Parameters.Count > 0)
            outputStruct = FindStruct(fragmentFunc.Parameters[0].Type.Name, structs);

        if (outputStruct is not null)
        {
            foreach (var field in outputStruct.Fields)
            {
                if (HasAttribute(field.Attributes, "Position"))
                    continue;
                sb.AppendLine($"out {GlslType(field.Type.Name)} v_{field.Name};");
            }
            sb.AppendLine();
        }

        // Uniforms
        EmitUniforms(sb, cbuffers);
        EmitSamplerUniforms(sb, samplers);

        // Helper functions
        foreach (var helper in helpers)
            EmitFunction(sb, helper);

        // Main function
        sb.AppendLine("void main()");
        sb.AppendLine("{");
        EmitGeometryBody(sb, func, inputStruct, inputParamName, outputStruct, 1);
        sb.AppendLine("}");

        return sb.ToString();
    }

    private string EmitComputeStage(
        FunctionDeclaration func,
        List<CBufferDeclaration> cbuffers,
        List<SamplerDeclaration> samplers,
        List<FunctionDeclaration> helpers,
        List<StructDeclaration> structs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#version 430 core");
        sb.AppendLine();

        // Local size from [NumThreads(x, y, z)]
        var numThreadsAttr = func.Attributes.FirstOrDefault(a =>
            string.Equals(a.Name, "NumThreads", StringComparison.OrdinalIgnoreCase));
        string x = "1", y = "1", z = "1";
        if (numThreadsAttr is not null)
        {
            if (numThreadsAttr.Arguments.Count >= 1)
                x = EmitExpression(numThreadsAttr.Arguments[0]);
            if (numThreadsAttr.Arguments.Count >= 2)
                y = EmitExpression(numThreadsAttr.Arguments[1]);
            if (numThreadsAttr.Arguments.Count >= 3)
                z = EmitExpression(numThreadsAttr.Arguments[2]);
        }
        sb.AppendLine($"layout(local_size_x = {x}, local_size_y = {y}, local_size_z = {z}) in;");
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
        EmitComputeBody(sb, func, 1);
        sb.AppendLine("}");

        return sb.ToString();
    }

    private void EmitVertexBody(StringBuilder sb, FunctionDeclaration func, StructDeclaration? returnStruct,
        StructDeclaration? inputStruct, string inputParam, int indent)
    {
        string pad = new(' ', indent * 4);
        SetStageContext(isVertex: true, inputStruct: inputStruct, inputParam: inputParam);

        foreach (var stmt in func.Body.Statements)
        {
            if (stmt is ReturnStatement ret && ret.Value is not null && returnStruct is not null)
            {
                // return result; → assign each struct field to out variable
                string resultName = GetReturnVarName(ret.Value);
                foreach (var field in returnStruct.Fields)
                {
                    if (HasAttribute(field.Attributes, "Position"))
                        sb.AppendLine($"{pad}gl_Position = {resultName}.{EscapeId(field.Name)};");
                    else
                        sb.AppendLine($"{pad}v_{field.Name} = {resultName}.{EscapeId(field.Name)};");
                }
            }
            else
            {
                EmitStatement(sb, stmt, indent);
            }
        }

        SetStageContext();
    }

    private void EmitFragmentBody(StringBuilder sb, FunctionDeclaration func, StructDeclaration? inputStruct, string inputParam, StructDeclaration? returnStruct, int indent)
    {
        string pad = new(' ', indent * 4);
        SetStageContext(inputStruct: inputStruct, inputParam: inputParam);

        foreach (var stmt in func.Body.Statements)
        {
            if (stmt is ReturnStatement ret && ret.Value is not null)
            {
                if (returnStruct is not null)
                {
                    string resultName = GetReturnVarName(ret.Value);
                    foreach (var field in returnStruct.Fields)
                        sb.AppendLine($"{pad}{EscapeId(field.Name)} = {resultName}.{EscapeId(field.Name)};");
                }
                else
                {
                    // Simple return: out fragColor = expr
                    sb.AppendLine($"{pad}fragColor = {EmitExpression(ret.Value)};");
                }
            }
            else
            {
                EmitStatement(sb, stmt, indent);
            }
        }

        SetStageContext();
    }

    private void SetStageContext(bool isVertex = false, bool isGeometry = false, bool isCompute = false,
        StructDeclaration? inputStruct = null, string? inputParam = null, StructDeclaration? geometryOutputStruct = null)
    {
        _isVertex = isVertex;
        _isGeometry = isGeometry;
        _isCompute = isCompute;
        _inputStruct = inputStruct;
        _inputParam = inputParam;
        _geometryOutputStruct = geometryOutputStruct;
    }

    private void EmitGeometryBody(StringBuilder sb, FunctionDeclaration func, StructDeclaration? inputStruct, string inputParam, StructDeclaration? outputStruct, int indent)
    {
        SetStageContext(isVertex: true, isGeometry: true, inputStruct: inputStruct, inputParam: inputParam, geometryOutputStruct: outputStruct);
        foreach (var stmt in func.Body.Statements)
            EmitStatement(sb, stmt, indent);
        SetStageContext();
    }

    private void EmitComputeBody(StringBuilder sb, FunctionDeclaration func, int indent)
    {
        SetStageContext(isCompute: true);
        foreach (var stmt in func.Body.Statements)
            EmitStatement(sb, stmt, indent);
        SetStageContext();
    }

    // ─── Shared emit ─────────────────────────────────────────

    // Emits GLSL struct declarations so locals such as the vertex-output
    // struct (and any user structs) resolve in the generated source.
    private void EmitStructs(StringBuilder sb, List<StructDeclaration> structs)
    {
        foreach (var s in structs)
        {
            sb.AppendLine($"struct {s.Name}");
            sb.AppendLine("{");
            foreach (var field in s.Fields)
                sb.AppendLine($"    {GlslType(field.Type.Name)} {EscapeId(field.Name)}{EmitArraySuffix(field.Type)};");
            sb.AppendLine("};");
            sb.AppendLine();
        }
    }

    private void EmitUniforms(StringBuilder sb, List<CBufferDeclaration> cbuffers)
    {
        foreach (var cbuffer in cbuffers)
        {
            sb.AppendLine($"// cbuffer {cbuffer.Name}");
            foreach (var field in cbuffer.Fields)
                sb.AppendLine($"uniform {GlslType(field.Type.Name)} {EscapeId(field.Name)}{EmitArraySuffix(field.Type)};");
            sb.AppendLine();
        }
    }

    private void EmitSamplerUniforms(StringBuilder sb, List<SamplerDeclaration> samplers)
    {
        foreach (var sampler in samplers)
            sb.AppendLine($"uniform {GlslType(sampler.Type.Name)} {EscapeId(sampler.Name)}{EmitArraySuffix(sampler.Type)};");
        if (samplers.Count > 0)
            sb.AppendLine();
    }

    private void EmitFunction(StringBuilder sb, FunctionDeclaration func)
    {
        string ret = GlslType(func.ReturnType.Name);
        string parms = string.Join(", ", func.Parameters.Select(p => $"{GlslType(p.Type.Name)} {EscapeId(p.Name)}"));
        sb.AppendLine($"{ret} {func.Name}({parms})");
        EmitBlock(sb, func.Body, 0);
        sb.AppendLine();
    }

    private void EmitBlock(StringBuilder sb, BlockStatement block, int indent)
    {
        string pad = new(' ', indent * 4);
        sb.AppendLine($"{pad}{{");
        foreach (var stmt in block.Statements)
            EmitStatement(sb, stmt, indent + 1);
        sb.AppendLine($"{pad}}}");
    }

    private void EmitStatement(StringBuilder sb, SyntaxNode node, int indent)
    {
        string pad = new(' ', indent * 4);

        switch (node)
        {
            case VariableDeclaration v:
                string varType = v.Type.Name == "var" ? "auto" : GlslType(v.Type.Name);
                if (v.Type.Name == "var" && v.Initializer is ConstructorExpression ctor)
                    varType = GlslType(ctor.Type.Name);
                else if (v.Type.Name == "var" && v.Initializer is NewExpression newExpr)
                    varType = newExpr.Type.Name;

                // GLSL has no zero-argument struct constructor — `new T()` lowers
                // to a default-initialized declaration with no initializer.
                string init;
                if (v.Initializer is NewExpression ne && ne.Arguments.Count == 0)
                    init = "";
                else
                    init = v.Initializer is not null ? $" = {EmitExpression(v.Initializer)}" : "";

                sb.AppendLine($"{pad}{varType} {EscapeId(v.Name)}{init};");
                break;

            case ReturnStatement r:
                string val = r.Value is not null ? $" {EmitExpression(r.Value)}" : "";
                sb.AppendLine($"{pad}return{val};");
                break;

            case ExpressionStatement e:
                sb.AppendLine($"{pad}{EmitExpression(e.Expression)};");
                break;

            case IfStatement i:
                sb.AppendLine($"{pad}if ({EmitExpression(i.Condition)})");
                EmitStatementOrBlock(sb, i.ThenBranch, indent);
                if (i.ElseBranch is not null)
                {
                    sb.AppendLine($"{pad}else");
                    EmitStatementOrBlock(sb, i.ElseBranch, indent);
                }
                break;

            case ForStatement f:
                sb.Append($"{pad}for (");
                if (f.Initializer is VariableDeclaration fv)
                {
                    string fType = fv.Type.Name == "var" ? "float" : GlslType(fv.Type.Name);
                    string fInit = fv.Initializer is not null ? $" = {EmitExpression(fv.Initializer)}" : "";
                    sb.Append($"{fType} {EscapeId(fv.Name)}{fInit}");
                }
                sb.Append("; ");
                if (f.Condition is not null)
                    sb.Append(EmitExpression(f.Condition));
                sb.Append("; ");
                if (f.Increment is not null)
                    sb.Append(EmitExpression(f.Increment));
                sb.AppendLine(")");
                EmitStatementOrBlock(sb, f.Body, indent);
                break;

            case WhileStatement w:
                sb.AppendLine($"{pad}while ({EmitExpression(w.Condition)})");
                EmitStatementOrBlock(sb, w.Body, indent);
                break;

            case BlockStatement b:
                EmitBlock(sb, b, indent);
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

    private void EmitStatementOrBlock(StringBuilder sb, Statement stmt, int indent)
    {
        if (stmt is BlockStatement block)
            EmitBlock(sb, block, indent);
        else
            EmitStatement(sb, stmt, indent + 1);
    }

    // ─── Expression emit ─────────────────────────────────────

    // Compute built-in variable mappings
    private static readonly Dictionary<string, string> ComputeBuiltins = new(StringComparer.Ordinal)
    {
        ["GlobalInvocationID"] = "gl_GlobalInvocationID",
        ["LocalInvocationID"] = "gl_LocalInvocationID",
        ["WorkGroupID"] = "gl_WorkGroupID",
        ["LocalInvocationIndex"] = "gl_LocalInvocationIndex",
        ["NumWorkGroups"] = "gl_NumWorkGroups",
        ["WorkGroupSize"] = "gl_WorkGroupSize",
    };

    private string EmitExpression(Expression expr)
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
                // Position → gl_Position in vertex/geometry stage
                if (id.Name == "Position" && (_isVertex || _isGeometry))
                    return "gl_Position";
                // Compute built-in variables
                if (_isCompute && ComputeBuiltins.TryGetValue(id.Name, out string? computeBuiltin))
                    return computeBuiltin;
                // Geometry built-in: PrimitiveID → gl_PrimitiveIDIn
                if (_isGeometry && id.Name == "PrimitiveID")
                    return "gl_PrimitiveIDIn";
                return EscapeId(id.Name);

            case BinaryExpression bin:
                return $"({EmitExpression(bin.Left)} {MapOperator(bin.Operator)} {EmitExpression(bin.Right)})";

            case UnaryExpression un:
                return $"({MapOperator(un.Operator)}{EmitExpression(un.Operand)})";

            case ConstructorExpression ctor:
                string ctorArgs = string.Join(", ", ctor.Arguments.Select(a => EmitExpression(a)));
                return $"{GlslType(ctor.Type.Name)}({ctorArgs})";

            case NewExpression newExpr:
                // new Struct() → Struct() — GLSL doesn't have 'new', structs are constructed by name
                string newArgs = string.Join(", ", newExpr.Arguments.Select(a => EmitExpression(a)));
                return $"{newExpr.Type.Name}({newArgs})";

            case CallExpression call:
                return EmitCall(call);

            case MemberAccessExpression ma:
                return EmitMemberAccess(ma);

            case IndexExpression idx:
                return EmitIndexExpression(idx);

            case AssignmentExpression assign:
                string target = EmitExpression(assign.Target);
                string value = EmitExpression(assign.Value);
                return $"{target} {MapOperator(assign.Operator)} {value}";

            default:
                return "/* unknown */";
        }
    }

    private string EmitCall(CallExpression call)
    {
        // Math.Func(args) → func(args)
        if (call.Target is MemberAccessExpression ma && ma.Object is IdentifierExpression obj && obj.Name == "Math")
        {
            if (MathBuiltins.TryGetValue(ma.Member, out string? glslFunc))
            {
                string args = string.Join(", ", call.Arguments.Select(a => EmitExpression(a)));
                return $"{glslFunc}({args})";
            }
        }

        // tex.Sample(uv) → texture(tex, uv)
        if (call.Target is MemberAccessExpression sampleAccess && sampleAccess.Member == "Sample")
        {
            string texName = EmitExpression(sampleAccess.Object);
            string args = string.Join(", ", call.Arguments.Select(a => EmitExpression(a)));
            return $"texture({texName}, {args})";
        }

        // Geometry: EmitVertex() / EndPrimitive() are direct GLSL calls
        // Compute: Barrier() → barrier(), MemoryBarrier() → memoryBarrier()
        if (call.Target is IdentifierExpression funcId)
        {
            if (funcId.Name == "Barrier" && _isCompute)
                return "barrier()";
            if (funcId.Name == "MemoryBarrier" && _isCompute)
                return "memoryBarrier()";
        }

        // Regular function call
        string callTarget = EmitExpression(call.Target);
        string callArgs = string.Join(", ", call.Arguments.Select(a => EmitExpression(a)));
        return $"{callTarget}({callArgs})";
    }

    private string EmitMemberAccess(MemberAccessExpression ma)
    {
        // input.field → a_field in the vertex stage (vertex attributes) and
        // v_field in the fragment stage (varyings from the vertex shader).
        if (!_isGeometry && _inputStruct is not null && _inputParam is not null
            && ma.Object is IdentifierExpression id && id.Name == _inputParam)
        {
            var field = _inputStruct.Fields.FirstOrDefault(f => f.Name == ma.Member);
            if (field is not null && !HasAttribute(field.Attributes, "Position"))
                return _isVertex ? $"a_{ma.Member}" : $"v_{ma.Member}";
        }

        // Geometry: vertices[i].field → gs_in[i].field or gl_in[i].gl_Position
        if (_isGeometry && _inputStruct is not null && _inputParam is not null
            && ma.Object is IndexExpression idx
            && idx.Object is IdentifierExpression arrayId && arrayId.Name == _inputParam)
        {
            string index = EmitExpression(idx.Index);
            var field = _inputStruct.Fields.FirstOrDefault(f => f.Name == ma.Member);
            if (field is not null && HasAttribute(field.Attributes, "Position"))
                return $"gl_in[{index}].gl_Position";
            return $"gs_in[{index}].{ma.Member}";
        }

        // Swizzle or regular member access. Member is escaped to match struct-field
        // declarations (a struct field named `output` is declared as `_ss_output`).
        // Swizzle component names (xyzw/rgba/stpq) are never reserved, so this is safe.
        return $"{EmitExpression(ma.Object)}.{EscapeId(ma.Member)}";
    }

    private string EmitIndexExpression(IndexExpression idx)
    {
        return $"{EmitExpression(idx.Object)}[{EmitExpression(idx.Index)}]";
    }

    // ─── Helpers ─────────────────────────────────────────────

    private static string GetReturnVarName(Expression expr)
    {
        if (expr is IdentifierExpression id)
            return EscapeId(id.Name);
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

    private string EmitArraySuffix(TypeSyntax type)
    {
        if (!type.IsArray)
            return "";
        if (type.ArraySize is not null)
            return $"[{EmitExpression(type.ArraySize)}]";
        return "[]";
    }

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
        "sampler2DArray" => "sampler2DArray",
        "sampler3D" => "sampler3D",
        "samplerCube" => "samplerCube",
        _ => name,
    };
}
