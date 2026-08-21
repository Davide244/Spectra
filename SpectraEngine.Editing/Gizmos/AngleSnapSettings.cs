using System;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Angle snapping for a rotate drag: the increment in <b>degrees</b>, and —
/// through <see cref="SnapSettings"/> — whether snapping is on and which
/// modifier inverts it.
/// </summary>
/// <remarks>
/// <b>Degrees, not radians, are the stored unit.</b> Every editor's rotate
/// increment is quoted in degrees, the useful presets are whole degrees, and
/// rounding in degrees is what makes four 15° steps land on exactly 60° instead
/// of on the nearest float to <c>4 · (π/12)</c>. The drag converts once at the
/// boundary through <see cref="SnapRadians"/>.
/// <para>
/// <b>The default is 15°</b>, with 5/15/45/90 on the ladder: 15 divides the
/// circle into 24, which covers the overwhelming majority of level-building
/// rotations; 45 and 90 are the box-world angles a Hammer or Roblox user reaches
/// for constantly, and 5 is the fine rung for props. The same Alt modifier that
/// frees a move from the grid frees a rotation from the increment.
/// </para>
/// </remarks>
public sealed class AngleSnapSettings : SnapSettings
{
    /// <summary>The default rotation increment, in degrees.</summary>
    public const float DefaultIncrementDegrees = 15f;

    private static readonly float[] PresetIncrements = [5f, 15f, 45f, 90f];

    /// <summary>Creates settings at the <see cref="DefaultIncrementDegrees"/> increment.</summary>
    public AngleSnapSettings()
        : base(DefaultIncrementDegrees, PresetIncrements)
    {
    }

    /// <summary>
    /// The selectable increments in degrees, ascending — the same ladder as
    /// <see cref="SnapSettings.Increments"/>, reachable without an instance.
    /// </summary>
    public static IReadOnlyList<float> Presets => PresetIncrements;

    /// <summary>
    /// Rounds an angle given in <em>radians</em> to the nearest multiple of
    /// <see cref="SnapSettings.Increment"/> degrees, returning radians.
    /// </summary>
    /// <remarks>
    /// The rounding itself happens in degrees (see the type remarks), so a
    /// snapped quarter turn comes back as exactly <c>90 · π/180</c> rather than
    /// as whatever the accumulated radian arithmetic produced.
    /// </remarks>
    public float SnapRadians(float radians)
    {
        const float toDegrees = 180f / MathF.PI;
        return SnapScalar(radians * toDegrees) / toDegrees;
    }
}
