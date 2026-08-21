using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// End-to-end queries against a compiled <see cref="CsgWorld"/> (carve → snap →
/// weld → BSP) for a floor-plus-pillar world: point containment, downward rays,
/// rays starting inside solid, and rays that miss. Pure CPU — no GPU involved.
/// </summary>
public sealed class CsgWorldBspTests
{
    // Floor slab y in [-1, 0], 16x16; pillar 2x2 standing on it, y in [0, 4].
    // The shared y=0 interface under the pillar footprint is interior and must
    // be removed by the carve.
    private static CsgWorld BuildFloorAndPillar()
    {
        Brush floor = Brush.CreateBox(new Vector3(-8f, -1f, -8f), new Vector3(8f, 0f, 8f));
        Brush pillar = Brush.CreateBox(new Vector3(-1f, 0f, -1f), new Vector3(1f, 4f, 1f));
        return CsgWorld.Build([floor, pillar]);
    }

    [Fact]
    public void World_surfaces_form_a_closed_two_manifold_after_welding()
    {
        // The pillar footprint punches a hole in the floor's top face, creating
        // T-junctions along the hole rim; after snap+weld the skin must still
        // be watertight.
        CsgWorld world = BuildFloorAndPillar();
        world.Surfaces.ShouldNotBeEmpty();
        GeometryTestHelpers.ShouldBeClosedTwoManifold(world.Surfaces);
    }

    [Fact]
    public void ContainsPoint_is_true_inside_floor_and_pillar()
    {
        CsgWorld world = BuildFloorAndPillar();
        world.ContainsPoint(new Vector3(4f, -0.5f, 4f)).ShouldBeTrue();  // inside the floor slab
        world.ContainsPoint(new Vector3(0f, 2f, 0f)).ShouldBeTrue();     // inside the pillar
        world.ContainsPoint(new Vector3(0f, -0.5f, 0f)).ShouldBeTrue();  // in the floor, under the pillar
    }

    [Fact]
    public void ContainsPoint_is_false_in_open_air()
    {
        CsgWorld world = BuildFloorAndPillar();
        world.ContainsPoint(new Vector3(4f, 2f, 4f)).ShouldBeFalse();    // beside the pillar, above the floor
        world.ContainsPoint(new Vector3(0f, 5f, 0f)).ShouldBeFalse();    // above the pillar
        world.ContainsPoint(new Vector3(0f, -5f, 0f)).ShouldBeFalse();   // below the floor
        world.ContainsPoint(new Vector3(50f, 50f, 50f)).ShouldBeFalse(); // far outside
    }

    [Fact]
    public void Raycast_straight_down_hits_the_floor_with_upward_normal()
    {
        CsgWorld world = BuildFloorAndPillar();

        world.Raycast(new Vector3(4f, 8f, 4f), new Vector3(0f, -1f, 0f), 100f, out BspRaycastHit hit)
            .ShouldBeTrue();

        hit.Distance.ShouldBe(8f, 1e-3);
        hit.Point.Y.ShouldBe(0f, 1e-3);
        hit.Normal.X.ShouldBe(0f, 1e-3);
        hit.Normal.Y.ShouldBe(1f, 1e-3);
        hit.Normal.Z.ShouldBe(0f, 1e-3);
    }

    [Fact]
    public void Raycast_straight_down_hits_the_pillar_top()
    {
        CsgWorld world = BuildFloorAndPillar();

        world.Raycast(new Vector3(0f, 8f, 0f), new Vector3(0f, -1f, 0f), 100f, out BspRaycastHit hit)
            .ShouldBeTrue();

        hit.Distance.ShouldBe(4f, 1e-3);
        hit.Point.Y.ShouldBe(4f, 1e-3);
        hit.Normal.Y.ShouldBe(1f, 1e-3);
    }

    [Fact]
    public void Raycast_starting_inside_solid_reports_immediate_hit_facing_back()
    {
        CsgWorld world = BuildFloorAndPillar();

        var origin = new Vector3(0f, 2f, 0f); // inside the pillar
        world.Raycast(origin, Vector3.UnitX, 10f, out BspRaycastHit hit).ShouldBeTrue();

        hit.Distance.ShouldBe(0f);
        hit.Point.ShouldBe(origin);
        hit.Normal.ShouldBe(-Vector3.UnitX);
    }

    [Fact]
    public void Raycast_pointing_away_from_all_solids_misses()
    {
        CsgWorld world = BuildFloorAndPillar();
        world.Raycast(new Vector3(4f, 8f, 4f), new Vector3(0f, 1f, 0f), 100f, out _).ShouldBeFalse();
    }

    [Fact]
    public void Raycast_stops_at_max_distance()
    {
        // The floor is 8 units below; a 5-unit segment must not reach it.
        CsgWorld world = BuildFloorAndPillar();
        world.Raycast(new Vector3(4f, 8f, 4f), new Vector3(0f, -1f, 0f), 5f, out _).ShouldBeFalse();
    }

