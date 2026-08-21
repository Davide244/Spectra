using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="Csg.Carve"/> output semantics: the union skin must be watertight,
/// coincident opposite-facing faces are interior interfaces (dropped from both
/// brushes), and coincident same-facing faces survive exactly once via brush
/// precedence (the rule documented in Csg.CarveFragment).
/// </summary>
public sealed class CsgCarveTests
{
    [Fact]
    public void Two_overlapping_boxes_carve_to_a_closed_two_manifold()
    {
        // Same Y/Z extents so the union is a plain 3x2x2 box — every edge of the
        // carved skin must be shared by exactly two polygons, with no doubled
        // coverage where the brushes overlap.
        Brush a = Brush.CreateBox(new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f));
        Brush b = Brush.CreateBox(new Vector3(1f, 0f, 0f), new Vector3(3f, 2f, 2f));

        Polygon[] surfaces = Csg.Carve([a, b]);

        GeometryTestHelpers.ShouldBeClosedTwoManifold(surfaces);
        GeometryTestHelpers.TotalArea(surfaces).ShouldBe(32f, 1e-2); // 2*(3*2 + 3*2 + 2*2)
    }

    [Fact]
    public void Coincident_same_facing_footprint_survives_exactly_once()
    {
        // Both boxes share the y=0 bottom plane; the overlap strip x in [1,2]
        // is covered by both bottom faces. Precedence must keep it exactly once:
        // total bottom area equals the union footprint (3x2), not 4+4.
        Brush a = Brush.CreateBox(new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f));
        Brush b = Brush.CreateBox(new Vector3(1f, 0f, 0f), new Vector3(3f, 2f, 2f));

        Polygon[] surfaces = Csg.Carve([a, b]);

        var bottom = surfaces
            .Where(p => p.Surface.Normal.Y < -0.999f && MathF.Abs(p.Surface.D) < 1e-3f)
            .ToArray();

        bottom.ShouldNotBeEmpty();
        GeometryTestHelpers.TotalArea(bottom).ShouldBe(6f, 1e-2);

        // The centre of the shared footprint lies in exactly one surviving polygon.
        var overlapCentre = new Vector3(1.5f, 0f, 1f);
        bottom.Count(p => p.Bounds.Contains(overlapCentre)).ShouldBe(1);
    }

    [Fact]
    public void Fully_coincident_boxes_leave_a_single_box_skin()
    {
        // Two identical brushes: every face is a same-facing duplicate. The
        // documented precedence rule (lower index wins) must keep exactly one
        // copy of each of the six faces.
        Brush a = Brush.CreateBox(new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f));
        Brush b = Brush.CreateBox(new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f));

        Polygon[] surfaces = Csg.Carve([a, b]);

        surfaces.Length.ShouldBe(6);
        GeometryTestHelpers.TotalArea(surfaces).ShouldBe(24f, 1e-2);
        GeometryTestHelpers.ShouldBeClosedTwoManifold(surfaces);
    }

    [Fact]
    public void Stacked_boxes_drop_the_interior_interface_from_both_brushes()
    {
        // Opposite-facing coincidence at y=2: an interior interface. Neither
        // brush may contribute a polygon on that plane, and the remaining skin
        // must still close into a single 2x4x2 tower.
        Brush lower = Brush.CreateBox(new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f));
        Brush upper = Brush.CreateBox(new Vector3(0f, 2f, 0f), new Vector3(2f, 4f, 2f));

        Polygon[] surfaces = Csg.Carve([lower, upper]);

        surfaces.Length.ShouldBe(10); // each box keeps 5 of its 6 faces
        surfaces.ShouldNotContain(p =>
            MathF.Abs(p.Surface.Normal.Y) > 0.999f && MathF.Abs(MathF.Abs(p.Surface.D) - 2f) < 1e-3f);
        GeometryTestHelpers.ShouldBeClosedTwoManifold(surfaces);
    }
}
