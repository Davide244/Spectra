using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// <see cref="BrushBroadphase.FindOverlaps"/> pairing semantics, including the
/// boundary case the carve relies on: boxes touching with zero gap count as
/// overlapping (so coincident-face interfaces reach the CSG), while any
/// strictly positive gap — however small — does not.
/// </summary>
public sealed class BrushBroadphaseTests
{
    private static Aabb Box(float minX, float maxX) =>
        new(new Vector3(minX, 0f, 0f), new Vector3(maxX, 1f, 1f));

    [Fact]
    public void Overlapping_pair_is_reported_symmetrically()
    {
        int[][] neighbors = BrushBroadphase.FindOverlaps([Box(0f, 2f), Box(1f, 3f)]);

        neighbors[0].ShouldBe(new[] { 1 });
        neighbors[1].ShouldBe(new[] { 0 });
    }

    [Fact]
    public void Separated_pair_is_not_reported()
    {
        int[][] neighbors = BrushBroadphase.FindOverlaps([Box(0f, 1f), Box(2f, 3f)]);

        neighbors[0].ShouldBeEmpty();
        neighbors[1].ShouldBeEmpty();
    }

    [Fact]
    public void Touching_boxes_with_zero_gap_count_as_overlapping()
    {
        // Coincident-face brushes (a wall meeting a floor) must be fed to the
        // carve so the shared interface is resolved there; the broadphase
        // therefore treats exact touching as overlap.
        int[][] neighbors = BrushBroadphase.FindOverlaps([Box(0f, 1f), Box(1f, 2f)]);

        neighbors[0].ShouldBe(new[] { 1 });
        neighbors[1].ShouldBe(new[] { 0 });
    }

    [Fact]
    public void Boxes_separated_by_a_hair_are_not_reported()
    {
        // Documents the epsilon behaviour: the broadphase does no inflation, so
        // any strictly positive gap — even 1e-3 — is a miss.
        int[][] neighbors = BrushBroadphase.FindOverlaps([Box(0f, 1f), Box(1.001f, 2f)]);

        neighbors[0].ShouldBeEmpty();
        neighbors[1].ShouldBeEmpty();
    }

    [Fact]
    public void Chain_pairs_only_actual_overlaps_regardless_of_input_order()
    {
        // C, A, B given out of X order: A-B and B-C overlap, A-C does not.
        Aabb c = Box(4f, 6f);
        Aabb a = Box(0f, 2f);
        Aabb b = Box(1.5f, 4.5f);

        int[][] neighbors = BrushBroadphase.FindOverlaps([c, a, b]);

        neighbors[0].ShouldBe(new[] { 2 });                       // C ↔ B only
        neighbors[1].ShouldBe(new[] { 2 });                       // A ↔ B only
        neighbors[2].OrderBy(i => i).ShouldBe(new[] { 0, 1 });    // B ↔ both
    }
}
