namespace SpectraShade.Compiler.Lexing;

public readonly struct Token
{
    public TokenKind Kind { get; }
    public string Text { get; }
    public SourceSpan Span { get; }

    public Token(TokenKind kind, string text, SourceSpan span)
    {
        Kind = kind;
        Text = text;
        Span = span;
    }

    public override string ToString() => $"{Kind} '{Text}' at {Span.Start}";
}
