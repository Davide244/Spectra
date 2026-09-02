using SpectraEngine.Core.Assets;
using System;
using System.Collections.Generic;
using System.IO;

namespace Spectra.Kitchen.Cooking;

/// <summary>One authored file the cook found, by both of its names.</summary>
/// <param name="ContentPath">
/// Normalised content-relative path: the string the asset caches key on and the
/// string the pack's id is hashed from.
/// </param>
/// <param name="FullPath">Where it is on this machine, which never reaches an artifact.</param>
public readonly record struct ContentFile(string ContentPath, string FullPath);

/// <summary>
/// Finds the authored content in a project.
/// </summary>
/// <remarks>
/// <para><b>The order is sorted ordinal, and that is a determinism rule rather
/// than tidiness.</b> <c>Directory.EnumerateFiles</c> has no documented order, so
/// a walk is a different list on a different filesystem; the pack writer sorts by
/// asset id and would absorb that, but the cook manifest, the diagnostic order and
/// any future first-reference string table would not. One walk order, decided
/// here.</para>
/// <para><b>It walks the content root and nothing else.</b> Maps and scripts live
/// outside <c>Assets/</c> in the project layout and reach a pack through their own
/// rules, which cook rather than copy: a map is compiled geometry and a script is
/// bytecode. Sweeping the whole project folder in here would put authored source
/// into a shipped pack under a path the engine cannot resolve.</para>
/// </remarks>
public static class ContentWalker
{
    /// <summary>
    /// Every file under <paramref name="contentRoot"/>, as content-relative paths
    /// in ordinal order. An absent root walks to nothing rather than throwing;
    /// the session reports it.
    /// </summary>
    public static IReadOnlyList<ContentFile> Walk(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        string root = Path.GetFullPath(contentRoot);
        if (!Directory.Exists(root)) return [];

        var found = new List<ContentFile>();
        foreach (string full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, full);

            string content;
            try
            {
                content = ContentRoot.NormalizeRelativePath(relative);
            }
            catch (ArgumentException)
            {
                // Nothing under the root can normally fail this, since the path
                // came from enumerating the root itself. Skipping rather than
                // throwing keeps one odd name from stopping a whole cook, and the
                // asset is simply absent from the pack, which a verify catches.
                continue;
            }

            found.Add(new ContentFile(content, full));
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.ContentPath, b.ContentPath));
        return found;
    }
}
