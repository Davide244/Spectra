using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The outcome of one hit test against the gizmo: which handle was picked, how
/// far along the picking ray, and how close to it the cursor came in pixels.
/// </summary>
/// <param name="Handle">The picked handle, or <see cref="GizmoHandle.None"/> for a miss.</param>
/// <param name="RayDistance">Distance along the picking ray to the hit, in world units.</param>
/// <param name="PixelDistance">
/// Screen-space miss distance in pixels. Zero for the surface handles (plane
/// quads and the centre disc), whose tests are exact containment rather than
/// proximity; the closest-approach distance for an axis arrow, which is a line
/// and would otherwise be unpickable.
/// </param>
/// <param name="Point">
/// The world-space point the ray hit at <paramref name="RayDistance"/>. For an
/// axis arrow this is the point on the <em>ray</em> nearest the arrow, not a
/// point on the arrow itself.
/// </param>
public readonly record struct GizmoPick(
    GizmoHandle Handle, float RayDistance, float PixelDistance, Vector3 Point)
{
    /// <summary>A miss: no handle, at infinite distance.</summary>
    public static GizmoPick Miss =>
        new(GizmoHandle.None, float.PositiveInfinity, float.PositiveInfinity, Vector3.Zero);

    /// <summary>True when a handle was picked.</summary>
    public bool IsHit => Handle != GizmoHandle.None;
}
