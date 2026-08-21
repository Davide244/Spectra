using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The closed-form geometry every part of the manipulator shares: the
/// perspective pixel-to-world scale that gives the gizmo a constant screen
/// size, the ray queries hit-testing picks handles with, and the ray-onto-line
/// and ray-onto-plane projections a drag is constrained by.
/// </summary>
/// <remarks>
/// It lives in one place because rendering, picking, and dragging must agree
/// <em>exactly</em>: the pixel scale that sizes the drawn arrows is the same
/// function that converts a picking ray's world miss distance into pixels, so
/// "within eight pixels of the arrow" means the same thing to the user's eye
/// and to the hit test. Every method is a pure function of its arguments —
/// no state, no allocation.
/// </remarks>
public static class GizmoMath
{
    // How close to parallel with the constraint a ray may come before the
    // projection is refused rather than solved: the closed forms below stay
    // finite there, but the solution runs away to the horizon, which as a drag
    // reads as the selection teleporting. Freezing at the last good value is
    // the behaviour every editor has.
    //
    // The threshold is a squared SINE of the angle between the ray and the
    // constraint surface, and both guards express it that way — 1e-3 is
    // sin² ≈ 0.0316², about 1.8°. That the two agree is load-bearing: a line
    // guard on sin² and a plane guard on |cos| (which is that same sine, not
    // its square) would differ by a factor of ~30 in angle, and the loose side
    // amplifies a one-pixel cursor move by up to 1/|cos| — hundreds of world
    // units of fling before the refusal finally bites, which is precisely the
    // teleport this constant exists to prevent.
    private const float ParallelSineSquaredEpsilon = 1e-3f;

    // Guards the degenerate-segment and parallel-lines branches of the
    // closest-approach solve, where the denominator is a squared quantity.
    private const float DegenerateEpsilon = 1e-9f;

    /// <summary>
    /// How many world units one viewport pixel spans at
    /// <paramref name="viewDepth"/> units in front of the camera, under
    /// <paramref name="camera"/>'s vertical field of view.
    /// </summary>
    /// <remarks>
    /// This is the whole constant-screen-size trick. A perspective projection
    /// maps a world length <c>L</c> at depth <c>d</c> to
    /// <c>L / (2·d·tan(fov/2)) · height</c> pixels, so inverting it gives the
    /// world length that always covers one pixel. Multiply by a desired pixel
    /// size and the gizmo is the same size on screen whether the selection is
    /// two units away or twenty thousand — which is the difference between a
    /// usable manipulator and one that vanishes to a dot the moment you pull
    /// the camera back across an open world.
    /// <para>
    /// <paramref name="viewDepth"/> is depth <em>along the view axis</em>, not
    /// euclidean distance: using the distance instead would swell the gizmo
    /// toward the edges of a wide field of view, because the projection divides
    /// by depth, not by distance.
    /// </para>
    /// </remarks>
    public static float WorldPerPixel(Camera camera, float viewportHeight, float viewDepth)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (viewportHeight <= 0f)
            return 0f; // A not-yet-sized viewport has no pixels to scale to.

