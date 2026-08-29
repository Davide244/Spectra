using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Serialization;
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
/// The shell's per-user state: today, the recent-projects list the start page
/// is built from. Stored under the user profile, never inside a project.
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
    }

    private void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
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
