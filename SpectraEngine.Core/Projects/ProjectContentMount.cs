using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using System;
using System.Collections.Generic;
using System.IO;

namespace SpectraEngine.Core.Projects;

/// <summary>
/// What a boot mounts: packs only for a shipped game, packs plus the loose
/// <c>Assets</c> folder above them for a developer.
/// </summary>
public enum ContentMountProfile
{
    /// <summary>
    /// Packs and nothing else. What a player's machine runs, and the only
    /// configuration in which a cook is actually being tested.
    /// </summary>
    Shipped,

    /// <summary>
    /// The same packs with loose files at <see cref="PackMountBand.Loose"/>, so
    /// an artist drops a PNG beside the cooked pack and it shadows the cooked
    /// entry with no rebuild.
    /// </summary>
    Dev,
}

/// <summary>
/// A project's content stack, assembled from its packs: the thing an
/// <c>AssetManager</c> is handed instead of a folder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything below this is a SOURCE SWAP, which is the whole point.</b> A
/// pack's entries are keyed on the exact string
/// <see cref="Assets.ContentRoot.NormalizeRelativePath"/> produces, so a texture
/// named <c>Textures/wall.png</c> in a material is the same asset whether it
/// came out of a folder or an archive - and nothing above this line has to know
/// which. No second normalisation is introduced here for the same reason: two
/// spellings of one identity is how a packed build silently resolves less
/// content than a loose one.
/// </para>
/// <para>
/// <b>Pure-pack forces hot reload OFF and says WHY.</b> A watcher needs a real
/// filesystem path and <see cref="IContentSource.TryGetWatchPath"/> answers
/// false for a pack, so hot reload would attach to nothing and go on claiming it
/// was live. That silence is the failure this reports rather than commits: the
/// reason travels on <see cref="HotReloadDisabledReason"/> and is logged once at
/// mount, because the symptom of getting it wrong is that saving a texture stops
/// doing anything and nothing anywhere says so.
/// </para>
/// <para>
/// <b>The stack is flattened at mount, so it is a SNAPSHOT of what was there.</b>
/// <see cref="PackMountStack"/> builds one dictionary from every source's
/// enumeration rather than probing per lookup, which is what makes forty mods
/// cost nothing per asset and what lets every shadowing decision be recorded
/// where it is made. The consequence, stated rather than discovered: a loose file
/// created AFTER the mount is not served until something remounts. Editing a file
/// that already existed - which is what hot reload is for - is unaffected.
/// </para>
/// </remarks>
public sealed class ProjectContentMount : IDisposable
{
    private readonly PackMountStack _packs;
    private bool _disposed;

    private ProjectContentMount(
        PackMountStack packs,
        ContentSourceStack content,
        IReadOnlyList<string> packPaths,
        ContentMountProfile profile,
        bool hotReloadEnabled,
        string? hotReloadDisabledReason)
    {
        _packs = packs;
        Content = content;
        PackPaths = packPaths;
        Profile = profile;
        HotReloadEnabled = hotReloadEnabled;
        HotReloadDisabledReason = hotReloadDisabledReason;
    }

    /// <summary>The stack an <c>AssetManager</c> takes.</summary>
    public ContentSourceStack Content { get; }

    /// <summary>The mounted packs, and the loose overlay when there is one.</summary>
    public PackMountStack Packs => _packs;

    /// <summary>The pack files that were mounted, in mount order.</summary>
    public IReadOnlyList<string> PackPaths { get; }

    /// <summary>Which profile this mount was assembled for.</summary>
    public ContentMountProfile Profile { get; }

    /// <summary>
    /// Whether an <c>AssetManager</c> over this stack may watch files. False for
    /// a pure-pack mount.
    /// </summary>
    public bool HotReloadEnabled { get; }

    /// <summary>
    /// Why hot reload is off, in a sentence, or null when it is on. Never left
    /// null while <see cref="HotReloadEnabled"/> is false.
    /// </summary>
    public string? HotReloadDisabledReason { get; }

    /// <summary>
    /// Every shadowing decision the flatten made: which source won a path, and
    /// which one it took it from.
    /// </summary>
    public IReadOnlyList<MountShadowing> Shadowings => _packs.Shadowings;

    /// <summary>
    /// Mounts <paramref name="project"/>'s packs and returns the stack to run
    /// on.
    /// </summary>
    /// <exception cref="PackMountException">
    /// A pack the project boots from is missing or is refused. Deliberately
    /// fatal: a shipped game that quietly ran on whatever content it could still
    /// reach would ship holes, and the loose fallback is a separate mode a host
    /// asks for by not calling this at all.
    /// </exception>
    public static ProjectContentMount Open(
        ILogger logger, ProjectLayout project, ContentMountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(project);

        IReadOnlyList<string> packPaths = ProjectPacks.Resolve(project);
        bool dev = profile == ContentMountProfile.Dev;
        var packs = new PackMountStack(logger);

        try
        {
            for (int i = 0; i < packPaths.Count; i++)
            {
                string path = packPaths[i];
                if (!File.Exists(path))
                {
                    // Named with the cook command that would produce it: the
                    // overwhelmingly common cause is a project that has never
                    // been cooked, and "file not found" alone sends people
                    // looking for a bug.
                    throw new PackMountException(
                        $"The project '{project.Project.Name}' boots from '{path}', which does not exist. "
                        + $"Cook it first (scook cook \"{project.Root}\"), or run without --pack to use loose files.");
                }

                // Manifest order IS mount order and later wins, so every pack
                // sits in the base band and the stack's mount-order tie-break
                // does the rest. A patch band exists for packs that declare
                // themselves one; nothing here promotes a pack the author
                // merely listed last.
                packs.Mount(new PackSource(logger, path, PackMountBand.Base));
            }

            if (dev)
                packs.Mount(new LooseFileSource(logger, project.AssetsPath, PackMountBand.Loose));

            packs.Flatten();
        }
        catch
        {
            // A stack that never reached a caller has to unmap what it already
            // mapped, or a mapped view is leaked for the process's life and the
            // folder holding it cannot be deleted on Windows.
            packs.Dispose();
            throw;
        }

        var content = new ContentSourceStack();
        content.Mount(packs);

        string? reason = dev
            ? null
            : "every content source is a pack, and a pack has no file for a watcher to watch";

        logger.LogInformation(
            "Project content mounted ({Profile}, {Packs} pack(s)): {Stack}",
            dev ? "dev" : "shipped", packPaths.Count, packs.Describe());

        if (reason is null)
        {
            logger.LogInformation(
                "Hot reload ON: loose files at priority {Band} shadow the packs beneath them.",
                PackMountBand.Loose);
        }
        else
        {
            // Reported rather than silently no-opped. An asset manager with hot
            // reload nominally on over a pure-pack stack attaches no watcher at
            // all, so the only symptom is that saving a file stops doing
            // anything, which reads as a broken editor rather than as a mode.
            logger.LogInformation("Hot reload OFF: {Reason}.", reason);
        }

        return new ProjectContentMount(packs, content, packPaths, profile, dev, reason);
    }

    /// <summary>Unmounts every pack, and every mapped view with them.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _packs.Dispose();
    }
}
