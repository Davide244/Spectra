using Avalonia.Controls;
using Avalonia.Interactivity;
using SpectraEngine.Core;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// One recent project as the start page shows it: the record, plus the labels
/// the card binds.
/// </summary>
/// <param name="Source">The stored entry, handed back on click.</param>
/// <param name="Name">The project's display name.</param>
/// <param name="Path">The project folder, shown in full so two same-named projects tell apart.</param>
/// <param name="OpenedLabel">When it was last opened, as a short phrase.</param>
public sealed record RecentProjectRow(RecentProject Source, string Name, string Path, string OpenedLabel);

/// <summary>
/// The launch experience: recent projects, and the three ways to get something
/// open. Shown instead of the editor until a session exists.
/// </summary>
/// <remarks>
/// <b>Deliberately dumb.</b> It raises events and renders a list; every real
/// decision — pickers, dialogs, session lifetimes, settings writes — belongs
/// to the window, which owns the storage provider and the engine. A page that
/// opened projects itself would be a second copy of that logic, one modal
/// dialog away from drifting.
/// </remarks>
public partial class StartPage : UserControl
{
    /// <summary>The user asked to create a project.</summary>
    public event Action? NewProjectRequested;

    /// <summary>The user asked to browse for a project.</summary>
    public event Action? OpenProjectRequested;

    /// <summary>The user asked to open a loose map bundle, outside any project.</summary>
    public event Action? OpenMapRequested;

    /// <summary>The user clicked a recent-project card.</summary>
    public event Action<RecentProject>? RecentProjectPicked;

    /// <summary>The user asked to drop a recent entry from the list.</summary>
    public event Action<RecentProject>? RecentProjectForgotten;

    /// <summary>The user asked to see a recent project in the OS file browser.</summary>
    public event Action<RecentProject>? RecentProjectRevealRequested;

    public StartPage()
    {
        InitializeComponent();
        VersionLabel.Text = EngineInfo.VersionString;
    }

    /// <summary>Rebuilds the recent list. Cheap: it is at most ten cards.</summary>
    public void ShowRecents(IReadOnlyList<RecentProject> recents)
    {
        ArgumentNullException.ThrowIfNull(recents);

        var rows = new List<RecentProjectRow>(recents.Count);
        foreach (RecentProject recent in recents)
            rows.Add(new RecentProjectRow(recent, recent.Name, recent.Path, OpenedLabel(recent.OpenedUtc)));

        RecentList.ItemsSource = rows;
        EmptyLabel.IsVisible = rows.Count == 0;
        RecentCountLabel.Text = rows.Count == 0 ? string.Empty : rows.Count.ToString();
    }

    // "today", "yesterday", or the date: precise enough to pick between two
    // projects, short enough not to become the card's loudest text.
    private static string OpenedLabel(DateTime openedUtc)
    {
        if (openedUtc == DateTime.MinValue)
            return string.Empty;

        DateTime local = openedUtc.ToLocalTime();
        int days = (DateTime.Now.Date - local.Date).Days;
        return days switch
        {
            <= 0 => $"today {local:HH:mm}",
            1 => "yesterday",
            < 7 => $"{days} days ago",
            _ => local.ToString("yyyy-MM-dd"),
        };
    }

    private void OnNewProjectClicked(object? sender, RoutedEventArgs e) => NewProjectRequested?.Invoke();
    private void OnOpenProjectClicked(object? sender, RoutedEventArgs e) => OpenProjectRequested?.Invoke();
    private void OnOpenMapClicked(object? sender, RoutedEventArgs e) => OpenMapRequested?.Invoke();

    private void OnRecentClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RecentProjectRow row })
            RecentProjectPicked?.Invoke(row.Source);
    }

    // Menu handlers read the row from the item's DataContext, inherited from
    // the card the shared menu was opened over.
    private static RecentProjectRow? MenuRow(object? sender) =>
        (sender as Control)?.DataContext as RecentProjectRow;

    private void OnRecentMenuOpen(object? sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row)
            RecentProjectPicked?.Invoke(row.Source);
    }

    private void OnRecentMenuReveal(object? sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row)
            RecentProjectRevealRequested?.Invoke(row.Source);
    }

    private void OnRecentMenuForget(object? sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row)
            RecentProjectForgotten?.Invoke(row.Source);
    }
}
