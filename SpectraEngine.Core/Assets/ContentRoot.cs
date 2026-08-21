using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// Locates the folder that holds the engine's on-disk content (textures, and
/// later materials/models) and normalises the relative paths used as asset
/// cache keys.
/// </summary>
/// <remarks>
/// <para>
/// Resolution mirrors <see cref="Graphics.Shaders.BaseShaders"/>: walk up from
/// <see cref="AppContext.BaseDirectory"/> looking for the solution file. If it
/// is found the process is running from a developer build, so the repo's
/// <c>Assets</c> folder wins — hot-reload then watches the files the developer
/// actually edits rather than the build's stale copies. Otherwise the copy next
/// to the executable is used, which is what a deployed/AOT-published build ships.
/// </para>
/// <para>
/// The walk hits the filesystem, so the result is resolved exactly once and
/// cached for the process lifetime; every member here is thread-safe.
/// </para>
/// </remarks>
public static class ContentRoot
{
    /// <summary>Name of the content folder, both in the repo and beside the executable.</summary>
    public const string DirectoryName = "Assets";

    // Lazy rather than a static constructor so the (filesystem-touching) walk
    // is paid only by processes that actually load content, and exactly once.
    private static readonly Lazy<Resolution> Resolved =
        new(ResolveCore, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Absolute path of the content root. Resolved on first access and cached;
    /// the directory is not guaranteed to exist (a build with no content still
    /// yields the path it would live at). Thread-safe.
    /// </summary>
    public static string Path => Resolved.Value.Root;

    /// <summary>
    /// True when the content root was found inside the source tree, i.e. this is
    /// a developer build. Asset hot-reload defaults to this value: watching the
    /// read-only copy beside a deployed executable would never fire. Thread-safe.
    /// </summary>
    public static bool IsDeveloperBuild => Resolved.Value.FromSourceTree;

    /// <summary>
    /// Canonical cache-key form of <paramref name="relativePath"/>: forward
    /// slashes, no leading separator, no <c>.</c> segments. Keys compare with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> so the same asset resolves
    /// to one cache entry regardless of how a caller spelled it. Pure, thread-safe.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The path is empty, rooted, or contains a <c>..</c> segment — content
    /// references must stay inside the content root.
    /// </exception>
    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length == 0)
            throw new ArgumentException("Asset path must not be empty.", nameof(relativePath));

        // A leading separator is just a habit ("/Textures/x.png" plainly means
        // content-relative), so strip it before the rooted check — which still
        // has to reject a genuine drive- or device-rooted path.
        ReadOnlySpan<char> remaining = relativePath.AsSpan().TrimStart("/\\");
        if (System.IO.Path.IsPathRooted(remaining))
            throw new ArgumentException(
                $"Asset path '{relativePath}' must be relative to the content root.", nameof(relativePath));

        var builder = new StringBuilder(relativePath.Length);
        while (!remaining.IsEmpty)
        {
            int cut = remaining.IndexOfAny('/', '\\');
            ReadOnlySpan<char> part = cut < 0 ? remaining : remaining[..cut];
            remaining = cut < 0 ? default : remaining[(cut + 1)..];

            part = part.Trim();
            if (part.Length == 0 || part.SequenceEqual(".")) continue;
            if (part.SequenceEqual(".."))
                throw new ArgumentException(
                    $"Asset path '{relativePath}' must not escape the content root with '..'.",
                    nameof(relativePath));

            if (builder.Length > 0) builder.Append('/');
            builder.Append(part);
        }

        if (builder.Length == 0)
            throw new ArgumentException(
                $"Asset path '{relativePath}' has no path segments.", nameof(relativePath));

        return builder.ToString();
    }

    /// <summary>
    /// Absolute filesystem path of <paramref name="relativePath"/> under
    /// <paramref name="root"/>. The path is normalised first, so it cannot
    /// escape the root. Pure, thread-safe.
    /// </summary>
    public static string ResolveAbsolute(string root, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        return System.IO.Path.GetFullPath(
            System.IO.Path.Combine(root, normalized.Replace('/', System.IO.Path.DirectorySeparatorChar)));
    }

    // Developer build: repo Assets\ (editable, watchable). Otherwise the copy
    // the build dropped next to the executable.
    private static Resolution ResolveCore()
    {
        string? sourceRoot = TryFindSourceRoot();
        if (sourceRoot is not null)
        {
            string candidate = System.IO.Path.Combine(sourceRoot, DirectoryName);
            if (Directory.Exists(candidate))
                return new Resolution(candidate, FromSourceTree: true);
        }

        return new Resolution(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, DirectoryName)),
            FromSourceTree: false);
    }

    // Same walk BaseShaders uses to enable shader hot-reload: the nearest
    // ancestor holding a solution file is the repo root.
    private static string? TryFindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private readonly record struct Resolution(string Root, bool FromSourceTree);
}
