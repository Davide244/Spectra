using SpectraEngine.Core.Assets.Sources;
using System;
using System.IO;

namespace SpectraEngine.Core.Assets.Audio;

/// <summary>
/// The one rule that says where a sound's bytes actually live: the cooked
/// <c>.saudio</c> beside it if a mounted source has one, otherwise the authored
/// file itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>A sound is named by its SOURCE path everywhere, forever.</b> A map, a
/// script or an entity says <c>Sounds/door_open.wav</c>, and cooking does not
/// rewrite that: it is the same identity decision the pack's id hash already
/// rests on - an asset's name must be one thing whether the content came from a
/// folder or an archive - and it is what makes cooking a source swap rather than
/// a migration of everything that names a sound.
/// </para>
/// <para>
/// <b>So the redirection lives HERE, in one function</b>, exactly as
/// <see cref="Images.ImageContentPath"/> does for textures, and for the reason
/// this repo has already paid for once: an existence probe that disagrees with
/// the open beside it resolves nothing in a packed build while every log line
/// reads healthy.
/// </para>
/// <para>
/// <b>The authored file is not loadable by the engine today, and that is a
/// deliberate division rather than a gap.</b> A source-format decoder is a
/// COOK-time reader: it belongs beside the cooker, which is where every other
/// authored format is parsed, and putting one in Core would put a WAV parser
/// inside every shipped game binary for a file a shipped game never opens.
/// <see cref="Resolve"/> still hands back the authored path on a miss, so the
/// message a caller gets names the file it actually looked for.
/// </para>
/// </remarks>
public static class AudioContentPath
{
    /// <summary>Whether <paramref name="contentPath"/> already names a cooked sound.</summary>
    public static bool IsCooked(string contentPath) =>
        contentPath.EndsWith(SaudioFormat.FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The cooked path for an authored one: the same path with the
    /// <c>.saudio</c> extension.
    /// </summary>
    /// <remarks>
    /// Named here rather than spelled at each call site for the reason
    /// <see cref="Images.ImageContentPath.CookedPathFor"/> gives: the cooker and
    /// the engine must produce the same string, and a disagreement between two
    /// spellings is not an error anywhere - the lookup simply misses.
    /// </remarks>
    public static string CookedPathFor(string contentPath) =>
        IsCooked(contentPath)
            ? contentPath
            : ContentRoot.NormalizeRelativePath(Path.ChangeExtension(contentPath, SaudioFormat.FileExtension));

    /// <summary>
    /// The path <paramref name="source"/> should actually be asked for when a
    /// caller wants the sound at <paramref name="contentPath"/>.
    /// </summary>
    public static string Resolve(IContentSource source, string contentPath)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (IsCooked(contentPath)) return contentPath;

        string cooked = CookedPathFor(contentPath);
        return source.Exists(cooked) ? cooked : contentPath;
    }
}
