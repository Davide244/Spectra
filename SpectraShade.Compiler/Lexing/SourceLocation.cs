namespace SpectraShade.Compiler.Lexing;

/// <summary>
/// Points to a specific position in source code. Used for diagnostics.
/// </summary>
public readonly struct SourceLocation
{
    public string File { get; }
    public int Line { get; }
    public int Column { get; }
    public int Offset { get; }

    public SourceLocation(string file, int line, int column, int offset)
    {
        File = file;
        Line = line;
        Column = column;
        Offset = offset;
    }

    public override string ToString() => $"{File}({Line},{Column})";
}

/// <summary>
/// A span of source code between two locations.
/// </summary>
public readonly struct SourceSpan
{
    public SourceLocation Start { get; }
    public SourceLocation End { get; }

    public SourceSpan(SourceLocation start, SourceLocation end)
    {
        Start = start;
        End = end;
    }
}
