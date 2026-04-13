using OmniSharp.Extensions.LanguageServer.Protocol;

namespace SpectraShade.LSP;

/// <summary>
/// Simple in-memory store of open document contents.
/// The LSP server needs to track file contents because the editor sends
/// full text on each change (TextDocumentSyncKind.Full).
/// </summary>
internal sealed class DocumentStore
{
    private readonly Dictionary<DocumentUri, string> _documents = [];

    public void Update(DocumentUri uri, string content)
    {
        _documents[uri] = content;
    }

    public void Remove(DocumentUri uri)
    {
        _documents.Remove(uri);
    }

    public string? Get(DocumentUri uri)
    {
        return _documents.GetValueOrDefault(uri);
    }
}
