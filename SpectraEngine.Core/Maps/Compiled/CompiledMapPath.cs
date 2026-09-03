using System;
using System.IO;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// Where a map bundle's compiled form lives, said once for the cook that writes
/// one and the boot that looks for one.
/// </summary>
/// <remarks>
/// <para><b>The redirect is the same rule <c>ImageContentPath</c> already
/// expresses for <c>.png</c> to <c>.simage</c>:</b> identity is the SOURCE path
/// with the cooked extension, so a level authored as <c>Maps/Lobby.smap</c> is
/// cooked to and resolved as <c>Maps/Lobby.scmap</c> forever.</para>
/// <para><b>It is in Core rather than in the cooker because BOTH sides need
/// it.</b> A cook that wrote one spelling and a boot that looked for another
/// produces no error anywhere: the runtime simply finds no compiled map, falls
/// back to whatever loose bundle it can see, and every log line reads healthy -
/// which is the exact failure <c>PackFormat.FileExtension</c> was moved into Core
/// to prevent, one format over.</para>
/// </remarks>
public static class CompiledMapPath
{
    /// <summary>
    /// The content path a bundle's compiled map is emitted and resolved under.
    /// </summary>
    /// <param name="bundlePath">
    /// The bundle's path relative to the PROJECT root - <c>Maps/Lobby.smap</c> -
    /// because a map bundle lives beside <c>Assets/</c> rather than inside it, so
    /// a map's paths are anchored one level up from every other asset's.
    /// </param>
    public static string For(string bundlePath)
    {
        ArgumentNullException.ThrowIfNull(bundlePath);
        return Path.ChangeExtension(bundlePath, ScmapFormat.FileExtension).Replace('\\', '/');
    }

    /// <summary>Whether <paramref name="contentPath"/> names a compiled map.</summary>
    public static bool IsCompiled(string contentPath)
    {
        ArgumentNullException.ThrowIfNull(contentPath);
        return contentPath.EndsWith(ScmapFormat.FileExtension, StringComparison.OrdinalIgnoreCase);
    }
}
