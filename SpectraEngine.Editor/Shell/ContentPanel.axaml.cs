using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The content browser's view. Every gesture resolves to a call on
/// <see cref="ContentBrowserModel"/>, or to an intent the window answers.
/// </summary>
public partial class ContentPanel : UserControl
{
    public ContentPanel() => InitializeComponent();

    /// <summary>
    /// Raised when the user activates a FILE (a folder is handled here, by
    /// descending into it).
    /// </summary>
    /// <remarks>
    /// An intent rather than an action, like every other panel in this shell:
    /// the panel knows what was double-clicked, and the window is the only
    /// thing that knows whether there is a session to insert it into.
    /// </remarks>
    public event Action<ContentEntry>? EntryActivated;

    private ContentBrowserModel? Model =>
        (DataContext as ShellModel)?.Content;

    private void OnUpClicked(object? sender, RoutedEventArgs e) => Model?.GoUp();

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => Model?.Refresh();

    private void OnEntryActivated(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ContentEntry entry })
            return;

        if (entry.IsFolder)
            Model?.Open(entry);
        else
            EntryActivated?.Invoke(entry);
    }
}
