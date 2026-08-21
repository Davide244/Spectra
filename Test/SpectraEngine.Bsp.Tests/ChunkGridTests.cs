using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The chunk-grid substrate: cell math (floor semantics, negative and
/// boundary-exact coordinates, weld-band inflation), owner-vs-resident
/// assignment through <see cref="CsgWorld"/> builds, and determinism of the
/// whole partitioning for equal inputs.
/// </summary>
public sealed class ChunkGridTests
{
    // --- Cell math ----------------------------------------------------------

    [Fact]
    public void FromPosition_floors_per_axis_including_negative_coordinates()
    {
        ChunkCoord.FromPosition(new Vector3(0.5f, 5f, 31.9f)).ShouldBe(new ChunkCoord(0, 0, 0));
        ChunkCoord.FromPosition(new Vector3(-0.5f, -32.5f, -63.9f)).ShouldBe(new ChunkCoord(-1, -2, -2));
        ChunkCoord.FromPosition(new Vector3(100f, -100f, 0f)).ShouldBe(new ChunkCoord(3, -4, 0));
    }

    [Fact]
    public void FromPosition_boundary_exact_positions_belong_to_the_positive_side_cell()
    {
        // CellSize is a power of two, so these boundaries are exactly
        // representable and the classification must not wobble.
        ChunkCoord.FromPosition(new Vector3(32f, 0f, 0f)).ShouldBe(new ChunkCoord(1, 0, 0));
        ChunkCoord.FromPosition(new Vector3(0f, 64f, 0f)).ShouldBe(new ChunkCoord(0, 2, 0));
        ChunkCoord.FromPosition(new Vector3(0f, 0f, -32f)).ShouldBe(new ChunkCoord(0, 0, -1));
        ChunkCoord.FromPosition(new Vector3(-64f, 0f, 0f)).ShouldBe(new ChunkCoord(-2, 0, 0));
    }

    [Fact]
    public void Cell_bounds_are_exact_and_shared_bit_for_bit_between_neighbours()
    {
        var cell = new ChunkCoord(1, -1, 0);
        cell.MinCorner.ShouldBe(new Vector3(32f, -32f, 0f));
        cell.MaxCorner.ShouldBe(new Vector3(64f, 0f, 32f));
        cell.Bounds.Min.ShouldBe(cell.MinCorner);
        cell.Bounds.Max.ShouldBe(cell.MaxCorner);

        // A cell's max corner IS its positive neighbour's min corner — the
        // exactness that keeps per-axis classification crack-free.
        cell.MaxCorner.X.ShouldBe(new ChunkCoord(2, -1, 0).MinCorner.X);
        cell.MaxCorner.Y.ShouldBe(new ChunkCoord(1, 0, 0).MinCorner.Y);
        cell.MaxCorner.Z.ShouldBe(new ChunkCoord(1, -1, 1).MinCorner.Z);
    }

    [Fact]
    public void Compare_orders_lexicographically_x_then_y_then_z()
    {
        new ChunkCoord(0, 0, 0).CompareTo(new ChunkCoord(0, 0, 1)).ShouldBeLessThan(0);
        new ChunkCoord(0, 0, 9).CompareTo(new ChunkCoord(0, 1, 0)).ShouldBeLessThan(0);
        new ChunkCoord(0, 9, 9).CompareTo(new ChunkCoord(1, 0, 0)).ShouldBeLessThan(0);
        new ChunkCoord(-1, 0, 0).CompareTo(new ChunkCoord(0, 0, 0)).ShouldBeLessThan(0);
        new ChunkCoord(2, 3, 4).CompareTo(new ChunkCoord(2, 3, 4)).ShouldBe(0);
    }

    [Fact]
    public void Weld_band_is_twice_the_larger_geometric_tolerance()
    {
        ChunkGrid.WeldBand.ShouldBe(2f * MathF.Max(Polygon.Epsilon, VertexSnapper.GridSize));
    }

    // --- Footprints ---------------------------------------------------------

    [Fact]
    public void Footprint_of_a_cell_interior_brush_is_its_single_cell()
    {
        BrushPlacement placement = PlaceUnitBoxAt(new Vector3(16f, 16f, 16f));

        ChunkGrid.ComputeFootprint(in placement).ShouldBe(new[] { new ChunkCoord(0, 0, 0) });
        ChunkGrid.OwnerCell(in placement).ShouldBe(new ChunkCoord(0, 0, 0));
    }

    [Fact]
    public void Weld_band_inflation_pushes_a_boundary_touching_brush_into_both_cells()
    {
        // World AABB x: 30..32 — touches the x=32 border exactly, so the
        // inflated box reaches into cell 1 even though the brush itself
        // doesn't cross it: geometry within the weld band of the border can
        // weld against the far side and must be resident there.
        BrushPlacement touching = PlaceUnitBoxAt(new Vector3(31f, 16f, 16f));
        ChunkGrid.ComputeFootprint(in touching).ShouldBe(
            new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(1, 0, 0) });

        // One weld band clear of the border: a single cell again.
        BrushPlacement clear = PlaceUnitBoxAt(new Vector3(30.9f, 16f, 16f));
        ChunkGrid.ComputeFootprint(in clear).ShouldBe(new[] { new ChunkCoord(0, 0, 0) });
    }

    [Fact]
    public void Footprint_enumeration_is_sorted_ascending()
    {
        // 24..40 on every axis: 2x2x2 = 8 cells spanning the origin corner.
        var placement = new BrushPlacement(
            Brush.CreateBox(new Vector3(-8f), new Vector3(8f)),
            Matrix4x4.CreateTranslation(32f, 32f, 32f));

        ChunkCoord[] footprint = ChunkGrid.ComputeFootprint(in placement);
        footprint.Length.ShouldBe(8);
        for (int i = 1; i < footprint.Length; i++)
            footprint[i - 1].CompareTo(footprint[i]).ShouldBeLessThan(0);
    }

    // --- Owner vs resident assignment through CsgWorld ----------------------

    [Fact]
    public void Brush_spanning_two_cells_is_resident_in_both_but_owned_by_one()
    {
        // World AABB x: 28..36 — cells 0 and 1; center x = 32 → owner (1,0,0).
        var placement = new BrushPlacement(
            Brush.CreateBox(new Vector3(-4f), new Vector3(4f)),
            Matrix4x4.CreateTranslation(32f, 16f, 16f));

        CsgWorld world = CsgWorld.Build(new[] { placement });

        world.Chunks.Count.ShouldBe(2);
        WorldChunk left = ChunkAt(world, new ChunkCoord(0, 0, 0));
        WorldChunk right = ChunkAt(world, new ChunkCoord(1, 0, 0));

        left.ResidentBrushIndices.ShouldBe(new[] { 0 });
        right.ResidentBrushIndices.ShouldBe(new[] { 0 });

        // The render surfaces live in exactly one cell — nothing draws twice.
        left.OwnedBrushIndices.ShouldBeEmpty();
        left.Surfaces.ShouldBeEmpty();
        right.OwnedBrushIndices.ShouldBe(new[] { 0 });
        right.Surfaces.Count.ShouldBe(world.Surfaces.Count);
    }

    [Fact]
    public void Brush_spanning_eight_cells_is_resident_in_all_but_owned_by_the_center_cell()
    {
        // 24..40 per axis → all eight cells around the (32,32,32) corner;
        // inflated-AABB center (32,32,32) → owner (1,1,1) by floor semantics.
        var placement = new BrushPlacement(
            Brush.CreateBox(new Vector3(-8f), new Vector3(8f)),
            Matrix4x4.CreateTranslation(32f, 32f, 32f));

        CsgWorld world = CsgWorld.Build(new[] { placement });

        world.Chunks.Count.ShouldBe(8);
        int ownedCells = 0;
        foreach (WorldChunk chunk in world.Chunks.OrderedChunks)
        {
            chunk.ResidentBrushIndices.ShouldBe(new[] { 0 });
            if (chunk.OwnedBrushIndices.Count > 0)
            {
                ownedCells++;
                chunk.Coord.ShouldBe(new ChunkCoord(1, 1, 1));
                chunk.Surfaces.Count.ShouldBe(world.Surfaces.Count);
            }
            else
            {
                chunk.Surfaces.ShouldBeEmpty();
            }
        }
        ownedCells.ShouldBe(1);
    }

    [Fact]
    public void Chunk_surface_partition_covers_the_carve_exactly_once()
    {
        // Overlapping pair straddling a cell border, so the carve does real
        // splitting and the brushes own different cells. Snap and weld never
        // change the polygon COUNT, so the flat (post-weld) surface list and
        // the chunk-bucketed (pre-snap) surfaces must agree on totals.
        CsgWorld world = CsgWorld.Build(TwoOverlappingBrushesAcrossABorder());

        int bucketed = 0;
        var ownedIndices = new List<int>();
        foreach (WorldChunk chunk in world.Chunks.OrderedChunks)
        {
            bucketed += chunk.Surfaces.Count;
            ownedIndices.AddRange(chunk.OwnedBrushIndices);
        }

        bucketed.ShouldBe(world.Surfaces.Count);
        // Every placement owned exactly once, whatever cell it landed in.
        ownedIndices.Sort();
        ownedIndices.ShouldBe(new[] { 0, 1 });
    }

    [Fact]
    public void Chunk_assignment_is_deterministic_for_identical_inputs()
    {
        CsgWorld first = CsgWorld.Build(TwoOverlappingBrushesAcrossABorder());
        CsgWorld second = CsgWorld.Build(TwoOverlappingBrushesAcrossABorder());

        second.Chunks.Count.ShouldBe(first.Chunks.Count);
        for (int c = 0; c < first.Chunks.OrderedChunks.Count; c++)
        {
            WorldChunk a = first.Chunks.OrderedChunks[c];
            WorldChunk b = second.Chunks.OrderedChunks[c];

            b.Coord.ShouldBe(a.Coord);
            b.OwnedBrushIndices.ShouldBe(a.OwnedBrushIndices);
            b.ResidentBrushIndices.ShouldBe(a.ResidentBrushIndices);

            // Bit-identical per-cell artifacts: same surfaces, same order,
            // same floats — for the carved buckets and the per-cell welds.
            ShouldBeIdenticalPolygons(a.Surfaces, b.Surfaces);
            ShouldBeIdenticalPolygons(a.WeldedSurfaces, b.WeldedSurfaces);
        }
    }

    // --- Helpers ------------------------------------------------------------

    // A 2-unit cube (local -1..1) placed by translation alone.
    private static BrushPlacement PlaceUnitBoxAt(Vector3 position) => new(
        Brush.CreateBox(new Vector3(-1f), new Vector3(1f)),
        Matrix4x4.CreateTranslation(position));

    // Freshly-built placements with identical values on every call, so two
    // builds exercise the full pipeline from equal-but-distinct inputs. The
    // pair overlaps AND straddles the x=32 border: brush 0 owned by cell 0,
    // brush 1 by cell 1, both resident in both.
    private static BrushPlacement[] TwoOverlappingBrushesAcrossABorder() =>
    [
        new(Brush.CreateBox(new Vector3(-4f), new Vector3(4f)), Matrix4x4.CreateTranslation(29f, 16f, 16f)),
        new(Brush.CreateBox(new Vector3(-4f), new Vector3(4f)), Matrix4x4.CreateTranslation(35f, 18f, 16f)),
    ];

    private static WorldChunk ChunkAt(CsgWorld world, ChunkCoord coord)
    {
        world.Chunks.TryGet(coord, out WorldChunk chunk).ShouldBeTrue($"expected an occupied chunk at {coord}");
        return chunk;
    }

    private static void ShouldBeIdenticalPolygons(IReadOnlyList<Polygon> expected, IReadOnlyList<Polygon> actual)
    {
        actual.Count.ShouldBe(expected.Count);
        for (int s = 0; s < expected.Count; s++)
        {
            Polygon pa = expected[s];
            Polygon pb = actual[s];
            pb.VertexCount.ShouldBe(pa.VertexCount);
            for (int v = 0; v < pa.VertexCount; v++)
                pb.Vertices[v].ShouldBe(pa.Vertices[v]);
        }
    }
}
