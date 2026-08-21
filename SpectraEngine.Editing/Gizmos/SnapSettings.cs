using SpectraEngine.Core.Input;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// What every manipulator's snapping has in common: whether it is on, how big
/// one step is, the preset ladder that step is chosen from, and the modifier
/// key that inverts the setting for the duration of a gesture.
/// </summary>
/// <remarks>
/// <b>The quantity differs per tool, the policy does not.</b> A move snaps a
/// world-unit position, a rotate snaps degrees, a resize snaps a world-unit size
/// change — but "hold Alt to invert", "the ladder clamps at both ends", "an
/// increment typed into a panel enters the ladder at its nearest rung" and
/// "exact halves round away from zero" are one behaviour that users learn once.
/// Subclasses supply the default and the ladder and add whatever unit-specific
/// helper they need (<see cref="GridSnapSettings.SnapMasked"/>,
/// <see cref="AngleSnapSettings.SnapRadians"/>); nothing else is duplicated.
/// <para>
/// All three units are absolute quantities of the thing being edited, never a
/// multiplier: an increment a UI puts in a box has to mean the same thing at
/// every object size, which is precisely what <see cref="ResizeSnapSettings"/>
/// stopped doing when it quantised a scale factor.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the tools that read it.
/// </para>
/// </remarks>
public abstract class SnapSettings
{
    private readonly float[] _increments;
    private float _increment;

    /// <summary>
    /// Creates settings starting at <paramref name="defaultIncrement"/> with the
    /// ladder <paramref name="increments"/> (ascending; copied, so the caller's
    /// array cannot be mutated out from under the ladder).
    /// </summary>
    protected SnapSettings(float defaultIncrement, params float[] increments)
    {
        ArgumentNullException.ThrowIfNull(increments);
        if (increments.Length == 0)
            throw new ArgumentException("A snap ladder needs at least one increment.", nameof(increments));

        _increments = (float[])increments.Clone();
        Increment = defaultIncrement;
    }

    /// <summary>
    /// Whether snapping applies by default. <see cref="ToggleModifier"/> inverts
    /// this per gesture, so a user who works snapped can drop to free movement —
    /// and a user who works free can snap — without leaving the drag.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The step size, in whatever unit the subclass documents. Must be positive;
    /// the setter throws otherwise, because a zero or negative step would divide
    /// by zero (or mirror the quantity) inside <see cref="SnapScalar"/>.
    /// </summary>
    public float Increment
    {
        get => _increment;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _increment = value;
        }
    }

    /// <summary>
    /// The modifier that inverts <see cref="Enabled"/> while held.
    /// <see cref="KeyModifiers.None"/> disables the override entirely.
    /// </summary>
    /// <remarks>
    /// Alt by default, and the same key for all three tools: Shift and Control
    /// are already spoken for by add-to-selection and toggle-selection, and Alt
    /// is the free-movement modifier in both Hammer and Roblox Studio.
    /// </remarks>
    public KeyModifiers ToggleModifier { get; set; } = KeyModifiers.Alt;

    /// <summary>The selectable increments, ascending. <see cref="CyclePreset"/> walks this ladder.</summary>
    public IReadOnlyList<float> Increments => _increments;

    /// <summary>
    /// Whether snapping applies given the modifiers held right now:
    /// <see cref="Enabled"/>, inverted while <see cref="ToggleModifier"/> is
    /// held.
    /// </summary>
    public bool IsActiveWith(KeyModifiers held)
    {
        bool overridden = ToggleModifier != KeyModifiers.None && (held & ToggleModifier) == ToggleModifier;
        return Enabled ^ overridden;
    }

    /// <summary>
    /// Rounds one value to the nearest multiple of <see cref="Increment"/>.
    /// Exact halves round away from zero, so the ladder stays symmetric about
    /// zero.
    /// </summary>
    public float SnapScalar(float value) =>
        MathF.Round(value / _increment, MidpointRounding.AwayFromZero) * _increment;

    /// <summary>
    /// Steps <see cref="Increment"/> along <see cref="Increments"/> by
    /// <paramref name="direction"/> rungs (negative for finer, positive for
    /// coarser) and returns the new value.
    /// </summary>
    /// <remarks>
    /// A current increment that is not itself a preset — one typed into a
    /// property panel — enters the ladder at its nearest rung rather than being
    /// rejected, and the ends clamp instead of wrapping so a repeated keypress
    /// settles at the coarsest or finest step.
    /// </remarks>
    public float CyclePreset(int direction)
    {
        int index = NearestIncrementIndex(_increment) + direction;
        Increment = _increments[Math.Clamp(index, 0, _increments.Length - 1)];
        return _increment;
    }

    /// <summary>
    /// Selects <see cref="Increments"/>[<paramref name="index"/>] as the current
    /// increment — the direct binding behind a snap-size menu or number key.
    /// </summary>
    public void SelectPreset(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _increments.Length);
        Increment = _increments[index];
    }

    // Nearest in RATIO, not in absolute difference, because every ladder here is
    // roughly geometric: 3.0 sits closer to 4 (a factor of 1.33) than to 2 (a
    // factor of 1.5), while by absolute distance it is exactly one away from
    // both and the choice would come down to enumeration order.
    private int NearestIncrementIndex(float increment)
    {
        int best = 0;
        float bestRatio = float.PositiveInfinity;
        for (int i = 0; i < _increments.Length; i++)
        {
            float ratio = increment > _increments[i]
                ? increment / _increments[i]
                : _increments[i] / increment;
            if (ratio < bestRatio)
            {
                bestRatio = ratio;
                best = i;
            }
        }
        return best;
    }
}
