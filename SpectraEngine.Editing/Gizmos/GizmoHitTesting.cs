using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The screen-space proximity tests the three gizmos' hit-testers share: the
/// pick tolerance every tool measures against, closest approach to a line-shaped
/// handle, and closest approach to a ring.
/// </summary>
/// <remarks>
/// <b>Every distance here comes back in pixels, not world units.</b> A gizmo has
/// a constant screen size, so "within eight pixels of the handle" is the only
/// tolerance that means the same thing at two units and at twenty thousand; the
/// conversion runs through the same <see cref="GizmoGeometry.WorldToPixels"/>
/// that sized what was drawn, so the tolerance the user feels and the tolerance
/// the code applies cannot drift apart.
/// <para>
/// <b>Threading:</b> render thread only, like everything it reads. Every method
/// is a pure function and allocates nothing.
/// </para>
/// </remarks>
public static class GizmoHitTesting
{
    /// <summary>
    /// The default pick tolerance in pixels — how far from a line-shaped handle
    /// (an axis arrow, a rotate ring) the cursor may sit and still grab it.
    /// </summary>
    public const float DefaultTolerancePixels = 8f;

    /// <summary>
    /// How many segments a rotate ring is approximated by, for both drawing and
    /// picking.
    /// </summary>
    /// <remarks>
    /// <b>Picking and rendering must use the same number</b>, because the polygon
    /// is what the user sees and therefore what they aim at. Forty-eight keeps
    /// the chord error of a ~96 px ring under a fifth of a pixel — far below the
    /// pick tolerance — while costing 48 segment solves per ring, which is
    /// nothing against a per-frame budget that already carries a BVH query.
    /// </remarks>
    public const int RingSegments = 48;

    /// <summary>
    /// The screen-space distance in pixels from <paramref name="ray"/> to the
    /// segment <paramref name="a"/>–<paramref name="b"/>, plus how far along the
    /// ray the closest approach happens.
    /// </summary>
    public static float SegmentPixelDistance(
        in GizmoGeometry geometry, in Ray3 ray, Vector3 a, Vector3 b, out float rayDistance)
    {
        GizmoMath.ClosestApproachToSegment(
            in ray, a, b, out rayDistance, out Vector3 onRay, out Vector3 onSegment);
        return geometry.WorldToPixels(Vector3.Distance(onRay, onSegment));
    }

    /// <summary>
    /// The screen-space distance in pixels from <paramref name="ray"/> to the
    /// circle of <paramref name="radius"/> around <paramref name="centre"/> in
    /// the plane with unit normal <paramref name="axis"/>, plus how far along the
    /// ray the closest approach happens.
    /// </summary>
    /// <remarks>
    /// <b>Measured against the drawn polygon, in screen space, deliberately.</b>
    /// The tempting closed form — intersect the ray with the ring's plane and
    /// compare the hit's in-plane distance from the centre against the radius —
    /// is wrong for a manipulator in two ways: it fails outright when the view is
    /// near edge-on to the ring (no usable plane intersection, so a ring seen
    /// almost side-on becomes unpickable exactly where it is easiest to aim at),
    /// and where it does work it measures an in-plane distance that projects to
    /// far fewer pixels than it claims, so the tolerance silently shrinks as the
    /// ring tilts. Walking the ring's segments costs a few hundred flops and is
    /// correct from every angle.
    /// </remarks>
    public static float RingPixelDistance(
        in GizmoGeometry geometry, in Ray3 ray, Vector3 centre, Vector3 axis, float radius, out float rayDistance)
    {
        BuildRingBasis(axis, out Vector3 u, out Vector3 v);

        float bestPixels = float.PositiveInfinity;
        rayDistance = float.PositiveInfinity;

        Vector3 previous = centre + u * radius;
        for (int i = 1; i <= RingSegments; i++)
        {
            float angle = MathF.Tau * i / RingSegments;
            Vector3 point = centre + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;

            float pixels = SegmentPixelDistance(in geometry, in ray, previous, point, out float distance);
            if (pixels < bestPixels)
            {
                bestPixels = pixels;
                rayDistance = distance;
            }

            previous = point;
        }

        return bestPixels;
    }

    /// <summary>
    /// Two unit vectors spanning the plane perpendicular to <paramref name="axis"/>
    /// — the basis a ring is generated in, shared by the hit tester and the
    /// renderer so the picked polygon is exactly the drawn one.
    /// </summary>
    public static void BuildRingBasis(Vector3 axis, out Vector3 first, out Vector3 second)
    {
        // Pick the reference away from the axis so the cross product is well
        // conditioned; the same rule Brush.CreatePlaneQuad uses.
        Vector3 reference = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        first = Vector3.Normalize(Vector3.Cross(reference, axis));
        second = Vector3.Cross(axis, first);
    }

    /// <summary>
    /// Intersects a ray with the axis-aligned-in-frame cube of half-extent
    /// <paramref name="radius"/> centred at <paramref name="centre"/>, oriented
    /// by the geometry's frame axes. Used for the scale gizmo's cube handles,
    /// whose test is exact containment rather than proximity.
    /// </summary>
    public static bool TryRayHandleBox(
        in GizmoGeometry geometry, in Ray3 ray, Vector3 centre, float radius, out float rayDistance)
    {
        // Slab test in the gizmo frame: transform the ray into the frame by
        // projecting onto its three orthonormal axes, which for an orthonormal
        // basis is the inverse rotation.
        Vector3 origin = ToFrame(in geometry, ray.Origin - centre);
        Vector3 direction = ToFrame(in geometry, ray.Direction);

        float near = 0f;
        float far = float.PositiveInfinity;

        if (!SlabClip(origin.X, direction.X, radius, ref near, ref far) ||
            !SlabClip(origin.Y, direction.Y, radius, ref near, ref far) ||
            !SlabClip(origin.Z, direction.Z, radius, ref near, ref far))
        {
            rayDistance = 0f;
            return false;
        }

        rayDistance = near;
        return true;
    }

    private static Vector3 ToFrame(in GizmoGeometry geometry, Vector3 world) => new(
        Vector3.Dot(world, geometry.AxisX),
        Vector3.Dot(world, geometry.AxisY),
        Vector3.Dot(world, geometry.AxisZ));

    private static bool SlabClip(float origin, float direction, float radius, ref float near, ref float far)
    {
        if (MathF.Abs(direction) < 1e-8f)
        {
            // Parallel to this slab: a hit is only possible if the ray already
            // lies between its planes.
            return origin >= -radius && origin <= radius;
        }

        float inverse = 1f / direction;
        float t0 = (-radius - origin) * inverse;
        float t1 = (radius - origin) * inverse;
        if (t0 > t1)
            (t0, t1) = (t1, t0);

        near = MathF.Max(near, t0);
        far = MathF.Min(far, t1);
        return near <= far;
    }
}
