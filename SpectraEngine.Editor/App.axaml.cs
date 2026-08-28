using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SpectraEngine.Editor;

/// <summary>The Avalonia application.</summary>
public partial class App : Application
{
    /// <inheritdoc/>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        // The window stops the engine and joins its render thread in its own
        // closing handler, before the lifetime tears the process down: the
        // render thread owns the swap chain presenting into the viewport, so
        // process exit racing that join is a present into a destroyed window.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
