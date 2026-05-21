using System.Globalization;
using System.Text;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.CodeGen;

/// <summary>
/// Generates HLSL (Shader Model 5.0 compatible) source from a SpectraShade AST.
/// Emits identical source for D3D11 and D3D12 — the runtime picks the compiler
/// (FXC for SM 5.0, DXC for SM 5.1+/DXIL) based on backend.
/// </summary>
public sealed class HlslGenerator : ICodeGenerator
{
    private readonly GraphicsBackend _backend;

    public GraphicsBackend Backend => _backend;
    public ShaderDataFormat OutputFormat => ShaderDataFormat.SourceText;

    private List<StructDeclaration> _structs = new();
    private List<CBufferDeclaration> _cbuffers = new();
    private List<SamplerDeclaration> _samplers = new();
    private List<FunctionDeclaration> _helpers = new();

    private bool _isVertex;
    private bool _isFragment;
    private bool _isGeometry;
    private bool _isCompute;

    // Geometry-stage context. Set when emitting a [Geometry] function body.
    private StructDeclaration? _geomOutputStruct;
    private string? _geomPositionField;
    private HashSet<string>? _geomOutputFieldNames;
    private const string GeomOutLocal = "_out";
    private const string GeomStreamParam = "_stream";

    // Name → SpectraShade type name. Populated with globals (cbuffer fields, samplers),
    // function parameters, and local var declarations. Used to decide when a binary
    // '*' needs lowering to mul() for matrix multiplication.
    private readonly Dictionary<string, string> _env = new();

    public HlslGenerator(GraphicsBackend backend)
    {
        if (backend is not (GraphicsBackend.D3D11 or GraphicsBackend.D3D12))
            throw new ArgumentException("HLSL generator requires D3D11 or D3D12 backend");
        _backend = backend;
    }

