using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Picks the scale gizmo's cube handles from a viewport ray: the uniform centre
/// cube first, then the three axis cubes.
/// </summary>
/// <remarks>
/// <b>The cubes are solid, so the test is exact containment</b> rather than the
/// pixel proximity the arrows and rings need — a box has an interior to be
/// inside of, and a ray-box test is both cheaper and less surprising than a
/// distance threshold around one. The pixel tolerance is used only to widen the
/// shafts, which run <em>under</em> their cubes and are what a user grabs when
/// they aim at the middle of an axis rather than at its tip.
/// <para>
/// <b>The centre wins ties</b> for the same reason it does on the translate
/// gizmo: it is the smallest target and is surrounded by its competitors, so if
/// it lost ties it could never be grabbed at all.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only. Pure function, no allocation.
/// </para>
/// </remarks>
public static class ScaleGizmoHitTester
{
    /// <summary>
    /// Picks the handle under <paramref name="ray"/>, or
    /// <see cref="GizmoPick.Miss"/> when the ray comes near none of them.
    /// </summary>
    /// <param name="geometry">This frame's gizmo geometry — the same one that was drawn.</param>
    /// <param name="ray">The viewport picking ray, from <see cref="Camera.ScreenPointToRay"/>.</param>
    /// <param name="tolerancePixels">Screen-space slack for the axis shafts.</param>
    public static GizmoPick Pick(in GizmoGeometry geometry, in Ray3 ray, float tolerancePixels)
    {
        if (geometry.IsBehindCamera)
            return GizmoPick.Miss;

        if (geometry.TryGetHandleBox(GizmoHandle.Screen, out Vector3 centre, out float radius) &&
            GizmoHitTesting.TryRayHandleBox(in geometry, in ray, centre, radius, out float centreDistance))
        {
            return new GizmoPick(GizmoHandle.Screen, centreDistance, 0f, ray.PointAt(centreDistance));
        }

        float tolerance = MathF.Max(tolerancePixels, 0f);
        GizmoPick best = GizmoPick.Miss;

        for (GizmoHandle handle = GizmoHandle.AxisX; handle <= GizmoHandle.AxisZ; handle++)
        {
            if (!geometry.TryGetHandleBox(handle, out Vector3 boxCentre, out float boxRadius))
                continue;

            // Pick only what the drag will accept: the resize drag measures
            // cursor travel along this axis through TryClosestPointOnLine, so
            // an axis the projection refuses (viewed near end-on) must not be
            // offered by cube or shaft; same pick/drag agreement as the
            // rotate tester and, now, the translate arrows.
            if (!GizmoMath.TryClosestPointOnLine(in ray, geometry.Pivot, geometry.Axis(handle), out _))
                continue;

            // The cube: exact, and reported at zero pixel distance so it always
            // beats a shaft that merely came close.
            if (GizmoHitTesting.TryRayHandleBox(in geometry, in ray, boxCentre, boxRadius, out float boxDistance) &&
                (0f < best.PixelDistance || boxDistance < best.RayDistance))
            {
                best = new GizmoPick(handle, boxDistance, 0f, ray.PointAt(boxDistance));
                continue;
            }

            // The shaft: pixel proximity, like a translate arrow.
            geometry.TryGetAxisSegment(handle, out Vector3 start, out Vector3 end);
            float pixels = GizmoHitTesting.SegmentPixelDistance(
                in geometry, in ray, start, end, out float distance);
            if (pixels > tolerance)
                continue;

            if (pixels < best.PixelDistance ||
                (pixels == best.PixelDistance && distance < best.RayDistance))
            {
                best = new GizmoPick(handle, distance, pixels, ray.PointAt(distance));
            }
        }

        return best;
    }
}
