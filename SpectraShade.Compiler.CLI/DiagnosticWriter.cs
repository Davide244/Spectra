using SpectraShade.Compiler;
using SpectraShade.Compiler.Lexing;

namespace SpectraShade.Compiler.CLI;

internal sealed class DiagnosticWriter
{
    private readonly TextWriter _out;
    private readonly string _fallbackPath;
    private readonly bool _color;

    public DiagnosticWriter(TextWriter output, string fallbackPath, bool color)
    {
        _out = output;
        _fallbackPath = Path.GetFullPath(fallbackPath);
        _color = color;
    }

    public void WriteAll(IReadOnlyList<Diagnostic> diagnostics)
    {
        for (int i = 0; i < diagnostics.Count; i++)
            Write(diagnostics[i]);
    }

    public void Write(Diagnostic diag)
    {
        var start = diag.Span.Start;
        var file = ResolvePath(start.File);
        var line = Math.Max(1, start.Line);
        var col = Math.Max(1, start.Column);
        var severity = SeverityText(diag.Severity);

        if (_color)
        {
            var color = SeverityColor(diag.Severity);
            _out.Write($"{file}({line},{col}): \u001b[{color};1m{severity}\u001b[0m: {diag.Message}");
            _out.WriteLine();
        }
        else
        {
            _out.WriteLine($"{file}({line},{col}): {severity}: {diag.Message}");
        }
    }

    private string ResolvePath(string pathFromDiag)
    {
        if (string.IsNullOrEmpty(pathFromDiag) || pathFromDiag == "<source>")
            return _fallbackPath;
        try { return Path.GetFullPath(pathFromDiag); }
        catch { return pathFromDiag; }
    }

    private static string SeverityText(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    private static string SeverityColor(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "31",
        DiagnosticSeverity.Warning => "33",
        _ => "36",
    };
}
