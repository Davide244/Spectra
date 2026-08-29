using Avalonia.Controls;
using Avalonia.Interactivity;
using System;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The project's maps list. Raises clicks and menu verbs; opening, confirming,
/// manifest writes and path resolution stay with the window, which owns the
/// document and the session.
/// </summary>
public partial class MapsPanel : UserControl
{
    /// <summary>The user clicked a map row, or picked Open from its menu.</summary>
    public event Action<ProjectMapRow>? MapClicked;

    /// <summary>The user asked to make this map the project's startup map.</summary>
    public event Action<ProjectMapRow>? SetStartupRequested;

    /// <summary>The user asked to see the map bundle in the OS file browser.</summary>
    public event Action<ProjectMapRow>? RevealRequested;

    public MapsPanel()
    {
        InitializeComponent();
    }

    private void OnMapClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectMapRow row })
            MapClicked?.Invoke(row);
    }

    // Menu handlers read the row from the menu item's DataContext, inherited
    // from the row card the shared menu was opened over.
    private static ProjectMapRow? MenuRow(object? sender) =>
        (sender as Control)?.DataContext as ProjectMapRow;

    private void OnMenuOpen(object? sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row)
            MapClicked?.Invoke(row);
    }

    private void OnMenuSetStartup(object? sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row)
            SetStartupRequested?.Invoke(row);
    }

    private void OnMenuReveal(object? sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row)
            RevealRequested?.Invoke(row);
    }
}
