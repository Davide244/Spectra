using System;
using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Physics.Character;

/// <summary>
/// Exact distance and sweep between a capsule and a convex piece.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plane distances are the broad phase, never the narrow phase.</b> Taking
/// the maximum signed distance over a convex's outward planes tests against the
/// <em>sharp-cornered</em> offset polytope, not the rounded one a capsule
/// actually sweeps: at a box corner the sharp apex sits <c>r·√3</c> from the
/// vertex against a true distance of <c>r</c>, so at a 0.35 radius there is a
/// quarter-unit of phantom solid at every convex corner in the world. Used as a
/// contact test that is "catching on brush edges" pre-installed, and it would
/// narrow a 1.0-unit doorway below the width of the character trying to walk
/// through it.
/// </para>
/// <para>
/// So the plane test is used for what it <em>is</em> exact at — proving
/// separation, since a positive maximum is a genuine separating axis — and the
/// real distance comes from the piece's face polygons, which the brush pipeline
/// already produces and which give the true rounded answer including the corner
/// direction as the contact normal.
/// </para>
/// </remarks>
public static class CapsuleGeometry
{
    /// <summary>Below this, two directions are treated as parallel.</summary>
    private const float ParallelEpsilon = 1e-6f;

    /// <summary>Slack for the "is this point inside the face polygon" test.</summary>
    private const float InsideEpsilon = 1e-5f;

    /// <summary>
    /// The largest signed distance from the capsule to any of the piece's
    /// planes. Positive proves separation; non-positive proves nothing.
    /// </summary>
    /// <remarks>
    /// Exact as a <em>reject</em>, because a positive value exhibits an actual
    /// separating plane. Never used as a contact distance — see the type
    /// remarks for what that costs.
    /// </remarks>
    public static float MaxPlaneSeparation(
        in CharacterCapsule capsule, ReadOnlySpan<Plane> planes, out int planeIndex)
    {
        float best = float.NegativeInfinity;
        planeIndex = -1;

        for (int i = 0; i < planes.Length; i++)
        {
            float d1 = Plane.DotCoordinate(planes[i], capsule.Center1);
            float d2 = Plane.DotCoordinate(planes[i], capsule.Center2);
            float separation = MathF.Min(d1, d2) - capsule.Radius;

            if (separation > best)
            {
                best = separation;
                planeIndex = i;
            }
        }

        return best;
    }

