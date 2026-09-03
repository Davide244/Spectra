using SpectraEngine.Core.Assets.Sources;
using System;
using System.IO;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// The one rule that says where a model's bytes actually live: the cooked
/// <c>.smodel</c> beside it if a mounted source has one, otherwise the authored
/// file itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>A model is named by its SOURCE path everywhere, forever.</b> A map, a
/// script or a scene node says <c>Models/signpost.gltf</c>, and cooking does not
/// rewrite that: it is the same identity decision the pack's id hash already
/// rests on - an asset's name must be one thing whether the content came from a
/// folder or an archive - and it is what makes cooking a source swap rather than
/// a migration of everything that names a prop.
/// </para>
/// <para>
/// <b>So the redirection lives HERE, in one function</b>, exactly as
/// <see cref="Images.ImageContentPath"/> does for textures and
/// <see cref="Audio.AudioContentPath"/> for sounds, and for the reason this repo
/// has already paid for once: an existence probe that disagrees with the open
/// beside it resolves nothing in a packed build while every log line reads
/// healthy. <see cref="AssetManager"/> and <c>scook verify</c> both call this.
/// </para>
/// <para>
/// <b>The authored file is still read from the FILESYSTEM, not from a mounted
/// source, and that limit is real rather than an oversight.</b>
/// <c>ModelImporter</c> hands a path to a native importer that opens the file
/// itself and follows the material library beside it, so a loose model cannot be
/// served out of a pack or out of any source that is not a folder. A cooked
/// model has no such problem: it is one self-contained payload read through the
/// stack. <see cref="Resolve"/> hands back the authored path on a miss, so the
/// message a caller gets names the file it actually looked for.
/// </para>
/// </remarks>
public static class ModelContentPath
{
    /// <summary>Whether <paramref name="contentPath"/> already names a cooked model.</summary>
    public static bool IsCooked(string contentPath)
    {
        ArgumentNullException.ThrowIfNull(contentPath);
        return contentPath.EndsWith(SmodelFormat.FileExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The cooked path for an authored one: the same path with the
    /// <c>.smodel</c> extension.
    /// </summary>
    /// <remarks>
    /// Named here rather than spelled at each call site for the reason
    /// <see cref="Images.ImageContentPath.CookedPathFor"/> gives: the cooker and
    /// the engine must produce the same string, and a disagreement between two
    /// spellings is not an error anywhere - the lookup simply misses and a
    /// packed build quietly imports source models it was meant to have stopped
    /// importing.
    /// </remarks>
    public static string CookedPathFor(string contentPath)
    {
        ArgumentNullException.ThrowIfNull(contentPath);

        return IsCooked(contentPath)
            ? contentPath
            : ContentRoot.NormalizeRelativePath(Path.ChangeExtension(contentPath, SmodelFormat.FileExtension));
    }

    /// <summary>
    /// The path <paramref name="source"/> should actually be asked for when a
    /// caller wants the model at <paramref name="contentPath"/>.
    /// </summary>
    public static string Resolve(IContentSource source, string contentPath)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (IsCooked(contentPath)) return contentPath;

        string cooked = CookedPathFor(contentPath);
        return source.Exists(cooked) ? cooked : contentPath;
    }
}
