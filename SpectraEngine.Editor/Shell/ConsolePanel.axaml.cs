using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The console's view: one input line, and the shared output above it.
/// </summary>
/// <remarks>
/// <b>Up and Down walk a history the panel owns.</b> Not the output log's
/// entries, which contain replies as well as commands and would make Up cycle
/// through error messages the user never typed.
/// </remarks>
public partial class ConsolePanel : UserControl
{
    private const int MaxHistory = 64;

    private readonly List<string> _history = [];
    private int _historyIndex;

    public ConsolePanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
    }

    /// <summary>Raised with a line the user submitted.</summary>
    /// <remarks>
    /// The panel resolves nothing itself: the window owns the session, so it
    /// owns the verb table. Same split as every other panel here.
    /// </remarks>
    public event Action<string>? CommandSubmitted;

    /// <summary>Puts the caret in the input line.</summary>
    public void FocusInput() => Input.Focus();

    private OutputLog? _log;

    private void Subscribe()
    {
        if (_log is not null)
            _log.Appended -= OnAppended;

        _log = (DataContext as ShellModel)?.Output;

        if (_log is not null)
            _log.Appended += OnAppended;
    }

    // The console always follows the tail, unlike the Output pane. A console is
    // a conversation: the reply to what you just typed is the only line that
    // matters, and it is always the last one.
    private void OnAppended(OutputEntry entry) =>
        Dispatcher.UIThread.Post(() => Scroller?.ScrollToEnd(), DispatcherPriority.Background);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Submit();
                e.Handled = true;
                break;

            case Key.Up:
                Recall(-1);
                e.Handled = true;
                break;

            case Key.Down:
                Recall(1);
                e.Handled = true;
                break;

            case Key.Escape:
                // Empties the line rather than closing anything: a console with
                // a half-typed command and no way to abandon it is the same
                // trap a property field without Escape has.
                Input.Text = string.Empty;
                e.Handled = true;
                break;
        }
    }

    private void Submit()
    {
        string line = Input.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(line))
            return;

        // Recorded before it runs, so a command that fails is still recallable -
        // which is the case where Up is most wanted, because the usual next step
        // is to fix a typo in it.
        _history.Remove(line);
        _history.Add(line);
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);

        _historyIndex = _history.Count;

        Input.Text = string.Empty;
        CommandSubmitted?.Invoke(line);
    }

    private void Recall(int direction)
    {
        if (_history.Count == 0)
            return;

        int index = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        _historyIndex = index;

        // Past the newest entry means a blank line, not the newest entry again:
        // otherwise Down has no way back to an empty prompt.
        Input.Text = index >= _history.Count ? string.Empty : _history[index];
        Input.CaretIndex = Input.Text.Length;
    }
}
