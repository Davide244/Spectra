using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Maps;

namespace Spectra.Kitchen.Maps;

/// <summary>
/// The <c>SourceMapDigest</c> a compiled map stamps: one hash over a whole
/// <c>.smap</c> bundle.
/// </summary>
/// <remarks>
/// <para><b>Over a canonical ENUMERATION, never over a directory walk.</b> A
/// walk returns files in whatever order the filesystem chose, which differs
/// between machines and between two filesystems on one machine, so hashing in walk
/// order would make two identical bundles hash differently: every incremental cook
/// would re-bake every map, and the digest would be a fact about the cooking
/// machine rather than about the map. Paths are sorted ordinally on their
/// bundle-relative, forward-slash spelling.</para>
/// <para><b>Each path's bytes go into the hash beside its file's.</b> Without
/// them, renaming a script to a name that sorts into the same slot is invisible,
/// and so is moving a file between two directories whose contents swap. The path
/// is hashed first so a file's bytes can never be mistaken for the next path.</para>
/// <para><b>The whole bundle, not just <c>map.json</c>.</b> A bundle carries the
/// document and its scripts as real files, and a script edit changes what the bake
/// produces exactly as a document edit does; one digest covering the folder is
/// what makes a stale compiled map detectable at all.</para>
/// <para><b>It lives in the cook rather than in the engine</b>, although
/// <c>MapBundle</c> next door already does file I/O. Only a cook can compute this
/// value, because it needs the authored bundle, and a shipped game has none: the
/// runtime only ever compares the number it was handed. Putting it in the engine
/// would put a function no shipped binary can call into every shipped binary.</para>
/// </remarks>
public static class MapBundleDigest
{
    /// <summary>
    /// Hashes every file in <paramref name="bundlePath"/>, recursively.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">There is no such bundle.</exception>
    public static UInt128 Compute(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        if (!Directory.Exists(bundlePath))
        {
            throw new DirectoryNotFoundException(
                $"'{bundlePath}' is not a map bundle directory, so it has no source digest.");
        }

        string root = Path.GetFullPath(bundlePath);
        List<(string Path, byte[] Bytes)> files = [];

        foreach (string absolute in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files.Add((Path.GetRelativePath(root, absolute).Replace('\\', '/'), File.ReadAllBytes(absolute)));
        }

        return Compute(files);
    }

    /// <summary>
    /// Hashes a bundle already read into memory, by bundle-relative path.
    /// </summary>
    /// <remarks>
    /// <b>The definition, and the folder form above is a way of gathering the same
    /// list.</b> A cook rule reads its inputs through its context so that every one
    /// is a recorded dependency, which means it holds the bytes rather than a
    /// directory to re-read, and a second hashing expression here is exactly the
    /// kind that gets corrected in one place and not in the other. Sorting is done
    /// here rather than trusted from the caller, since the whole point is that the
    /// answer does not depend on how the list was gathered.
    /// </remarks>
    /// <param name="files">
    /// Bundle-relative, forward-slash paths and their bytes, in any order.
    /// </param>
    public static UInt128 Compute(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var kept = new List<(string Path, byte[] Bytes)>(files.Count);
        foreach ((string path, byte[] bytes) in files)
        {
            if (IsSourceFile(path)) kept.Add((path, bytes));
        }

        (string Path, byte[] Bytes)[] sorted = [.. kept];
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        var digest = new PackDigest.Accumulator();
        foreach ((string path, byte[] bytes) in sorted)
        {
            digest.Append(Encoding.UTF8.GetBytes(path));
            digest.Append(bytes);
        }

        return digest.Finish();
    }

    /// <summary>
    /// Whether a bundle-relative file is part of what the bake reads at all.
    /// </summary>
    /// <remarks>
    /// <b>One predicate, because the alternative is a per-developer cook.</b>
    /// <c>editor.user.json</c> is per-user state, gitignored and never
    /// load-bearing, and it changes every time somebody moves the viewport camera.
    /// Hashed into the digest it would put a different number in every developer's
    /// compiled map for the same level; read as a dependency it would miss the cook
    /// cache on every launch. Both gatherings and the rule that feeds them ask
    /// here, so the answer cannot differ between them.
    /// </remarks>
    public static bool IsSourceFile(string bundleRelativePath)
    {
        ArgumentNullException.ThrowIfNull(bundleRelativePath);

        return !Path.GetFileName(bundleRelativePath.AsSpan())
            .Equals(MapFormat.UserStateFileName, StringComparison.OrdinalIgnoreCase);
    }
}
