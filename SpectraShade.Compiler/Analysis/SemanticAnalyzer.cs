using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.Analysis;

/// <summary>
/// Walks a parsed AST and performs semantic validation.
/// </summary>
public sealed class SemanticAnalyzer
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public bool Analyze(CompilationUnit unit)
    {
        var shader = unit.Shader;
        var allStructs = new List<StructDeclaration>(unit.Structs);
        allStructs.AddRange(shader.Members.OfType<StructDeclaration>());

        ValidateStages(shader);
        ValidateBindings(shader);
        ValidatePositionAssignment(shader, allStructs);
        ValidateRenderTargets(shader, allStructs);
        ValidateDepthHints(shader);
        ValidateVertexInputs(shader, allStructs);
        return !_diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    private void ValidateStages(ShaderDeclaration shader)
    {
        var stageFunctions = shader.Members
            .OfType<FunctionDeclaration>()
            .Where(f => f.HasAttribute("Vertex") || f.HasAttribute("Fragment")
                     || f.HasAttribute("Geometry") || f.HasAttribute("Compute"))
            .ToList();

        bool hasVertex = stageFunctions.Any(f => f.HasAttribute("Vertex"));
        bool hasFragment = stageFunctions.Any(f => f.HasAttribute("Fragment"));
        bool hasCompute = stageFunctions.Any(f => f.HasAttribute("Compute"));

        if (!hasCompute && !hasVertex)
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                "Shader must have a [Vertex] function", shader.Span));

        if (!hasCompute && !hasFragment)
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                "Shader must have a [Fragment] function", shader.Span));

        // Validate vertex function has a struct return type (not void)
        foreach (var func in stageFunctions.Where(f => f.HasAttribute("Vertex")))
        {
            if (func.ReturnType.Name == "void")
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    "[Vertex] function must return a struct (the fragment input), not void", func.Span));
        }

        // A [Fragment] function may return void, and that is not a loophole: a
        // depth-only pass (a shadow map) binds no render target at all, so a
        // fragment stage that returns a colour is asking the hardware to
        // discard a value it computed. Worse, both D3D debug layers report the
        // mismatch, and D3D11 reports it once per DRAW, which floods the same
        // info queue the engine reads to detect real errors.

        // Validate geometry function
        foreach (var func in stageFunctions.Where(f => f.HasAttribute("Geometry")))
        {
            if (func.ReturnType.Name != "void")
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    "[Geometry] function must return void (use EmitVertex() to output vertices)", func.Span));

            if (!func.HasAttribute("MaxVertexCount"))
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    "[Geometry] function requires a [MaxVertexCount(N)] attribute", func.Span));

            if (func.Parameters.Count < 1 || !func.Parameters[0].Type.IsArray)
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    "[Geometry] function must take an array parameter as input (e.g. VertexOutput[] vertices)", func.Span));
        }

        // Validate compute function
        foreach (var func in stageFunctions.Where(f => f.HasAttribute("Compute")))
        {
            if (!func.HasAttribute("NumThreads"))
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    "[Compute] function requires a [NumThreads(x, y, z)] attribute", func.Span));

            if (func.ReturnType.Name != "void")
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    "[Compute] function must return void", func.Span));
        }
    }

    private void ValidateBindings(ShaderDeclaration shader)
    {
        // Validate that cbuffers and samplers have [Binding(N)] attributes
        foreach (var member in shader.Members)
        {
            if (member is CBufferDeclaration cbuffer)
            {
                if (!cbuffer.Attributes.Any(a => string.Equals(a.Name, "Binding", StringComparison.OrdinalIgnoreCase)))
                    _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning,
                        $"cbuffer '{cbuffer.Name}' has no [Binding] attribute", cbuffer.Span));
            }
            else if (member is SamplerDeclaration sampler)
            {
                if (!sampler.Attributes.Any(a => string.Equals(a.Name, "Binding", StringComparison.OrdinalIgnoreCase)))
                    _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning,
                        $"Sampler '{sampler.Name}' has no [Binding] attribute", sampler.Span));
            }
        }
    }

    private void ValidatePositionAssignment(ShaderDeclaration shader, List<StructDeclaration> allStructs)
    {
        var vertexFuncs = shader.Members
            .OfType<FunctionDeclaration>()
            .Where(f => f.HasAttribute("Vertex"));

        foreach (var func in vertexFuncs)
        {
            var returnStruct = allStructs.FirstOrDefault(s => s.Name == func.ReturnType.Name);
            if (returnStruct is null)
                continue;

            var positionField = returnStruct.Fields.FirstOrDefault(f =>
                f.Attributes.Any(a => string.Equals(a.Name, "Position", StringComparison.OrdinalIgnoreCase)));
            if (positionField is null)
            {
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning,
                    $"[Vertex] return type '{returnStruct.Name}' has no field marked [Position]", func.Span));
                continue;
            }

            if (!ContainsPositionAssignment(func.Body, positionField.Name))
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning,
                    $"[Vertex] function should assign '{positionField.Name}' (the [Position] field)", func.Span));
        }
    }

    private void ValidateDepthHints(ShaderDeclaration shader)
    {
        foreach (var func in shader.Members.OfType<FunctionDeclaration>())
        {
            bool hasDepthHint = func.HasAttribute("EarlyDepthStencil") || func.HasAttribute("DepthWrite");
            if (hasDepthHint && !func.HasAttribute("Fragment"))
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    "[EarlyDepthStencil] and [DepthWrite] are only valid on [Fragment] functions",
                    func.Span));
        }
    }

    private void ValidateRenderTargets(ShaderDeclaration shader, List<StructDeclaration> allStructs)
    {
        var fragmentFuncs = shader.Members
            .OfType<FunctionDeclaration>()
            .Where(f => f.HasAttribute("Fragment"));

        foreach (var func in fragmentFuncs)
        {
            var returnStruct = allStructs.FirstOrDefault(s => s.Name == func.ReturnType.Name);
            if (returnStruct is null)
                continue;

            // Validate Target index uniqueness
            var targetIndices = new HashSet<int>();
            foreach (var field in returnStruct.Fields)
            {
                var targetAttr = field.Attributes.FirstOrDefault(a =>
                    string.Equals(a.Name, "Target", StringComparison.OrdinalIgnoreCase));
                if (targetAttr is null)
                    continue;

                if (targetAttr.Arguments.Count > 0 && targetAttr.Arguments[0] is IntLiteralExpression indexExpr)
                {
                    if (!targetIndices.Add(indexExpr.Value))
                        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                            $"Duplicate [Target({indexExpr.Value})] in render target struct '{returnStruct.Name}'",
                            field.Span));
                }
            }
        }
    }

    /// <summary>
    /// Checks the vertex input struct's locations and rates.
    /// </summary>
    /// <remarks>
    /// <b>Everything here fails silently at runtime if it is not caught here.</b>
    /// Overlapping locations link and draw, and simply feed one attribute the
    /// other's bytes. A <c>[PerInstance]</c> on a fragment output is ignored by
    /// both generators. A matrix taking its location from the field index leaves
    /// the next three fields sitting inside it. None of these produce a
    /// compiler error, a linker error or a debug-layer message on any of the
    /// three backends, which is the entire argument for validating them.
    /// </remarks>
    private void ValidateVertexInputs(ShaderDeclaration shader, List<StructDeclaration> allStructs)
    {
        var vertexFuncs = shader.Members
            .OfType<FunctionDeclaration>()
            .Where(f => f.HasAttribute("Vertex"))
            .ToList();

        var inputStructs = new HashSet<StructDeclaration>();
        foreach (var func in vertexFuncs)
        {
            if (func.Parameters.Count == 0)
                continue;
            var inputStruct = allStructs.FirstOrDefault(s => s.Name == func.Parameters[0].Type.Name);
            if (inputStruct is not null)
                inputStructs.Add(inputStruct);
        }

        foreach (var inputStruct in inputStructs)
            ValidateVertexInputStruct(inputStruct);

        ValidatePerInstanceUniforms(shader);

        // [PerInstance] anywhere that is not a vertex input is a
        // misunderstanding worth naming, because both generators ignore it and
        // the author is left believing they asked for something.
        foreach (var s in allStructs)
        {
            if (inputStructs.Contains(s))
                continue;

            foreach (var field in s.Fields)
            {
                if (VertexInputLayout.IsPerInstance(field))
                    _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                        $"[PerInstance] is only valid on a vertex input field; '{s.Name}.{field.Name}' is not one",
                        field.Span));
            }
        }
    }

    /// <summary>
    /// Checks <c>[PerInstance]</c> where it marks a <c>cbuffer</c> field, i.e.
    /// a uniform the compiler should also emit an instanced vertex stage for.
    /// </summary>
    /// <remarks>
    /// Both refusals here are cases where the variant would be built wrong and
    /// nothing downstream could tell. A non-matrix type has a different location
    /// span and buffer stride than the instance layout describes, and a second
    /// marked uniform would need a stride this does not express, so
    /// <c>InstancedVariant</c> would silently take only the first.
    /// </remarks>
    private void ValidatePerInstanceUniforms(ShaderDeclaration shader)
    {
        FieldDeclaration? first = null;

        foreach (var member in shader.Members)
        {
            if (member is not CBufferDeclaration cbuffer)
                continue;

            foreach (var field in cbuffer.Fields)
            {
                if (!VertexInputLayout.IsPerInstance(field))
                    continue;

                if (field.Type.Name != InstancedVariant.SupportedType)
                {
                    _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                        $"[PerInstance] uniform '{cbuffer.Name}.{field.Name}' must be " +
                        $"{InstancedVariant.SupportedType}, not {field.Type.Name}",
                        field.Span));
                    continue;
                }

                if (first is not null)
                {
                    _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                        $"A shader may declare one [PerInstance] uniform; '{field.Name}' is a second " +
                        $"(the first was '{first.Name}')",
                        field.Span));
                    continue;
                }

                first = field;
            }
        }
    }

    private void ValidateVertexInputStruct(StructDeclaration inputStruct)
    {
        var claimed = new List<(VertexInputElement Element, FieldDeclaration Field)>();

        for (int i = 0; i < inputStruct.Fields.Count; i++)
        {
            FieldDeclaration field = inputStruct.Fields[i];

            if (!VertexInputLayout.TryDescribeType(field.Type.Name, out uint components, out uint span))
            {
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    $"'{field.Type.Name}' cannot be a vertex input ('{inputStruct.Name}.{field.Name}')",
                    field.Span));
                continue;
            }

            bool perInstance = VertexInputLayout.IsPerInstance(field);
            bool explicitLocation = VertexInputLayout.HasExplicitLocation(field);

            // A multi-location type taking the field-index fallback is an
            // overlap by construction: a mat4 at index 1 owns 1 through 4 while
            // the next field believes it owns 2.
            if (span > 1 && !explicitLocation)
            {
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    $"'{inputStruct.Name}.{field.Name}' is a {field.Type.Name} and occupies {span} locations, " +
                    "so it needs an explicit [Location(N)]",
                    field.Span));
                continue;
            }

            // Per-instance data lives in its own buffer and its locations are
            // chosen to sit past the per-vertex ones. Defaulting to the field
            // index would put it on top of them.
            if (perInstance && !explicitLocation)
            {
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    $"[PerInstance] field '{inputStruct.Name}.{field.Name}' needs an explicit [Location(N)]",
                    field.Span));
                continue;
            }

            var element = new VertexInputElement(
                field.Name,
                VertexInputLayout.ResolveLocation(field, i),
                span,
                components,
                perInstance ? VertexInputRate.PerInstance : VertexInputRate.PerVertex);

            foreach ((VertexInputElement other, FieldDeclaration otherField) in claimed)
            {
                if (!element.Overlaps(other))
                    continue;

                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    $"'{inputStruct.Name}.{field.Name}' claims location {element.Location}" +
                    (element.LocationSpan > 1 ? $"..{element.LocationEnd - 1}" : "") +
                    $", which overlaps '{otherField.Name}'",
                    field.Span));
                break;
            }

            claimed.Add((element, field));
        }
    }

    private static bool ContainsPositionAssignment(BlockStatement block, string fieldName)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is ExpressionStatement exprStmt
                && exprStmt.Expression is AssignmentExpression assign
                && assign.Target is MemberAccessExpression member
                && string.Equals(member.Member, fieldName, StringComparison.Ordinal))
            {
                return true;
            }

            if (stmt is BlockStatement inner && ContainsPositionAssignment(inner, fieldName))
                return true;

            if (stmt is IfStatement ifStmt)
            {
                if (ifStmt.ThenBranch is BlockStatement thenBlock && ContainsPositionAssignment(thenBlock, fieldName))
                    return true;
                if (ifStmt.ElseBranch is BlockStatement elseBlock && ContainsPositionAssignment(elseBlock, fieldName))
                    return true;
            }
        }
        return false;
    }
}
