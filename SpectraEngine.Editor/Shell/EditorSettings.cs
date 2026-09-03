using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Serialization;
using SpectraEngine.Editor.Viewport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// One project the shell has opened before: where it lives, what to call it on
/// a card, and when it was last touched.
/// </summary>
public sealed record RecentProject(string Path, string Name, DateTime OpenedUtc);

/// <summary>
/// The shell's per-user state: the recent-projects list the start page is built
/// from, and which viewport this machine has earned. Stored under the user
/// profile, never inside a project.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-rolled UTF-8 JSON, like every other document this engine reads.</b>
/// The obvious serializer discovers members by reflection, which is exactly
/// what trimming removes; the codec below names each member once and is
/// AOT-safe by construction. Unknown members are skipped rather than
/// preserved, deliberately — this is a per-user cache whose only reader is the
/// shell, not an authored document with the round-trip promise the map and
/// project codecs carry.
/// </para>
/// <para>
/// <b>A missing or corrupt file is an empty list, never an error.</b> The
/// settings are a convenience; a shell that refused to start over a damaged
/// recents cache would have turned a nicety into a dependency. The next save
/// rewrites the file whole.
/// </para>
/// <para>
/// UI thread only, like the dialogs and the file pickers beside it.
/// </para>
/// </remarks>
public sealed class EditorSettings
{
    // Enough that nothing anyone works on falls off, few enough that the start
    // page stays a list of things rather than a history.
    private const int MaxRecentProjects = 10;

    private readonly List<RecentProject> _recentProjects = [];

    /// <summary>Most recently opened first.</summary>
    public IReadOnlyList<RecentProject> RecentProjects => _recentProjects;

    /// <summary>Where the settings live for this user.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Spectra", "editor.json");

    /// <summary>
    /// Records that a project was opened or created, moving it to the front.
    /// </summary>
    /// <remarks>
    /// Deduplicated by full path, case-insensitively, because this file only
    /// exists on Windows-cased filesystems today and two spellings of one
    /// folder as two cards is exactly the confusion a recents list exists to
    /// prevent.
    /// </remarks>
    public void TouchProject(string path, string name, DateTime openedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string full = Path.GetFullPath(path);
        _recentProjects.RemoveAll(p =>
            string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase));

