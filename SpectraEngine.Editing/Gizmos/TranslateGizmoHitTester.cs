using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Picks the translate gizmo's handles from a viewport ray, in screen space
/// with a pixel tolerance.
/// </summary>
/// <remarks>
/// <b>Handles overlap on screen, and the order they are tested in is the whole
/// design.</b> Near the pivot every handle is within a few pixels of every
/// other, so the test is not "which handle is closest" but a fixed priority:
/// the centre disc first, then the plane quads, then the axis arrows.
/// <list type="bullet">
///   <item>
///     The centre wins because it is the smallest target and the only one whose
///     region is entirely surrounded by its competitors — if it lost ties it
///     could never be grabbed at all.
///   </item>
///   <item>
///     Plane quads beat axis arrows because an arrow runs <em>underneath</em>
///     its two quads: a cursor inside a quad is always also within a few pixels
///     of the arrows bounding it, and the user who aimed at a filled square
///     meant the square.
///   </item>
///   <item>
///     Within the planes, the nearest hit to the camera wins — they are real
///     surfaces at different depths, and the one in front is the one you can
///     see. Within the axes, the smallest pixel distance wins (ties broken by
///     the nearer hit), because all three arrows share the pivot and so sit at
///     essentially one depth; proximity to the cursor is the only signal that
///     distinguishes them.
///   </item>
/// </list>
/// <para>
/// <b>Threading:</b> render thread only, like everything it reads. Every method
/// is a pure function and allocates nothing.
/// </para>
/// </remarks>
public static class TranslateGizmoHitTester
{
    /// <summary>
    /// The default pick tolerance in pixels — how far from an axis arrow the
    /// cursor may sit and still grab it. The same tolerance every gizmo uses;
    /// see <see cref="GizmoHitTesting.DefaultTolerancePixels"/>.
    /// </summary>
    public const float DefaultTolerancePixels = GizmoHitTesting.DefaultTolerancePixels;

    /// <summary>
    /// Picks the handle under <paramref name="ray"/>, or
    /// <see cref="GizmoPick.Miss"/> when the ray comes near none of them.
    /// </summary>
    /// <param name="geometry">This frame's gizmo geometry — the same one that was drawn.</param>
    /// <param name="ray">The viewport picking ray, from <see cref="Camera.ScreenPointToRay"/>.</param>
    /// <param name="tolerancePixels">
    /// Screen-space slack for the line-shaped axis handles. The surface handles
    /// ignore it: their tests are exact.
    /// </param>
    public static GizmoPick Pick(in GizmoGeometry geometry, in Ray3 ray, float tolerancePixels)
    {
        // A gizmo behind the camera has no coherent projection; picking it
        // would grab a handle the user cannot see.
        if (geometry.IsBehindCamera)
            return GizmoPick.Miss;

        GizmoPick centre = PickScreenHandle(in geometry, in ray);
        if (centre.IsHit)
            return centre;

        GizmoPick plane = PickPlaneHandles(in geometry, in ray);
        if (plane.IsHit)
            return plane;

        return PickAxisHandles(in geometry, in ray, tolerancePixels);
    }

    // The centre disc, tested in the camera-facing plane through the pivot —
    // the same plane its drag is constrained to, so what you grab is what you
    // then move in.
    private static GizmoPick PickScreenHandle(in GizmoGeometry geometry, in Ray3 ray)
    {
        if (!GizmoMath.TryRayDisc(
                in ray, geometry.Pivot, geometry.ViewNormal, geometry.ScreenRadius, out float distance))
        {
            return GizmoPick.Miss;
        }

        return new GizmoPick(GizmoHandle.Screen, distance, 0f, ray.PointAt(distance));
    }

    private static GizmoPick PickPlaneHandles(in GizmoGeometry geometry, in Ray3 ray)
    {
        GizmoPick best = GizmoPick.Miss;

        // Fixed order, but the comparison is by depth, so the enumeration order
        // never decides the winner — only a genuine depth tie would, and a tie
        // means the two quads are coincident on screen anyway.
        for (GizmoHandle handle = GizmoHandle.PlaneYZ; handle <= GizmoHandle.PlaneXY; handle++)
        {
            if (!geometry.TryGetPlaneQuad(handle, out Vector3 corner, out Vector3 u, out Vector3 v, out float size))
                continue;

            if (!GizmoMath.TryRayQuad(in ray, corner, u, size, v, size, out float distance))
                continue;

            if (distance < best.RayDistance)
                best = new GizmoPick(handle, distance, 0f, ray.PointAt(distance));
        }

        return best;
    }

    private static GizmoPick PickAxisHandles(
        in GizmoGeometry geometry, in Ray3 ray, float tolerancePixels)
    {
        GizmoPick best = GizmoPick.Miss;
        float tolerance = MathF.Max(tolerancePixels, 0f);

        for (GizmoHandle handle = GizmoHandle.AxisX; handle <= GizmoHandle.AxisZ; handle++)
        {
            if (!geometry.TryGetAxisSegment(handle, out Vector3 start, out Vector3 end))
                continue;

            // World gap → pixels through the same scale that sized the gizmo,
            // so the tolerance means what it says on screen.
            float pixels = GizmoHitTesting.SegmentPixelDistance(
                in geometry, in ray, start, end, out float distance);
            if (pixels > tolerance)
                continue;

            // Closer to the cursor wins; an exact pixel tie (looking straight
            // down the corner between two arrows) falls back to depth.
            if (pixels < best.PixelDistance ||
                (pixels == best.PixelDistance && distance < best.RayDistance))
            {
                best = new GizmoPick(handle, distance, pixels, ray.PointAt(distance));
            }
        }

        return best;
    }
}
