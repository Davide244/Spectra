using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using SpectraShade.Compiler.Analysis;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.LSP.Handlers;

/// <summary>
/// Handles document open/change/close events.
/// Runs the SpectraShade parser on every change and publishes diagnostics.
/// </summary>
internal sealed class TextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly ILanguageServerFacade _server;
    private readonly DocumentStore _documents = new();

    public TextDocumentSyncHandler(ILanguageServerFacade server)
    {
        _server = server;
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, "spectrashade");
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new TextDocumentSyncRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.spectrashade"
            ),
            Change = TextDocumentSyncKind.Full,
        };
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        _documents.Update(uri, request.TextDocument.Text);
        PublishDiagnostics(uri, request.TextDocument.Text);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var text = request.ContentChanges.First().Text!;
        _documents.Update(uri, text);
        PublishDiagnostics(uri, text);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _documents.Remove(request.TextDocument.Uri);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        return Unit.Task;
    }

    private void PublishDiagnostics(DocumentUri uri, string source)
    {
        var diagnostics = new List<Diagnostic>();

        try
        {
            var lexer = new Lexer(source, uri.Path ?? "<source>");
            var tokens = lexer.Tokenize();

            // Report invalid tokens
            foreach (var token in tokens)
            {
                if (token.Kind == TokenKind.Invalid)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Range = ToRange(token.Span),
                        Severity = DiagnosticSeverity.Error,
                        Source = "spectrashade",
                        Message = $"Unexpected character '{token.Text}'",
                    });
                }
            }

            var parser = new Parser(tokens);
            var shader = parser.Parse();

            foreach (var diag in parser.Diagnostics)
            {
                diagnostics.Add(new Diagnostic
                {
                    Range = ToRange(diag.Span),
                    Severity = MapSeverity(diag.Severity),
                    Source = "spectrashade",
                    Message = diag.Message,
                });
            }

            var analyzer = new SemanticAnalyzer();
            analyzer.Analyze(shader);

            foreach (var diag in analyzer.Diagnostics)
            {
                diagnostics.Add(new Diagnostic
                {
                    Range = ToRange(diag.Span),
                    Severity = MapSeverity(diag.Severity),
                    Source = "spectrashade",
                    Message = diag.Message,
                });
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new Diagnostic
            {
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 0),
                Severity = DiagnosticSeverity.Error,
                Source = "spectrashade",
                Message = $"Internal compiler error: {ex.Message}",
            });
        }

        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = new Container<Diagnostic>(diagnostics),
        });
    }

    private static OmniSharp.Extensions.LanguageServer.Protocol.Models.Range ToRange(SourceSpan span)
    {
        // LSP lines/columns are 0-based, SourceLocation is 1-based
        return new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
            Math.Max(0, span.Start.Line - 1),
            Math.Max(0, span.Start.Column - 1),
            Math.Max(0, span.End.Line - 1),
            Math.Max(0, span.End.Column - 1));
    }

    private static DiagnosticSeverity MapSeverity(Compiler.DiagnosticSeverity severity) => severity switch
    {
        Compiler.DiagnosticSeverity.Error => DiagnosticSeverity.Error,
        Compiler.DiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
        Compiler.DiagnosticSeverity.Info => DiagnosticSeverity.Information,
        _ => DiagnosticSeverity.Information,
    };
}
