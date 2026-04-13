using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace SpectraShade.LSP.Handlers;

/// <summary>
/// Provides keyword and type completions for SpectraShade files.
/// </summary>
internal sealed class CompletionHandler : CompletionHandlerBase
{
    private static readonly CompletionItem[] KeywordCompletions =
    [
        MakeKeyword("shader"),
        MakeKeyword("struct"),
        MakeKeyword("cbuffer"),
        MakeKeyword("import"),
        MakeKeyword("var"),
        MakeKeyword("new"),
        MakeKeyword("in"),
        MakeKeyword("out"),
        MakeKeyword("const"),
        MakeKeyword("if"),
        MakeKeyword("else"),
        MakeKeyword("for"),
        MakeKeyword("while"),
        MakeKeyword("return"),
        MakeKeyword("break"),
        MakeKeyword("continue"),
        MakeKeyword("discard"),
    ];

    private static readonly CompletionItem[] TypeCompletions =
    [
        MakeType("void"),
        MakeType("bool"),
        MakeType("int"),
        MakeType("uint"),
        MakeType("float"),
        MakeType("double"),
        MakeType("vec2"),
        MakeType("vec3"),
        MakeType("vec4"),
        MakeType("ivec2"),
        MakeType("ivec3"),
        MakeType("ivec4"),
        MakeType("uvec2"),
        MakeType("uvec3"),
        MakeType("uvec4"),
        MakeType("bvec2"),
        MakeType("bvec3"),
        MakeType("bvec4"),
        MakeType("mat2"),
        MakeType("mat3"),
        MakeType("mat4"),
        MakeType("sampler2D"),
        MakeType("sampler3D"),
        MakeType("samplerCube"),
    ];

    private static readonly CompletionItem[] AttributeCompletions =
    [
        MakeSnippet("Vertex", "[Vertex]", "Marks a function as the vertex stage entry point"),
        MakeSnippet("Fragment", "[Fragment]", "Marks a function as the fragment stage entry point"),
        MakeSnippet("Geometry", "[Geometry]", "Marks a function as the geometry stage entry point"),
        MakeSnippet("Compute", "[Compute]", "Marks a function as the compute stage entry point"),
        MakeSnippet("Location", "[Location(${1:0})]", "Specifies vertex attribute location"),
        MakeSnippet("Binding", "[Binding(${1:0})]", "Specifies resource binding slot"),
        MakeSnippet("Position", "[Position]", "Marks a struct field as the clip-space position output"),
        MakeSnippet("Target", "[Target(${1:0})]", "Specifies render target index"),
    ];

    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForPattern("**/*.spectrashade"
            ),
            TriggerCharacters = new Container<string>("."),
        };
    }

    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        var items = new List<CompletionItem>();
        items.AddRange(KeywordCompletions);
        items.AddRange(TypeCompletions);
        items.AddRange(AttributeCompletions);

        return Task.FromResult(new CompletionList(items));
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }

    private static CompletionItem MakeKeyword(string name) => new()
    {
        Label = name,
        Kind = CompletionItemKind.Keyword,
        InsertText = name,
    };

    private static CompletionItem MakeType(string name) => new()
    {
        Label = name,
        Kind = CompletionItemKind.TypeParameter,
        InsertText = name,
    };

    private static CompletionItem MakeSnippet(string label, string snippet, string detail) => new()
    {
        Label = label,
        Kind = CompletionItemKind.Snippet,
        InsertText = snippet,
        InsertTextFormat = InsertTextFormat.Snippet,
        Detail = detail,
    };
}