        _recentProjects.Insert(0, new RecentProject(full, name, openedUtc));
        _forgotten.Remove(full);
        if (_recentProjects.Count > MaxRecentProjects)
            _recentProjects.RemoveRange(MaxRecentProjects, _recentProjects.Count - MaxRecentProjects);
    }

    /// <summary>Drops one entry, for a card whose folder no longer exists.</summary>
    public void ForgetProject(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        _recentProjects.RemoveAll(p =>
            string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase));

        // Remembered so the save-time merge cannot resurrect it from another
        // shell's stale copy of the file: forgetting is this session's
        // deliberate decision. Touching the same project again clears it,
        // because opening IS the new information.
        _forgotten.Add(full);
    }

    // Paths this session explicitly forgot, exempt from the save-time merge.
    private readonly HashSet<string> _forgotten = new(StringComparer.OrdinalIgnoreCase);

    // --- The viewport's persisted half ---------------------------------------

    private ViewportPreference _viewport = ViewportPreference.Default;
    private DateTime _viewportRecordedUtc = DateTime.MinValue;

    /// <summary>
    /// What was asked for and what this machine has earned. See
    /// <see cref="ViewportModePolicy"/>, which owns every rule about it.
    /// </summary>
    /// <remarks>
    /// <b>Stored per user rather than per project</b>, because it is a fact
    /// about the machine and its driver: opening a different project does not
    /// change whether this GPU can composite an engine frame.
    /// </remarks>
    public ViewportPreference ViewportPreference => _viewport;

    /// <summary>
    /// Records which viewport to ask for from now on.
    /// </summary>
    /// <remarks>
    /// <b><c>--viewport=</c> is a preference rather than a one-run override, and
    /// that is deliberate.</b> There is no UI for this yet, so the switch is the
    /// only way to express it, and a switch whose effect vanished on the next
    /// launch would mean typing it forever. <c>--viewport=auto</c> is how it is
    /// put back, which is why auto is a value somebody can name rather than
    /// merely the default.
    /// </remarks>
    public void SetViewportMode(ViewportMode mode)
    {
        if (_viewport.Mode == mode)
            return;

        _viewport = _viewport with { Mode = mode };
        _viewportRecordedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Re-anchors the composited history on the machine that is actually here,
    /// through <see cref="ViewportModePolicy.Rebase"/>.
    /// </summary>
    /// <remarks>
    /// Called only when the machine was measured. An unmeasured launch knows
    /// nothing about the adapter and would overwrite a real history with empty
    /// strings, which reads afterwards as an adapter that changed.
    /// </remarks>
    public void RebaseViewport(string adapterLuid, string driverVersion)
    {
        ViewportPreference rebased = ViewportModePolicy.Rebase(_viewport, adapterLuid, driverVersion);
        if (rebased == _viewport)
            return;

        _viewport = rebased;
        _viewportRecordedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Folds one finished COMPOSITED session into the history: one longer if it
    /// was green, back to zero if it was not.
    /// </summary>
    /// <remarks>
    /// A native session is never recorded here. It says nothing either way about
    /// the composited path, and counting one would let a machine earn the flip
    /// without ever having composited a frame.
    /// </remarks>
    public void RecordCompositedSession(bool sessionGreen)
    {
        _viewport = ViewportModePolicy.Record(_viewport, sessionGreen);
        _viewportRecordedUtc = DateTime.UtcNow;
    }

    // --- The ribbon's persisted half -----------------------------------------

    private bool _ribbonExpanded = true;
    private DateTime _ribbonRecordedUtc = DateTime.MinValue;

    /// <summary>
    /// Whether the command ribbon is pinned open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A surface whose size resets every launch is a preference nobody
    /// keeps.</b> Collapsing the ribbon gives about seventy pixels back to the
    /// viewport, which is a choice somebody makes once about how they work, not
    /// per session.
    /// </para>
    /// <para>
    /// <b>Open by default, and the ACTIVE TAB is deliberately not stored beside
    /// it.</b> A shell that reopened on the View page would start every session
    /// with Insert hidden behind a click, which is precisely the failure that
    /// retired the previous tab strip; every launch therefore opens on
    /// <c>RibbonLayout.DefaultTabId</c>.
    /// </para>
    /// </remarks>
    public bool RibbonExpanded => _ribbonExpanded;

    /// <summary>Records the pin state for next time.</summary>
    public void SetRibbonExpanded(bool expanded)
    {
        if (_ribbonExpanded == expanded)
            return;

        _ribbonExpanded = expanded;
        _ribbonRecordedUtc = DateTime.UtcNow;
    }

    /// <summary>Loads the settings, or returns empty ones when there is nothing to load.</summary>
    public static EditorSettings Load(ILogger logger) => Load(DefaultPath, logger);

    /// <summary>Loads from an explicit path, for tests.</summary>
    public static EditorSettings Load(string path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var settings = new EditorSettings();
        if (!File.Exists(path))
            return settings;

        try
        {
            settings.Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (IsSettingsReadFailure(ex))
        {
            // Said out loud, then started fresh: silently losing the list looks
            // identical to a bug in the list.
            logger.LogWarning(ex, "Could not read editor settings at {Path}; starting fresh", path);
            settings._recentProjects.Clear();

            // The viewport history goes with it. A half-read block would be a
            // count with no adapter behind it, which reads as "proven on this
            // machine" the next time the adapter happens to match nothing.
            settings._viewport = ViewportPreference.Default;
            settings._viewportRecordedUtc = DateTime.MinValue;

            // And the ribbon, back to open: the state that shows what the
            // surface can do is the right one to fall back to.
            settings._ribbonExpanded = true;
            settings._ribbonRecordedUtc = DateTime.MinValue;
        }

        return settings;
    }

    // Everything a damaged file can throw, in one place so the load and the
    // merge agree. InvalidOperationException and FormatException are the
    // reader's answers to a member holding the WRONG TYPE ("path": 5), which
    // is exactly as recoverable as invalid JSON and must not crash a startup.
    private static bool IsSettingsReadFailure(Exception ex) =>
        ex is JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or FormatException;

    /// <summary>Writes the settings, creating the folder on first use.</summary>
    /// <remarks>
    /// Failures are logged rather than thrown: a full disk must not turn
    /// "open a project" into an error dialog about a recents cache.
    /// </remarks>
    public void Save(ILogger logger) => Save(DefaultPath, logger);

    /// <summary>Saves to an explicit path, for tests.</summary>
    public void Save(string path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);

            MergeFromDisk(path);
            File.WriteAllBytes(path, CanonicalJson.Write(Write));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write editor settings to {Path}", path);
        }
    }

    /// <summary>
    /// Folds in whatever another shell wrote since this one loaded, newest
    /// touch per project winning, so two editors do not take turns erasing
    /// each other's history.
    /// </summary>
    /// <remarks>
    /// Best-effort, like everything else here: an unreadable file merges
    /// nothing and the write proceeds, because the alternative is a recents
    /// cache that can block saving itself.
    /// </remarks>
    private void MergeFromDisk(string path)
    {
        if (!File.Exists(path))
            return;

        var onDisk = new EditorSettings();
        try
        {
            onDisk.Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (IsSettingsReadFailure(ex))
        {
            return;
        }

        foreach (RecentProject theirs in onDisk._recentProjects)
        {
            if (_forgotten.Contains(theirs.Path))
                continue;

            int mine = _recentProjects.FindIndex(p =>
                string.Equals(p.Path, theirs.Path, StringComparison.OrdinalIgnoreCase));

            if (mine < 0)
                _recentProjects.Add(theirs);
            else if (theirs.OpenedUtc > _recentProjects[mine].OpenedUtc)
                _recentProjects[mine] = theirs;
        }

        _recentProjects.Sort((a, b) => b.OpenedUtc.CompareTo(a.OpenedUtc));
        if (_recentProjects.Count > MaxRecentProjects)
            _recentProjects.RemoveRange(MaxRecentProjects, _recentProjects.Count - MaxRecentProjects);

        // The viewport block is one state, not a set of entries, so it merges by
        // RECENCY rather than element by element. Two shells that both ran a
        // composited session would otherwise interleave their counts into a
        // number neither of them measured.
        if (onDisk._viewportRecordedUtc > _viewportRecordedUtc)
        {
            _viewport = onDisk._viewport;
            _viewportRecordedUtc = onDisk._viewportRecordedUtc;
        }

        // The ribbon's pin merges by recency for the same reason: it is one
        // state rather than a set of entries, and the shell that touched it
        // last is the one the user was looking at.
        if (onDisk._ribbonRecordedUtc > _ribbonRecordedUtc)
        {
            _ribbonExpanded = onDisk._ribbonExpanded;
            _ribbonRecordedUtc = onDisk._ribbonRecordedUtc;
        }
    }

    private void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        writer.WriteStartObject("viewport");
        writer.WriteString("mode", ViewportModePolicy.NameOf(_viewport.Mode));
        writer.WriteNumber("greenSessions", _viewport.GreenSessions);
        writer.WriteString("adapterLuid", _viewport.AdapterLuid);
        writer.WriteString("driverVersion", _viewport.DriverVersion);
        writer.WriteString("recordedUtc", _viewportRecordedUtc.ToString("O"));
        writer.WriteEndObject();

        writer.WriteStartObject("ribbon");
        writer.WriteBoolean("expanded", _ribbonExpanded);
        writer.WriteString("recordedUtc", _ribbonRecordedUtc.ToString("O"));
        writer.WriteEndObject();

        writer.WriteStartArray("recentProjects");
        foreach (RecentProject project in _recentProjects)
        {
            writer.WriteStartObject();
            writer.WriteString("path", project.Path);
            writer.WriteString("name", project.Name);
            writer.WriteString("openedUtc", project.OpenedUtc.ToString("O"));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void Read(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(CanonicalJson.StripBom(utf8), CanonicalJson.ReaderOptions);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The settings root must be an object.");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("recentProjects"))
            {
                ReadRecentProjects(ref reader);
            }
            else if (reader.ValueTextEquals("viewport"))
            {
                ReadViewport(ref reader);
            }
            else if (reader.ValueTextEquals("ribbon"))
            {
                ReadRibbon(ref reader);
            }
            else
            {
                // A member a newer shell wrote. Skipped, not preserved: the
                // next save is that newer shell's problem, and this file makes
                // no round-trip promise.
                reader.Read();
                reader.Skip();
            }
        }
    }

    /// <summary>
    /// Reads the viewport block, leaving anything it cannot make sense of at its
    /// default.
    /// </summary>
    /// <remarks>
    /// <b>A mode word this build does not know falls back to auto rather than
    /// failing the file.</b> The one thing that must not happen is a settings
    /// file written by a newer shell stopping this one from starting, and auto
    /// is the conservative answer by construction: it resolves to the native
    /// child until a history says otherwise, and the history is read separately.
    /// </remarks>
    private void ReadViewport(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("'viewport' must be an object.");

        ViewportMode mode = ViewportPreference.Default.Mode;
        int green = 0;
        string luid = string.Empty;
        string driver = string.Empty;
        DateTime recorded = DateTime.MinValue;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("mode"))
            {
                reader.Read();
                ViewportModePolicy.TryParseMode(reader.GetString(), out mode);
            }
            else if (reader.ValueTextEquals("greenSessions"))
            {
                reader.Read();
                green = Math.Max(0, reader.GetInt32());
            }
            else if (reader.ValueTextEquals("adapterLuid"))
            {
                reader.Read();
                luid = reader.GetString() ?? string.Empty;
            }
            else if (reader.ValueTextEquals("driverVersion"))
            {
                reader.Read();
                driver = reader.GetString() ?? string.Empty;
            }
            else if (reader.ValueTextEquals("recordedUtc"))
            {
                reader.Read();
                DateTime.TryParse(
                    reader.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out recorded);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        _viewport = new ViewportPreference(mode, green, luid, driver);
        _viewportRecordedUtc = recorded;
    }

    /// <summary>
    /// Reads the ribbon block, leaving anything it cannot make sense of at its
    /// default.
    /// </summary>
    /// <remarks>
    /// Open is the conservative fallback: a collapsed ribbon read out of a
    /// damaged file would hide the command surface with no explanation on
    /// screen, while an open one merely takes space somebody can take back with
    /// one click.
    /// </remarks>
    private void ReadRibbon(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("'ribbon' must be an object.");

        bool expanded = true;
        DateTime recorded = DateTime.MinValue;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("expanded"))
            {
                reader.Read();
                expanded = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("recordedUtc"))
            {
                reader.Read();
                DateTime.TryParse(
                    reader.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out recorded);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        _ribbonExpanded = expanded;
        _ribbonRecordedUtc = recorded;
    }

    private void ReadRecentProjects(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("'recentProjects' must be an array.");

        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            string? path = null;
            string? name = null;
            DateTime opened = DateTime.MinValue;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("path"))
                {
                    reader.Read();
                    path = reader.GetString();
                }
                else if (reader.ValueTextEquals("name"))
                {
                    reader.Read();
                    name = reader.GetString();
                }
                else if (reader.ValueTextEquals("openedUtc"))
                {
                    reader.Read();
                    DateTime.TryParse(
                        reader.GetString(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out opened);
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            // An entry missing its essentials is dropped, not fatal: the rest
            // of the list is still worth having.
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(name)
                && _recentProjects.Count < MaxRecentProjects)
            {
                _recentProjects.Add(new RecentProject(path, name, opened));
            }
        }
    }
}
