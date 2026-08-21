using System.Text;
using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The W2 oracle: the per-cell snap+weld (<c>ChunkWelder</c> +
/// <see cref="CsgWeldCache"/>) must be semantically equivalent to the
/// monolithic pipeline it replaced. Every equivalence test compares a chunked
/// build against a reference built from the retained primitives — global
/// <see cref="VertexSnapper.Snap"/> + global
/// <see cref="TJunctionWelder.Weld(System.Collections.Generic.IReadOnlyList{Polygon})"/>
/// over the full carve — three ways: the flat surface list bit-for-bit
/// (placement order, vertex sequences, floats — the strongest form), the
/// per-cell welded surfaces as an exact instance partition of that list, and
/// an independent multiset of snapped lattice keys. The incremental tests then
/// pin that cache-carrying recompiles are bit-identical to from-scratch
/// compiles per cell, reuse actually happens, and BSP queries agree.
/// </summary>
public sealed class ChunkWeldEquivalenceTests
{
    // ------------------------------------------------------------------
    // (a) Full-compile equivalence for representative worlds.
    // ------------------------------------------------------------------

    [Fact]
    public void Per_cell_weld_matches_the_global_pipeline_for_a_two_box_overlap_on_a_cell_border() =>
        AssertChunkedMatchesGlobal(TwoBoxOverlapOnBorder());

    [Fact]
    public void Per_cell_weld_matches_the_global_pipeline_for_brushes_exactly_on_cell_boundaries()
    {
        // Boundary-exact geometry is the weld band's reason to exist: faces
        // and centers landing bitwise on x=32 / the (32,32,32) corner put
        // vertices exactly where cell classification flips, so every candidate
        // must be found across the border.
        BrushPlacement[] placements =
        [
            // +x face exactly on the x=32 boundary plane.
            new(Box(4f), Translation(28f, 16f, 16f)),
            // Overlapping neighbour crossing that same border, carving the
            // boundary-exact face for real.
            new(Box(4f), Translation(33f, 17f, 16f)),
            // Centered exactly on a cell corner: every face straddles cells.
            new(Box(4f), Translation(32f, 32f, 32f)),
            // And its overlapping neighbour.
            new(Box(4f), Translation(35f, 34f, 33f)),
        ];
        AssertChunkedMatchesGlobal(placements);
    }

    // A slab-with-overhanging-pillars structure straddling the x=32 border,
    // at coordinates FOUND BY SEARCH to produce genuine cross-brush
    // T-junction insertions there (at tidy integer coordinates the seam
    // subdivisions coincide bitwise and the weld is a no-op; at generic
    // floats, clip interpolation and snapping land fragment corners strictly
    // inside neighbouring edges — the crack the welder exists to close). The
    // three positions cover both directions of the dependency: one where the
    // SLAB's fragments gain pillar vertices, two where PILLAR fragments gain
    // slab vertices, with slab and pillars owned by cells on opposite sides
    // of the border. Do not "clean up" these constants — their fractional
    // parts are the fixture.
    [Theory]
    [InlineData(31.168327f, 17.944384f, 14.90725f)]
    [InlineData(31.224281f, 15.108146f, 14.222553f)]
    [InlineData(30.06375f, 15.893757f, 17.044592f)]
    public void Per_cell_weld_finds_t_junction_candidates_across_a_cell_border(float x, float y, float z)
    {
        BrushPlacement[] placements =
        [
            new(Brush.CreateBox(new Vector3(-4f, -1f, -4f), new Vector3(4f, 1f, 4f)), Translation(x, y, z)),
            new(Box(1f), Translation(x + 3.5f, y + 1.5f, z)),
            new(Box(1f), Translation(x - 3.5f, y + 1.5f, z + 1.5f)),
            new(Box(1f), Translation(x, y + 1.5f, z - 3.5f)),
        ];

        // The structure must straddle the border with a pillar wholly on the
        // far side — the geometry the candidate band exists for.
        (x - 4f).ShouldBeLessThan(32f);
        (x + 4f).ShouldBeGreaterThan(32f);

        // Guard against a vacuous fixture: some insertion must come from
        // ANOTHER brush's vertices. Welding each brush against only its own
        // snapped surfaces must diverge from the global weld — otherwise
        // every T-vertex is intra-brush and the fixture proves nothing about
        // candidate gathering.
        Polygon[] snapped = VertexSnapper.Snap(Csg.Carve(placements));
        Polygon[] global = TJunctionWelder.Weld(snapped);
        var brushLocal = new List<Polygon>();
        foreach (Polygon[] carve in Csg.CarvePerBrush(placements))
        {
            Polygon[] ownSnapped = VertexSnapper.Snap(carve);
            brushLocal.AddRange(TJunctionWelder.Weld(ownSnapped, ownSnapped));
        }
        bool anyCrossBrushInsertion = false;
        for (int i = 0; i < global.Length; i++)
            anyCrossBrushInsertion |= global[i].VertexCount != brushLocal[i].VertexCount;
        anyCrossBrushInsertion.ShouldBeTrue(
            "fixture produced no cross-brush T-junction insertions — it cannot exercise the border band");

        AssertChunkedMatchesGlobal(placements);
    }

    [Fact]
    public void Per_cell_weld_matches_the_global_pipeline_for_a_200_part_scattered_world()
    {
        List<BrushPlacement> placements = ScatteredWorld(structures: 50, seed: 0xC0FFEE0DDBA5EBA1UL);
        placements.Count.ShouldBe(200);

        CsgWorld world = AssertChunkedMatchesGlobal(placements);
        // Guard against a vacuous pass: the scatter must genuinely spread the
        // weld across many cells (negative coordinates included).
        world.Chunks.Count.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void Per_cell_weld_matches_the_global_pipeline_for_a_dense_grid_world()
    {
        // 6x6x6 size-2 cubes at spacing 1.8 (every axis-adjacent pair overlaps
        // by 0.2), positioned so the block straddles the (32,32,32) cell
        // corner — dense mutual carving right across cell borders.
        var placements = new List<BrushPlacement>(216);
        for (int x = 0; x < 6; x++)
            for (int y = 0; y < 6; y++)
                for (int z = 0; z < 6; z++)
                    placements.Add(new BrushPlacement(
                        Box(1f), Translation(27.5f + x * 1.8f, 27.5f + y * 1.8f, 27.5f + z * 1.8f)));

        AssertChunkedMatchesGlobal(placements);
    }

    // ------------------------------------------------------------------
    // (b) Incremental correctness: recompiling through the carried caches is
    // bit-identical to a from-scratch compile of the new placements, per cell.
    // ------------------------------------------------------------------

    [Fact]
    public void Moving_one_brush_and_recompiling_incrementally_matches_a_from_scratch_compile_per_cell()
    {
        List<BrushPlacement> placements = ScatteredWorld(structures: 50, seed: 0xC0FFEE0DDBA5EBA1UL);
        CsgWorld first = CsgWorld.Build(placements, dirtyCells: null, previousCache: null, previousWeldCache: null);

        List<BrushPlacement> edited = [.. placements];
        edited[1] = edited[1] with
        {
            Transform = edited[1].Transform * Matrix4x4.CreateTranslation(0.3f, 0f, 0.2f),
        };

        CsgWorld incremental = CsgWorld.Build(edited, dirtyCells: null, first.CompileCache, first.WeldCache);
        CsgWorld scratch = CsgWorld.Build(edited);

        // Flat list, mesh arrays, and every per-cell artifact, bit for bit.
        ShouldBeIdenticalSurfaces(scratch.Surfaces, incremental.Surfaces, "flat surfaces");
        ShouldHaveIdenticalChunkArtifacts(scratch, incremental);
        (float[] expectedVertices, uint[] expectedIndices) = scratch.BuildMesh();
        (float[] actualVertices, uint[] actualIndices) = incremental.BuildMesh();
        actualVertices.SequenceEqual(expectedVertices).ShouldBeTrue("mesh vertices diverged");
        actualIndices.SequenceEqual(expectedIndices).ShouldBeTrue("mesh indices diverged");

        // The edit must have stayed local: the moved pillar's structure (and
        // any scatter neighbours) re-weld, the rest of the world reuses.
        CsgWeldStats stats = incremental.WeldStats.ShouldNotBeNull();
        stats.Total.ShouldBe(edited.Count);
        stats.Welded.ShouldBeGreaterThan(0);
        stats.Reused.ShouldBeGreaterThan(edited.Count / 2, "a one-brush edit re-welded most of the world");

        // Spatial-query oracle: the BSPs must answer identically everywhere.
        ShouldAgreeOnSpatialQueries(scratch, incremental, seed: 0x0DDBA11DEADBEEFUL);
    }

    [Fact]
    public void Clean_cells_keep_their_previously_welded_surface_instances()
    {
        // The border pair welds T-junctions across the x=32 cell border; the
        // far box lives many cells away. Moving the far box must leave the
        // pair's welded arrays untouched — the same Polygon instances, not
        // merely equal ones.
        List<BrushPlacement> placements = [.. TwoBoxOverlapOnBorder(), new(Box(1f), Translation(200f, 16f, 16f))];
        CsgWorld first = CsgWorld.Build(placements, dirtyCells: null, previousCache: null, previousWeldCache: null);

        List<BrushPlacement> edited = [.. placements];
        edited[2] = edited[2] with
        {
            Transform = edited[2].Transform * Matrix4x4.CreateTranslation(0f, 0f, 1.5f),
        };

        CsgWorld incremental = CsgWorld.Build(edited, dirtyCells: null, first.CompileCache, first.WeldCache);

        // Exactly the far box re-welded; the border pair reused.
        incremental.WeldStats.ShouldBe(new CsgWeldStats(Reused: 2, Welded: 1));

        // The pair's welded surfaces are the previous compile's instances.
        for (int i = 0; i < 2; i++)
        {
            IReadOnlyList<Polygon> before = OwnerCellWeldedSurfaces(first, placements[i]);
            IReadOnlyList<Polygon> after = OwnerCellWeldedSurfaces(incremental, edited[i]);
            after.Count.ShouldBe(before.Count);
            for (int s = 0; s < before.Count; s++)
                after[s].ShouldBeSameAs(before[s]);
        }

        // And the reuse is still exactly what a from-scratch compile produces.
        ShouldBeIdenticalSurfaces(CsgWorld.Build(edited).Surfaces, incremental.Surfaces, "reuse vs scratch");
    }

    // ------------------------------------------------------------------
    // (c) The chunked-path random walk: the carve-cache walk's discipline,
    // with BOTH caches chained exactly as the scene pump chains them.
    // ------------------------------------------------------------------

    [Fact]
    public void Twenty_random_moves_through_the_chunked_path_stay_bit_identical_and_reuse_welds()
    {
        List<BrushPlacement> placements = ScatteredWorld(structures: 25, seed: 0xBADC0DEBADC0DE01UL);
        CsgWorld incremental = CsgWorld.Build(placements, dirtyCells: null, previousCache: null, previousWeldCache: null);

        // Fixed-seed LCG (same MMIX constants as CsgBench) so the move
        // sequence is identical on every runtime and machine.
        ulong state = 0xC0FFEE0DDBA5EBA1UL;
        float NextFloat01()
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (state >> 40) * (1.0f / (1 << 24));
        }

        for (int move = 0; move < 20; move++)
        {
            int index = (int)(NextFloat01() * placements.Count) % placements.Count;
            var delta = new Vector3(
                NextFloat01() - 0.5f, NextFloat01() - 0.5f, NextFloat01() - 0.5f);
            placements[index] = placements[index] with
            {
                Transform = placements[index].Transform * Matrix4x4.CreateTranslation(delta),
            };

            incremental = CsgWorld.Build(placements, dirtyCells: null, incremental.CompileCache, incremental.WeldCache);
            CsgWorld scratch = CsgWorld.Build(placements);

            // Flat list AND per-cell artifacts, bit for bit, after every move.
            ShouldBeIdenticalSurfaces(scratch.Surfaces, incremental.Surfaces, $"move #{move}");
            ShouldHaveIdenticalChunkArtifacts(scratch, incremental);

            // The walk must actually exercise weld reuse, not degrade to full
            // re-welds that are trivially identical.
            CsgWeldStats stats = incremental.WeldStats.ShouldNotBeNull();
            stats.Reused.ShouldBeGreaterThan(0, $"no weld reuse at move #{move}");
            stats.Total.ShouldBe(placements.Count);
        }
    }

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    // A cube of half-extent `h` centered on its local origin.
    private static Brush Box(float h) => Brush.CreateBox(new Vector3(-h), new Vector3(h));

    private static Matrix4x4 Translation(float x, float y, float z) => Matrix4x4.CreateTranslation(x, y, z);

    // Overlapping pair straddling the x=32 cell border: brush 0 owned by cell
    // (0,0,0), brush 1 by (1,0,0), both resident in both — the minimal world
    // where per-cell welding must reach across a border.
    private static BrushPlacement[] TwoBoxOverlapOnBorder() =>
    [
        new(Box(4f), Translation(29f, 16f, 16f)),
        new(Box(4f), Translation(35f, 18f, 16f)),
    ];

    // `structures` four-part structures (a floor slab with three overlapping
    // pillars, so every structure welds real T-junctions) scattered by a
    // fixed-seed LCG over a ±160-unit region — spanning many cells, negative
    // coordinates included, with structures frequently straddling borders.
    private static List<BrushPlacement> ScatteredWorld(int structures, ulong seed)
    {
        ulong state = seed;
        float NextFloat01()
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (state >> 40) * (1.0f / (1 << 24));
        }

        var placements = new List<BrushPlacement>(structures * 4);
        for (int s = 0; s < structures; s++)
        {
            var p = new Vector3(
                (NextFloat01() - 0.5f) * 320f,
                (NextFloat01() - 0.5f) * 320f,
                (NextFloat01() - 0.5f) * 320f);

            // A fresh Brush instance per part: both caches key entries by
            // brush reference (one entry per instance), so instance sharing
            // would artificially serialise cache hits and mask reuse.
            placements.Add(new BrushPlacement(
                Brush.CreateBox(new Vector3(-4f, -1f, -4f), new Vector3(4f, 1f, 4f)),
                Translation(p.X, p.Y, p.Z)));
            placements.Add(new BrushPlacement(Box(1f), Translation(p.X + 3.5f, p.Y + 1.5f, p.Z)));
            placements.Add(new BrushPlacement(Box(1f), Translation(p.X - 3.5f, p.Y + 1.5f, p.Z + 1.5f)));
            placements.Add(new BrushPlacement(Box(1f), Translation(p.X, p.Y + 1.5f, p.Z - 3.5f)));
        }
        return placements;
    }

    // ------------------------------------------------------------------
    // Assertions
    // ------------------------------------------------------------------

    // The monolithic reference pipeline, built from the retained primitives
    // exactly as the pre-chunked compile ran: full carve, then global snap,
    // then one global weld over everything.
    private static Polygon[] GlobalReference(IReadOnlyList<BrushPlacement> placements) =>
        TJunctionWelder.Weld(VertexSnapper.Snap(Csg.Carve(placements)));

    private static CsgWorld AssertChunkedMatchesGlobal(IReadOnlyList<BrushPlacement> placements)
    {
        CsgWorld world = CsgWorld.Build(placements);
        Polygon[] global = GlobalReference(placements);
        global.ShouldNotBeEmpty(); // guard against a vacuous comparison

        // (1) The flat surface list IS the global pipeline's output — order,
        // vertex sequences, and floats all bit-identical. (The per-cell union
        // is cell-major rather than placement-major, so the ordered bit-
        // identity claim is asserted here, on the placement-ordered flat list;
        // the union is compared as a partition and a multiset below.)
        ShouldBeIdenticalSurfaces(global, world.Surfaces, "flat vs global");

        // (2) The per-cell welded surfaces partition the flat list exactly:
        // the same Polygon instances, each under exactly one owner cell.
        var remaining = new HashSet<Polygon>(ReferenceEqualityComparer.Instance);
        foreach (Polygon poly in world.Surfaces)
            remaining.Add(poly).ShouldBeTrue("duplicate instance in the flat surface list");
        foreach (WorldChunk chunk in world.Chunks.OrderedChunks)
        {
            foreach (Polygon poly in chunk.WeldedSurfaces)
                remaining.Remove(poly).ShouldBeTrue($"cell {chunk.Coord} holds a surface not in (or already taken from) the flat list");
        }
        remaining.ShouldBeEmpty("flat surfaces missing from every per-cell union");

        // (3) Belt and braces: the union also matches the global reference as
        // a multiset of snapped lattice keys, independent of instance sharing.
        var union = new List<Polygon>();
        foreach (WorldChunk chunk in world.Chunks.OrderedChunks)
            union.AddRange(chunk.WeldedSurfaces);
        ShouldBeEqualKeyMultisets(global, union);

        return world;
    }

    private static void ShouldHaveIdenticalChunkArtifacts(CsgWorld expected, CsgWorld actual)
    {
        actual.Chunks.Count.ShouldBe(expected.Chunks.Count, "occupied cell count diverged");
        for (int c = 0; c < expected.Chunks.OrderedChunks.Count; c++)
        {
            WorldChunk e = expected.Chunks.OrderedChunks[c];
            WorldChunk a = actual.Chunks.OrderedChunks[c];

            a.Coord.ShouldBe(e.Coord);
            a.OwnedBrushIndices.ShouldBe(e.OwnedBrushIndices, $"owned brushes diverged in cell {e.Coord}");
            a.ResidentBrushIndices.ShouldBe(e.ResidentBrushIndices, $"resident brushes diverged in cell {e.Coord}");
            ShouldBeIdenticalSurfaces(e.WeldedSurfaces, a.WeldedSurfaces, $"welded surfaces of cell {e.Coord}");
        }
    }

    // Deterministic probe points and rays over the scatter region: identical
    // surface lists must yield identical BSPs, so every answer — hit flags,
    // points, normals, distances — is compared exactly.
    private static void ShouldAgreeOnSpatialQueries(CsgWorld expected, CsgWorld actual, ulong seed)
    {
        ulong state = seed;
        float NextFloat01()
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (state >> 40) * (1.0f / (1 << 24));
        }

        for (int p = 0; p < 500; p++)
        {
            var point = new Vector3(
                (NextFloat01() - 0.5f) * 340f,
                (NextFloat01() - 0.5f) * 340f,
                (NextFloat01() - 0.5f) * 340f);
            actual.ContainsPoint(point).ShouldBe(
                expected.ContainsPoint(point), $"ContainsPoint diverged at {point}");
        }

        for (int r = 0; r < 100; r++)
        {
            var origin = new Vector3(
                (NextFloat01() - 0.5f) * 340f,
                200f,
                (NextFloat01() - 0.5f) * 340f);
            bool expectedHit = expected.Raycast(origin, -Vector3.UnitY, 600f, out BspRaycastHit expectedResult);
            bool actualHit = actual.Raycast(origin, -Vector3.UnitY, 600f, out BspRaycastHit actualResult);

            actualHit.ShouldBe(expectedHit, $"Raycast hit flag diverged from {origin}");
            if (expectedHit)
                actualResult.ShouldBe(expectedResult, $"Raycast result diverged from {origin}");
        }
    }

    // The welded surfaces of the cell owning `placement` — the per-cell
    // artifact reuse hands forward untouched.
    private static IReadOnlyList<Polygon> OwnerCellWeldedSurfaces(CsgWorld world, in BrushPlacement placement)
    {
        world.Chunks.TryGet(ChunkGrid.OwnerCell(in placement), out WorldChunk chunk)
            .ShouldBeTrue("expected the owner cell to be occupied");
        return chunk.WeldedSurfaces;
    }

    // Bit-exact comparison (same rationale as PlacementEquivalenceTests): both
    // paths run the same weld code over the same float inputs, so any drift is
    // a real divergence — a missed candidate or a stale reuse — not FP noise.
    private static void ShouldBeIdenticalSurfaces(
        IReadOnlyList<Polygon> expected, IReadOnlyList<Polygon> actual, string context)
    {
        actual.Count.ShouldBe(expected.Count, $"surface count diverged ({context})");
        for (int i = 0; i < expected.Count; i++)
        {
            actual[i].Surface.ShouldBe(expected[i].Surface, $"surface plane #{i} diverged ({context})");
            actual[i].VertexCount.ShouldBe(expected[i].VertexCount, $"vertex count of surface #{i} diverged ({context})");
            for (int v = 0; v < expected[i].VertexCount; v++)
                actual[i].Vertices[v].ShouldBe(expected[i].Vertices[v], $"vertex {v} of surface #{i} diverged ({context})");
        }
    }

    // Order-insensitive comparison keyed on the snap lattice: each surface
    // becomes the string of its lattice-quantised vertex triples (welding
    // preserves winding and start vertex, so no rotation canonicalisation is
    // needed for equal pipelines).
    private static void ShouldBeEqualKeyMultisets(IReadOnlyList<Polygon> expected, IReadOnlyList<Polygon> actual)
    {
        Dictionary<string, int> expectedKeys = KeyMultiset(expected);
        Dictionary<string, int> actualKeys = KeyMultiset(actual);

        actualKeys.Count.ShouldBe(expectedKeys.Count, "distinct surface-key count diverged");
        foreach ((string key, int count) in expectedKeys)
        {
            actualKeys.TryGetValue(key, out int actualCount)
                .ShouldBeTrue($"surface key missing from the per-cell union: {key}");
            actualCount.ShouldBe(count, $"multiplicity diverged for surface key: {key}");
        }
    }

    private static Dictionary<string, int> KeyMultiset(IReadOnlyList<Polygon> surfaces)
    {
        var multiset = new Dictionary<string, int>();
        var builder = new StringBuilder();
        foreach (Polygon poly in surfaces)
        {
            builder.Clear();
            foreach (Vector3 v in poly.VertexSpan)
            {
                (long x, long y, long z) = GeometryTestHelpers.LatticeKey(v);
                builder.Append(x).Append(',').Append(y).Append(',').Append(z).Append(';');
            }
            string key = builder.ToString();
            multiset[key] = multiset.TryGetValue(key, out int count) ? count + 1 : 1;
        }
        return multiset;
    }
}
