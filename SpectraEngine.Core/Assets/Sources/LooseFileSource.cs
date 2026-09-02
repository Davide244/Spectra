using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace SpectraEngine.Core.Assets.Sources;

/// <summary>
/// Content served from a folder of loose files — the engine's <c>Assets</c>
/// directory, and the only source that exists today.
/// </summary>
/// <remarks>
/// <para>This is the filesystem path the asset manager used to walk inline,
/// moved behind the seam rather than rewritten: the same normalisation, the same
/// read-with-retry, the same treatment of a missing file as a plain miss.</para>
/// <para><b>It is the only source that can supply a watch path</b>, which is what
/// makes hot-reload a property of loose content rather than a feature the rest
/// of the engine has to reason about. A packed archive answers false and is
/// simply not watched.</para>
/// <para><b>Thread-safe by having no mutable state.</b> Every member resolves a
/// path and touches the filesystem, so any number of threads may call it at
/// once.</para>
/// </remarks>
public sealed class LooseFileSource : IContentSource
{
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a source over <paramref name="rootPath"/>. The folder need not
    /// exist: a build with no content resolves every path to a miss rather than
    /// failing to construct.
    /// </summary>
    public LooseFileSource(ILogger logger, string rootPath, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(rootPath);

        _logger = logger;
        RootPath = Path.GetFullPath(rootPath);
        Priority = priority;
    }

    /// <summary>Absolute path of the folder this source reads from.</summary>
    public string RootPath { get; }

    /// <inheritdoc/>
    public int Priority { get; }

    /// <inheritdoc/>
    public bool TryOpen(string path, [NotNullWhen(true)] out ContentBlob? blob)
    {
        blob = null;
        if (!TryResolve(path, out string absolute) || !File.Exists(absolute))
            return false;

        try
        {
            blob = FileContent.Read(absolute);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the probe and the open. That is a miss, not a
            // fault, and logging it would put a line in front of the caller's
            // own "not found, using the fallback" one.
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Present but unreadable: the caller degrades exactly as it would
            // for a miss, so this line is the only place the difference between
            // the two is ever recorded.
            _logger.LogWarning("Could not read content '{Path}' from {Source}: {Message}", path, this, ex.Message);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Exists(string path) => TryResolve(path, out string absolute) && File.Exists(absolute);

    /// <inheritdoc/>
    public bool TryGetWatchPath(string path, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        if (!TryResolve(path, out string absolute) || !File.Exists(absolute))
            return false;

        fullPath = absolute;
        return true;
    }

    /// <inheritdoc/>
    public void TryEnumerate(string prefix, string extension, List<string> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        string directory = RootPath;
        if (!string.IsNullOrEmpty(prefix) && !TryResolve(prefix, out directory))
            return;
        if (!Directory.Exists(directory))
            return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!string.IsNullOrEmpty(extension) &&
                    !file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(Path.GetRelativePath(RootPath, file).Replace('\\', '/'));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that vanished or refused access mid-walk yields what was
            // found so far: enumeration feeds tools, and half a listing beats a
            // dialog with a stack trace in it.
            _logger.LogWarning("Enumerating '{Prefix}' in {Source} stopped early: {Message}", prefix, this, ex.Message);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"loose files @ {RootPath}";

    // Content paths are normalised (and rejected) by ContentRoot, which is what
    // stops '..' and a rooted path from reaching outside this folder. A path
    // this source cannot resolve is a miss, never an exception: every caller
    // above is in the middle of deciding between real content and a fallback.
    private bool TryResolve(string path, out string absolute)
    {
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                absolute = ContentRoot.ResolveAbsolute(RootPath, path);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
            }
        }

        absolute = string.Empty;
        return false;
    }
}
