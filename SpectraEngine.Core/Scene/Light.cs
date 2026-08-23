using System;
using System.Numerics;

namespace SpectraEngine.Core.Scene;

/// <summary>What shape a light's emission has.</summary>
public enum LightKind
{
    /// <summary>
    /// Parallel rays from infinitely far away: a sun. Position is ignored and
    /// the node's forward axis is the direction the light travels.
    /// </summary>
    Directional,

    /// <summary>
    /// Radiates from the node's position in every direction, falling off with
    /// distance and stopping at <see cref="Light.Range"/>.
    /// </summary>
    Point,
}

/// <summary>
/// A light attached to a <see cref="SceneNode"/>, parallel to
/// <see cref="MeshRenderer"/>: the node supplies the position and orientation,
/// this supplies everything else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Colour is linear, and intensity is separate from it.</b> Everything the
/// engine shades with is linear light, so a light's colour is a linear triple
/// and not a display colour; <c>ColorSpace.SrgbToLinear</c> is what turns a
/// picked colour into one. Keeping intensity out of the colour means a light can
/// be brightened past 1 without its hue drifting, which is exactly what the HDR
/// target and the tone curve exist to carry.
/// </para>
/// <para>
/// <b>Mutable, unlike a <c>Brush</c>.</b> A brush is immutable because the CSG
/// caches key on its identity; a light feeds nothing derived, so animating one
/// is an ordinary property write and costs no recompile.
/// </para>
/// </remarks>
public sealed class Light
{
    private float _intensity = 1f;
    private float _range = 10f;

    /// <summary>Directional or point. See <see cref="LightKind"/>.</summary>
    public LightKind Kind { get; set; } = LightKind.Directional;

    /// <summary>Linear RGB colour. Not a display colour; see the remarks.</summary>
    public Vector3 Color { get; set; } = Vector3.One;

    /// <summary>Multiplier on <see cref="Color"/>. Negative values are refused.</summary>
    public float Intensity
    {
        get => _intensity;
        set => _intensity = value >= 0f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Light intensity cannot be negative.");
    }

    /// <summary>
    /// Distance at which a point light's contribution reaches zero. Ignored by a
    /// directional light, which has no falloff.
    /// </summary>
    /// <remarks>
    /// A hard cutoff rather than a physical inverse-square tail, because a light
    /// that never quite reaches zero can never be culled: every light would
    /// affect every surface and the nearest-N selection would be arbitrary
    /// rather than merely limited.
    /// </remarks>
    public float Range
    {
        get => _range;
        set => _range = value > 0f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Light range must be positive.");
    }

    /// <summary>Whether this light contributes at all. A disabled light is collected by nothing.</summary>
    public bool Enabled { get; set; } = true;
}
