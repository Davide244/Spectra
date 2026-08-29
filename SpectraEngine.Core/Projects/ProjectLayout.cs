using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Serialization;
using System;
using System.Collections.Generic;
using System.IO;

namespace SpectraEngine.Core.Projects;

/// <summary>
/// A project on disk: the folder, its manifest, and where things live inside
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout is a contract, not a convention.</b> A person opening the
/// folder in VS Code, a cook walking it, and the editor listing maps all have
/// to agree on where things are, and the only way three tools agree is if one
/// place says so.
/// </para>
/// <code>
/// MyGame/
///   MyGame.spectraproj    the manifest, and the double-clickable identity
///   Assets/               the content root, unchanged
///   Maps/                 Lobby.smap/, Arena.smap/  (folders, not files)
///   Scripts/              shared script modules
///   cooked/               cook output; derived, gitignored, never authored
/// </code>
/// <para>
/// <b>Nothing authored here is binary.</b> The manifest, the map bundles, the
/// materials and the shader sources are all text; binary exists only under
/// <c>cooked/</c> as build output. That is the one lesson taken hardest from
/// the platform this engine is aimed at, where an opaque place file is the
/// reason an entire third-party sync tool had to exist.
/// </para>
/// </remarks>
public sealed class ProjectLayout
{
    private ProjectLayout(string root, string manifestPath, SpectraProject project)
    {
        Root = root;
        ManifestPath = manifestPath;
        Project = project;
    }

    /// <summary>The project folder.</summary>
    public string Root { get; }

    /// <summary>Full path of the <c>.spectraproj</c> manifest.</summary>
    public string ManifestPath { get; }

    /// <summary>The manifest's contents.</summary>
    public SpectraProject Project { get; }

    public string AssetsPath => Path.Combine(Root, ProjectFormat.AssetsFolder);
    public string MapsPath => Path.Combine(Root, ProjectFormat.MapsFolder);
    public string ScriptsPath => Path.Combine(Root, ProjectFormat.ScriptsFolder);
    public string CookedPath => Path.Combine(Root, ProjectFormat.CookedFolder);

    /// <summary>Resolves a project-relative path (a map bundle, say) to a full one.</summary>
    public string Resolve(string projectRelativePath) =>
        Path.Combine(Root, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Opens the project whose manifest is at <paramref name="manifestPath"/>,
    /// or which lives in the folder <paramref name="manifestPath"/> names.
    /// </summary>
    /// <remarks>
    /// Both are accepted because both are what a person means. Double-clicking
    /// gives the file; dragging a folder onto the editor, or typing a path on a
    /// command line, gives the directory.
    /// </remarks>
    /// <exception cref="FileNotFoundException">No manifest was found.</exception>
    /// <exception cref="ProjectFormatException">The manifest is malformed.</exception>
    public static ProjectLayout Open(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        string resolved = Directory.Exists(manifestPath)
            ? FindManifest(manifestPath)
            : manifestPath;

        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException(
                $"'{manifestPath}' is not a Spectra project: no {ProjectFormat.Extension} file.", resolved);
        }

        string root = Path.GetDirectoryName(Path.GetFullPath(resolved))
            ?? throw new FileNotFoundException($"'{resolved}' has no containing folder.", resolved);

        return new ProjectLayout(root, Path.GetFullPath(resolved), ProjectReader.Read(File.ReadAllBytes(resolved)));
    }

