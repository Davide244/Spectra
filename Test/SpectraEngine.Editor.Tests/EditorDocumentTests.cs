using SpectraEngine.Core.Projects;
using SpectraEngine.Editor.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// What the shell has open, and whether it has been edited since it was last
/// written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, which is why it gets tests at all.</b> The rest of the file menu is
/// dialogs and a render-thread command queue; this is the part that decides
/// whether somebody is warned before their work is thrown away, and it decides
/// it with no window, no dispatcher and no GPU.
/// </para>
/// </remarks>
public sealed class EditorDocumentTests
{
    [Fact]
    public void A_new_document_is_untitled_clean_and_has_no_project()
    {
        var document = new EditorDocument();

        document.IsDirty.ShouldBeFalse();
        document.HasProject.ShouldBeFalse();
        document.HasMapPath.ShouldBeFalse();
        document.MapLabel.ShouldBe("untitled");
        document.ProjectLabel.ShouldBe("no project");
    }

    [Fact]
    public void The_map_label_is_the_bundle_folder_rather_than_the_file_inside_it()
    {
        // A map IS a folder. Showing the document file would name map.json for
        // every level anybody ever opens.
        var document = new EditorDocument();

        document.MarkOpened(Path.Combine("C:", "Games", "MyGame", "Maps", "Lobby.smap"));

        document.MapLabel.ShouldBe("Lobby");
    }

    [Fact]
    public void A_trailing_separator_does_not_swallow_the_bundle_name()
    {
        // A folder picker can hand back a path with a trailing separator, and
        // GetFileNameWithoutExtension on that returns an empty string - which
        // would title the window with nothing at all.
        var document = new EditorDocument();

        document.MarkOpened(Path.Combine("C:", "Games", "Maps", "Arena.smap") + Path.DirectorySeparatorChar);

        document.MapLabel.ShouldBe("Arena");
    }

    [Fact]
    public void Editing_marks_the_document_dirty_and_saving_clears_it()
    {
        var document = new EditorDocument();
        document.MarkOpened(Path.Combine(Path.GetTempPath(), "Lobby.smap"));

        document.MarkDirty();
        document.IsDirty.ShouldBeTrue();

        document.MarkSaved(Path.Combine(Path.GetTempPath(), "Lobby.smap"));
        document.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Opening_and_starting_a_new_map_both_leave_the_document_clean()
    {
        // Neither is an edit. A load that left the document dirty would warn
        // about discarding work on the very next action, which trains people to
        // dismiss the warning that matters.
        var document = new EditorDocument();
        document.MarkDirty();

        document.MarkOpened(Path.Combine(Path.GetTempPath(), "Lobby.smap"));
        document.IsDirty.ShouldBeFalse();

        document.MarkDirty();
        document.MarkNew();
        document.IsDirty.ShouldBeFalse();
        document.HasMapPath.ShouldBeFalse("a new map has no file behind it yet");
    }

    [Fact]
    public void The_title_says_what_is_open_and_whether_it_is_saved()
    {
        var document = new EditorDocument();
        document.MarkOpened(Path.Combine(Path.GetTempPath(), "Lobby.smap"));

        document.Title.ShouldBe("Lobby - no project - Spectra Editor");

        document.MarkDirty();
        document.Title.ShouldBe("Lobby* - no project - Spectra Editor");
    }

    [Fact]
    public void The_title_changes_notify_so_the_window_can_follow_it()
    {
        // The window binds nothing here: it subscribes and assigns. Without the
        // notification the title would be written once at startup and then be
        // wrong for the rest of the session.
        var document = new EditorDocument();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)document).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        document.MarkDirty();

        raised.ShouldContain(nameof(EditorDocument.Title));
        raised.ShouldContain(nameof(EditorDocument.DirtyMark));
    }

    [Fact]
    public void An_unchanged_value_notifies_nothing()
    {
        var document = new EditorDocument();
        document.MarkDirty();

        var raised = new List<string?>();
        ((INotifyPropertyChanged)document).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        document.MarkDirty();

        raised.ShouldBeEmpty("marking an already-dirty document must not churn the binding layer");
    }

    // --- projects -----------------------------------------------------------

    [Fact]
    public void A_map_inside_the_project_gets_a_project_relative_path()
    {
        // The manifest holds project-relative paths, and that is the form a map
        // has to be listed in.
        using var temp = new TemporaryFolder();
        ProjectLayout project = ProjectLayout.Create(temp.Path, "MyGame");

        var document = new EditorDocument();
        document.SetProject(project);
        document.MarkOpened(Path.Combine(project.MapsPath, "Lobby.smap"));

        document.MapPathWithinProject().ShouldBe("Maps/Lobby.smap");
    }

    [Fact]
    public void A_map_outside_the_project_has_no_project_relative_path()
    {
        // Opening a bundle from somewhere else is perfectly legal and must not
        // be listed in the manifest as though it belonged: a relative path that
        // escapes the folder breaks the moment the project moves.
        using var temp = new TemporaryFolder();
        using var elsewhere = new TemporaryFolder();
        ProjectLayout project = ProjectLayout.Create(temp.Path, "MyGame");
        Directory.CreateDirectory(elsewhere.Path);

        var document = new EditorDocument();
        document.SetProject(project);
        document.MarkOpened(Path.Combine(elsewhere.Path, "Stray.smap"));

        document.MapPathWithinProject().ShouldBeNull();
    }

    [Fact]
    public void A_save_as_starts_in_the_projects_maps_folder_when_there_is_one()
    {
        using var temp = new TemporaryFolder();
        ProjectLayout project = ProjectLayout.Create(temp.Path, "MyGame");

        var document = new EditorDocument();
        document.SuggestedMapFolder.ShouldBeNull("nothing is open, so there is nowhere sensible to start");

        document.SetProject(project);
        document.SuggestedMapFolder.ShouldBe(project.MapsPath);
    }

    [Fact]
    public void Without_a_project_a_save_as_starts_beside_the_open_map()
    {
        var document = new EditorDocument();
        string folder = Path.Combine(Path.GetTempPath(), "levels");
        document.MarkOpened(Path.Combine(folder, "Lobby.smap"));

        document.SuggestedMapFolder.ShouldBe(Path.GetFullPath(folder));
    }

    [Fact]
    public void Opening_a_project_leaves_the_open_map_alone()
    {
        // The two are independent: a bundle can be open without a project, and
        // a project can be opened while one is.
        using var temp = new TemporaryFolder();
        ProjectLayout project = ProjectLayout.Create(temp.Path, "MyGame");

        var document = new EditorDocument();
        document.MarkOpened(Path.Combine(Path.GetTempPath(), "Lobby.smap"));
        document.SetProject(project);

        document.MapLabel.ShouldBe("Lobby");
        document.ProjectLabel.ShouldBe("MyGame");
        document.Title.ShouldBe("Lobby - MyGame - Spectra Editor");
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder() =>
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"spectra_doc_{Guid.NewGuid():N}");

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a temp directory that outlives the run is not a failure */ }
        }
    }
}
