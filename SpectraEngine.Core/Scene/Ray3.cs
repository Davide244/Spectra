using System.Numerics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// A ray in world space: a start point and a direction of travel.
/// <para>
/// Contract: <see cref="Direction"/> must be unit length. Producers (e.g.
/// <see cref="Camera.ScreenPointToRay"/>) guarantee this so consumers can treat
/// the parameter along the ray as a world-space distance without re-normalizing
/// on every query. An immutable value type — safe to copy across threads.
/// </para>
/// </summary>
/// <param name="Origin">World-space start point of the ray.</param>
/// <param name="Direction">Unit-length world-space direction of the ray.</param>
public readonly record struct Ray3(Vector3 Origin, Vector3 Direction)
{
    /// <summary>The point <paramref name="distance"/> world units along the ray.</summary>
    public Vector3 PointAt(float distance) => Origin + Direction * distance;
}
