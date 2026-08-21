using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// World-grid snapping for a translate drag: how big the grid step is, and —
/// through <see cref="SnapSettings"/> — whether snapping is on and which
/// modifier inverts it.
/// </summary>
/// <remarks>
/// <b>The result is snapped, never the delta.</b> Rounding the movement would
/// only preserve whatever sub-grid offset the object already had — drag a brush
/// sitting at x = 3.7 by a snapped one unit and it lands on 4.7, still off the
/// grid, forever. Rounding the resulting position instead makes the first
/// snapped drag pull the object <em>onto</em> absolute grid coordinates and
/// keep it there, which is what "snap to grid" means to anyone who has used
/// Hammer or Roblox Studio.
/// <para>
/// <b>The default step is one world unit</b> — the engine's working scale, and
/// the same size as a Roblox stud, so a value typed into a property panel and a
/// value dragged with the gizmo agree, and the default feels like the editor
/// the user came from. <see cref="Presets"/> offers the usual halving/doubling
/// ladder around it.
/// </para>
/// </remarks>
public sealed class GridSnapSettings : SnapSettings
{
    /// <summary>The default grid step, in world units.</summary>
    public const float DefaultIncrement = 1f;

    // Halving and doubling around the default: fine enough for trim work,
    // coarse enough to lay out a room quickly.
    private static readonly float[] PresetIncrements = [0.25f, 0.5f, 1f, 2f, 4f];

    /// <summary>Creates settings at the <see cref="DefaultIncrement"/> grid step.</summary>
    public GridSnapSettings()
        : base(DefaultIncrement, PresetIncrements)
    {
    }

    /// <summary>
    /// The selectable grid steps, ascending — the same ladder as
    /// <see cref="SnapSettings.Increments"/>, reachable without an instance.
    /// </summary>
    public static IReadOnlyList<float> Presets => PresetIncrements;

    /// <summary>
    /// Rounds only the components <paramref name="axisMask"/> marks as free
    /// (non-zero), passing the rest through untouched — see
    /// <see cref="GizmoHandles.FreeAxisMask"/> for why a constrained drag must
    /// not quantize the axes it never moved.
    /// </summary>
    public Vector3 SnapMasked(Vector3 value, Vector3 axisMask) => new(
        axisMask.X != 0f ? SnapScalar(value.X) : value.X,
        axisMask.Y != 0f ? SnapScalar(value.Y) : value.Y,
        axisMask.Z != 0f ? SnapScalar(value.Z) : value.Z);
}