    [Fact]
    public void Raycast_with_unbounded_distance_terminates_and_answers_correctly()
    {
        // Regression: the cell walk had no termination bound beyond
        // maxDistance itself, so a miss ray with float.MaxValue (or
        // PositiveInfinity — Scene.Raycast's own "unbounded" sentinel) walked
        // cells forever once the DDA's tMax accumulation saturated in float.
        // The walk must clip to the occupied-cell region: misses return
        // promptly (this test would time out otherwise) and hits are
        // unaffected by the unbounded budget.
        CsgWorld world = BuildFloorAndPillar();

        world.Raycast(new Vector3(4f, 8f, 4f), new Vector3(0f, 1f, 0f), float.MaxValue, out _).ShouldBeFalse();
        world.Raycast(new Vector3(4f, 8f, 4f), new Vector3(0f, 1f, 0f), float.PositiveInfinity, out _).ShouldBeFalse();
        world.Raycast(new Vector3(4f, 8f, 4f), new Vector3(1f, 0.25f, 0.5f), float.PositiveInfinity, out _).ShouldBeFalse();

        // A ray that starts far outside the occupied region and points at it
        // still hits, and one that points away misses without walking.
        world.Raycast(new Vector3(4f, 1e6f, 4f), new Vector3(0f, -1f, 0f), float.PositiveInfinity, out BspRaycastHit hit)
            .ShouldBeTrue();
        hit.Point.Y.ShouldBe(0f, 1e-2); // the floor top, beside the pillar
        world.Raycast(new Vector3(4f, 1e6f, 4f), new Vector3(0f, 1f, 0f), float.MaxValue, out _).ShouldBeFalse();

        world.Raycast(new Vector3(4f, 8f, 4f), new Vector3(0f, -1f, 0f), float.PositiveInfinity, out BspRaycastHit down)
            .ShouldBeTrue();
        down.Distance.ShouldBe(8f, 1e-3);
    }

    [Fact]
    public void Routed_raycast_matches_monolithic_across_a_sub_probe_gap_at_a_cell_border()
    {
        // Regression: two facing surfaces separated by 5.5e-4 — wider than
        // the weld band (2e-4) but narrower than the old raycast probe
        // (1e-3) — straddling the x=32 cell boundary. The near box E ends at
        // 31.99995 (resident in both cells); the far box D starts at 32.0005
        // (NOT resident in cell 0). The old fixed-epsilon probe made the
        // monolithic tree fabricate a hit at E's face PLANE for rays passing
        // above E into D (it sampled straight through the air gap), while the
        // routed walk — whose cell-0 tree cannot see D — reported the true
        // entry at D's face: 684 divergences of 5.5e-4 over a small sweep.
        // With entry detection by leaf containment, both must agree — on the
        // TRUE surface — for every ray.
        Brush e = Brush.CreateBox(new Vector3(28f, 0f, 0f), new Vector3(31.99995f, 2f, 4f));
        Brush d = Brush.CreateBox(new Vector3(32.0005f, 0f, 0f), new Vector3(36f, 4f, 4f));
        CsgWorld world = CsgWorld.Build([e, d]);
        BspTree mono = BspTree.BuildFromSurfaces(world.Surfaces);

        // Rays passing over E (whose profile stops at y=2) straight into D's
        // taller face: they cross E's max-x face plane in open air inside
        // cell 0, exactly where the old probe overshot into D.
        for (int i = 0; i <= 16; i++)
        {
            float y = 2.1f + i * 0.1f; // 2.1 .. 3.7, above E, within D
            var origin = new Vector3(30f, y, 2f);
            Vector3 direction = Vector3.UnitX;

            bool monoHit = mono.Raycast(origin, direction, 40f, out BspRaycastHit expected);
            bool routedHit = world.Raycast(origin, direction, 40f, out BspRaycastHit actual);

            routedHit.ShouldBe(monoHit, $"hit flag diverged at y={y}");
            monoHit.ShouldBeTrue($"ray at y={y} must hit D");
            expected.Point.X.ShouldBe(32.0005f, 1e-4, $"monolithic reported a phantom pre-gap hit at y={y}");
            actual.Distance.ShouldBe(expected.Distance, 1e-4f, $"distance diverged at y={y}");
            actual.Normal.X.ShouldBe(expected.Normal.X, 1e-4f, $"normal diverged at y={y}");
        }

        // And rays into E itself (below y=2) still agree on E's near face.
        for (int i = 0; i <= 8; i++)
        {
            float y = 0.2f + i * 0.2f;
            var origin = new Vector3(26f, y, 2f);
            bool monoHit = mono.Raycast(origin, Vector3.UnitX, 40f, out BspRaycastHit expected);
            bool routedHit = world.Raycast(origin, Vector3.UnitX, 40f, out BspRaycastHit actual);
            routedHit.ShouldBe(monoHit, $"hit flag diverged at y={y}");
            monoHit.ShouldBeTrue($"ray at y={y} must hit E");
            actual.Distance.ShouldBe(expected.Distance, 1e-4f, $"distance diverged at y={y}");
        }
    }
}