    /// <summary>
    /// Writes the manifest back, leaving it alone when nothing changed.
    /// </summary>
    /// <returns>True when the file was written; false when it was already byte-identical.</returns>
    public bool Save()
    {
        byte[] content = ProjectWriter.Write(Project);
        if (File.Exists(ManifestPath) && File.ReadAllBytes(ManifestPath).AsSpan().SequenceEqual(content))
            return false;

        // Temp file plus rename, exactly as a map bundle saves: a crash between
        // the open and the last byte must not leave half a manifest where a
        // whole one was, because a project that will not open is worse than one
        // with a stale field in it.
        string temporary = ManifestPath + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, ManifestPath, overwrite: true);
        return true;
    }

    /// <summary>
    /// Creates a project folder with the canonical layout and an empty
    /// manifest.
    /// </summary>
    /// <remarks>
    /// The template ships <c>.gitignore</c> and <c>.gitattributes</c> because
    /// both are load-bearing rather than nice to have: the first keeps
    /// <c>cooked/</c> and per-user sidecars out of history, and the second pins
    /// bundle text to LF so a Windows checkout does not rewrite every map
    /// underneath the person editing it.
    /// </remarks>
    public static ProjectLayout Create(string root, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ProjectFormat.AssetsFolder));
        Directory.CreateDirectory(Path.Combine(root, ProjectFormat.MapsFolder));
        Directory.CreateDirectory(Path.Combine(root, ProjectFormat.ScriptsFolder));

        WriteIfAbsent(Path.Combine(root, ".gitignore"), GitIgnore);
        WriteIfAbsent(Path.Combine(root, ".gitattributes"), GitAttributes);

        var project = new SpectraProject { Name = name, Id = Guid.NewGuid() };
        string manifestPath = Path.Combine(root, name + ProjectFormat.Extension);
        var layout = new ProjectLayout(Path.GetFullPath(root), Path.GetFullPath(manifestPath), project);
        layout.Save();
        return layout;
    }

    /// <summary>
    /// Every map bundle actually present under <c>Maps/</c>, as
    /// project-relative paths, sorted.
    /// </summary>
    /// <remarks>
    /// <b>Discovery and the manifest are both real, and neither is the whole
    /// truth.</b> The manifest is the author's ordered list and is what a cook
    /// bakes; the folder is what a person actually put there. An editor shows
    /// the difference and offers to reconcile it, because silently ignoring a
    /// map somebody added is the same class of surprise as silently adding one
    /// they were not ready to ship.
    /// </remarks>
    public IReadOnlyList<string> DiscoverMaps()
    {
        if (!Directory.Exists(MapsPath)) return [];

        var found = new List<string>();
        foreach (string directory in Directory.EnumerateDirectories(MapsPath))
        {
            if (!MapBundle.IsBundle(directory)) continue;
            found.Add($"{ProjectFormat.MapsFolder}/{Path.GetFileName(directory)}");
        }

        // Sorted, because Directory.EnumerateDirectories has no documented
        // order and a list that reshuffles between runs is a list nobody can
        // review.
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Loads a map bundle named by a project-relative path.</summary>
    public MapDocument LoadMap(string projectRelativePath) => MapBundle.Load(Resolve(projectRelativePath));

    private static string FindManifest(string folder)
    {
        string[] candidates = Directory.GetFiles(folder, "*" + ProjectFormat.Extension);
        if (candidates.Length == 1) return candidates[0];

        if (candidates.Length == 0)
        {
            throw new FileNotFoundException(
                $"'{folder}' contains no {ProjectFormat.Extension} file.",
                Path.Combine(folder, "project" + ProjectFormat.Extension));
        }

        // Refused rather than picking the first: which project a folder IS is
        // not something to guess at, and the guess would be alphabetical.
        Array.Sort(candidates, StringComparer.Ordinal);
        throw new FileNotFoundException(
            $"'{folder}' contains {candidates.Length} project files "
            + $"({string.Join(", ", Array.ConvertAll(candidates, Path.GetFileName))}); name the one to open.",
            candidates[0]);
    }

    private static void WriteIfAbsent(string path, string content)
    {
        // Never overwritten: these are the user's files the moment the folder
        // exists, and a scaffold that clobbers a hand-edited .gitignore is a
        // scaffold nobody runs twice.
        if (!File.Exists(path))
            File.WriteAllText(path, content);
    }

    private const string GitIgnore = """
        # Cook output. Derived from the authored files beside it; never authored.
        cooked/

        # Per-user editor state: viewport camera, selection, window layout.
        # Losing one loses nothing but a camera position.
        *.user
        *.user.json
        """;

    private const string GitAttributes = """
        * text=auto

        # A .smap map is a FOLDER bundle of text, and the codec's promise is that
        # save/load/save is byte-identical so a hand edit stays a small diff. Under
        # `* text=auto` a Windows checkout rewrites those files to CRLF underneath
        # you and the next no-op save becomes a whole-file diff.
        #
        # Both stars are needed: attribute patterns use gitignore syntax, where a
        # separator in the middle anchors the pattern to this file's directory, so
        # `*.smap/**` would match only a bundle sitting in the project root.
        **/*.smap/** text eol=lf
        *.spectraproj text eol=lf
        """;
}
