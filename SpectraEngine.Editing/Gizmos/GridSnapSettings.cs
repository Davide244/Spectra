using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// World-grid snapping for a translate drag: how big the grid step is, what a
/// snapped drag quantises (<see cref="Mode"/>), and, through
/// <see cref="SnapSettings"/>, whether snapping is on and which modifier
/// inverts it.
/// </summary>
/// <remarks>
/// <b>The DELTA is snapped by default, and the earlier claim here that Studio
/// rounds the result was wrong.</b> Studio's handle drags quantise the
/// movement relative to the grab, so a part at x = 3.7 dragged one 1-unit
/// notch lands at 4.7 with its sub-grid offset intact; Blender's incremental
/// snap does the same. Rounding the absolute destination is Hammer's model and
/// Blender's opt-in "Absolute Grid Snap", kept here as
/// <see cref="TranslateSnapMode.AbsoluteGrid"/>; see
/// <see cref="TranslateSnapMode"/> for the full story, including why the
/// absolute mode anchors on the reference node rather than the pivot average.
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

    /// <summary>
    /// What a snapped drag quantises: the displacement (default) or the
    /// reference node's absolute destination. See <see cref="TranslateSnapMode"/>.
    /// </summary>
    public TranslateSnapMode Mode { get; set; } = TranslateSnapMode.Delta;

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
