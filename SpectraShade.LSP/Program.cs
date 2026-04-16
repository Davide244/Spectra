using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using SpectraShade.LSP.Handlers;

var server = await LanguageServer.From(options =>
{
    options
        .WithInput(Console.OpenStandardInput())
        .WithOutput(Console.OpenStandardOutput())
        .ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
        })
        .WithHandler<TextDocumentSyncHandler>()
        .WithHandler<CompletionHandler>();
});

await server.WaitForExit;
