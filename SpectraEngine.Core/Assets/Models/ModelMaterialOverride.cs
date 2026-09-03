using System;
using System.IO;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// The one rule that turns a material NAME out of a model file into the content
/// path of the <c>.spectramat</c> that stands for it.
/// </summary>
/// <remarks>
/// <para><b>Both the running engine and the cook ask this, and they must get the
/// same answer.</b> <see cref="AssetManager"/> asks it at load to let an authored
/// material override whatever a model file said about its own surface; the model
/// cook asks it to decide what a cooked submesh's material reference IS, and
/// records the answer in the file. If the two spelled the folder or the extension
/// differently, a cooked model would name a material the engine never looked for
/// and a loose one would find a material the cook never recorded - the same
/// prop, textured in a loose build and grey in a packed one, with every log line
/// reading healthy.</para>
/// <para><b>A material name is CONTENT, so none of its refusals may throw.</b> It
/// arrives from a file somebody else's exporter wrote: it may be empty, carry
/// separators, or contain characters no filesystem accepts. Each of those is
/// simply "there is no override", answered as null.</para>
/// </remarks>
public static class ModelMaterialOverride
{
    /// <summary>
    /// Folder, under the content root, searched for a material whose name matches
    /// an imported one.
    /// </summary>
    public const string Folder = "Materials";

    /// <summary>
    /// The content path an override for <paramref name="materialName"/> would
    /// have, or null when the name cannot address a file at all.
    /// </summary>
    /// <remarks>
    /// It does not say whether anything is THERE. Existence is the caller's
    /// question because the two callers ask different sources - the engine asks
    /// its mounted stack, the cook asks its recording rule context - and a probe
    /// buried in here would answer neither.
    /// </remarks>
    public static string? PathFor(string materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName)) return null;
        if (materialName.AsSpan().IndexOfAny('/', '\\') >= 0) return null;
        if (materialName.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        return $"{Folder}/{materialName}{MaterialParser.FileExtension}";
    }
}
