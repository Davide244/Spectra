using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Serialization;
using SpectraEngine.Core.Windowing;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Projects;

/// <summary>
/// A game project: the text manifest at the root of a project folder, naming
/// the maps, the display defaults and the backends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a binary format.</b> It is a few dozen lines read
/// exactly once at boot, so a binary encoding would save microseconds while
/// costing git-diffability on the one file a person most needs to hand-edit and
/// merge. It follows the map's rules exactly: canonical UTF-8, one shared
/// writer, unknown members carried through untouched.
/// </para>
/// <para>
/// <b>Maps are plural and first-class, which is the whole reason this exists.</b>
/// A game is made of levels, and until there was a manifest there was nowhere
/// to say which ones or which comes first. <see cref="Maps"/> is an ordered
/// list of bundle paths relative to the project folder, and
/// <see cref="StartupMap"/> names the one a shipped game boots into.
/// </para>
/// <para>
/// <b>Members this engine has not built yet are carried, not dropped</b>, the
/// same three-tier rule the map uses. <c>packs</c>, <c>input</c>,
/// <c>bootScript</c>, <c>entityDefinitions</c> and <c>settings</c> are all
/// specified and none of them has anything in the tree to bind to, so they ride
/// through in <see cref="Unknown"/> rather than being decoded into values that
/// would silently mean nothing.
/// </para>
/// </remarks>
public sealed class SpectraProject
{
    internal static readonly string[] MemberOrder =
        [ProjectFormat.FormatVersionMember, ProjectFormat.MinimumReadableMember, ProjectFormat.EngineMember,
         ProjectFormat.NameMember, ProjectFormat.IdMember, ProjectFormat.StartupMapMember,
         ProjectFormat.MapsMember, ProjectFormat.DisplayMember, ProjectFormat.DefaultBackendMember,
         ProjectFormat.AllowedBackendsMember];

    public int FormatVersion { get; set; } = EngineInfo.ProjectFormatVersion;

    /// <summary>
    /// The oldest reader that can still make sense of this project. A reader
    /// refuses a document whose value here exceeds what it implements.
    /// </summary>
    public int MinimumReadableVersion { get; set; } = EngineInfo.MinimumReadableProjectVersion;

    /// <summary>Engine version that last wrote this file. Informational; never a load gate.</summary>
    public string Engine { get; set; } = EngineInfo.VersionString;

    /// <summary>The game's display name.</summary>
    public string Name { get; set; } = "Untitled";

    /// <summary>
    /// Stable identity for the project, used to namespace save data and packs.
    /// </summary>
    /// <remarks>
    /// A GUID rather than the name, because the name is the one field a person
    /// changes casually and save data keyed on it would be orphaned by a rename.
    /// </remarks>
    public Guid Id { get; set; }

    /// <summary>
    /// Project-relative path of the map bundle a shipped game boots into, or
    /// null when the project has none yet.
    /// </summary>
    public string? StartupMap { get; set; }

    /// <summary>
    /// Project-relative paths of the map bundles this project contains, in
    /// order.
    /// </summary>
    /// <remarks>
    /// <b>Listed rather than discovered, and the editor reconciles the two.</b>
    /// The cook needs to know what to bake without walking a tree it does not
    /// own, and the order is the author's. But a person who drops a folder into
    /// <c>Maps/</c> expects to see it, so
    /// <see cref="ProjectLayout.DiscoverMaps"/> exists and the editor offers to
    /// add what it finds rather than the manifest silently being the only
    /// truth.
    /// </remarks>
    public List<string> Maps { get; } = [];

    /// <summary>Window defaults for a shipped game.</summary>
    public ProjectDisplay Display { get; set; } = new();

    /// <summary>
    /// Which backend a shipped game asks for first, or null to let the host
    /// decide.
    /// </summary>
    public GraphicsBackend? DefaultBackend { get; set; }

    /// <summary>
    /// Backends this project is allowed to run on. Empty means no restriction.
    /// </summary>
    /// <remarks>
    /// A project that has only ever been tested on one backend can say so, and
    /// a shipped game refusing a backend loudly at boot beats rendering
    /// something nobody has looked at.
    /// </remarks>
    public List<GraphicsBackend> AllowedBackends { get; } = [];

    /// <summary>Members this engine version does not recognise.</summary>
    public List<PreservedMember> Unknown { get; } = [];
}

/// <summary>Window defaults for a shipped game.</summary>
public sealed class ProjectDisplay
{
    internal static readonly string[] MemberOrder =
        [ProjectFormat.WidthMember, ProjectFormat.HeightMember,
         ProjectFormat.VsyncMember, ProjectFormat.ModeMember];

    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    public bool Vsync { get; set; } = true;

    public WindowMode Mode { get; set; } = WindowMode.Windowed;

    public List<PreservedMember> Unknown { get; } = [];
}
