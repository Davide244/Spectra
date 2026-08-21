using System.Collections.Generic;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Size snapping for a resize drag: the increment is a step of the object's
/// resulting <b>world size</b>, in world units — one notch grows a brush by one
/// unit whether it started at 0.4 units or at 10.
/// </summary>
/// <remarks>
/// <b>The size is snapped, not the multiplier.</b> Quantising the factor — what
/// this type used to do — makes the world-space change per notch proportional to
/// whatever the object already measures: at a 0.25 factor step a 10-unit brush
/// jumps 2.5 units per notch while a 0.4-unit brush jumps 0.1, so the "grid" the
/// user is working to silently changes size with every object they click. A
/// resize increment quoted in world units is the same increment everywhere, which
/// is what both Hammer and Roblox Studio give you and what the increment box a UI
/// will eventually put on screen has to mean.
/// <para>
/// <b>The quantity is the size CHANGE, measured from the size the drag was
/// grabbed at</b> — not the absolute size measured from zero. That is the one
/// place this deliberately differs from <see cref="GridSnapSettings"/>, which
/// rounds a position onto the absolute world grid. Rounding an absolute size
/// would make the first notch on a 0.4-unit brush a jump to 1.0 (a change of 0.6)
/// and forbid fractional sizes outright; anchoring the ladder at the starting
/// size keeps <em>every</em> notch worth exactly one increment, which is the
/// property the user asked for and the one Studio's resize handles have.
/// </para>
/// <para>
/// <b>The default is one world unit, on the same 0.25/0.5/1/2/4 ladder as the
/// translate grid.</b> Roblox Studio governs Move and Resize with a single studs
/// box (and gives Rotate its own degrees box), so a user who has set their grid
/// to "1" expects a resize notch to be one unit too, and a brush moved a notch
/// and grown a notch stay on the same grid. It is nevertheless a
/// <em>separate</em> settings object from <see cref="TranslateGizmo"/>'s, so a
/// future UI can offer them linked or split without the tools having to be
/// rewired.
/// </para>
/// <para>
/// A snapped size change of exactly zero is not an edit at all: the drag holds
/// the object at its starting size and the gesture commits nothing, exactly as a
/// sub-increment rotate does.
/// </para>
/// </remarks>
public sealed class ResizeSnapSettings : SnapSettings
{
    /// <summary>The default resize step, in world units.</summary>
    public const float DefaultIncrement = 1f;

    // Deliberately the same rungs as GridSnapSettings: see the type remarks for
    // why move and resize share a unit and a ladder without sharing an instance.
    private static readonly float[] PresetIncrements = [0.25f, 0.5f, 1f, 2f, 4f];

    /// <summary>Creates settings at the <see cref="DefaultIncrement"/> resize step.</summary>
    public ResizeSnapSettings()
        : base(DefaultIncrement, PresetIncrements)
    {
    }

    /// <summary>
    /// The selectable resize steps in world units, ascending — the same ladder as
    /// <see cref="SnapSettings.Increments"/>, reachable without an instance.
    /// </summary>
    public static IReadOnlyList<float> Presets => PresetIncrements;
}