    /// <summary>Whether a point lies inside every one of the piece's half-spaces.</summary>
    public static bool ContainsPoint(Vector3 point, ReadOnlySpan<Plane> planes, float slack = 0f)
    {
        for (int i = 0; i < planes.Length; i++)
        {
            if (Plane.DotCoordinate(planes[i], point) > slack)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The exact distance from the capsule's surface to the piece, with the
    /// outward contact normal and the closest point on the piece.
    /// </summary>
    /// <remarks>
    /// Negative means penetrating. When penetrating, the normal and depth come
    /// from the least-violated plane, which is the correct minimum-translation
    /// axis for a convex and is what lets depenetration push along the shortest
    /// way out rather than the way it happened to arrive.
    /// </remarks>
    public static float Distance(
        in CharacterCapsule capsule,
        ReadOnlySpan<Plane> planes,
        ReadOnlySpan<Polygon> faces,
        out Vector3 normal,
        out Vector3 pointOnPiece)
    {
        normal = Vector3.UnitY;
        pointOnPiece = capsule.Center1;

        // A positive maximum is a real separating plane, so the capsule is
        // certainly clear — but the VALUE is the sharp-corner underestimate, so
        // it is used only to skip the expensive path when comfortably clear.
        float planeSeparation = MaxPlaneSeparation(in capsule, planes, out int planeIndex);
        if (planeSeparation > capsule.Radius)
        {
            normal = planes[planeIndex].Normal;
            pointOnPiece = ClosestPointOnSegment(
                capsule.Center1, capsule.Center2, capsule.Center1) - normal * planeSeparation;
            return planeSeparation;
        }

        // Penetrating: the capsule's AXIS passes through the solid.
        //
        // This must be an exact segment-versus-convex test, not a guess from the
        // face distance. A capsule straddling a face has its nearest axis point
        // OUTSIDE the solid — so the face path measures a small positive gap and
        // reports "barely touching" while half the body is still buried, and
        // depenetration converges to a resting position inside the wall.
        //
        // The depth is the LEAST-violated plane's separation, which is the
        // smallest push that frees the capsule and therefore the correct
        // minimum-translation direction for a convex.
        if (planeSeparation < 0f &&
            SegmentIntersectsConvex(capsule.Center1, capsule.Center2, planes))
        {
            normal = planes[planeIndex].Normal;
            pointOnPiece = ClosestPointOnSegment(capsule.Center1, capsule.Center2, capsule.Center1)
                - normal * planeSeparation;
            return planeSeparation;
        }

        // The real answer: nearest approach between the capsule's axis and the
        // piece's surface, minus the radius. This is the rounded distance, and
        // near a corner its normal is the corner direction rather than a face
        // normal — which is the entire reason this path exists.
        float bestSquared = float.MaxValue;
        Vector3 bestOnFace = default;
        Vector3 bestOnAxis = default;

        for (int f = 0; f < faces.Length; f++)
        {
            ClosestBetweenSegmentAndPolygon(
                capsule.Center1, capsule.Center2, faces[f],
                out Vector3 onAxis, out Vector3 onFace);

            float squared = Vector3.DistanceSquared(onAxis, onFace);
            if (squared < bestSquared)
            {
                bestSquared = squared;
                bestOnFace = onFace;
                bestOnAxis = onAxis;
            }
        }

        if (bestSquared == float.MaxValue)
        {
            // No faces at all — a degenerate piece. Fall back to the plane
            // answer rather than claiming contact.
            normal = planeIndex >= 0 ? planes[planeIndex].Normal : Vector3.UnitY;
            return planeSeparation;
        }

        float axisDistance = MathF.Sqrt(bestSquared);
        pointOnPiece = bestOnFace;

        Vector3 away = bestOnAxis - bestOnFace;
        if (axisDistance > ParallelEpsilon)
        {
            normal = away / axisDistance;
        }
        else if (planeIndex >= 0)
        {
            // Axis exactly on the surface: no direction to derive, so take the
            // touched plane's.
            normal = planes[planeIndex].Normal;
        }

        // If the axis is INSIDE the solid the surface distance is negative, and
        // the sign cannot come from the face distance alone.
        if (ContainsPoint(bestOnAxis, planes, InsideEpsilon))
            return -(axisDistance + capsule.Radius);

        return axisDistance - capsule.Radius;
    }

    /// <summary>
    /// Conservative advancement: the fraction of <paramref name="translation"/>
    /// the capsule may travel before its surface is <paramref name="skinWidth"/>
    /// from the piece.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns 1 when unobstructed, and <b>0 with a valid plane when the capsule
    /// already overlaps</b> — never "no hit", because a character that spawned
    /// inside geometry must be pushed out rather than sweeping through the
    /// world.
    /// </para>
    /// <para>
    /// <b>On iteration exhaustion this reports a hit at the current fraction,
    /// never a miss.</b> Being blocked slightly early is survivable for a
    /// character; tunnelling through a wall is not.
    /// </para>
    /// </remarks>
    public static float Sweep(
        in CharacterCapsule capsule,
        Vector3 translation,
        ReadOnlySpan<Plane> planes,
        ReadOnlySpan<Polygon> faces,
        float skinWidth,
        int maxIterations,
        float tolerance,
        out Vector3 normal,
        out Vector3 pointOnPiece)
    {
        normal = Vector3.UnitY;
        pointOnPiece = capsule.Center1;

        float length = translation.Length();
        if (length <= ParallelEpsilon)
        {
            float d0 = Distance(in capsule, planes, faces, out normal, out pointOnPiece);
            return d0 <= skinWidth ? 0f : 1f;
        }

        Vector3 direction = translation / length;
        float travelled = 0f;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            CharacterCapsule probe = capsule.Translated(direction * travelled);
            float distance = Distance(in probe, planes, faces, out normal, out pointOnPiece);

            // How fast the gap closes along the travel direction — tested
            // BEFORE the contact check, and the order is load-bearing.
            //
            // A capsule resting against a surface is already at contact
            // distance, so a contact-first test reports "blocked at fraction
            // zero" for motion that is tangential to that surface or moving
            // away from it. That is what stops a step probe from ever rising:
            // the character is touching the riser, so its upward sweep returns
            // zero, the probe advances nothing, and a perfectly climbable step
            // becomes a wall. Only motion that closes the gap can be blocked by
            // this piece.
            float closing = -Vector3.Dot(direction, normal);
            if (closing <= ParallelEpsilon)
                return 1f;

            float clearance = distance - skinWidth;
            if (clearance <= tolerance)
                return travelled / length;

            travelled += clearance / closing;
            if (travelled >= length)
                return 1f;
        }

        // Out of iterations while still approaching: stop here rather than
        // letting the capsule through.
        return travelled / length;
    }

    /// <summary>Whether a segment intersects the convex solid bounded by the planes.</summary>
    /// <remarks>
    /// Parametric slab clipping: keep shrinking the interval the segment could
    /// be inside on, and it either survives or empties. Exact and branch-cheap,
    /// which matters because it runs for every candidate every query.
    /// </remarks>
    public static bool SegmentIntersectsConvex(Vector3 a, Vector3 b, ReadOnlySpan<Plane> planes)
    {
        Vector3 direction = b - a;
        float enter = 0f;
        float exit = 1f;

        for (int i = 0; i < planes.Length; i++)
        {
            float distance = Plane.DotCoordinate(planes[i], a);
            float rate = Vector3.Dot(planes[i].Normal, direction);

            if (MathF.Abs(rate) < ParallelEpsilon)
            {
                // Parallel to this plane: either wholly inside its half-space or
                // wholly outside, for the entire segment.
                if (distance > 0f)
                    return false;
                continue;
            }

            float t = -distance / rate;
            if (rate > 0f)
                exit = MathF.Min(exit, t);
            else
                enter = MathF.Max(enter, t);

            if (enter > exit)
                return false;
        }

        return true;
    }

    /// <summary>Closest point to <paramref name="point"/> on the segment.</summary>
    public static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        Vector3 ab = b - a;
        float lengthSquared = Vector3.Dot(ab, ab);
        if (lengthSquared <= ParallelEpsilon)
            return a;

        float t = Math.Clamp(Vector3.Dot(point - a, ab) / lengthSquared, 0f, 1f);
        return a + ab * t;
    }

