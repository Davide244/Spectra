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
        ValidateStages(shader);
        ValidateBindings(shader);
        ValidatePositionAssignment(shader);
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

    private void ValidatePositionAssignment(ShaderDeclaration shader)
    {
        // Check that vertex stage assigns Position
        // This is a basic check — just looks for "Position" as an assignment target
        var vertexFuncs = shader.Members
            .OfType<FunctionDeclaration>()
            .Where(f => f.HasAttribute("Vertex"));

        foreach (var func in vertexFuncs)
        {
            if (!ContainsPositionAssignment(func.Body))
                _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning,
                    "[Vertex] function should assign 'Position'", func.Span));
        }
    }

    private static bool ContainsPositionAssignment(BlockStatement block)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is ExpressionStatement exprStmt
                && exprStmt.Expression is AssignmentExpression assign
                && assign.Target is IdentifierExpression id
                && id.Name == "Position")
            {
                return true;
            }

            if (stmt is BlockStatement inner && ContainsPositionAssignment(inner))
                return true;

            if (stmt is IfStatement ifStmt)
            {
                if (ifStmt.ThenBranch is BlockStatement thenBlock && ContainsPositionAssignment(thenBlock))
                    return true;
                if (ifStmt.ElseBranch is BlockStatement elseBlock && ContainsPositionAssignment(elseBlock))
                    return true;
            }
        }
        return false;
    }
}
