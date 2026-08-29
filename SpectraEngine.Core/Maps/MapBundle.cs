using System;
using System.IO;

namespace SpectraEngine.Core.Maps;

/// <summary>
/// Reads and writes a <c>.smap</c> bundle: a folder of text, not a file.
/// </summary>
/// <remarks>
/// <para>
/// <b>A map is a folder because a game is made of maps and a person edits them
/// outside the editor.</b> The bundle holds <c>map.json</c> — the scene
/// document — and, once scripting lands, <c>scripts/*.luau</c> as real files, so
/// git, VS Code and <c>luau-lsp</c> work on them directly with no sync layer in
/// between. That is the Rojo lesson made native: the alternative, a single
/// opaque place file, is what forced an entire third-party tool to exist on the
/// platform this engine is aimed at.
/// </para>
/// <para>
/// <b>Three save rules, and each prevents a specific way an editor bullies the
/// person editing alongside it.</b> A file whose bytes have not changed is not
/// written at all, so a save with no edits in it does not churn mtimes, file
/// watchers or build systems. Every write is a temp file plus a rename, so a
/// crash mid-save cannot leave half a document where a whole one was. And the
/// save never touches a file it does not reference — a README, a <c>.blend</c>
/// source, notes, anything — because the bundle is a folder the user owns and
/// an editor that tidies it is an editor that deletes things.
/// </para>
/// </remarks>
public static class MapBundle
{
    /// <summary>Reads the scene document out of a bundle directory.</summary>
    /// <exception cref="MapFormatException">The document is malformed.</exception>
    /// <exception cref="FileNotFoundException">The bundle has no <c>map.json</c>.</exception>
    public static MapDocument Load(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        string document = DocumentPath(bundlePath);
        if (!File.Exists(document))
        {
            throw new FileNotFoundException(
                $"'{bundlePath}' is not a map bundle: it has no {MapFormat.DocumentFileName}.", document);
        }

        return MapReader.Read(File.ReadAllBytes(document));
    }

    /// <summary>
    /// Writes the scene document into a bundle directory, creating it if
    /// needed.
    /// </summary>
    /// <returns>
    /// True when the file was actually written; false when it was already
    /// byte-identical and was therefore left alone.
    /// </returns>
    public static bool Save(string bundlePath, MapDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentNullException.ThrowIfNull(document);

        Directory.CreateDirectory(bundlePath);
        return WriteIfChanged(DocumentPath(bundlePath), MapWriter.Write(document));
    }

    /// <summary>The bundle-relative document path.</summary>
    public static string DocumentPath(string bundlePath) =>
        Path.Combine(bundlePath, MapFormat.DocumentFileName);

    /// <summary>Whether a directory looks like a map bundle.</summary>
    public static bool IsBundle(string path) =>
        Directory.Exists(path) && File.Exists(DocumentPath(path));

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> unless the
    /// file already holds exactly those bytes.
    /// </summary>
    /// <remarks>
    /// <b>The read-back is the point, not an optimisation.</b> The codec
    /// guarantees that saving an unedited document reproduces its bytes, and
    /// this is what turns that guarantee into observable behaviour: the file's
    /// timestamp does not move, so nothing downstream — a watcher, a cook, a
    /// git status — sees a change that did not happen.
    /// </remarks>
    private static bool WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            return false;

        // Temp file beside the target, then rename: same volume, so the rename
        // is a metadata operation and a reader either sees the old file whole or
        // the new one whole. Writing in place would leave a truncated document
        // on any crash between the open and the last byte.
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: true);
        return true;
    }
}
