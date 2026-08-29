using Avalonia.Controls;
using Avalonia.Interactivity;
using System;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The project's maps list. Raises a click; opening, confirming and resolving
/// paths stay with the window, which owns the document and the session.
/// </summary>
public partial class MapsPanel : UserControl
{
    /// <summary>The user clicked a map row.</summary>
    public event Action<ProjectMapRow>? MapClicked;

    public MapsPanel()
    {
        InitializeComponent();
    }

    private void OnMapClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectMapRow row })
            MapClicked?.Invoke(row);
    }
}