    /// <summary>Nearest points between two segments.</summary>
    /// <remarks>
    /// The standard clamped-parameter solve, with the parallel case falling back
    /// to clamping one segment against the other — which is exactly the
    /// configuration a capsule makes with a wall edge it is sliding along, so it
    /// is not a rare branch.
    /// </remarks>
    public static void ClosestBetweenSegments(
        Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;

        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);

        float s, t;

        if (a <= ParallelEpsilon && e <= ParallelEpsilon)
        {
            c1 = p1;
            c2 = p2;
            return;
        }

        if (a <= ParallelEpsilon)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= ParallelEpsilon)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;

                s = denom > ParallelEpsilon ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;

                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Math.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }

        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    /// <summary>Nearest points between a segment and a convex polygon.</summary>
    /// <remarks>
    /// Two cases, both needed: the segment may pass over the polygon's
    /// <em>interior</em> — the common one, a character standing on a floor — or
    /// its nearest approach may be to an <em>edge</em>, which is the corner case
    /// the plane test gets wrong and this exists to get right.
    /// </remarks>
    public static void ClosestBetweenSegmentAndPolygon(
        Vector3 a, Vector3 b, in Polygon polygon, out Vector3 onSegment, out Vector3 onPolygon)
    {
        ReadOnlySpan<Vector3> verts = polygon.VertexSpan;
        onSegment = a;
        onPolygon = verts.Length > 0 ? verts[0] : a;

        if (verts.Length < 3)
        {
            if (verts.Length == 2)
                ClosestBetweenSegments(a, b, verts[0], verts[1], out onSegment, out onPolygon);
            else if (verts.Length == 1)
                onSegment = ClosestPointOnSegment(a, b, verts[0]);
            return;
        }

        float bestSquared = float.MaxValue;

        // Interior case: project each segment endpoint onto the polygon's plane
        // and accept it when the projection lands inside.
        Vector3 normal = polygon.Surface.Normal;
        for (int end = 0; end < 2; end++)
        {
            Vector3 point = end == 0 ? a : b;
            float signed = Plane.DotCoordinate(polygon.Surface, point);
            Vector3 projected = point - normal * signed;

            if (!PointInPolygon(projected, verts, normal))
                continue;

            float squared = Vector3.DistanceSquared(point, projected);
            if (squared < bestSquared)
            {
                bestSquared = squared;
                onSegment = point;
                onPolygon = projected;
            }
        }

        // Edge case, and the one that produces correct behaviour at corners.
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 e0 = verts[i];
            Vector3 e1 = verts[(i + 1) % verts.Length];
            ClosestBetweenSegments(a, b, e0, e1, out Vector3 c1, out Vector3 c2);

            float squared = Vector3.DistanceSquared(c1, c2);
            if (squared < bestSquared)
            {
                bestSquared = squared;
                onSegment = c1;
                onPolygon = c2;
            }
        }
    }

    /// <summary>Whether a coplanar point lies inside a convex polygon.</summary>
    public static bool PointInPolygon(Vector3 point, ReadOnlySpan<Vector3> verts, Vector3 normal)
    {
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 edge = verts[(i + 1) % verts.Length] - verts[i];
            Vector3 toPoint = point - verts[i];
            if (Vector3.Dot(Vector3.Cross(edge, toPoint), normal) < -InsideEpsilon)
                return false;
        }

        return true;
    }
}
