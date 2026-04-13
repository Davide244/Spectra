using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace SpectraShade.VSIX;

/// <summary>
/// LSP client that launches the SpectraShade language server
/// and connects Visual Studio to it for diagnostics, completions, etc.
/// </summary>
[ContentType("spectrashade")]
[Export(typeof(ILanguageClient))]
public sealed class SpectraShadeLspClient : ILanguageClient
{
    public string Name => "SpectraShade Language Server";

    public IEnumerable<string>? ConfigurationSections => null;

    public object? InitializationOptions => null;

    public IEnumerable<string> FilesToWatch => ["**/*.spectrashade"];

    public bool ShowNotificationOnInitializeFailed => true;

    public event AsyncEventHandler<EventArgs>? StartAsync;
    public event AsyncEventHandler<EventArgs>? StopAsync;

    public async Task<Connection?> ActivateAsync(CancellationToken token)
    {
        // Find the LSP server executable
        // In development: look relative to the extension directory
        // In production: the server would be bundled with the VSIX
        string extensionDir = Path.GetDirectoryName(typeof(SpectraShadeLspClient).Assembly.Location)!;
        string serverPath = Path.Combine(extensionDir, "spectrashade-lsp", "spectrashade-lsp.exe");

        if (!File.Exists(serverPath))
        {
            // Fallback: try to find it via dotnet tool or PATH
            serverPath = "spectrashade-lsp";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(startInfo);
        if (process is null)
            return null;

        return new Connection(
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream);
    }

    public async Task OnLoadedAsync()
    {
        if (StartAsync is not null)
            await StartAsync.InvokeAsync(this, EventArgs.Empty);
    }

    public Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
    {
        return Task.FromResult<InitializationFailureContext?>(null);
    }

    public Task OnServerInitializedAsync()
    {
        return Task.CompletedTask;
    }
}
