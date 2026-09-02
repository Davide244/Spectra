namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// The priority bands a mount stack is ordered by. Higher wins.
/// </summary>
/// <remarks>
/// <para><b>They are spaced by a hundred so a band can be ordered WITHIN
/// itself.</b> Patch packs order among their peers by
/// <see cref="PackHeader.PackSequence"/> and mods by the user's list, and both of
/// those are an offset from the band's floor rather than a second sort key the
/// stack has to know about.</para>
/// <para><b>Loose files sit at the top and that is the editor's whole
/// workflow</b>: an artist drops a PNG beside the cooked pack and it shadows the
/// cooked entry with no rebuild. In a shipped build loose mounting is opt-in and
/// logged, because content resolving from somewhere other than the pack is the
/// first thing to know when a shipped game does not match the cook.</para>
/// </remarks>
public static class PackMountBand
{
    /// <summary>Base packs: what the game shipped with.</summary>
    public const int Base = 0;

    /// <summary>Patch packs, ordered among themselves by <see cref="PackHeader.PackSequence"/>.</summary>
    public const int Patch = 100;

    /// <summary>Mod packs, ordered among themselves by the user's list.</summary>
    public const int Mod = 200;

    /// <summary>Loose files: the editor always, a shipped build only when asked.</summary>
    public const int Loose = 1000;
}
