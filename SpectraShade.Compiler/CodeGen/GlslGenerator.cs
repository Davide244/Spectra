using System.Globalization;
using System.Text;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.CodeGen;

/// <summary>
/// Generates GLSL source from a SpectraShade AST. Stages target 330 core by
/// default; a stage's #version is raised only when the features it actually
/// emits require it (see <see cref="FinishStage"/>).
///
/// Transforms:
///   [Vertex] function params     → layout(location=N) in declarations + void main()
///   [Fragment] function params   → in declarations from vertex output struct + void main()
///   Position = expr              → gl_Position = expr
///   return result                → assigns stage outputs + bare return (at any nesting depth)
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

    // Geometry-stage output mapping (GLSL counterpart of HlslGenerator's
    // _geomOutputStruct context): bare output-struct field names assigned in a
    // geometry body become the loose g_* varyings; the [Position] field becomes
    // gl_Position. Set while emitting a [Geometry] body.
    private string? _geomPositionField;
    private HashSet<string>? _geomOutputFieldNames;

    // Fragment inputs read the vertex stage's v_* varyings — unless a geometry
    // stage sits in between, in which case they read its g_* outputs instead.
    private string _fragmentVaryingPrefix = "v_";

    // Stage-entry return lowering: GLSL entry points compile to void main(), so
    // every `return expr;` in a [Vertex]/[Fragment] body — top-level or nested
    // inside control flow — must assign the stage outputs and then emit a bare
    // return. Mode is None while emitting helper functions, whose returns stay
    // real returns.
    private enum StageReturnMode { None, Vertex, Fragment }
    private StageReturnMode _returnMode;
    private StructDeclaration? _stageReturnStruct;
    private int _returnTempCounter;

    // Minimum #version the current stage requires; see FinishStage.
    private int _minVersion;

    // Shared name → SpectraShade-type environment (see TypeInference), populated
    // with globals, parameters, and locals per stage. Used to resolve `var`
    // declarations to concrete GLSL types.
    private readonly TypeInference _types = new();

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

    // Generator-owned interface names for the stage currently being emitted:
    // the invented fragment output (fragColor) plus the active a_* attribute
    // and v_*/g_* varying names. A user local or parameter reusing one of
    // these would silently shadow the interface variable — the stage output
    // would never be written — so colliding user identifiers get the same
    // _ss_ escape as reserved words. Uniform/cbuffer names are deliberately
    // NOT tracked here: shadowing a uniform with a local is legitimate and
    // stays untouched. Populated at the top of each stage emitter (before
    // ANY emission) so declaration and reference sites escape identically.
    private readonly HashSet<string> _stageOwnedNames = new(StringComparer.Ordinal);

    private string EscapeId(string name)
        => GlslReservedWords.Contains(name) || _stageOwnedNames.Contains(name) ? "_ss_" + name : name;

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
        _types.Configure(allStructs, helperFunctions);

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
                EmitGeometryStage(geometryFunc, vertexFunc, cbuffers, samplers, helperFunctions, allStructs));
            stages |= ShaderStageFlags.Geometry;
        }

        if (fragmentFunc is not null)
        {
            fragmentData = Encoding.UTF8.GetBytes(
                EmitFragmentStage(fragmentFunc, geometryFunc is not null, cbuffers, samplers, helperFunctions, allStructs));
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
        _minVersion = 330;
        var sb = new StringBuilder();

        _types.Reset();
        _types.DeclareGlobals(cbuffers, samplers);

        // Vertex inputs: the input struct's fields become attribute declarations.
        StructDeclaration? inputStruct = null;
        string inputParamName = "input";
        if (func.Parameters.Count > 0)
        {
            inputStruct = FindStruct(func.Parameters[0].Type.Name, structs);
            inputParamName = func.Parameters[0].Name;
        }
        var returnStruct = FindStruct(func.ReturnType.Name, structs);

        // Register the stage-owned interface names (see EscapeId) before any
        // emission happens.
        _stageOwnedNames.Clear();
        if (inputStruct is not null)
            foreach (var field in inputStruct.Fields)
                _stageOwnedNames.Add($"a_{field.Name}");
        if (returnStruct is not null)
            foreach (var field in returnStruct.Fields)
                if (!HasAttribute(field.Attributes, "Position"))
                    _stageOwnedNames.Add($"v_{field.Name}");

        EmitStructs(sb, structs);

        if (inputStruct is not null)
        {
            for (int i = 0; i < inputStruct.Fields.Count; i++)
            {
                var field = inputStruct.Fields[i];
                // A [Location] without a literal argument falls back to the field
                // index (same recovery as HlslGenerator.GetIntArg) — the argument
                // list must never be indexed unguarded.
                string layout = GetAttribute(field.Attributes, "Location") is not null
                    ? $"layout(location = {GetIntArg(field.Attributes, "Location", i)}) "
                    : "";
                sb.AppendLine($"{layout}in {GlslType(field.Type.Name)} a_{field.Name};");
            }
            sb.AppendLine();
        }

        // Vertex outputs from return struct fields → out declarations
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

        return FinishStage(sb);
    }

    private string EmitFragmentStage(
        FunctionDeclaration func,
        bool hasGeometry,
        List<CBufferDeclaration> cbuffers,
        List<SamplerDeclaration> samplers,
        List<FunctionDeclaration> helpers,
        List<StructDeclaration> structs)
    {
        _minVersion = 330;
        var sb = new StringBuilder();

        _types.Reset();
        _types.DeclareGlobals(cbuffers, samplers);

        // When a geometry stage sits between vertex and fragment, the fragment
        // must consume the geometry stage's g_* outputs instead of the vertex
        // stage's v_* ones — GLSL links stages by matching varying names.
        _fragmentVaryingPrefix = hasGeometry ? "g_" : "v_";

        // Fragment inputs: matching upstream (vertex or geometry) outputs
        StructDeclaration? inputStruct = null;
        string inputParamName = "input";
        if (func.Parameters.Count > 0)
        {
            inputStruct = FindStruct(func.Parameters[0].Type.Name, structs);
            inputParamName = func.Parameters[0].Name;
        }
        var returnStruct = FindStruct(func.ReturnType.Name, structs);

        // Register the stage-owned interface names (see EscapeId) before any
        // emission happens. A non-struct return owns the invented fragColor
        // output; struct-return target names are user-authored field names
        // and already escape consistently through EscapeId.
        _stageOwnedNames.Clear();
        if (inputStruct is not null)
            foreach (var field in inputStruct.Fields)
                if (!HasAttribute(field.Attributes, "Position"))
                    _stageOwnedNames.Add($"{_fragmentVaryingPrefix}{field.Name}");
        if (returnStruct is null)
            _stageOwnedNames.Add("fragColor");

        EmitStructs(sb, structs);

        if (inputStruct is not null)
        {
            foreach (var field in inputStruct.Fields)
            {
                if (HasAttribute(field.Attributes, "Position"))
                    continue;
                sb.AppendLine($"in {GlslType(field.Type.Name)} {_fragmentVaryingPrefix}{field.Name};");
            }
            sb.AppendLine();
        }

        // Depth testing hints. Both fragment-depth layout qualifiers were
        // introduced in GLSL 4.20 (layout(depth_*) is otherwise only available
        // through GL_ARB_conservative_depth), so using them raises the stage
        // version above the 330 baseline.
        if (func.HasAttribute("EarlyDepthStencil"))
        {
            Require(420);
            sb.AppendLine("layout(early_fragment_tests) in;");
        }

        var depthWriteAttr = GetAttribute(func.Attributes, "DepthWrite");
        if (depthWriteAttr is not null)
        {
            Require(420);
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
        if (returnStruct is not null)
        {
            for (int i = 0; i < returnStruct.Fields.Count; i++)
            {
                var field = returnStruct.Fields[i];
                // A [Target] without a literal argument falls back to the field
                // index (same recovery as HlslGenerator.GetIntArg) — the argument
                // list must never be indexed unguarded.
                string layout = GetAttribute(field.Attributes, "Target") is not null
                    ? $"layout(location = {GetIntArg(field.Attributes, "Target", i)}) "
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

        return FinishStage(sb);
    }

    private string EmitGeometryStage(
        FunctionDeclaration func,
        FunctionDeclaration? vertexFunc,
        List<CBufferDeclaration> cbuffers,
        List<SamplerDeclaration> samplers,
        List<FunctionDeclaration> helpers,
        List<StructDeclaration> structs)
    {
        _minVersion = 330;
        var sb = new StringBuilder();

        _types.Reset();
        _types.DeclareGlobals(cbuffers, samplers);

        // Input primitive layout
        string inputPrimitive = "triangles";
        var inputPrimAttr = GetAttribute(func.Attributes, "InputPrimitive");
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
        var outputPrimAttr = GetAttribute(func.Attributes, "OutputPrimitive");
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

        string maxVerts = GetIntArg(func.Attributes, "MaxVertexCount", 3).ToString(CultureInfo.InvariantCulture);
        sb.AppendLine($"layout({outputPrimitive}, max_vertices = {maxVerts}) out;");
        sb.AppendLine();

        // Geometry inputs: loose per-vertex arrays. GLSL links separately
        // compiled stages by matching varying names, so these must carry the
        // exact v_* names the vertex stage declares — an interface block on one
        // side and loose varyings on the other never link. For the same reason
        // the vertex stage's return struct is preferred over the declared
        // parameter type (they only differ in malformed shaders).
        var declaredInput = func.Parameters.Count > 0 ? FindStruct(func.Parameters[0].Type.Name, structs) : null;
        var vertexOutput = vertexFunc is not null ? FindStruct(vertexFunc.ReturnType.Name, structs) : null;
        var inputStruct = declaredInput is not null ? (vertexOutput ?? declaredInput) : null;
        string inputParamName = func.Parameters.Count > 0 ? func.Parameters[0].Name : "vertices";

        // Geometry outputs: loose g_* varyings from the vertex return struct
        // (the per-vertex stream type, mirroring HlslGenerator). The fragment
        // stage consumes the g_* names whenever a geometry stage is present.
        var outputStruct = vertexOutput ?? inputStruct;

        // Register the stage-owned interface names (see EscapeId) before any
        // emission happens.
        _stageOwnedNames.Clear();
        if (inputStruct is not null)
            foreach (var field in inputStruct.Fields)
                if (!HasAttribute(field.Attributes, "Position"))
                    _stageOwnedNames.Add($"v_{field.Name}");
        if (outputStruct is not null)
            foreach (var field in outputStruct.Fields)
                if (!HasAttribute(field.Attributes, "Position"))
                    _stageOwnedNames.Add($"g_{field.Name}");

        EmitStructs(sb, structs);

        if (inputStruct is not null)
        {
            foreach (var field in inputStruct.Fields)
            {
                if (HasAttribute(field.Attributes, "Position"))
                    continue; // read through the gl_in[i].gl_Position built-in instead
                sb.AppendLine($"in {GlslType(field.Type.Name)} v_{field.Name}[];");
            }
            sb.AppendLine();
        }

        if (outputStruct is not null)
        {
            foreach (var field in outputStruct.Fields)
            {
                if (HasAttribute(field.Attributes, "Position"))
                    continue; // Position is gl_Position, not a user varying
                sb.AppendLine($"out {GlslType(field.Type.Name)} g_{field.Name};");
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

        return FinishStage(sb);
    }

    private string EmitComputeStage(
        FunctionDeclaration func,
        List<CBufferDeclaration> cbuffers,
        List<SamplerDeclaration> samplers,
        List<FunctionDeclaration> helpers,
        List<StructDeclaration> structs)
    {
        // Compute shaders don't exist before GLSL 4.30.
        _minVersion = 430;
        var sb = new StringBuilder();

        _types.Reset();
        _types.DeclareGlobals(cbuffers, samplers);

        // Compute stages have no generator-invented interface names.
        _stageOwnedNames.Clear();

        // Local size from [NumThreads(x, y, z)]
        var numThreadsAttr = GetAttribute(func.Attributes, "NumThreads");
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

        return FinishStage(sb);
    }

    private void EmitVertexBody(StringBuilder sb, FunctionDeclaration func, StructDeclaration? returnStruct,
        StructDeclaration? inputStruct, string inputParam, int indent)
    {
        SetStageContext(isVertex: true, inputStruct: inputStruct, inputParam: inputParam);
        _returnMode = StageReturnMode.Vertex;
        _stageReturnStruct = returnStruct;
        _returnTempCounter = 0;
        foreach (var p in func.Parameters)
            _types.Declare(p.Name, p.Type.Name);

        foreach (var stmt in func.Body.Statements)
            EmitStatement(sb, stmt, indent);

        _returnMode = StageReturnMode.None;
        _stageReturnStruct = null;
        SetStageContext();
    }

    private void EmitFragmentBody(StringBuilder sb, FunctionDeclaration func, StructDeclaration? inputStruct, string inputParam, StructDeclaration? returnStruct, int indent)
    {
        SetStageContext(inputStruct: inputStruct, inputParam: inputParam);
        _returnMode = StageReturnMode.Fragment;
        _stageReturnStruct = returnStruct;
        _returnTempCounter = 0;
        foreach (var p in func.Parameters)
            _types.Declare(p.Name, p.Type.Name);

        foreach (var stmt in func.Body.Statements)
            EmitStatement(sb, stmt, indent);

        _returnMode = StageReturnMode.None;
        _stageReturnStruct = null;
        SetStageContext();
    }

    private void SetStageContext(bool isVertex = false, bool isGeometry = false, bool isCompute = false,
        StructDeclaration? inputStruct = null, string? inputParam = null)
    {
        _isVertex = isVertex;
        _isGeometry = isGeometry;
        _isCompute = isCompute;
        _inputStruct = inputStruct;
        _inputParam = inputParam;
    }

    private void EmitGeometryBody(StringBuilder sb, FunctionDeclaration func, StructDeclaration? inputStruct, string inputParam, StructDeclaration? outputStruct, int indent)
    {
        SetStageContext(isGeometry: true, inputStruct: inputStruct, inputParam: inputParam);
        _geomPositionField = outputStruct?.Fields
            .FirstOrDefault(f => HasAttribute(f.Attributes, "Position"))?.Name;
        _geomOutputFieldNames = outputStruct is not null
            ? new HashSet<string>(outputStruct.Fields.Select(f => f.Name), StringComparer.Ordinal)
            : null;
        foreach (var p in func.Parameters)
            _types.Declare(p.Name, p.Type.Name);
        foreach (var stmt in func.Body.Statements)
            EmitStatement(sb, stmt, indent);
        _geomPositionField = null;
        _geomOutputFieldNames = null;
        SetStageContext();
    }

    private void EmitComputeBody(StringBuilder sb, FunctionDeclaration func, int indent)
    {
        SetStageContext(isCompute: true);
        _types.Declare("GlobalInvocationID", "uvec3");
        _types.Declare("LocalInvocationID", "uvec3");
        _types.Declare("WorkGroupID", "uvec3");
        _types.Declare("LocalInvocationIndex", "uint");
        foreach (var p in func.Parameters)
            _types.Declare(p.Name, p.Type.Name);
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

        var saved = _types.Snapshot();
        foreach (var p in func.Parameters)
            _types.Declare(p.Name, p.Type.Name);
        EmitBlock(sb, func.Body, 0);
        _types.Restore(saved);

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
                // `var` resolves through inference (GLSL has no auto); when the
                // initializer's type can't be determined we fall back to float,
                // mirroring the HLSL generator.
                string specType = v.Type.Name == "var"
                    ? _types.Infer(v.Initializer!) ?? "float"
                    : v.Type.Name;
                string varType = GlslType(specType);
                _types.Declare(v.Name, specType);

                // GLSL has no zero-argument struct constructor — `new T()` lowers
                // to a default-initialized declaration with no initializer.
                string init;
                if (v.Initializer is NewExpression ne && ne.Arguments.Count == 0)
                    init = "";
                else
                    init = v.Initializer is not null ? $" = {EmitExpression(v.Initializer)}" : "";

                sb.AppendLine($"{pad}{varType} {EscapeId(v.Name)}{EmitArraySuffix(v.Type)}{init};");
                break;

            case ReturnStatement r:
                // Inside a stage entry body every return — at any nesting
                // depth — routes through the stage outputs: main() is void,
                // so `return expr;` would not compile.
                if (_returnMode != StageReturnMode.None)
                {
                    EmitStageReturn(sb, r, pad);
                    break;
                }
                string val = r.Value is not null ? $" {EmitExpression(r.Value)}" : "";
                sb.AppendLine($"{pad}return{val};");
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
                    // Loop counters declared `var` infer from the initializer,
                    // defaulting to int (matches the HLSL generator).
                    string fSpec = fv.Type.Name == "var"
                        ? (fv.Initializer is not null ? _types.Infer(fv.Initializer) ?? "int" : "int")
                        : fv.Type.Name;
                    _types.Declare(fv.Name, fSpec);
                    string fInit = fv.Initializer is not null ? $" = {EmitExpression(fv.Initializer)}" : "";
                    sb.Append($"{GlslType(fSpec)} {EscapeId(fv.Name)}{fInit}");
                }
                else if (f.Initializer is ExpressionStatement fes)
                {
                    // Assignment to a pre-declared counter (matches the HLSL
                    // generator) — dropping it would run the loop on an
                    // uninitialized counter.
                    sb.Append(EmitExpression(fes.Expression));
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

    // Lowers `return expr;` in a stage entry body: assign the stage outputs
    // (gl_Position + v_* varyings for vertex, render targets / fragColor for
    // fragment) from the returned value, then exit with a bare return.
    private void EmitStageReturn(StringBuilder sb, ReturnStatement ret, string pad)
    {
        if (ret.Value is not null)
        {
            if (_stageReturnStruct is not null)
            {
                string source = MaterializeReturnValue(sb, ret.Value, _stageReturnStruct, pad);
                foreach (var field in _stageReturnStruct.Fields)
                {
                    string fieldRef = $"{source}.{EscapeId(field.Name)}";
                    if (_returnMode == StageReturnMode.Vertex)
                    {
                        if (HasAttribute(field.Attributes, "Position"))
                            sb.AppendLine($"{pad}gl_Position = {fieldRef};");
                        else
                            sb.AppendLine($"{pad}v_{field.Name} = {fieldRef};");
                    }
                    else
                    {
                        sb.AppendLine($"{pad}{EscapeId(field.Name)} = {fieldRef};");
                    }
                }
            }
            else if (_returnMode == StageReturnMode.Fragment)
            {
                // Simple (non-struct) fragment return: the single render target.
                sb.AppendLine($"{pad}fragColor = {EmitExpression(ret.Value)};");
            }
            else
            {
                // Simple (non-struct) vertex return: the returned value is the
                // clip-space position itself. The analyzer only rejects *void*
                // vertex returns, so this shape reaches the generator and must
                // route to gl_Position — dropping it would leave the position
                // unwritten while the shader still compiles cleanly.
                sb.AppendLine($"{pad}gl_Position = {EmitExpression(ret.Value)};");
            }
        }
        sb.AppendLine($"{pad}return;");
    }

    // A `return <identifier>;` expands its fields straight off the named local.
    // Any other returned expression (helper call, constructor, ...) is
    // materialized into a uniquely named temporary first so field expansion has
    // a valid source — main() is void, so the value itself cannot be returned.
    private string MaterializeReturnValue(StringBuilder sb, Expression value, StructDeclaration returnStruct, string pad)
    {
        if (value is IdentifierExpression id)
            return EscapeId(id.Name);

        string temp = $"_ss_ret{_returnTempCounter++}";
        // `new T()` has no GLSL constructor equivalent — default-initialized local.
        if (value is NewExpression ne && ne.Arguments.Count == 0)
            sb.AppendLine($"{pad}{GlslType(returnStruct.Name)} {temp};");
        else
            sb.AppendLine($"{pad}{GlslType(returnStruct.Name)} {temp} = {EmitExpression(value)};");
        return temp;
    }

    // GLSL counterpart of HlslGenerator.TryEmitGeometryStmt: geometry bodies
    // write varyings by bare output-struct field name. Those lower to the loose
    // g_* outputs; the [Position] field lowers to gl_Position. (A `Position =`
    // assignment is already covered by the identifier mapping in EmitExpression.)
    private bool TryEmitGeometryStmt(StringBuilder sb, Expression expr, string pad)
    {
        if (expr is AssignmentExpression a && a.Target is IdentifierExpression lhs
            && _geomOutputFieldNames is not null && _geomOutputFieldNames.Contains(lhs.Name))
        {
            string mapped = lhs.Name == _geomPositionField ? "gl_Position" : $"g_{lhs.Name}";
            sb.AppendLine($"{pad}{mapped} {MapOperator(a.Operator)} {EmitExpression(a.Value)};");
            return true;
        }
        return false;
    }

    private void EmitStatementOrBlock(StringBuilder sb, Statement stmt, int indent)
    {
        if (stmt is BlockStatement block)
        {
            EmitBlock(sb, block, indent);
            return;
        }

        // Inside a stage entry a single source statement can lower to SEVERAL
        // sibling statements: EmitStageReturn expands one `return expr;` into
        // an optional temp declaration, the output assignments, and a bare
        // return. A brace-less if/else/for/while body would then guard only
        // the first lowered statement while the rest escape the control flow —
        // a silent miscompile (the leaked GLSL still compiles cleanly). Emit
        // every non-block body as a single-statement block to keep the
        // lowering contained.
        if (_returnMode != StageReturnMode.None)
        {
            string pad = new(' ', indent * 4);
            sb.AppendLine($"{pad}{{");
            EmitStatement(sb, stmt, indent + 1);
            sb.AppendLine($"{pad}}}");
            return;
        }

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
                // Every parser-produced node is handled above; failing loudly
                // beats silently emitting source that cannot compile.
                throw new NotSupportedException(
                    $"GLSL generator has no emission for expression node '{expr.GetType().Name}'.");
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
        // v_field / g_field in the fragment stage (varyings from the vertex or
        // geometry shader, depending on which stage feeds the fragment).
        if (!_isGeometry && _inputStruct is not null && _inputParam is not null
            && ma.Object is IdentifierExpression id && id.Name == _inputParam)
        {
            var field = _inputStruct.Fields.FirstOrDefault(f => f.Name == ma.Member);
            if (field is not null && !HasAttribute(field.Attributes, "Position"))
                return _isVertex ? $"a_{ma.Member}" : $"{_fragmentVaryingPrefix}{ma.Member}";
        }

        // Geometry: vertices[i].field → the vertex stage's loose varying arrays
        // (v_field[i]); the [Position] field reads the gl_in built-in block.
        if (_isGeometry && _inputStruct is not null && _inputParam is not null
            && ma.Object is IndexExpression idx
            && idx.Object is IdentifierExpression arrayId && arrayId.Name == _inputParam)
        {
            string index = EmitExpression(idx.Index);
            var field = _inputStruct.Fields.FirstOrDefault(f => f.Name == ma.Member);
            if (field is not null && HasAttribute(field.Attributes, "Position"))
                return $"gl_in[{index}].gl_Position";
            return $"v_{ma.Member}[{index}]";
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

    // Each stage declares the minimum #version its emitted features require, so
    // plain shaders stay on 330 core — the engine's GL 3.3 baseline — and the
    // existing engine shaders compile byte-for-byte against the same profile:
    //   330 — baseline vertex/fragment/geometry stages
    //   400 — double-precision types
    //   420 — layout(early_fragment_tests) / layout(depth_*) fragment qualifiers
    //   430 — compute stages
    // Emission sites report their needs through Require; FinishStage prepends
    // the resulting header once the stage body is complete.
    private void Require(int version)
    {
        if (version > _minVersion)
            _minVersion = version;
    }

    private string FinishStage(StringBuilder body)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#version {_minVersion} core");
        sb.AppendLine();
        sb.Append(body);
        return sb.ToString();
    }

    private static StructDeclaration? FindStruct(string name, List<StructDeclaration> structs)
    {
        return structs.FirstOrDefault(s => s.Name == name);
    }

    private static AttributeSyntax? GetAttribute(IReadOnlyList<AttributeSyntax> attrs, string name)
    {
        return attrs.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAttribute(IReadOnlyList<AttributeSyntax> attrs, string name)
    {
        return attrs.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // Guarded attribute-argument read, mirroring HlslGenerator.GetIntArg: an
    // absent attribute, missing argument, or non-literal argument yields the
    // fallback instead of throwing.
    private static int GetIntArg(IReadOnlyList<AttributeSyntax> attrs, string name, int fallback, int argIndex = 0)
    {
        var attr = GetAttribute(attrs, name);
        if (attr is null || attr.Arguments.Count <= argIndex) return fallback;
        if (attr.Arguments[argIndex] is IntLiteralExpression i) return i.Value;
        return fallback;
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
        TokenKind.PercentAssign => "%=",
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

    private string GlslType(string name)
    {
        // Double-precision scalars only exist from GLSL 4.00 on.
        if (name == "double")
            Require(400);

        return name switch
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
}
