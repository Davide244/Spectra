using System.Collections.Generic;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Factor snapping for a resize drag: the increment is a step of the
/// <b>multiplier</b>, so a 0.25 increment makes a drag land on ×0.75, ×1.0,
/// ×1.25 and so on.
/// </summary>
/// <remarks>
/// <b>The factor is snapped, not the resulting size.</b> A resize gizmo does not
/// know what unit its target is measured in — a mesh node's scale is
/// dimensionless and a brush's extents are world units — so there is no absolute
/// grid to land on the way a translate has one. Quantising the multiplier is the
/// honest equivalent, and it is what makes "drag to exactly double" a thing the
/// user can hit rather than approach. Anyone who needs an exact world size types
/// it into the property panel; that is what the panel is for.
/// <para>
/// The result is clamped away from zero by the tool
/// (<see cref="ScaleGizmo.MinimumFactor"/>): a snapped factor of exactly zero
/// would collapse a mesh to a plane and would be rejected outright by
/// <c>Brush.WithScaledExtents</c>.
/// </para>
/// </remarks>
public sealed class ScaleSnapSettings : SnapSettings
{
    /// <summary>The default factor step.</summary>
    public const float DefaultIncrement = 0.25f;

    private static readonly float[] PresetIncrements = [0.05f, 0.1f, 0.25f, 0.5f, 1f];

    /// <summary>Creates settings at the <see cref="DefaultIncrement"/> factor step.</summary>
    public ScaleSnapSettings()
        : base(DefaultIncrement, PresetIncrements)
    {
    }

    /// <summary>
    /// The selectable factor steps, ascending — the same ladder as
    /// <see cref="SnapSettings.Increments"/>, reachable without an instance.
    /// </summary>
    public static IReadOnlyList<float> Presets => PresetIncrements;
}
