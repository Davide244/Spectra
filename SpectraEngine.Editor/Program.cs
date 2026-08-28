using Avalonia;
using Serilog;
using System;

namespace SpectraEngine.Editor;

/// <summary>The editor shell's entry point.</summary>
internal static class Program
{
    /// <summary>
    /// The graphics backend the viewport runs on, from the command line.
    /// Read by <see cref="MainWindow"/> when it starts the session.
    /// </summary>
    internal static string[] StartupArgs { get; private set; } = [];

    // STAThread because Windows requires it of any thread that owns windows and
    // touches the shell APIs (drag and drop, file dialogs) an editor will grow.
    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/spectra-editor-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The editor terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>The app builder, also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
