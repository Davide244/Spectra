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

        // Validate fragment function return type
        foreach (var func in stageFunctions.Where(f => f.HasAttribute("Fragment")))
        {
            if (func.ReturnType.Name == "void")
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    "[Fragment] function must return a value (the render target output)", func.Span));
        }

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
