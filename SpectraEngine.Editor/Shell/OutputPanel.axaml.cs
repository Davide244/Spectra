using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SpectraEngine.Editor.Shell;

/// <summary>The output log's view.</summary>
/// <remarks>
/// <b>It follows the tail, but only while the user is already at it.</b> An
/// output pane that scrolls to the bottom unconditionally is unusable the
/// moment anything is logging, because reading an older entry means fighting
/// every new line for the scroll position. Sticking only when the view is
/// already parked at the end is the behaviour every terminal and log viewer
/// has, and it needs no setting.
/// </remarks>
public partial class OutputPanel : UserControl
{
    private const double TailSlack = 4.0;

    public OutputPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
    }

    private OutputLog? _log;

    private void Subscribe()
    {
        if (_log is not null)
            _log.Appended -= OnAppended;

        _log = (DataContext as ShellModel)?.Output;

        if (_log is not null)
            _log.Appended += OnAppended;
    }

    private void OnAppended(OutputEntry entry)
    {
        if (Scroller is not { } scroller)
            return;

        // Measured BEFORE the new row is laid out: after it, the extent has
        // already grown and the view is no longer at the end by definition, so
        // the test would say "not following" every single time.
        bool atTail = scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - TailSlack;
        if (!atTail)
            return;

        // One dispatcher hop, because the row this was raised for has not been
        // measured yet and ScrollToEnd against the old extent lands short.
        Dispatcher.UIThread.Post(scroller.ScrollToEnd, DispatcherPriority.Background);
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e) =>
        (DataContext as ShellModel)?.Output.Clear();
}
