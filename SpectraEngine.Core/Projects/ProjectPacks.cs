using SpectraEngine.Core.Assets.Packs;
using System;
using System.Collections.Generic;
using System.IO;

namespace SpectraEngine.Core.Projects;

/// <summary>
/// Which pack files a project boots from, in mount order.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place decides, because two would disagree silently.</b> The manifest's
/// <see cref="SpectraProject.Packs"/> is authoritative when it names anything;
/// when it names nothing, a project that has been cooked still has a pack, and
/// it is <c>cooked/&lt;manifest name&gt;.spack</c> - the same name
/// <c>CookSession</c> writes, derived from the same string. A boot that spelled
/// that convention its own way would find no pack, fall back to loose files, and
/// report nothing: the run would look healthy and would not be testing the cook
/// at all.
/// </para>
/// <para>
/// <b>Named after the manifest FILE, never the display name.</b> A display name
/// is free text that may hold characters no filesystem accepts, so deriving a
/// path from it would report a naming problem as an I/O one.
/// </para>
/// </remarks>
public static class ProjectPacks
{
    /// <summary>
    /// The pack files <paramref name="project"/> mounts, lowest priority first,
    /// as absolute paths. Existence is not checked here: whether a missing pack
    /// is fatal is the mount's decision, and it has the log to say so.
    /// </summary>
    public static IReadOnlyList<string> Resolve(ProjectLayout project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.Project.Packs.Count > 0)
        {
            var listed = new List<string>(project.Project.Packs.Count);
            foreach (string pack in project.Project.Packs)
                listed.Add(Path.GetFullPath(project.Resolve(pack)));

            return listed;
        }

        return [ConventionalPackPath(project)];
    }

    /// <summary>
    /// Where <c>scook</c> puts this project's pack when nothing overrode its
    /// output directory.
    /// </summary>
    public static string ConventionalPackPath(ProjectLayout project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return Path.GetFullPath(Path.Combine(
            project.CookedPath,
            Path.GetFileNameWithoutExtension(project.ManifestPath) + PackFormat.FileExtension));
    }
}
