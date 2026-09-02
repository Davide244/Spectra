using System;

namespace Spectra.Kitchen.Diagnostics;

/// <summary>
/// One thing the cook has to say, in the form an IDE can jump to.
/// </summary>
/// <remarks>
/// <para><b>The MSBuild-parseable line is rendered HERE, not in the CLI.</b> The
/// library is hosted in process by the editor as well as run from
/// <c>scook</c>, and two renderings of one diagnostic drift the first time one of
/// them is fixed; the CLI adds colour around this text and nothing else.</para>
/// <para><b>A diagnostic with no file is legitimate and has its own form.</b>
/// "this folder is not a project" has no line to point at, and MSBuild's
/// canonical format covers it: <c>origin : category code: text</c>, where the
/// origin is the tool name. Inventing <c>(1,1)</c> against the project folder
/// would make an IDE open a directory as a file.</para>
/// </remarks>
public sealed record CookDiagnostic
{
    private CookDiagnostic(
        CookDiagnosticId id,
        CookDiagnosticSeverity severity,
        string message,
        string? file,
        int line,
        int column)
    {
        Id = id;
        Severity = severity;
        Message = message;
        File = file;
        Line = line;
        Column = column;
    }

    /// <summary>The code, <c>SC####</c> or a wrapped foreign one.</summary>
    public CookDiagnosticId Id { get; }

    /// <summary>How loud it is.</summary>
    public CookDiagnosticSeverity Severity { get; init; }

    /// <summary>What went wrong, as a sentence.</summary>
    public string Message { get; }

    /// <summary>The file it is about, or null when it is about the run itself.</summary>
    public string? File { get; }

    /// <summary>One-based line, or zero when the diagnostic is about the whole file.</summary>
    public int Line { get; }

    /// <summary>One-based column, or zero.</summary>
    public int Column { get; }

    /// <summary>Whether this diagnostic fails the cook.</summary>
    public bool IsError => Severity == CookDiagnosticSeverity.Error;

    public static CookDiagnostic Error(CookDiagnosticId id, string message, string? file = null, int line = 0, int column = 0) =>
        new(id, CookDiagnosticSeverity.Error, message, file, line, column);

    public static CookDiagnostic Warning(CookDiagnosticId id, string message, string? file = null, int line = 0, int column = 0) =>
        new(id, CookDiagnosticSeverity.Warning, message, file, line, column);

    public static CookDiagnostic Info(CookDiagnosticId id, string message, string? file = null, int line = 0, int column = 0) =>
        new(id, CookDiagnosticSeverity.Info, message, file, line, column);

    /// <summary>
    /// This diagnostic as an error, for <c>--strict</c>. Returns the same instance
    /// when it is already one.
    /// </summary>
    public CookDiagnostic AsError() =>
        Severity == CookDiagnosticSeverity.Error ? this : this with { Severity = CookDiagnosticSeverity.Error };

    /// <summary>The severity word MSBuild matches on.</summary>
    public static string SeverityText(CookDiagnosticSeverity severity) => severity switch
    {
        CookDiagnosticSeverity.Error => "error",
        CookDiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    /// <summary>
    /// The IDE-parseable line, without colour: either
    /// <c>&lt;file&gt;(&lt;line&gt;,&lt;col&gt;): error SC0001: message</c> or
    /// <c>&lt;tool&gt; : error SC0001: message</c>.
    /// </summary>
    public string ToBuildLine(string toolName) =>
        $"{Origin(toolName)} {SeverityText(Severity)} {Id}: {Message}";

    /// <summary>
    /// The part before the severity, terminating colon included, which is where
    /// the two forms differ. Split out so the CLI can colour the middle without
    /// re-deriving either.
    /// </summary>
    /// <remarks>
    /// The colon belongs to the origin rather than to the joiner, because the
    /// tool form wants a space before it (<c>scook : error</c>) and the file form
    /// must not have one (<c>file(1,1): error</c>). Joining with <c>": "</c>
    /// instead produces a doubled colon on the file form, which no IDE matches.
    /// </remarks>
    public string Origin(string toolName)
    {
        if (File is null) return $"{toolName} :";

        // A file with no position still gets the file form: an IDE that cannot
        // find a line opens the file, which is the right answer, whereas the tool
        // form would lose the path entirely.
        return Line > 0
            ? $"{File}({Math.Max(1, Line)},{Math.Max(1, Column)}):"
            : $"{File}:";
    }

    /// <inheritdoc/>
    public override string ToString() => ToBuildLine("scook");
}
