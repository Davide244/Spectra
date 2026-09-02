using System.Text;

namespace SpectraEngine.Entities.Generator;

/// <summary>An indent-tracking text builder for the emitted source.</summary>
/// <remarks>
/// <b>Line endings are <c>\n</c>, never <see cref="System.Environment"/>'s.</b>
/// Generated source is compared byte for byte by the snapshot tests and lands in
/// build outputs that are diffed across machines, so a generator whose output
/// changed with the operating system it ran on would produce a difference nobody
/// authored. It is also one of the APIs an analyzer is not allowed to touch.
/// </remarks>
internal sealed class SourceWriter
{
    private readonly StringBuilder _text = new();
    private int _indent;

    /// <summary>Writes a header, opens a brace block and indents.</summary>
    public SourceWriter Open(string header)
    {
        Line(header);
        Line("{");
        _indent++;
        return this;
    }

    /// <summary>Outdents and closes a brace block.</summary>
    public SourceWriter Close(string trailer = "}")
    {
        _indent--;
        Line(trailer);
        return this;
    }

    /// <summary>Indents without writing a brace, for a wrapped argument list.</summary>
    public SourceWriter Indent()
    {
        _indent++;
        return this;
    }

    /// <summary>Outdents without writing a brace.</summary>
    public SourceWriter Outdent()
    {
        _indent--;
        return this;
    }

    /// <summary>Writes one line at the current indent, or a blank line for empty text.</summary>
    public SourceWriter Line(string text = "")
    {
        if (text.Length > 0)
            _text.Append(' ', _indent * 4).Append(text);

        _text.Append('\n');
        return this;
    }

    public override string ToString() => _text.ToString();
}
