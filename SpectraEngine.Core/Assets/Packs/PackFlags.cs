using System;

namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// Whole-pack properties, as the <see cref="PackHeader.Flags"/> word.
/// </summary>
[Flags]
public enum PackFlags : uint
{
    /// <summary>No flags. Not a legal v1 header, which requires
    /// <see cref="EntriesSortedByAssetId"/>.</summary>
    None = 0,

    /// <summary>
    /// The entry table is sorted ascending by <see cref="PackEntry.AssetId"/>
    /// compared as an unsigned 128-bit value. <b>Required to be set in v1.</b>
    /// </summary>
    /// <remarks>
    /// It is a flag rather than an unstated invariant so a reader can refuse an
    /// unsorted table outright instead of binary-searching one and silently
    /// missing entries. A miss on a lookup degrades to a magenta placeholder, so
    /// an unsorted pack would present as content that is intermittently absent
    /// rather than as a corrupt file.
    /// </remarks>
    EntriesSortedByAssetId = 1u << 0,

    /// <summary>
    /// A patch pack: mounted above the base band and ordered among its peers by
    /// <see cref="PackHeader.PackSequence"/>.
    /// </summary>
    IsPatchPack = 1u << 1,

    /// <summary>
    /// A mod pack: mounted above the patch band and ordered by the user's list.
    /// </summary>
    IsModPack = 1u << 2,

    /// <summary>
    /// A name table is present. Emitted by default: it costs roughly 40 bytes an
    /// asset and it is what makes every log line, every inspect row and every bug
    /// report readable rather than a list of 128-bit numbers.
    /// </summary>
    NameTablePresent = 1u << 3,
}
