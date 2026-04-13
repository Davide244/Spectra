using SpectraShade.Compiler.Lexing;

namespace SpectraShade.Compiler;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed class Diagnostic
{
    public DiagnosticSeverity Severity { get; }
    public string Message { get; }
    public SourceSpan Span { get; }

    public Diagnostic(DiagnosticSeverity severity, string message, SourceSpan span)
    {
        Severity = severity;
        Message = message;
        Span = span;
    }

    public override string ToString() => $"{Span.Start}: {Severity}: {Message}";
}
