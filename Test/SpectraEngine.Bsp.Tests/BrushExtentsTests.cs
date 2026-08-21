using System;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="Brush.WithScaledExtents"/>: the resize primitive the editor's
/// scale gizmo is built on, and the reason a brush node's transform never needs
/// to carry a scale.
/// </summary>
/// <remarks>
/// The method is the exact image of the solid under a diagonal map, derived from
/// the half-space form rather than special-cased for boxes, so the tests go past
/// the axis-aligned case to a wedge — where "just multiply the half-extents"
/// would give the wrong planes and only the half-space transform is right.
/// </remarks>
public sealed class BrushExtentsTests
{
    private const float Tolerance = 1e-4f;

    [Fact]
    public void Scaling_a_box_multiplies_its_half_extents()
    {
        Brush box = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));

        Brush resized = box.WithScaledExtents(new Vector3(2f, 3f, 0.5f));

        resized.LocalBounds.Min.ShouldBeCloseTo(new Vector3(-2f, -3f, -0.5f), Tolerance);
        resized.LocalBounds.Max.ShouldBeCloseTo(new Vector3(2f, 3f, 0.5f), Tolerance);
    }

    [Fact]
    public void Scaling_leaves_the_source_brush_untouched()
    {
        // Brushes are immutable after construction; the whole compile pipeline
        // (and its carve cache, keyed on reference identity) depends on it.
        Brush box = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));

        Brush resized = box.WithScaledExtents(new Vector3(4f));

        resized.ShouldNotBeSameAs(box);
        box.LocalBounds.Max.ShouldBeCloseTo(Vector3.One, Tolerance);
        resized.LocalBounds.Max.ShouldBeCloseTo(new Vector3(4f), Tolerance);
    }

    [Fact]
    public void Scaling_by_one_returns_the_same_instance()
    {
        // A drag that has not left its starting size must not invalidate the
        // cached carve — and must not pay for a rebuild either.
        Brush box = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));

        box.WithScaledExtents(Vector3.One).ShouldBeSameAs(box);
    }

    [Fact]
    public void Scaled_planes_stay_unit_length()
    {
        // The CSG epsilon scheme assumes it everywhere; a non-normalized plane
        // silently changes the meaning of every distance tolerance downstream,
        // which is precisely the failure that makes node scale unusable here.
        Brush box = Brush.CreateBox(new Vector3(-1f), new Vector3(2f, 5f, 0.25f));

        Brush resized = box.WithScaledExtents(new Vector3(0.3f, 7f, 2.5f));

        foreach (Plane plane in resized.LocalPlanes)
            plane.Normal.Length().ShouldBe(1f, Tolerance);
    }

    [Fact]
    public void A_non_axis_aligned_face_is_mapped_by_the_half_space_transform()
    {
        // A wedge: the unit cube's planes plus a diagonal cut through the
        // (+x, +y) corner. Under a 2x stretch along x, naively scaling the
        // plane offset would leave the cut in the wrong place; the correct
        // answer re-points the NORMAL as well.
        var diagonal = new Vector3(1f, 1f, 0f) / MathF.Sqrt(2f);
        Plane[] planes =
        [
            new(new Vector3(-1f, 0f, 0f), -1f),
            new(new Vector3(0f, -1f, 0f), -1f),
            new(new Vector3(0f, 0f, 1f), -1f),
            new(new Vector3(0f, 0f, -1f), -1f),
            new(diagonal, -1f), // x + y <= sqrt(2)
        ];
        var wedge = new Brush(planes);

        Brush resized = wedge.WithScaledExtents(new Vector3(2f, 1f, 1f));

        // The cut plane maps to x/2 + y <= sqrt(2), i.e. normal ∝ (0.5, 1, 0).
        Vector3 expected = Vector3.Normalize(new Vector3(0.5f, 1f, 0f));
        Plane cut = FindPlaneLike(resized, expected);
        cut.Normal.ShouldBeCloseTo(expected, Tolerance);

        // And the offset moves with it, which is the half of the mapping a
        // normal-only fix would miss: the image of a point ON the original cut
        // must land exactly ON the new one. (The plane's nearest point to the
        // origin is n·−D, which is on it by construction.)
        var onOriginalCut = new Vector3(diagonal.X, diagonal.Y, 0f);
        var image = new Vector3(onOriginalCut.X * 2f, onOriginalCut.Y, 0f);
        SignedDistance(cut, image).ShouldBe(0f, Tolerance);

        // …while the image of a point strictly inside stays strictly inside.
        SignedDistance(cut, Vector3.Zero).ShouldBeLessThan(-Tolerance);
    }

    [Fact]
    public void Scaling_a_rotated_plane_set_keeps_the_solid_convex_and_closed()
    {
        // Every face must survive the round trip: a brush whose planes stopped
        // enclosing a volume is rejected at construction, so simply getting an
        // instance back proves the mapped set is still a closed solid.
        var octahedron = new Plane[8];
        int i = 0;
        foreach (float sx in new[] { 1f, -1f })
        {
            foreach (float sy in new[] { 1f, -1f })
            {
                foreach (float sz in new[] { 1f, -1f })
                    octahedron[i++] = new Plane(Vector3.Normalize(new Vector3(sx, sy, sz)), -1f);
            }
        }

        Brush resized = new Brush(octahedron).WithScaledExtents(new Vector3(3f, 1f, 0.5f));

        resized.LocalPlanes.Count.ShouldBe(8);
        resized.LocalFaces.Count.ShouldBe(8);
        // The extreme point along each axis scales with that axis.
        resized.LocalBounds.Max.ShouldBeCloseTo(new Vector3(3f, 1f, 0.5f) * MathF.Sqrt(3f), 1e-3f);
    }

    [Fact]
    public void Face_payloads_ride_along_so_a_resize_does_not_restyle_the_brush()
    {
        MaterialRef material = MaterialRegistry.Intern("bricks");
        Brush box = Brush.CreateBox(new Vector3(-1f), new Vector3(1f), material);

        Brush resized = box.WithScaledExtents(new Vector3(2f, 1f, 1f));

        resized.FaceSurfaces.Count.ShouldBe(box.FaceSurfaces.Count);
        for (int i = 0; i < resized.FaceSurfaces.Count; i++)
            resized.FaceSurfaces[i].ShouldBe(box.FaceSurfaces[i]);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void An_unusable_factor_is_rejected(float bad)
    {
        Brush box = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));

        // Zero collapses the solid to a plane and a negative factor mirrors it,
        // inverting every outward normal into an inward one — both would corrupt
        // the BSP's solid marking rather than merely look wrong.
        Should.Throw<ArgumentOutOfRangeException>(() => box.WithScaledExtents(new Vector3(bad, 1f, 1f)));
        Should.Throw<ArgumentOutOfRangeException>(() => box.WithScaledExtents(new Vector3(1f, bad, 1f)));
        Should.Throw<ArgumentOutOfRangeException>(() => box.WithScaledExtents(new Vector3(1f, 1f, bad)));
    }

    [Fact]
    public void Successive_resizes_compose_like_the_factors_do()
    {
        Brush box = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));

        Brush twice = box.WithScaledExtents(new Vector3(2f, 1f, 1f)).WithScaledExtents(new Vector3(1.5f, 4f, 1f));
        Brush once = box.WithScaledExtents(new Vector3(3f, 4f, 1f));

        twice.LocalBounds.Max.ShouldBeCloseTo(once.LocalBounds.Max, Tolerance);
        twice.LocalBounds.Min.ShouldBeCloseTo(once.LocalBounds.Min, Tolerance);
    }

    // --- Helpers -------------------------------------------------------------

    private static Plane FindPlaneLike(Brush brush, Vector3 normal)
    {
        foreach (Plane plane in brush.LocalPlanes)
        {
            if (Vector3.Dot(plane.Normal, normal) > 0.999f)
                return plane;
        }

        throw new InvalidOperationException($"No plane facing {normal} in the resized brush.");
    }

    private static float SignedDistance(Plane plane, Vector3 point) =>
        Vector3.Dot(plane.Normal, point) + plane.D;
}

/// <summary>Component-wise vector closeness, so a failure names the axis that drifted.</summary>
internal static class BrushExtentsAssertions
{
    public static void ShouldBeCloseTo(this Vector3 actual, Vector3 expected, float tolerance)
    {
        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
    }
}