        return 2f * viewDepth * MathF.Tan(camera.FieldOfView * 0.5f) / viewportHeight;
    }

    /// <summary>
    /// The depth of <paramref name="point"/> along the camera's view axis:
    /// positive in front of the camera, negative behind it.
    /// </summary>
    public static float ViewDepth(Camera camera, Vector3 point)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return Vector3.Dot(point - camera.Position, camera.Forward);
    }

    /// <summary>
    /// Closest approach between a ray and the segment
    /// <paramref name="a"/>–<paramref name="b"/>: the points on each that are
    /// nearest to the other, and how far along the ray that happens.
    /// </summary>
    /// <remarks>
    /// The standard two-parameter least-squares solve, clamped to the segment
    /// and to the forward half of the ray, with a second pass when the ray
    /// clamp bites so the returned pair really is the constrained minimum.
    /// Degenerate inputs — a zero-length segment, or a ray parallel to it —
    /// fall back to projecting onto the segment start, which is finite and
    /// still the right answer for the parallel case.
    /// </remarks>
    /// <param name="ray">The picking ray; its direction must be unit length.</param>
    /// <param name="a">Segment start.</param>
    /// <param name="b">Segment end.</param>
    /// <param name="rayDistance">Distance along the ray to <paramref name="rayPoint"/>.</param>
    /// <param name="rayPoint">The point on the ray closest to the segment.</param>
    /// <param name="segmentPoint">The point on the segment closest to the ray.</param>
    public static void ClosestApproachToSegment(
        in Ray3 ray,
        Vector3 a,
        Vector3 b,
        out float rayDistance,
        out Vector3 rayPoint,
        out Vector3 segmentPoint)
    {
        Vector3 u = ray.Direction; // unit by Ray3's contract, so dot(u, u) = 1
        Vector3 v = b - a;
        Vector3 w = ray.Origin - a;

        float vv = Vector3.Dot(v, v);
        float uv = Vector3.Dot(u, v);
        float uw = Vector3.Dot(w, u);
        float vw = Vector3.Dot(w, v);

        // dot(u,u)·dot(v,v) − dot(u,v)² with dot(u,u) = 1; zero exactly when
        // the ray and the segment are parallel (or the segment is a point).
        float denominator = vv - uv * uv;

        float t = denominator <= DegenerateEpsilon || vv <= DegenerateEpsilon
            ? 0f
            : Math.Clamp((vw - uv * uw) / denominator, 0f, 1f);

        // s = (t·dot(u,v) − dot(w,u)) / dot(u,u), with dot(u,u) = 1.
        float s = t * uv - uw;
        if (s <= 0f)
        {
            // The unconstrained solution sits behind the ray origin. Pin the
            // ray to its origin and re-minimise over the segment alone.
            s = 0f;
            t = vv <= DegenerateEpsilon ? 0f : Math.Clamp(vw / vv, 0f, 1f);
        }

        rayDistance = s;
        rayPoint = ray.Origin + u * s;
        segmentPoint = a + v * t;
    }

    /// <summary>
    /// Intersects a ray with the plane through <paramref name="planePoint"/>
    /// with unit normal <paramref name="planeNormal"/>, returning false for a
    /// grazing hit (see the parallel-refusal note on this class) or an
    /// intersection behind the ray origin.
    /// </summary>
    public static bool TryRayPlane(in Ray3 ray, Vector3 planePoint, Vector3 planeNormal, out float rayDistance)
    {
        // dot(unit ray, unit normal) is the SINE of the angle between the ray
        // and the plane, so squaring it puts this guard on exactly the same
        // scale — and therefore at exactly the same angle — as the line guard
        // in TryClosestPointOnLine.
        float denominator = Vector3.Dot(ray.Direction, planeNormal);
        if (denominator * denominator < ParallelSineSquaredEpsilon)
        {
            rayDistance = 0f;
            return false;
        }

        float distance = Vector3.Dot(planePoint - ray.Origin, planeNormal) / denominator;
        if (distance < 0f)
        {
            rayDistance = 0f;
            return false;
        }

        rayDistance = distance;
        return true;
    }

    /// <summary>
    /// Intersects a ray with an axis-aligned rectangle in its own plane: the
    /// quad spans <paramref name="uLength"/> along <paramref name="uAxis"/> and
    /// <paramref name="vLength"/> along <paramref name="vAxis"/> from
    /// <paramref name="corner"/>. Both axes must be unit length and mutually
    /// perpendicular.
    /// </summary>
    public static bool TryRayQuad(
        in Ray3 ray,
        Vector3 corner,
        Vector3 uAxis,
        float uLength,
        Vector3 vAxis,
        float vLength,
        out float rayDistance)
    {
        Vector3 normal = Vector3.Cross(uAxis, vAxis);
        if (!TryRayPlane(in ray, corner, normal, out rayDistance))
            return false;

        // Perpendicular unit axes make the in-plane coordinates plain dots.
        Vector3 local = ray.PointAt(rayDistance) - corner;
        float u = Vector3.Dot(local, uAxis);
        float v = Vector3.Dot(local, vAxis);
        if (u < 0f || u > uLength || v < 0f || v > vLength)
        {
            rayDistance = 0f;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Intersects a ray with the disc of <paramref name="radius"/> centred at
    /// <paramref name="centre"/> in the plane with unit normal
    /// <paramref name="normal"/>.
    /// </summary>
    public static bool TryRayDisc(in Ray3 ray, Vector3 centre, Vector3 normal, float radius, out float rayDistance)
    {
        if (!TryRayPlane(in ray, centre, normal, out rayDistance))
            return false;

        if (Vector3.DistanceSquared(ray.PointAt(rayDistance), centre) > radius * radius)
        {
            rayDistance = 0f;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The point on the infinite line through <paramref name="linePoint"/>
    /// along unit <paramref name="lineDirection"/> that is closest to the ray
    /// — the projection an axis-constrained drag follows. Returns false when
    /// the ray is close to parallel with the line, where the projection is
    /// numerically meaningless.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ClosestApproachToSegment"/> nothing is clamped here:
    /// a drag must be able to run the selection past the drawn end of the arrow
    /// and behind the pivot, so both parameters stay unbounded.
    /// </remarks>
    public static bool TryClosestPointOnLine(
        in Ray3 ray, Vector3 linePoint, Vector3 lineDirection, out Vector3 point)
    {
        Vector3 u = ray.Direction;
        Vector3 w = ray.Origin - linePoint;

        float uv = Vector3.Dot(u, lineDirection);
        // dot(u,u)·dot(v,v) − dot(u,v)² with both unit: 1 − cos² = sin² of the
        // angle between ray and line, so the epsilon is an angle threshold.
        float denominator = 1f - uv * uv;
        if (denominator < ParallelSineSquaredEpsilon)
        {
            point = linePoint;
            return false;
        }

        float uw = Vector3.Dot(w, u);
        float vw = Vector3.Dot(w, lineDirection);
        float t = (vw - uv * uw) / denominator;

        point = linePoint + lineDirection * t;
        return true;
    }
}
