using SpectraEngine.Core.Scene;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Picks the rotate gizmo's rings from a viewport ray, by screen-space
/// proximity to the ring the user can see.
/// </summary>
/// <remarks>
/// <b>Rings are line-shaped handles, so proximity is the whole test</b> — there
/// is no surface to be inside of. Each candidate reports how many pixels the
/// cursor missed the drawn circle by (see
/// <see cref="GizmoHitTesting.RingPixelDistance"/>), anything beyond the
/// tolerance is discarded, and the smallest miss wins.
/// <para>
/// <b>The tie-break is depth, and it matters exactly where the rings cross.</b>
/// Seen face-on the three axis rings and the larger view-aligned ring are
/// disjoint on screen, so proximity alone decides. Seen edge-on an axis ring
/// projects to a line through the pivot that does cross the view ring, and there
/// both are genuinely under the cursor; the nearer one is the one drawn in front,
/// which is the one the user believes they are pointing at.
/// </para>
/// <para>
/// <b>A ring the drag could not follow is not a candidate at all.</b> Each ring
/// must first survive the same ray-onto-its-plane projection
/// <see cref="RotateGizmo"/> measures the sweep with, so what highlights is
/// always what a press will grab. This is what keeps a nearly edge-on ring —
/// whose silhouette is a line straight through the pivot, and which therefore
/// wins the pixel-distance test everywhere it crosses another ring — from
/// stealing the pick from the face-on ring the user is aiming at, and from
/// promising a manipulation the tool would then refuse.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only. Pure function, no allocation — the ring
/// walk is a fixed loop over <see cref="GizmoHitTesting.RingSegments"/> chords.
/// </para>
/// </remarks>
public static class RotateGizmoHitTester
{
    /// <summary>
    /// How far from the pivot, as a fraction of the ring's radius, a grab point
    /// must land in the ring's own plane for the sweep to have a direction to
    /// start from. Lives here rather than in <see cref="RotateGizmo"/> because
    /// picking has to apply the identical bar: a ring the tool would refuse to
    /// sweep must not be offered as a target.
    /// </summary>
    public const float MinimumGrabRadiusFactor = 0.05f;

    /// <summary>
    /// Picks the ring under <paramref name="ray"/>, or
    /// <see cref="GizmoPick.Miss"/> when the ray comes near none of them.
    /// </summary>
    /// <param name="geometry">This frame's gizmo geometry — the same one that was drawn.</param>
    /// <param name="ray">The viewport picking ray, from <see cref="Camera.ScreenPointToRay"/>.</param>
    /// <param name="tolerancePixels">Screen-space slack around a ring.</param>
    public static GizmoPick Pick(in GizmoGeometry geometry, in Ray3 ray, float tolerancePixels)
    {
        if (geometry.IsBehindCamera)
            return GizmoPick.Miss;

        float tolerance = MathF.Max(tolerancePixels, 0f);
        GizmoPick best = GizmoPick.Miss;

        // The three axis rings, then the view-aligned one. Order only decides a
        // dead-exact tie in both pixel distance and depth, which means the two
        // rings are coincident on screen anyway.
        for (GizmoHandle handle = GizmoHandle.AxisX; handle <= GizmoHandle.AxisZ; handle++)
            best = Consider(in geometry, in ray, handle, tolerance, best);

        return Consider(in geometry, in ray, GizmoHandle.Screen, tolerance, best);
    }

    private static GizmoPick Consider(
        in GizmoGeometry geometry, in Ray3 ray, GizmoHandle handle, float tolerance, GizmoPick best)
    {
        if (!geometry.TryGetRing(handle, out Vector3 axis, out float radius))
            return best;

        // Pick only what can actually be dragged. A rotation is measured in the
        // ring's own plane, and RotateGizmo projects the cursor ray onto that
        // plane through this very function — so a ring the ray cannot meet the
        // plane of (edge-on, or with the eye inside it, which is exactly where
        // its silhouette collapses to a line through the pivot) must not be
        // picked either. Otherwise the ring highlights, ClassifyPress promises
        // Manipulate, and the press is then refused and falls through to
        // click-select or, over empty space, to a marquee that replaces the
        // selection. The translate tool never had this mismatch because its
        // plane quads pick through the same projection they drag through; this
        // is the rotate tool holding to the same rule.
        if (!GizmoMath.TryRayPlane(in ray, geometry.Pivot, axis, out float planeDistance))
            return best;

        // The second half of the same agreement: a grab point that projects
        // essentially onto the pivot has no spoke to measure a sweep from, and
        // the tool refuses it. A nearly-edge-on ring is exactly where that
        // happens for a cursor near the centre — its silhouette passes within a
        // few pixels of the pivot — so the pick has to apply the same bar.
        float spoke = (ray.PointAt(planeDistance) - geometry.Pivot).Length();
        if (spoke < radius * MinimumGrabRadiusFactor)
            return best;

        float pixels = GizmoHitTesting.RingPixelDistance(
            in geometry, in ray, geometry.Pivot, axis, radius, out float distance);

        if (pixels > tolerance)
            return best;

        if (pixels < best.PixelDistance ||
            (pixels == best.PixelDistance && distance < best.RayDistance))
        {
            return new GizmoPick(handle, distance, pixels, ray.PointAt(distance));
        }

        return best;
    }
}
