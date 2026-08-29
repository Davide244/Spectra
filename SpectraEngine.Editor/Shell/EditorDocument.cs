using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Projects;
using System.IO;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// What the shell currently has open: a project, a map bundle, and whether the
/// scene has been edited since it was last written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves are optional and independent.</b> A map can be opened without
/// a project, because a level designer handed one bundle is a real case and
/// making them scaffold a project first would be ceremony. A project can be
/// open with no map yet, because that is what a new project is. The pairing
/// only decides where a "Save as" starts browsing.
/// </para>
/// <para>
/// <b>Dirty is set by any movement of the undo history, and cleared only by a
/// save or a load.</b> That is deliberately conservative rather than exact: it
/// stays dirty after an undo back to the state that was saved, which costs a
/// redundant write, and the alternative errs the other way. Undo depth alone
/// cannot tell the two apart, since editing after an undo returns to the same
/// depth with different content, so the only exact answer would be a content
/// hash of the whole scene on every frame.
/// </para>
/// </remarks>
public sealed class EditorDocument : ObservableObject
{
    private ProjectLayout? _project;
    private string? _mapPath;
    private bool _isDirty;

    /// <summary>The open project, or null when a bundle was opened on its own.</summary>
    public ProjectLayout? Project
    {
        get => _project;
        private set
        {
            if (!Set(ref _project, value)) return;
            Raise(nameof(HasProject));
            Raise(nameof(ProjectLabel));
            Raise(nameof(Title));
        }
    }

    /// <summary>Full path of the open map bundle, or null when it has never been saved.</summary>
    public string? MapPath
    {
        get => _mapPath;
        private set
        {
            if (!Set(ref _mapPath, value)) return;
            Raise(nameof(HasMapPath));
            Raise(nameof(MapLabel));
            Raise(nameof(Title));
        }
    }

    /// <summary>Whether the scene has been edited since it was last written.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!Set(ref _isDirty, value)) return;
            Raise(nameof(Title));
            Raise(nameof(DirtyMark));
        }
    }

    public bool HasProject => _project is not null;

    public bool HasMapPath => _mapPath is not null;

    /// <summary>The project's name, or a placeholder when none is open.</summary>
    public string ProjectLabel => _project?.Project.Name ?? "no project";

    /// <summary>
    /// The bundle's folder name without its extension, or "untitled".
    /// </summary>
    /// <remarks>
    /// The folder name, because a map IS a folder: showing <c>map.json</c>
    /// would name the same file for every level anyone ever opens.
    /// </remarks>
    public string MapLabel => _mapPath is null
        ? "untitled"
        : Path.GetFileNameWithoutExtension(_mapPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>An asterisk while unsaved, so the title reads at a glance.</summary>
    public string DirtyMark => _isDirty ? "*" : string.Empty;

    /// <summary>The window title: what is open, whether it is saved, and the app.</summary>
    public string Title => $"{MapLabel}{DirtyMark} - {ProjectLabel} - Spectra Editor";

    /// <summary>
    /// Where a save-as should start browsing: the project's Maps folder when
    /// there is one, otherwise beside whatever is already open.
    /// </summary>
    public string? SuggestedMapFolder => _project?.MapsPath
        ?? (_mapPath is null ? null : Path.GetDirectoryName(_mapPath));

    /// <summary>Records that the scene has been edited.</summary>
    public void MarkDirty() => IsDirty = true;

    /// <summary>Records a successful write of <paramref name="mapPath"/>.</summary>
    public void MarkSaved(string mapPath)
    {
        MapPath = Path.GetFullPath(mapPath);
        IsDirty = false;
    }

    /// <summary>Records that a bundle was loaded and is therefore unedited.</summary>
    public void MarkOpened(string mapPath)
    {
        MapPath = Path.GetFullPath(mapPath);
        IsDirty = false;
    }

    /// <summary>Records a scene with no file behind it yet.</summary>
    public void MarkNew()
    {
        MapPath = null;
        IsDirty = false;
    }

    /// <summary>Opens a project, leaving the map alone.</summary>
    public void SetProject(ProjectLayout? project) => Project = project;

    /// <summary>
    /// The project-relative form of the open map, or null when the map is not
    /// inside the open project.
    /// </summary>
    /// <remarks>
    /// A map opened from outside the project folder is perfectly legal and must
    /// not be listed in the manifest as though it were part of the project: the
    /// manifest holds project-relative paths, and one that escapes the folder
    /// would break the moment the project moved.
    /// </remarks>
    public string? MapPathWithinProject()
    {
        if (_project is null || _mapPath is null) return null;

        string relative = Path.GetRelativePath(_project.Root, _mapPath);
        if (relative.StartsWith("..", System.StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Convenience for the codec's bundle extension.</summary>
    public static string BundleExtension => MapFormat.BundleExtension;
}
