using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SpectraEngine.Core.Assets.Packs;

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
        List<string> relative = [];

        foreach (string absolute in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            relative.Add(Path.GetRelativePath(root, absolute).Replace('\\', '/'));
        }

        relative.Sort(StringComparer.Ordinal);

        var digest = new PackDigest.Accumulator();
        foreach (string path in relative)
        {
            digest.Append(Encoding.UTF8.GetBytes(path));
            digest.Append(File.ReadAllBytes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))));
        }

        return digest.Finish();
    }
}
