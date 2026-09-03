using Avalonia.Input;
using SpectraEngine.Core.Assets;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// One asset, being dragged out of the content browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>The path is CONTENT-RELATIVE, never a filesystem path, and that is the
/// whole of this type.</b> Identity in this engine is the normalized
/// content-relative path: it is what <c>AssetManager</c>'s caches key on, what a
/// <c>.spectramat</c> writes down, what a map's <c>mesh</c> member records, and
/// what the pack's asset-id hash is taken over. A payload carrying
/// <c>D:\Projekte\MyGame\Assets\Models\crate.obj</c> would be a fifth spelling
/// of one thing, and the failure it produces is the quiet kind: the drop
/// resolves nothing, the node arrives empty, and every log line reads healthy
/// because the path it names really does exist.
/// </para>
/// <para>
/// <b>The conversion is the part that can be wrong, so it is a pure function
/// with a test on it</b> rather than three lines inside a pointer handler that
/// only a GUI can reach.
/// </para>
/// <para>
/// A reference type rather than a struct because
/// <see cref="DataFormat.CreateInProcessFormat{T}"/> constrains its payload to
/// one; it is immutable either way, which is what actually matters for
/// something crossing a drag gesture.
/// </para>
/// </remarks>
/// <param name="Kind">What the browser classified the file as.</param>
/// <param name="ContentPath">
/// The file's path relative to the content root, normalized:
/// forward slashes, no leading separator, no <c>.</c> segments.
/// </param>
/// <param name="Name">The file name, for a message that names what was dragged.</param>
public sealed record ContentDragPayload(ContentKind Kind, string ContentPath, string Name)
{
    /// <summary>
    /// Turns a browsed file into a payload, or refuses it.
    /// </summary>
    /// <remarks>
    /// <b>Refusal has three shapes and all three are real.</b> A folder is not
    /// an asset; a browser with no root open has nothing to be relative to; and
    /// a file outside the root cannot be named at all, which
    /// <c>ContentRoot.NormalizeRelativePath</c> reports by throwing on the
    /// <c>..</c> that <see cref="Path.GetRelativePath"/> produces. Catching that
    /// here rather than letting it travel means a mis-rooted browser refuses the
    /// drag instead of handing the engine a path it will reject three threads
    /// later.
    /// </remarks>
    /// <param name="assetsRoot">The absolute content root, or null for none.</param>
    /// <param name="fullPath">The file's absolute path.</param>
    /// <param name="kind">Its browser classification.</param>
    /// <param name="payload">The payload, when this returns true.</param>
    public static bool TryCreate(
        string? assetsRoot,
        string fullPath,
        ContentKind kind,
        [NotNullWhen(true)] out ContentDragPayload? payload)
    {
        payload = null;

        if (kind == ContentKind.Folder)
            return false;

        if (string.IsNullOrEmpty(assetsRoot) || string.IsNullOrEmpty(fullPath))
            return false;

        try
        {
            string relative = Path.GetRelativePath(assetsRoot, fullPath);

            // GetRelativePath hands back the input unchanged when the two are on
            // different volumes, which NormalizeRelativePath then refuses as
            // rooted - so both escapes land in the same catch rather than one
            // of them slipping through as a plausible-looking relative path.
            payload = new ContentDragPayload(
                kind, ContentRoot.NormalizeRelativePath(relative), Path.GetFileName(fullPath));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>
/// The in-process clipboard format an asset drag travels in.
/// </summary>
/// <remarks>
/// A type of its own so <see cref="ContentDragPayload"/> stays free of Avalonia
/// statics: the payload's conversion is tested with no UI framework running, and
/// a static field on the same type would be initialised by the first test that
/// touched it.
/// </remarks>
internal static class ContentDrag
{
    /// <summary>
    /// The format an asset drag carries, in-process only.
    /// </summary>
    /// <remarks>
    /// In-process, like the scene tree's node drag, because the payload is a
    /// managed value and there is no outside consumer for it: a drag that left
    /// this application would want a file list, which the OS already provides
    /// for the same files.
    /// </remarks>
    public static readonly DataFormat<ContentDragPayload> Format =
        DataFormat.CreateInProcessFormat<ContentDragPayload>("spectra-content-asset");
}
