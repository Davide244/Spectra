using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Collections.ObjectModel;

namespace SpectraEngine.Editor.Shell;

/// <summary>How loud one line in the output is.</summary>
public enum OutputSeverity
{
    /// <summary>Something happened and it worked.</summary>
    Info,

    /// <summary>Something is not right but the editor carried on.</summary>
    Warning,

    /// <summary>Something the user asked for did not happen.</summary>
    Error,

    /// <summary>A line the user typed into the console.</summary>
    Command,
}

/// <summary>One line in the output.</summary>
/// <param name="Severity">How loud it is.</param>
/// <param name="Text">What happened.</param>
/// <param name="TimeLabel">When, as <c>HH:mm:ss</c>.</param>
public sealed record OutputEntry(OutputSeverity Severity, string Text, string TimeLabel)
{
    /// <summary>Whether this line should carry the error colour.</summary>
    public bool IsError => Severity == OutputSeverity.Error;

    /// <summary>Whether this line should carry the warning colour.</summary>
    public bool IsWarning => Severity == OutputSeverity.Warning;

    /// <summary>Whether this line is something the user typed.</summary>
    public bool IsCommand => Severity == OutputSeverity.Command;

    /// <summary>
    /// The colour this line is set in, from the token dictionary.
    /// </summary>
    /// <remarks>
    /// <b>An error is TextDanger, not the accent.</b> The accent as text is
    /// 4.0:1 on the panel, which is not an error message - the split exists in
    /// the palette precisely so this row can be red and legible at the same
    /// time. A command echo is muted, because the reply beneath it is the part
    /// worth reading; the user already knows what they typed.
    /// </remarks>
    public IBrush? SeverityBrush => Resource(Severity switch
    {
        OutputSeverity.Error => "SpectraTextDanger",
        OutputSeverity.Warning => "SpectraMode",
        OutputSeverity.Command => "SpectraTextMuted",
        _ => "SpectraTextBody",
    });

    private static IBrush? Resource(string key)
        => Application.Current?.TryFindResource(key, out object? value) == true ? value as IBrush : null;
}

/// <summary>
/// The editor's diagnostic history: everything that used to be a single
/// status-bar sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole diagnostic surface of this application was one line of text that
/// anything could overwrite.</b> About thirty call sites wrote to it, none of
/// them knew what was already there, and a failure reported while the user was
/// looking elsewhere was gone by the time they looked back - which for a save
/// failure or a content error is the difference between a problem and a lost
/// afternoon. The status line still exists and still shows the newest entry;
/// what changed is that the entry before it survives.
/// </para>
/// <para>
/// <b>Bounded, like every other queue in this shell, and for the same reason.</b>
/// A background compile can report a content warning per frame, and an unbounded
/// list is a memory leak with a scrollbar. The oldest lines go first, which is
/// the right end to lose: the newest failure is the one being investigated.
/// </para>
/// <para>UI thread only.</para>
/// </remarks>
public sealed class OutputLog : ObservableObject
{
    /// <summary>How many lines are kept.</summary>
    public const int Capacity = 500;

    private int _errorCount;
    private int _warningCount;

    /// <summary>The lines, oldest first.</summary>
    public ObservableCollection<OutputEntry> Entries { get; } = [];

    /// <summary>How many errors are currently in the log.</summary>
    public int ErrorCount
    {
        get => _errorCount;
        private set
        {
            if (Set(ref _errorCount, value))
                Raise(nameof(ProblemSummary));
        }
    }

    /// <summary>How many warnings are currently in the log.</summary>
    public int WarningCount
    {
        get => _warningCount;
        private set
        {
            if (Set(ref _warningCount, value))
                Raise(nameof(ProblemSummary));
        }
    }

    /// <summary>A one-line count for the panel header and the status bar.</summary>
    public string ProblemSummary => (_errorCount, _warningCount) switch
    {
        (0, 0) => "no problems",
        (0, 1) => "1 warning",
        (0, var w) => $"{w} warnings",
        (1, 0) => "1 error",
        (var e, 0) => $"{e} errors",
        (1, 1) => "1 error, 1 warning",
        (var e, 1) => $"{e} errors, 1 warning",
        (1, var w) => $"1 error, {w} warnings",
        var (e, w) => $"{e} errors, {w} warnings",
    };

    /// <summary>Raised after an entry is appended, so a view can scroll to it.</summary>
    public event Action<OutputEntry>? Appended;

    /// <summary>Appends one line.</summary>
    public void Append(OutputSeverity severity, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // Wall-clock rather than a frame number: the reader is a person
        // correlating this against something they just did.
        var entry = new OutputEntry(severity, text, DateTime.Now.ToString("HH:mm:ss"));

        while (Entries.Count >= Capacity)
        {
            OutputEntry dropped = Entries[0];
            Entries.RemoveAt(0);

            if (dropped.Severity == OutputSeverity.Error)
                ErrorCount--;
            else if (dropped.Severity == OutputSeverity.Warning)
                WarningCount--;
        }

        Entries.Add(entry);

        if (severity == OutputSeverity.Error)
            ErrorCount++;
        else if (severity == OutputSeverity.Warning)
            WarningCount++;

        Appended?.Invoke(entry);
    }

    /// <summary>Empties the log.</summary>
    public void Clear()
    {
        Entries.Clear();
        ErrorCount = 0;
        WarningCount = 0;
    }
}