    public PipelineBlob Generate(CompilationUnit unit)
    {
        var shader = unit.Shader;

        var functions = shader.Members.OfType<FunctionDeclaration>().ToList();
        _cbuffers = shader.Members.OfType<CBufferDeclaration>().ToList();
        _samplers = shader.Members.OfType<SamplerDeclaration>().ToList();

        var vertexFunc = functions.FirstOrDefault(f => f.HasAttribute("Vertex"));
        var fragmentFunc = functions.FirstOrDefault(f => f.HasAttribute("Fragment"));
        var geometryFunc = functions.FirstOrDefault(f => f.HasAttribute("Geometry"));
        var computeFunc = functions.FirstOrDefault(f => f.HasAttribute("Compute"));
        byte[]? geometryData = null;
        _helpers = functions.Where(f =>
            !f.HasAttribute("Vertex") && !f.HasAttribute("Fragment")
            && !f.HasAttribute("Geometry") && !f.HasAttribute("Compute")).ToList();

        _structs = new List<StructDeclaration>(unit.Structs);
        _structs.AddRange(shader.Members.OfType<StructDeclaration>());

        byte[]? vertexData = null;
        byte[]? fragmentData = null;
        byte[]? computeData = null;
        var stages = ShaderStageFlags.None;

        if (vertexFunc is not null)
        {
            vertexData = Encoding.UTF8.GetBytes(EmitVertexStage(vertexFunc));
            stages |= ShaderStageFlags.Vertex;
        }
        if (fragmentFunc is not null)
        {
            fragmentData = Encoding.UTF8.GetBytes(EmitFragmentStage(fragmentFunc));
            stages |= ShaderStageFlags.Fragment;
        }
        if (computeFunc is not null)
        {
            computeData = Encoding.UTF8.GetBytes(EmitComputeStage(computeFunc));
            stages |= ShaderStageFlags.Compute;
        }

        if (geometryFunc is not null)
        {
            geometryData = Encoding.UTF8.GetBytes(EmitGeometryStage(geometryFunc, vertexFunc));
            stages |= ShaderStageFlags.Geometry;
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

    // ─── Stage emission ──────────────────────────────────────

    private string EmitVertexStage(FunctionDeclaration func)
    {
        ResetEnv();
        SetStage(isVertex: true);
        var sb = new StringBuilder();

        var inputStruct = func.Parameters.Count > 0 ? FindStruct(func.Parameters[0].Type.Name) : null;
        var outputStruct = FindStruct(func.ReturnType.Name);

        EmitInterfaceStructs(sb, inputStruct, outputStruct, InterfaceKind.VertexInput, InterfaceKind.Varying);
        EmitCBuffers(sb);
        EmitSamplers(sb);
        EmitHelpers(sb);
        EmitEntryPoint(sb, func, entrySignatureSemantic: null);
        return sb.ToString();
    }

    private string EmitFragmentStage(FunctionDeclaration func)
    {
        ResetEnv();
        SetStage(isFragment: true);
        var sb = new StringBuilder();

        var inputStruct = func.Parameters.Count > 0 ? FindStruct(func.Parameters[0].Type.Name) : null;
        var outputStruct = FindStruct(func.ReturnType.Name);

        EmitInterfaceStructs(sb, inputStruct, outputStruct, InterfaceKind.Varying, InterfaceKind.FragmentOutput);
        EmitCBuffers(sb);
        EmitSamplers(sb);
        EmitHelpers(sb);

        if (func.HasAttribute("EarlyDepthStencil"))
            sb.AppendLine("[earlydepthstencil]");

        string? entrySem = null;
        if (outputStruct is null)
            entrySem = $": SV_Target{GetIntArg(func.Attributes, "Target", 0)}";
        EmitEntryPoint(sb, func, entrySem);
        return sb.ToString();
    }

    private string EmitGeometryStage(FunctionDeclaration func, FunctionDeclaration? vertexFunc)
    {
        ResetEnv();
        SetStage(isGeometry: true);
        var sb = new StringBuilder();

        // Input: first parameter must be T[] where T is the vertex output struct.
        var inputParam = func.Parameters.Count > 0 ? func.Parameters[0] : null;
        var inputStruct = inputParam is not null ? FindStruct(inputParam.Type.Name) : null;

        // Output: the vertex stage return struct (has [Position]). The geometry
        // stream outputs this same struct so it reaches the rasterizer/fragment.
        var outputStruct = vertexFunc is not null ? FindStruct(vertexFunc.ReturnType.Name) : inputStruct;
        _geomOutputStruct = outputStruct;
        _geomPositionField = outputStruct?.Fields
            .FirstOrDefault(f => HasAttr(f.Attributes, "Position"))?.Name;
        _geomOutputFieldNames = outputStruct is not null
            ? new HashSet<string>(outputStruct.Fields.Select(f => f.Name), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        // Emit structs with varying semantics on the output (shared with the vertex stage).
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        if (inputStruct is not null)
        {
            EmitStruct(sb, inputStruct, InterfaceKind.Varying);
            emitted.Add(inputStruct.Name);
        }
        if (outputStruct is not null && !emitted.Contains(outputStruct.Name))
        {
            EmitStruct(sb, outputStruct, InterfaceKind.Varying);
            emitted.Add(outputStruct.Name);
        }
        foreach (var s in _structs)
        {
            if (emitted.Contains(s.Name)) continue;
            EmitStruct(sb, s, InterfaceKind.None);
            emitted.Add(s.Name);
        }

        EmitCBuffers(sb);
        EmitSamplers(sb);
        EmitHelpers(sb);

        int maxVerts = GetIntArg(func.Attributes, "MaxVertexCount", 3);
        string inPrim = GeomInputPrimitive(func.Attributes, out int inArrSize);
        string streamType = GeomOutputStreamType(func.Attributes);
        string outTypeName = outputStruct?.Name ?? "float4";
        string inTypeName = inputStruct?.Name ?? "float4";
        string inName = inputParam?.Name ?? "vertices";

        PopulateGlobalEnv();
        _env[inName] = inTypeName;
        _env[GeomOutLocal] = outTypeName;

        sb.AppendLine($"[maxvertexcount({maxVerts})]");
        sb.AppendLine($"void main({inPrim} {inTypeName} {inName}[{inArrSize}], uint PrimitiveID : SV_PrimitiveID, inout {streamType}<{outTypeName}> {GeomStreamParam})");
        sb.AppendLine("{");
        sb.AppendLine($"    {outTypeName} {GeomOutLocal} = ({outTypeName})0;");
        foreach (var stmt in func.Body.Statements)
            EmitStatement(sb, stmt, 1);
        sb.AppendLine("}");

        _geomOutputStruct = null;
        _geomPositionField = null;
        _geomOutputFieldNames = null;
        return sb.ToString();
    }

    private static string GeomInputPrimitive(IReadOnlyList<AttributeSyntax> attrs, out int arraySize)
    {
        var attr = GetAttribute(attrs, "InputPrimitive");
        string name = "Triangles";
        if (attr is not null && attr.Arguments.Count > 0 && attr.Arguments[0] is IdentifierExpression id)
            name = id.Name;
        switch (name)
        {
            case "Points": arraySize = 1; return "point";
            case "Lines": arraySize = 2; return "line";
            case "LinesAdjacency": arraySize = 4; return "lineadj";
            case "TrianglesAdjacency": arraySize = 6; return "triangleadj";
            default: arraySize = 3; return "triangle";
        }
    }

    private static string GeomOutputStreamType(IReadOnlyList<AttributeSyntax> attrs)
    {
        var attr = GetAttribute(attrs, "OutputPrimitive");
        string name = "TriangleStrip";
        if (attr is not null && attr.Arguments.Count > 0 && attr.Arguments[0] is IdentifierExpression id)
            name = id.Name;
        return name switch
        {
            "Points" => "PointStream",
            "LineStrip" => "LineStream",
            _ => "TriangleStream",
        };
    }

    private string EmitComputeStage(FunctionDeclaration func)
    {
        ResetEnv();
        SetStage(isCompute: true);
        var sb = new StringBuilder();

        EmitNonInterfaceStructs(sb);
        EmitCBuffers(sb);
        EmitSamplers(sb);
        EmitHelpers(sb);

        int x = GetIntArg(func.Attributes, "NumThreads", 1, 0);
        int y = GetIntArg(func.Attributes, "NumThreads", 1, 1);
        int z = GetIntArg(func.Attributes, "NumThreads", 1, 2);

        PopulateGlobalEnv();
        foreach (var p in func.Parameters)
            _env[p.Name] = p.Type.Name;

        sb.AppendLine($"[numthreads({x}, {y}, {z})]");
        sb.Append("void main(");
        sb.Append("uint3 GlobalInvocationID : SV_DispatchThreadID, ");
        sb.Append("uint3 LocalInvocationID : SV_GroupThreadID, ");
        sb.Append("uint3 WorkGroupID : SV_GroupID, ");
        sb.Append("uint LocalInvocationIndex : SV_GroupIndex");
        sb.AppendLine(")");
        sb.AppendLine("{");
        _env["GlobalInvocationID"] = "uvec3";
        _env["LocalInvocationID"] = "uvec3";
        _env["WorkGroupID"] = "uvec3";
        _env["LocalInvocationIndex"] = "uint";
        foreach (var stmt in func.Body.Statements)
            EmitStatement(sb, stmt, 1);
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ─── Struct / resource emitters ──────────────────────────

    private void EmitInterfaceStructs(StringBuilder sb, StructDeclaration? inputStruct, StructDeclaration? outputStruct,
        InterfaceKind inputKind, InterfaceKind outputKind)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        if (inputStruct is not null)
        {
            EmitStruct(sb, inputStruct, inputKind);
            emitted.Add(inputStruct.Name);
        }
        if (outputStruct is not null && !emitted.Contains(outputStruct.Name))
        {
            EmitStruct(sb, outputStruct, outputKind);
            emitted.Add(outputStruct.Name);
        }
        foreach (var s in _structs)
        {
            if (emitted.Contains(s.Name)) continue;
            EmitStruct(sb, s, InterfaceKind.None);
            emitted.Add(s.Name);
        }
    }

    private void EmitNonInterfaceStructs(StringBuilder sb)
    {
        foreach (var s in _structs)
            EmitStruct(sb, s, InterfaceKind.None);
    }

    private void EmitStruct(StringBuilder sb, StructDeclaration s, InterfaceKind kind)
    {
        sb.AppendLine($"struct {s.Name}");
        sb.AppendLine("{");
        int varyingIdx = 0;
        int targetFallback = 0;
        for (int i = 0; i < s.Fields.Count; i++)
        {
            var f = s.Fields[i];
            string type = HlslType(f.Type.Name);
            string arr = EmitArraySuffix(f.Type);
            string semantic = kind switch
            {
                InterfaceKind.VertexInput
                    => $" : TEXCOORD{GetIntArg(f.Attributes, "Location", i)}",
                InterfaceKind.Varying when HasAttr(f.Attributes, "Position")
                    => " : SV_Position",
                InterfaceKind.Varying
                    => $" : TEXCOORD{varyingIdx++}",
                InterfaceKind.FragmentOutput
                    => $" : SV_Target{GetIntArg(f.Attributes, "Target", targetFallback++)}",
                _ => "",
            };
            sb.AppendLine($"    {type} {f.Name}{arr}{semantic};");
        }
        sb.AppendLine("};");
        sb.AppendLine();
    }

    private void EmitCBuffers(StringBuilder sb)
    {
        foreach (var cb in _cbuffers)
        {
            int slot = GetIntArg(cb.Attributes, "Binding", 0);
            sb.AppendLine($"cbuffer {cb.Name} : register(b{slot})");
            sb.AppendLine("{");
            foreach (var f in cb.Fields)
                sb.AppendLine($"    {HlslType(f.Type.Name)} {f.Name}{EmitArraySuffix(f.Type)};");
            sb.AppendLine("};");
            sb.AppendLine();
        }
    }

    private void EmitSamplers(StringBuilder sb)
    {
        foreach (var s in _samplers)
        {
            int slot = GetIntArg(s.Attributes, "Binding", 0);
            string texType = HlslTextureType(s.Type.Name);
            sb.AppendLine($"{texType} {s.Name}{EmitArraySuffix(s.Type)} : register(t{slot});");
            sb.AppendLine($"SamplerState {s.Name}_sampler{EmitArraySuffix(s.Type)} : register(s{slot});");
        }
        if (_samplers.Count > 0) sb.AppendLine();
    }

    private void EmitHelpers(StringBuilder sb)
    {
        foreach (var h in _helpers)
            EmitHelper(sb, h);
    }

    private void EmitHelper(StringBuilder sb, FunctionDeclaration func)
    {
        string ret = HlslType(func.ReturnType.Name);
        string parms = string.Join(", ", func.Parameters.Select(p => $"{HlslType(p.Type.Name)} {p.Name}"));
        sb.AppendLine($"{ret} {func.Name}({parms})");
        sb.AppendLine("{");

        var saved = SnapshotEnv();
        PopulateGlobalEnv();
        foreach (var p in func.Parameters)
            _env[p.Name] = p.Type.Name;
        foreach (var stmt in func.Body.Statements)
            EmitStatement(sb, stmt, 1);
        RestoreEnv(saved);

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private void EmitEntryPoint(StringBuilder sb, FunctionDeclaration func, string? entrySignatureSemantic)
    {
        PopulateGlobalEnv();
        foreach (var p in func.Parameters)
            _env[p.Name] = p.Type.Name;

        string ret = HlslType(func.ReturnType.Name);
        string parms = string.Join(", ", func.Parameters.Select(p => $"{HlslType(p.Type.Name)} {p.Name}"));
        var sig = $"{ret} main({parms})";
        if (entrySignatureSemantic is not null)
            sig += " " + entrySignatureSemantic;
        sb.AppendLine(sig);
        sb.AppendLine("{");
        foreach (var stmt in func.Body.Statements)
            EmitStatement(sb, stmt, 1);
        sb.AppendLine("}");
    }

    // ─── Statements ──────────────────────────────────────────

    private void EmitStatement(StringBuilder sb, SyntaxNode node, int indent)
    {
        string pad = new(' ', indent * 4);
        switch (node)
        {
            case VariableDeclaration v:
                EmitVarDecl(sb, v, pad);
                break;

            case ReturnStatement r:
                sb.AppendLine(r.Value is null
                    ? $"{pad}return;"
                    : $"{pad}return {EmitExpression(r.Value)};");
                break;

            case ExpressionStatement e:
                if (_isGeometry && TryEmitGeometryStmt(sb, e.Expression, pad))
                    break;
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
                    string ftype = fv.Type.Name == "var" ? "int" : HlslType(fv.Type.Name);
                    _env[fv.Name] = fv.Type.Name == "var" ? "int" : fv.Type.Name;
                    string fi = fv.Initializer is not null ? $" = {EmitExpression(fv.Initializer)}" : "";
                    sb.Append($"{ftype} {fv.Name}{fi}");
                }
                else if (f.Initializer is ExpressionStatement fes)
                {
                    sb.Append(EmitExpression(fes.Expression));
                }
                sb.Append("; ");
                if (f.Condition is not null) sb.Append(EmitExpression(f.Condition));
                sb.Append("; ");
                if (f.Increment is not null) sb.Append(EmitExpression(f.Increment));
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

    private bool TryEmitGeometryStmt(StringBuilder sb, Expression expr, string pad)
    {
        // Assignment rewrites: `Position = X;` → `_out.<posField> = X;`
        // and bare `<fieldName> = X;` where fieldName is a geometry output field.
        if (expr is AssignmentExpression a && a.Target is IdentifierExpression lhs)
        {
            string? mapped = null;
            if (lhs.Name == "Position" && _geomPositionField is not null)
                mapped = _geomPositionField;
            else if (_geomOutputFieldNames is not null && _geomOutputFieldNames.Contains(lhs.Name))
                mapped = lhs.Name;

            if (mapped is not null)
            {
                sb.AppendLine($"{pad}{GeomOutLocal}.{mapped} {MapOperator(a.Operator)} {EmitExpression(a.Value)};");
                return true;
            }
        }
        return false;
    }

    private void EmitVarDecl(StringBuilder sb, VariableDeclaration v, string pad)
    {
        string varType;
        string specType;
        if (v.Type.Name == "var")
        {
            specType = InferType(v.Initializer!) ?? "float";
            varType = HlslType(specType);
        }
        else
        {
            specType = v.Type.Name;
            varType = HlslType(specType);
        }
        _env[v.Name] = specType;

        string arr = EmitArraySuffix(v.Type);
        string init = v.Initializer is not null ? $" = {EmitExpression(v.Initializer)}" : "";
        sb.AppendLine($"{pad}{varType} {v.Name}{arr}{init};");
    }

    private void EmitBlock(StringBuilder sb, BlockStatement block, int indent)
    {
        string pad = new(' ', indent * 4);
        sb.AppendLine($"{pad}{{");
        foreach (var stmt in block.Statements)
            EmitStatement(sb, stmt, indent + 1);
        sb.AppendLine($"{pad}}}");
    }

    private void EmitStatementOrBlock(StringBuilder sb, Statement stmt, int indent)
    {
        if (stmt is BlockStatement block) EmitBlock(sb, block, indent);
        else EmitStatement(sb, stmt, indent + 1);
    }

    // ─── Expressions ─────────────────────────────────────────

    private string EmitExpression(Expression expr)
    {
        switch (expr)
        {
            case IntLiteralExpression i:
                return i.Value.ToString(CultureInfo.InvariantCulture);

            case FloatLiteralExpression f:
                return FormatFloat(f.Value);

            case BoolLiteralExpression b:
                return b.Value ? "true" : "false";

            case IdentifierExpression id:
                return id.Name;

            case BinaryExpression bin:
                return EmitBinary(bin);

            case UnaryExpression un:
                return $"({MapOperator(un.Operator)}{EmitExpression(un.Operand)})";

            case ConstructorExpression ctor:
            {
                // matN(singleMatrix) → (floatNxN)matrix. HLSL has no single-matrix
                // constructor; the GLSL idiom mat3(uModel) for upper-left
                // extraction has to lower to an explicit truncating cast.
                if (IsMatrixType(ctor.Type.Name) && ctor.Arguments.Count == 1
                    && IsMatrixType(InferType(ctor.Arguments[0])))
                {
                    return $"(({HlslType(ctor.Type.Name)}){EmitExpression(ctor.Arguments[0])})";
                }

                string args = string.Join(", ", ctor.Arguments.Select(a => EmitExpression(a)));
                return $"{HlslType(ctor.Type.Name)}({args})";
            }

            case NewExpression ne:
                // HLSL has no 'new'. A default-initialized struct is spelled '(StructName)0'.
                return $"({ne.Type.Name})0";

            case CallExpression call:
                return EmitCall(call);

            case MemberAccessExpression ma:
                return $"{EmitExpression(ma.Object)}.{ma.Member}";

            case IndexExpression idx:
                return $"{EmitExpression(idx.Object)}[{EmitExpression(idx.Index)}]";

            case AssignmentExpression a:
                return $"{EmitExpression(a.Target)} {MapOperator(a.Operator)} {EmitExpression(a.Value)}";

            default:
                return "/* unknown */";
        }
    }

    private string EmitBinary(BinaryExpression bin)
    {
        if (bin.Operator == TokenKind.Star)
        {
            var lt = InferType(bin.Left);
            var rt = InferType(bin.Right);
            // HLSL '*' is componentwise on matrices/vectors — mul() is required for actual
            // linear-algebra multiply whenever a matrix is involved.
            if (IsMatrixType(lt) || IsMatrixType(rt))
                return $"mul({EmitExpression(bin.Left)}, {EmitExpression(bin.Right)})";
        }
        return $"({EmitExpression(bin.Left)} {MapOperator(bin.Operator)} {EmitExpression(bin.Right)})";
    }

    private string EmitCall(CallExpression call)
    {
        // Math.X(...) → hlslName(...)
        if (call.Target is MemberAccessExpression ma && ma.Object is IdentifierExpression math
            && math.Name == "Math" && MathBuiltins.TryGetValue(ma.Member, out string? hlslName))
        {
            string args = string.Join(", ", call.Arguments.Select(a => EmitExpression(a)));
            return $"{hlslName}({args})";
        }

        // tex.Sample(uv) → tex.Sample(tex_sampler, uv)
        if (call.Target is MemberAccessExpression sa && sa.Member == "Sample")
        {
            string samplerRef = EmitSamplerRef(sa.Object);
            string args = string.Join(", ", call.Arguments.Select(a => EmitExpression(a)));
            return $"{EmitExpression(sa.Object)}.Sample({samplerRef}, {args})";
        }

        // Compute-stage sync primitives
        if (_isCompute && call.Target is IdentifierExpression fid)
        {
            if (fid.Name == "Barrier") return "GroupMemoryBarrierWithGroupSync()";
            if (fid.Name == "MemoryBarrier") return "GroupMemoryBarrier()";
        }

        // Geometry-stage stream primitives
        if (_isGeometry && call.Target is IdentifierExpression gid)
        {
            if (gid.Name == "EmitVertex") return $"{GeomStreamParam}.Append({GeomOutLocal})";
            if (gid.Name == "EndPrimitive") return $"{GeomStreamParam}.RestartStrip()";
        }

        string target = EmitExpression(call.Target);
        string cargs = string.Join(", ", call.Arguments.Select(a => EmitExpression(a)));
        return $"{target}({cargs})";
    }

    private string EmitSamplerRef(Expression obj)
    {
        // Plain sampler: `tex` → `tex_sampler`
        if (obj is IdentifierExpression id)
            return $"{id.Name}_sampler";
        // Array sampler: `textures[i]` → `textures_sampler[i]`
        if (obj is IndexExpression idx && idx.Object is IdentifierExpression arrId)
            return $"{arrId.Name}_sampler[{EmitExpression(idx.Index)}]";
        return "/* unresolved sampler ref */";
    }

    // ─── Type inference (minimal, for mul() lowering) ────────

    private string? InferType(Expression expr)
    {
        switch (expr)
        {
            case IdentifierExpression id:
                return _env.TryGetValue(id.Name, out var t) ? t : null;

            case ConstructorExpression c:
                return c.Type.Name;

            case NewExpression n:
                return n.Type.Name;

            case IntLiteralExpression:
                return "int";

            case FloatLiteralExpression:
                return "float";

            case BoolLiteralExpression:
                return "bool";

            case MemberAccessExpression ma:
                return InferMember(ma);

            case BinaryExpression b:
            {
                var lt = InferType(b.Left);
                var rt = InferType(b.Right);
                if (IsMatrixType(lt) && IsVectorType(rt)) return rt;
                if (IsVectorType(lt) && IsMatrixType(rt)) return lt;
                return lt ?? rt;
            }

            case CallExpression call:
                return InferCall(call);

            case IndexExpression ix:
                return InferType(ix.Object);

            case UnaryExpression un:
                return InferType(un.Operand);

            case AssignmentExpression a:
                return InferType(a.Target);
        }
        return null;
    }

    private string? InferMember(MemberAccessExpression ma)
    {
        var objType = InferType(ma.Object);
        if (objType is null) return null;

        if (IsVectorType(objType))
        {
            int dim = ma.Member.Length;
            string scalar = VectorScalar(objType);
            return dim switch
            {
                1 => scalar,
                2 => ScalarToVec(scalar, 2),
                3 => ScalarToVec(scalar, 3),
                4 => ScalarToVec(scalar, 4),
                _ => null,
            };
        }

        var s = FindStruct(objType);
        if (s is not null)
        {
            var field = s.Fields.FirstOrDefault(f => f.Name == ma.Member);
            return field?.Type.Name;
        }
        return null;
    }

    private string? InferCall(CallExpression call)
    {
        if (call.Target is MemberAccessExpression ma
            && ma.Object is IdentifierExpression o && o.Name == "Math")
        {
            // Math.* mostly return the same type as the first argument.
            return call.Arguments.Count > 0 ? InferType(call.Arguments[0]) : null;
        }
        if (call.Target is MemberAccessExpression sa && sa.Member == "Sample")
            return "vec4";
        if (call.Target is IdentifierExpression fid)
        {
            var fn = _helpers.FirstOrDefault(f => f.Name == fid.Name);
            return fn?.ReturnType.Name;
        }
        return null;
    }

    // ─── Helpers ─────────────────────────────────────────────

    private enum InterfaceKind { None, VertexInput, Varying, FragmentOutput }

    private StructDeclaration? FindStruct(string name)
        => _structs.FirstOrDefault(s => s.Name == name);

    private static AttributeSyntax? GetAttribute(IReadOnlyList<AttributeSyntax> attrs, string name)
        => attrs.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool HasAttr(IReadOnlyList<AttributeSyntax> attrs, string name)
        => attrs.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private static int GetIntArg(IReadOnlyList<AttributeSyntax> attrs, string name, int fallback, int argIndex = 0)
    {
        var attr = GetAttribute(attrs, name);
        if (attr is null || attr.Arguments.Count <= argIndex) return fallback;
        if (attr.Arguments[argIndex] is IntLiteralExpression i) return i.Value;
        return fallback;
    }

    private string EmitArraySuffix(TypeSyntax type)
    {
        if (!type.IsArray) return "";
        return type.ArraySize is not null
            ? $"[{EmitExpression(type.ArraySize)}]"
            : "[]";
    }

    private void PopulateGlobalEnv()
    {
        foreach (var cb in _cbuffers)
            foreach (var f in cb.Fields)
                _env[f.Name] = f.Type.Name;
        foreach (var s in _samplers)
            _env[s.Name] = s.Type.Name;
    }

    private void ResetEnv() => _env.Clear();
    private Dictionary<string, string> SnapshotEnv() => new(_env);
    private void RestoreEnv(Dictionary<string, string> snapshot)
    {
        _env.Clear();
        foreach (var kv in snapshot) _env[kv.Key] = kv.Value;
    }

    private void SetStage(bool isVertex = false, bool isFragment = false, bool isGeometry = false, bool isCompute = false)
    {
        _isVertex = isVertex;
        _isFragment = isFragment;
        _isGeometry = isGeometry;
        _isCompute = isCompute;
    }

    private static string FormatFloat(float v)
    {
        string s = v.ToString("R", CultureInfo.InvariantCulture);
        // Ensure it parses as a float literal and not an int in HLSL.
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) s += ".0";
        return s;
    }

    // ─── Type / builtin tables ───────────────────────────────

    private static bool IsMatrixType(string? t) => t is "mat2" or "mat3" or "mat4"
        or "float2x2" or "float3x3" or "float4x4";

    private static bool IsVectorType(string? t) => t is
        "vec2" or "vec3" or "vec4"
        or "ivec2" or "ivec3" or "ivec4"
        or "uvec2" or "uvec3" or "uvec4"
        or "bvec2" or "bvec3" or "bvec4";

    private static string VectorScalar(string t) => t[0] switch
    {
        'i' => "int",
        'u' => "uint",
        'b' => "bool",
        _ => "float",
    };

    private static string ScalarToVec(string scalar, int dim)
        => scalar switch
        {
            "int" => $"ivec{dim}",
            "uint" => $"uvec{dim}",
            "bool" => $"bvec{dim}",
            _ => $"vec{dim}",
        };

    private static string HlslType(string name) => name switch
    {
        "void" => "void",
        "bool" => "bool",
        "int" => "int",
        "uint" => "uint",
        "float" => "float",
        "double" => "double",
        "vec2" => "float2",
        "vec3" => "float3",
        "vec4" => "float4",
        "ivec2" => "int2",
        "ivec3" => "int3",
        "ivec4" => "int4",
        "uvec2" => "uint2",
        "uvec3" => "uint3",
        "uvec4" => "uint4",
        "bvec2" => "bool2",
        "bvec3" => "bool3",
        "bvec4" => "bool4",
        "mat2" => "float2x2",
        "mat3" => "float3x3",
        "mat4" => "float4x4",
        "sampler2D" => "Texture2D",
        "sampler2DArray" => "Texture2DArray",
        "sampler3D" => "Texture3D",
        "samplerCube" => "TextureCube",
        _ => name,
    };

    private static string HlslTextureType(string specType) => specType switch
    {
        "sampler2D" => "Texture2D",
        "sampler2DArray" => "Texture2DArray",
        "sampler3D" => "Texture3D",
        "samplerCube" => "TextureCube",
        _ => specType,
    };

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
        _ => "?",
    };

    private static readonly Dictionary<string, string> MathBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normalize"] = "normalize",
        ["Dot"] = "dot",
        ["Cross"] = "cross",
        ["Length"] = "length",
        ["Distance"] = "distance",
        ["Mix"] = "lerp",
        ["Lerp"] = "lerp",
        ["Clamp"] = "clamp",
        ["Min"] = "min",
        ["Max"] = "max",
        ["Abs"] = "abs",
        ["Floor"] = "floor",
        ["Ceil"] = "ceil",
        ["Fract"] = "frac",
        ["Mod"] = "fmod",
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
        ["Transpose"] = "transpose",
        ["Determinant"] = "determinant",
        // Note: HLSL has no built-in 'inverse'. Users must provide their own helper.
    };
}
