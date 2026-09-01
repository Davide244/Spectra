using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// One recent project as the start page shows it: the record, plus the labels
/// the row binds.
/// </summary>
/// <param name="Source">The stored entry, handed back on activation.</param>
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
/// <para>
/// <b>The filter is the page's own state, not the shell's.</b> It narrows a
/// list of at most a handful of rows that nothing else in the app displays, so
/// putting it on <c>ShellModel</c> beside the scene filter would be a second
/// meaning for the same word in the same session.
/// </para>
/// </remarks>
public partial class StartPage : UserControl
{
    /// <summary>The user asked to create a project.</summary>
    public event Action? NewProjectRequested;

    /// <summary>The user asked to browse for a project.</summary>
    public event Action? OpenProjectRequested;

    /// <summary>The user asked to open a loose map bundle, outside any project.</summary>
    public event Action? OpenMapRequested;

    /// <summary>The user activated a recent-project row.</summary>
    public event Action<RecentProject>? RecentProjectPicked;

    /// <summary>The user asked to drop a recent entry from the list.</summary>
    public event Action<RecentProject>? RecentProjectForgotten;

    /// <summary>The user asked to see a recent project in the OS file browser.</summary>
    public event Action<RecentProject>? RecentProjectRevealRequested;

    // Everything known, and the subset the filter admits. Kept apart so
    // typing never loses entries: the filter is a view, and clearing it must
    // bring the rest back without asking the shell to re-read its settings.
    private readonly List<RecentProjectRow> _all = [];
    private readonly List<RecentProjectRow> _shown = [];

    public StartPage()
    {
        InitializeComponent();
    }

    /// <summary>Rebuilds the recent list. Cheap: it is at most ten rows.</summary>
    public void ShowRecents(IReadOnlyList<RecentProject> recents)
    {
        ArgumentNullException.ThrowIfNull(recents);

        _all.Clear();
        foreach (RecentProject recent in recents)
            _all.Add(new RecentProjectRow(recent, recent.Name, recent.Path, OpenedLabel(recent.OpenedUtc)));

        ApplyFilter();
    }

    // "today", "yesterday", or the date: precise enough to pick between two
    // projects, short enough not to become the row's loudest text.
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

    private void ApplyFilter()
    {
        string query = (FilterBox.Text ?? string.Empty).Trim();

        _shown.Clear();
        foreach (RecentProjectRow row in _all)
        {
            // Name OR path: half of telling two projects apart is where they
            // live, so a filter that only searched names would be useless for
            // exactly the case a filter exists for.
            if (query.Length == 0 ||
                row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _shown.Add(row);
            }
        }

        // Assigned rather than patched: at most ten rows, rebuilt only when
        // the settings change or a key is typed, and nothing here has scroll
        // or selection worth preserving across a filter change.
        RecentList.ItemsSource = null;
        RecentList.ItemsSource = _shown;

        RecentList.IsVisible = _shown.Count > 0;

        // Every piece of chrome here is sized to what it has to show. Headings
        // appear once there is a column's worth of rows to head; the filter
        // appears once the list is longer than a glance; the empty state
        // replaces the whole thing rather than sitting above a void.
        ColumnHeadings.IsVisible = _shown.Count > 1;
        FilterBox.IsVisible = _all.Count > 4;
        EmptyState.IsVisible = _shown.Count == 0;
        EmptyActions.IsVisible = _all.Count == 0;
        FirstRunHelp.IsVisible = _all.Count == 0;

        // The empty state stands in for two different situations and has to
        // say which: a first launch, and a filter that matched nothing. The
        // body knew; the heading was a literal reading "Nothing open yet.",
        // which over a filter miss tells the user their projects are gone.
        bool firstRun = _all.Count == 0;
        EmptyTitle.Text = firstRun ? "Nothing open yet." : "No match.";
        EmptyLabel.Text = firstRun
            ? "Make a project to start building, or open one you already have. Projects you open appear in this list."
            : $"None of your recent projects match “{query}”.";
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                FilterBox.Text = string.Empty;
                e.Handled = true;
                break;

            // Down out of the box and Enter both hand over to the list, so a
            // filter-then-open is one uninterrupted keyboard gesture.
            case Key.Down when _shown.Count > 0:
                RecentList.SelectedIndex = 0;
                RecentList.Focus();
                e.Handled = true;
                break;

            case Key.Enter when _shown.Count > 0:
                RecentProjectPicked?.Invoke(_shown[0].Source);
                e.Handled = true;
                break;
        }
    }

    private void OnRecentActivated(object? sender, TappedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentProjectRow row)
            RecentProjectPicked?.Invoke(row.Source);
    }

    private void OnRecentKeyDown(object? sender, KeyEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentProjectRow row)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                RecentProjectPicked?.Invoke(row.Source);
                e.Handled = true;
                break;

            // Forgetting an entry, not deleting a project: the list is the
            // only thing this touches, which is why it needs no confirmation.
            case Key.Delete:
                RecentProjectForgotten?.Invoke(row.Source);
                e.Handled = true;
                break;
        }
    }

    private void OnNewProjectClicked(object? sender, RoutedEventArgs e) => NewProjectRequested?.Invoke();
    private void OnOpenProjectClicked(object? sender, RoutedEventArgs e) => OpenProjectRequested?.Invoke();
    private void OnOpenMapClicked(object? sender, RoutedEventArgs e) => OpenMapRequested?.Invoke();

    // Menu handlers read the row from the item's DataContext, inherited from
    // the row the shared menu was opened over.
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
