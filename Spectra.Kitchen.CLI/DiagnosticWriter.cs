using Spectra.Kitchen.Diagnostics;

namespace Spectra.Kitchen.CLI;

/// <summary>
/// Writes cook diagnostics to stderr in the form MSBuild and every IDE parse.
/// </summary>
/// <remarks>
/// <para><b>The line's TEXT comes from the diagnostic, not from here.</b>
/// <see cref="CookDiagnostic.ToBuildLine"/> is the one rendering, because the
/// editor hosts the cooking library in process and a second rendering would drift
/// from this one the first time either was fixed. This class adds colour and
/// nothing else, which is why the coloured path splits the same three pieces
/// rather than re-deriving them.</para>
/// <para><b>Everything goes to stderr, warnings and infos included</b>, matching
/// <c>ssc</c>: stdout carries the tool's result and stderr carries what it has to
/// say about it, so a caller piping stdout gets a clean answer.</para>
/// </remarks>
internal sealed class DiagnosticWriter(TextWriter output, string toolName, bool color)
{
    public void WriteAll(IReadOnlyList<CookDiagnostic> diagnostics)
    {
        for (int i = 0; i < diagnostics.Count; i++)
            Write(diagnostics[i]);
    }

    public void Write(CookDiagnostic diagnostic)
    {
        string origin = diagnostic.Origin(toolName);
        string severity = CookDiagnostic.SeverityText(diagnostic.Severity);

        if (!color)
        {
            output.WriteLine(diagnostic.ToBuildLine(toolName));
            return;
        }

        string tint = SeverityColor(diagnostic.Severity);
        output.WriteLine($"{origin} \u001b[{tint};1m{severity} {diagnostic.Id}\u001b[0m: {diagnostic.Message}");
    }

    private static string SeverityColor(CookDiagnosticSeverity severity) => severity switch
    {
        CookDiagnosticSeverity.Error => "31",
        CookDiagnosticSeverity.Warning => "33",
        _ => "36",
    };
}
