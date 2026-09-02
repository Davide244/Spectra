namespace Spectra.Kitchen.Diagnostics;

/// <summary>
/// How loud one cook diagnostic is, in the same three levels the shader
/// compiler's <c>DiagnosticSeverity</c> carries.
/// </summary>
/// <remarks>
/// The order is ascending by loudness, so a caller may compare, and it matches
/// the compiler's so a wrapped <c>SS####</c> diagnostic keeps the severity it
/// was reported with rather than being reclassified on its way through.
/// </remarks>
public enum CookDiagnosticSeverity
{
    /// <summary>Something worth saying that changes nothing about the outcome.</summary>
    Info,

    /// <summary>
    /// The cook completed and something in it deserves attention. Under
    /// <c>--strict</c> the session promotes these to errors.
    /// </summary>
    Warning,

    /// <summary>The cook failed. The tool exits 1 and writes no successful pack.</summary>
    Error,
}
