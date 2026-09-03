using SpectraEngine.Core.Assets.Sources;
using System;
using System.IO;

namespace SpectraEngine.Core.Assets.Images;

/// <summary>
/// The one rule that says where an image's bytes actually live: the cooked
/// <c>.simage</c> beside it if a mounted source has one, otherwise the authored
/// file itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>An image is named by its SOURCE path everywhere, forever.</b> A material
/// says <c>Textures/wall_brick.png</c>, a model's diffuse map says
/// <c>Textures/wall_brick.png</c>, and neither is rewritten by cooking. That is
/// the same identity decision the pack's id hash already rests on - an asset's
/// name must be one thing whether the content came from a folder or an archive -
/// and it is what makes cooking a source swap rather than a migration of every
/// file that names a texture.
/// </para>
/// <para>
/// <b>So the redirection lives HERE, in one function, and every caller that
/// resolves an image goes through it.</b> The engine's asset manager and the
/// cook's verifier both ask this question, and the failure of answering it twice
/// is the one this repo has already paid for once: an existence probe that
/// disagrees with the open beside it binds the magenta placeholder into every
/// packed material while every log line reads healthy.
/// </para>
/// <para>
/// <b>The cooked file WINS where both are mounted, and that is not the same as a
/// priority band.</b> Bands decide which SOURCE answers for one path; this
/// decides which of two paths is asked for. An editor mounting loose files above
/// a pack still gets the loose PNG whenever there is one, because the loose
/// source answers first for <c>.simage</c> too and simply has none.
/// </para>
/// </remarks>
public static class ImageContentPath
{
    /// <summary>Whether <paramref name="contentPath"/> already names a cooked image.</summary>
    public static bool IsCooked(string contentPath) =>
        contentPath.EndsWith(SimageFormat.FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The cooked path for an authored one: the same path with the
    /// <c>.simage</c> extension.
    /// </summary>
    /// <remarks>
    /// Named here rather than spelled at each call site for the reason
    /// <c>ShaderRule.CookedPathFor</c> gives: the cooker and the engine must
    /// produce the same string, and a disagreement between two spellings is not
    /// an error anywhere - the lookup simply misses and a packed build quietly
    /// decodes PNGs it was meant to have stopped decoding.
    /// </remarks>
    public static string CookedPathFor(string contentPath) =>
        IsCooked(contentPath)
            ? contentPath
            : ContentRoot.NormalizeRelativePath(Path.ChangeExtension(contentPath, SimageFormat.FileExtension));

    /// <summary>
    /// The path <paramref name="source"/> should actually be asked for when a
    /// caller wants the image at <paramref name="contentPath"/>.
    /// </summary>
    /// <remarks>
    /// This is a probe, and it is the one place a probe is safe here: it decides
    /// between two paths rather than between real content and a fallback, and
    /// whichever it names is then opened through the SAME source, so the two
    /// cannot disagree about what exists. A miss simply hands back the authored
    /// path, which is exactly what a build with no cooked images should get.
    /// </remarks>
    public static string Resolve(IContentSource source, string contentPath)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (IsCooked(contentPath)) return contentPath;

        string cooked = CookedPathFor(contentPath);
        return source.Exists(cooked) ? cooked : contentPath;
    }
}
