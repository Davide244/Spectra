using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Editor.Shell;
using System;
using System.IO;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The per-user recents store behind the start page.
/// </summary>
/// <remarks>
/// <b>The claims worth pinning are the forgiving ones.</b> A recents cache
/// must never become a reason the shell cannot start: a missing file is an
/// empty list, a corrupt file is an empty list with a warning, and a bad
/// entry inside an otherwise good file costs that entry and nothing else.
/// The round trip itself matters less — it is a cache — but a list that
/// reordered or duplicated across restarts would read as the shell forgetting
/// things, which is the one impression a recents list must not give.
/// </remarks>
public sealed class EditorSettingsTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "spectra-tests", Path.GetRandomFileName(), "editor.json");

    [Fact]
    public void Touching_a_project_puts_it_first_and_deduplicates_by_path()
    {
        var settings = new EditorSettings();

        settings.TouchProject(@"C:\Games\Alpha", "Alpha", new DateTime(2026, 8, 1));
        settings.TouchProject(@"C:\Games\Beta", "Beta", new DateTime(2026, 8, 2));

        // Same folder, different spelling: one card, moved to the front, not
        // two cards for one project.
        settings.TouchProject(@"C:\GAMES\ALPHA", "Alpha", new DateTime(2026, 8, 3));

        settings.RecentProjects.Count.ShouldBe(2);
        settings.RecentProjects[0].Name.ShouldBe("Alpha");
        settings.RecentProjects[1].Name.ShouldBe("Beta");
    }

    [Fact]
    public void The_list_is_capped_rather_than_unbounded()
    {
        var settings = new EditorSettings();
        for (int i = 0; i < 25; i++)
            settings.TouchProject($@"C:\Games\P{i}", $"P{i}", new DateTime(2026, 1, 1).AddDays(i));

        settings.RecentProjects.Count.ShouldBe(10);
        settings.RecentProjects[0].Name.ShouldBe("P24", "newest first");
    }

    [Fact]
    public void Settings_round_trip_through_disk_in_order()
    {
        string path = TempPath();
        var settings = new EditorSettings();
        settings.TouchProject(@"C:\Games\Alpha", "Alpha", new DateTime(2026, 8, 1, 10, 30, 0, DateTimeKind.Utc));
        settings.TouchProject(@"C:\Games\Beta", "Beta", new DateTime(2026, 8, 2, 11, 0, 0, DateTimeKind.Utc));

        settings.Save(path, NullLogger.Instance);
        EditorSettings loaded = EditorSettings.Load(path, NullLogger.Instance);

        loaded.RecentProjects.Count.ShouldBe(2);
        loaded.RecentProjects[0].Name.ShouldBe("Beta");
        loaded.RecentProjects[0].Path.ShouldBe(@"C:\Games\Beta");
        loaded.RecentProjects[0].OpenedUtc.ShouldBe(new DateTime(2026, 8, 2, 11, 0, 0, DateTimeKind.Utc));
        loaded.RecentProjects[1].Name.ShouldBe("Alpha");
    }

    [Fact]
    public void A_missing_file_is_an_empty_list_not_an_error()
    {
        EditorSettings loaded = EditorSettings.Load(TempPath(), NullLogger.Instance);
        loaded.RecentProjects.ShouldBeEmpty();
    }

    [Fact]
    public void A_corrupt_file_is_an_empty_list_not_an_error()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        EditorSettings loaded = EditorSettings.Load(path, NullLogger.Instance);
        loaded.RecentProjects.ShouldBeEmpty();
    }

    [Fact]
    public void An_entry_missing_its_essentials_is_dropped_alone()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "recentProjects": [
                { "name": "NoPath" },
                { "path": "C:\\Games\\Good", "name": "Good", "openedUtc": "2026-08-01T10:00:00.0000000Z" }
              ]
            }
            """);

        EditorSettings loaded = EditorSettings.Load(path, NullLogger.Instance);

        loaded.RecentProjects.Count.ShouldBe(1);
        loaded.RecentProjects[0].Name.ShouldBe("Good");
    }

    [Fact]
    public void Forgetting_a_project_removes_exactly_that_card()
    {
        var settings = new EditorSettings();
        settings.TouchProject(@"C:\Games\Alpha", "Alpha", DateTime.UtcNow);
        settings.TouchProject(@"C:\Games\Beta", "Beta", DateTime.UtcNow);

        settings.ForgetProject(@"C:\games\alpha");

        settings.RecentProjects.Count.ShouldBe(1);
        settings.RecentProjects[0].Name.ShouldBe("Beta");
    }

    [Fact]
    public void A_wrong_typed_member_is_an_empty_list_not_a_crash()
    {
        // Valid JSON of the wrong SHAPE is a different exception class from
        // invalid JSON, and a settings cache must survive both: it is a
        // convenience, not a dependency the shell can refuse to start over.
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "recentProjects": [ { "path": 5, "name": true } ] }""");

        EditorSettings loaded = EditorSettings.Load(path, NullLogger.Instance);
        loaded.RecentProjects.ShouldBeEmpty();
    }

    [Fact]
    public void Two_shells_merge_on_save_rather_than_clobbering_each_other()
    {
        // Both shells load the same (empty) file, each opens its own project,
        // each saves. Last-writer-wins would erase the first shell's entry;
        // the save-time merge keeps both, newest touch first.
        string path = TempPath();

        var first = EditorSettings.Load(path, NullLogger.Instance);
        var second = EditorSettings.Load(path, NullLogger.Instance);

        first.TouchProject(@"C:\Games\Alpha", "Alpha", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        first.Save(path, NullLogger.Instance);

        second.TouchProject(@"C:\Games\Beta", "Beta", new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc));
        second.Save(path, NullLogger.Instance);

        EditorSettings merged = EditorSettings.Load(path, NullLogger.Instance);
        merged.RecentProjects.Count.ShouldBe(2);
        merged.RecentProjects[0].Name.ShouldBe("Beta");
        merged.RecentProjects[1].Name.ShouldBe("Alpha");
    }

    [Fact]
    public void A_forgotten_project_is_not_resurrected_by_the_merge()
    {
        // Forgetting happens because the folder is gone; the copy of the file
        // on disk still lists it, and the merge folding it back in would make
        // the dead card immortal.
        string path = TempPath();

        var settings = new EditorSettings();
        settings.TouchProject(@"C:\Games\Gone", "Gone", DateTime.UtcNow);
        settings.Save(path, NullLogger.Instance);

        settings.ForgetProject(@"C:\Games\Gone");
        settings.Save(path, NullLogger.Instance);

        EditorSettings loaded = EditorSettings.Load(path, NullLogger.Instance);
        loaded.RecentProjects.ShouldBeEmpty();
    }
}
