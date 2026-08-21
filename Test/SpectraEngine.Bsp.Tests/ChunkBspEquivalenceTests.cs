using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The W3 oracle: the per-cell BSP stage (<c>ChunkBspBuilder</c> +
/// <see cref="CsgBspCache"/>) with routed queries
/// (<see cref="CsgWorld.ContainsPoint"/> / <see cref="CsgWorld.Raycast"/>)
/// must be semantically equivalent to one monolithic
/// <see cref="BspTree.BuildFromSurfaces"/> over the full welded surface list.
/// Every equivalence test compares the routed answers against that retained
/// primitive: point containment over dense probe grids (cell-boundary points
/// included), fixed-seed rays crossing many cells (hit flag, distance within
/// 1e-4, normal within 1e-3), rays along cell-boundary planes, and rays whose
/// origin starts inside solid. The incremental tests then pin that
/// cache-carrying recompiles reuse clean cells' tree INSTANCES while staying
/// structurally identical to from-scratch compiles, and the determinism test
/// pins bit-identical per-cell trees for identical inputs.
/// </summary>
public sealed class ChunkBspEquivalenceTests
{
    // ------------------------------------------------------------------
    // (a) Routed queries match the monolithic tree for the W2 worlds.
    // ------------------------------------------------------------------

    [Fact]
    public void Routed_queries_match_monolithic_for_a_two_box_overlap_on_a_cell_border()
    {
        BrushPlacement[] placements = TwoBoxOverlapOnBorder();
        AssertContainsPointOracle(placements, samplesPerAxis: 34);
        AssertRaycastOracle(placements, rayCount: 300, seed: 0x5EEDC0DE12345678UL);
    }

    [Fact]
    public void Routed_queries_match_monolithic_for_brushes_exactly_on_cell_boundaries()
    {
        BrushPlacement[] placements = BoundaryExactWorld();
        AssertContainsPointOracle(placements, samplesPerAxis: 34);
        AssertRaycastOracle(placements, rayCount: 300, seed: 0xB0A2DDA7A5EEDF00UL);
    }

    [Fact]
    public void Routed_queries_match_monolithic_for_a_200_part_scattered_world()
    {
        List<BrushPlacement> placements = ScatteredWorld(structures: 50, seed: 0xC0FFEE0DDBA5EBA1UL);
        placements.Count.ShouldBe(200);

        // Guard against a vacuous pass: the scatter must genuinely span many
        // cells so the probes and rays actually cross chunk borders.
        CsgWorld world = CsgWorld.Build(placements);
        world.Chunks.Count.ShouldBeGreaterThan(20);

        AssertContainsPointOracle(placements, samplesPerAxis: 36);
        AssertRaycastOracle(placements, rayCount: 400, seed: 0x0DDBA11DEADBEEFUL);
    }

    [Fact]
    public void Routed_queries_match_monolithic_for_a_dense_grid_world()
    {
        List<BrushPlacement> placements = DenseGridWorld();
        AssertContainsPointOracle(placements, samplesPerAxis: 34);
        AssertRaycastOracle(placements, rayCount: 300, seed: 0xFACEFEEDDEADF00DUL);
    }

    // ------------------------------------------------------------------
    // (b) Deliberate boundary geometry: rays along cell-boundary planes and
    // rays starting exactly on cell boundaries, in both axis directions.
    // ------------------------------------------------------------------

    [Fact]
    public void Rays_along_and_across_cell_boundary_planes_match_monolithic()
    {
        // Both fixtures put real carved geometry at or straddling the x=32
        // boundary plane — the worst case for interval clamping.
        foreach (IReadOnlyList<BrushPlacement> placements in
                 new IReadOnlyList<BrushPlacement>[] { TwoBoxOverlapOnBorder(), BoundaryExactWorld() })
        {
            CsgWorld world = CsgWorld.Build(placements);
            BspTree mono = BspTree.BuildFromSurfaces(world.Surfaces);

            // Directions strictly inside the x=32 plane, plus both crossings.
            Vector3[] directions =
            [
                -Vector3.UnitY, Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ,
                Vector3.Normalize(new Vector3(0f, -1f, 1f)),
                Vector3.Normalize(new Vector3(0f, -1f, -1f)),
                -Vector3.UnitX, Vector3.UnitX,
            ];

            // The z lattice is deliberately offset from the y lattice: with
            // both on the same integer-friendly grid, the diagonal in-plane
            // directions graze box EDGES exactly (e.g. a ray through the
            // y=20/z=20 edge line), where two normals are equally valid first
            // surfaces and the two trees may legitimately pick different
            // planes. The offset keeps every origin exactly on the x=32
            // boundary plane while breaking those measure-zero corner hits.
            for (float a = 8f; a <= 44f; a += 1.5f)
            {
                for (float b = 8.37f; b <= 44f; b += 1.5f)
                {
                    // Origin exactly on the x=32 cell-boundary plane.
                    var origin = new Vector3(32f, a, b);
                    foreach (Vector3 direction in directions)
                        CompareRay(world, mono, origin, direction, 120f, $"boundary ray from {origin} along {direction}");
                }
            }

            // Rays running exactly along a cell-boundary LINE (two coordinates
            // pinned to boundary planes at once).
            for (float a = 8f; a <= 44f; a += 1.5f)
            {
                CompareRay(world, mono, new Vector3(32f, a, 32f), -Vector3.UnitY, 120f, "boundary-line ray -Y");
                CompareRay(world, mono, new Vector3(32f, 32f, a), -Vector3.UnitZ, 120f, "boundary-line ray -Z");
            }
        }
    }

    [Fact]
    public void Rays_starting_inside_solid_report_an_immediate_hit_like_monolithic()
    {
        List<BrushPlacement> placements = ScatteredWorld(structures: 20, seed: 0xBADC0DEBADC0DE01UL);
        CsgWorld world = CsgWorld.Build(placements);
        BspTree mono = BspTree.BuildFromSurfaces(world.Surfaces);

        Vector3 direction = Vector3.Normalize(new Vector3(1f, 2f, 3f));
        foreach (BrushPlacement placement in placements)
        {
            // The world center of a box brush is inside it, and therefore
            // inside solid space — unless a probe ever lands exactly on a
            // carved face, both trees must report the immediate-hit contract.
            Vector3 origin = placement.WorldBounds.Center;
            bool expected = mono.Raycast(origin, direction, 50f, out BspRaycastHit expectedHit);
            bool actual = world.Raycast(origin, direction, 50f, out BspRaycastHit actualHit);

            expected.ShouldBeTrue($"monolithic missed from inside {origin}");
            actual.ShouldBeTrue($"routed missed from inside {origin}");
            actualHit.ShouldBe(expectedHit, $"inside-solid hit diverged at {origin}");
            actualHit.Distance.ShouldBe(0f);
            actualHit.Point.ShouldBe(origin);
        }
    }

    [Fact]
    public void Queries_in_unoccupied_cells_are_open_air()
    {
        CsgWorld world = CsgWorld.Build(TwoBoxOverlapOnBorder());

        // Far from every chunk: no cell, no solid.
        world.ContainsPoint(new Vector3(5000f, -3000f, 800f)).ShouldBeFalse();

        // A ray that never touches an occupied cell stays a miss.
        world.Raycast(new Vector3(500f, 500f, 500f), Vector3.UnitX, 1000f, out _).ShouldBeFalse();

        // A ray starting far outside any chunk and entering the world hits
        // exactly like the monolithic tree.
        BspTree mono = BspTree.BuildFromSurfaces(world.Surfaces);
        CompareRay(world, mono,
            new Vector3(-400f, 16f, 16f), Vector3.UnitX, 1000f, "long approach from open air");
    }

    // ------------------------------------------------------------------
    // (c) Determinism: identical inputs produce bit-identical per-cell trees.
    // ------------------------------------------------------------------

    [Fact]
    public void Two_builds_of_the_same_placements_produce_structurally_identical_cell_trees()
    {
        List<BrushPlacement> placements = ScatteredWorld(structures: 25, seed: 0xBADC0DEBADC0DE01UL);
        CsgWorld first = CsgWorld.Build(placements);
        CsgWorld second = CsgWorld.Build(placements);

        first.Chunks.Count.ShouldBe(second.Chunks.Count);
        for (int c = 0; c < first.Chunks.OrderedChunks.Count; c++)
        {
            WorldChunk a = first.Chunks.OrderedChunks[c];
            WorldChunk b = second.Chunks.OrderedChunks[c];
            b.Coord.ShouldBe(a.Coord);
            AssertStructurallyIdentical(a.Bsp, b.Bsp, $"cell {a.Coord}");
        }
    }

    // ------------------------------------------------------------------
    // (d) Incremental correctness: clean cells keep their tree INSTANCES, and
    // the carried recompile stays equivalent to a from-scratch compile.
    // ------------------------------------------------------------------

    [Fact]
    public void Clean_cells_keep_their_previous_tree_instances()
    {
        // The border pair occupies the cells around x=32; the far box lives
        // many cells away. Moving the far box must leave the pair's cell
        // trees untouched — the same BspTree instances, not merely equal ones.
        List<BrushPlacement> placements =
            [.. TwoBoxOverlapOnBorder(), new(Box(1f), Translation(200f, 16f, 16f))];
        CsgWorld first = CsgWorld.Build(
            placements, dirtyCells: null, previousCache: null, previousWeldCache: null, previousBspCache: null);

        List<BrushPlacement> edited = [.. placements];
        edited[2] = edited[2] with
        {
            Transform = edited[2].Transform * Matrix4x4.CreateTranslation(0f, 0f, 1.5f),
        };

        CsgWorld incremental = CsgWorld.Build(
            edited, dirtyCells: null, first.CompileCache, first.WeldCache, first.BspCache);

        // Exactly the far box's cell rebuilt; the pair's two cells reused.
        incremental.BspStats.ShouldBe(new CsgBspStats(Reused: 2, Built: 1));

        foreach (WorldChunk before in first.Chunks.OrderedChunks)
        {
            if (before.Coord.X >= 6)
                continue; // the far box's moving neighbourhood
            incremental.Chunks.TryGet(before.Coord, out WorldChunk after).ShouldBeTrue();
            after.Bsp.ShouldBeSameAs(before.Bsp, $"cell {before.Coord} rebuilt its tree needlessly");
        }
    }

    [Fact]
    public void Moving_one_brush_and_recompiling_incrementally_matches_scratch_and_monolithic()
    {
        List<BrushPlacement> placements = ScatteredWorld(structures: 50, seed: 0xC0FFEE0DDBA5EBA1UL);
        CsgWorld first = CsgWorld.Build(
            placements, dirtyCells: null, previousCache: null, previousWeldCache: null, previousBspCache: null);

        List<BrushPlacement> edited = [.. placements];
        edited[1] = edited[1] with
        {
            Transform = edited[1].Transform * Matrix4x4.CreateTranslation(0.3f, 0f, 0.2f),
        };

        CsgWorld incremental = CsgWorld.Build(
            edited, dirtyCells: null, first.CompileCache, first.WeldCache, first.BspCache);
        CsgWorld scratch = CsgWorld.Build(edited);

        // The edit must have stayed local: most cells reuse their tree.
        CsgBspStats stats = incremental.BspStats.ShouldNotBeNull();
        stats.Total.ShouldBe(incremental.Chunks.Count);
        stats.Built.ShouldBeGreaterThan(0);
        stats.Reused.ShouldBeGreaterThan(incremental.Chunks.Count / 2,
            "a one-brush edit rebuilt most of the world's cell trees");

        // Reused or rebuilt, every cell's tree is exactly what a from-scratch
        // compile produces (the inputs are bit-identical, the build is
        // deterministic — so the structures must match node for node).
        incremental.Chunks.Count.ShouldBe(scratch.Chunks.Count);
        for (int c = 0; c < scratch.Chunks.OrderedChunks.Count; c++)
        {
            WorldChunk e = scratch.Chunks.OrderedChunks[c];
            WorldChunk a = incremental.Chunks.OrderedChunks[c];
            a.Coord.ShouldBe(e.Coord);
            AssertStructurallyIdentical(e.Bsp, a.Bsp, $"cell {e.Coord}");
        }

        // And the routed answers still match one monolithic tree of the
        // edited world.
        BspTree mono = BspTree.BuildFromSurfaces(scratch.Surfaces);
        AssertQueriesMatch(incremental, mono, seed: 0x0DDBA11DEADBEEFUL, rayCount: 200);
    }

    // ------------------------------------------------------------------
    // Fixtures (mirroring ChunkWeldEquivalenceTests)
    // ------------------------------------------------------------------

    // A cube of half-extent `h` centered on its local origin.
    private static Brush Box(float h) => Brush.CreateBox(new Vector3(-h), new Vector3(h));

    private static Matrix4x4 Translation(float x, float y, float z) => Matrix4x4.CreateTranslation(x, y, z);

    // Overlapping pair straddling the x=32 cell border — the minimal world
    // where routed queries must agree across a border.
    private static BrushPlacement[] TwoBoxOverlapOnBorder() =>
    [
        new(Box(4f), Translation(29f, 16f, 16f)),
        new(Box(4f), Translation(35f, 18f, 16f)),
    ];

    // Faces and centers landing bitwise on x=32 / the (32,32,32) corner: the
    // fixture where carved geometry sits exactly where cell classification
    // flips.
    private static BrushPlacement[] BoundaryExactWorld() =>
    [
        new(Box(4f), Translation(28f, 16f, 16f)),
        new(Box(4f), Translation(33f, 17f, 16f)),
        new(Box(4f), Translation(32f, 32f, 32f)),
        new(Box(4f), Translation(35f, 34f, 33f)),
    ];

    // `structures` four-part structures (a floor slab with three overlapping
    // pillars) scattered by a fixed-seed LCG over a ±160-unit region —
    // spanning many cells, negative coordinates included, with structures
    // frequently straddling borders.
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

            placements.Add(new BrushPlacement(
                Brush.CreateBox(new Vector3(-4f, -1f, -4f), new Vector3(4f, 1f, 4f)),
                Translation(p.X, p.Y, p.Z)));
            placements.Add(new BrushPlacement(Box(1f), Translation(p.X + 3.5f, p.Y + 1.5f, p.Z)));
            placements.Add(new BrushPlacement(Box(1f), Translation(p.X - 3.5f, p.Y + 1.5f, p.Z + 1.5f)));
            placements.Add(new BrushPlacement(Box(1f), Translation(p.X, p.Y + 1.5f, p.Z - 3.5f)));
        }
        return placements;
    }

    // 6x6x6 size-2 cubes at spacing 1.8, straddling the (32,32,32) cell
    // corner — dense mutual carving right across cell borders.
    private static List<BrushPlacement> DenseGridWorld()
    {
        var placements = new List<BrushPlacement>(216);
        for (int x = 0; x < 6; x++)
            for (int y = 0; y < 6; y++)
                for (int z = 0; z < 6; z++)
                    placements.Add(new BrushPlacement(
                        Box(1f), Translation(27.5f + x * 1.8f, 27.5f + y * 1.8f, 27.5f + z * 1.8f)));
        return placements;
    }

    // ------------------------------------------------------------------
    // Oracle assertions
    // ------------------------------------------------------------------

    // Dense ContainsPoint grid over the world's expanded bounds: uniform
    // samples per axis PLUS every cell-boundary plane in range, so the probe
    // set contains points exactly on cell boundaries (and, at boundary-exact
    // fixtures, exactly on carved faces).
    private static void AssertContainsPointOracle(IReadOnlyList<BrushPlacement> placements, int samplesPerAxis)
    {
        CsgWorld world = CsgWorld.Build(placements);
        BspTree mono = BspTree.BuildFromSurfaces(world.Surfaces);
        Aabb bounds = WorldBounds(placements).Expanded(3f);

        float[] xs = AxisSamples(bounds.Min.X, bounds.Max.X, samplesPerAxis);
        float[] ys = AxisSamples(bounds.Min.Y, bounds.Max.Y, samplesPerAxis);
        float[] zs = AxisSamples(bounds.Min.Z, bounds.Max.Z, samplesPerAxis);

        // Tens of thousands of probes: compare manually and report the first
        // divergence (per-probe Shouldly assertions would dominate the run).
        int probes = 0, solid = 0, mismatches = 0;
        Vector3 firstMismatch = default;
        foreach (float x in xs)
        {
            foreach (float y in ys)
            {
                foreach (float z in zs)
                {
                    var point = new Vector3(x, y, z);
                    bool expected = mono.ContainsPoint(point);
                    if (expected)
                        solid++;
                    if (world.ContainsPoint(point) != expected && mismatches++ == 0)
                        firstMismatch = point;
                    probes++;
                }
            }
        }

        probes.ShouldBeGreaterThanOrEqualTo(30_000, "probe grid too sparse to be an oracle");
        solid.ShouldBeGreaterThan(0, "no probe landed in solid — vacuous oracle");
        solid.ShouldBeLessThan(probes, "every probe landed in solid — vacuous oracle");
        mismatches.ShouldBe(0, $"ContainsPoint diverged {mismatches} times, first at {firstMismatch}");
    }

    private static float[] AxisSamples(float min, float max, int count)
    {
        var values = new List<float>(count + 8);
        for (int i = 0; i < count; i++)
            values.Add(min + (max - min) * i / (count - 1));
        for (int c = (int)MathF.Ceiling(min / ChunkCoord.CellSize); c * ChunkCoord.CellSize <= max; c++)
            values.Add(c * ChunkCoord.CellSize);
        return [.. values];
    }

    // Fixed-seed rays whose origins and directions span the world (and its
    // surrounding open air), long enough to cross many cells.
    private static void AssertRaycastOracle(IReadOnlyList<BrushPlacement> placements, int rayCount, ulong seed)
    {
        CsgWorld world = CsgWorld.Build(placements);
        BspTree mono = BspTree.BuildFromSurfaces(world.Surfaces);
        AssertQueriesMatch(world, mono, seed, rayCount, placements);
    }

    private static void AssertQueriesMatch(
        CsgWorld world, BspTree mono, ulong seed, int rayCount, IReadOnlyList<BrushPlacement>? placements = null)
    {
        Aabb bounds = WorldBounds(placements ?? world.Placements).Expanded(8f);
        Vector3 size = bounds.Size;
        float maxDistance = size.Length() + 16f;

        ulong state = seed;
        float NextFloat01()
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (state >> 40) * (1.0f / (1 << 24));
        }
        Vector3 NextDirection()
        {
            while (true)
            {
                var v = new Vector3(
                    NextFloat01() * 2f - 1f, NextFloat01() * 2f - 1f, NextFloat01() * 2f - 1f);
                float lengthSquared = v.LengthSquared();
                if (lengthSquared is > 1e-4f and <= 1f)
                    return v / MathF.Sqrt(lengthSquared);
            }
        }

        int hits = 0;
        for (int r = 0; r < rayCount; r++)
        {
            Vector3 origin = bounds.Min + size * new Vector3(NextFloat01(), NextFloat01(), NextFloat01());
            Vector3 direction = NextDirection();
            if (CompareRay(world, mono, origin, direction, maxDistance, $"ray #{r} from {origin} along {direction}"))
                hits++;
        }
        hits.ShouldBeGreaterThan(0, "no ray hit solid — vacuous oracle");
        hits.ShouldBeLessThan(rayCount, "every ray hit solid — vacuous oracle");
    }

    // One routed-vs-monolithic ray comparison: hit flag exactly, distance
    // within 1e-4, normal within 1e-3 per component (node-agnostic — the two
    // trees may find the same surface through different splitter paths).
    // Returns whether the ray hit, for vacuousness accounting.
    private static bool CompareRay(
        CsgWorld world, BspTree mono, Vector3 origin, Vector3 direction, float maxDistance, string context)
    {
        bool expected = mono.Raycast(origin, direction, maxDistance, out BspRaycastHit expectedHit);
        bool actual = world.Raycast(origin, direction, maxDistance, out BspRaycastHit actualHit);

        actual.ShouldBe(expected, $"hit flag diverged ({context})");
        if (!expected)
            return false;

        string detail = $"{context}; expected {expectedHit}, actual {actualHit}";
        actualHit.Distance.ShouldBe(expectedHit.Distance, 1e-4f, $"hit distance diverged ({detail})");
        actualHit.Normal.X.ShouldBe(expectedHit.Normal.X, 1e-3f, $"hit normal X diverged ({detail})");
        actualHit.Normal.Y.ShouldBe(expectedHit.Normal.Y, 1e-3f, $"hit normal Y diverged ({detail})");
        actualHit.Normal.Z.ShouldBe(expectedHit.Normal.Z, 1e-3f, $"hit normal Z diverged ({detail})");
        return true;
    }

    private static Aabb WorldBounds(IReadOnlyList<BrushPlacement> placements)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (BrushPlacement placement in placements)
        {
            Aabb b = placement.WorldBounds;
            min = Vector3.Min(min, b.Min);
            max = Vector3.Max(max, b.Max);
        }
        return new Aabb(min, max);
    }

    // Structural tree equality, node for node: same topology, same leaf
    // solidity, same splitter planes (== on Plane compares all four floats;
    // a deterministic build reproduces them bitwise).
    private static void AssertStructurallyIdentical(BspTree expected, BspTree actual, string context)
    {
        var stack = new Stack<(BspNode E, BspNode A)>();
        stack.Push((expected.Root, actual.Root));
        while (stack.Count > 0)
        {
            (BspNode e, BspNode a) = stack.Pop();
            a.IsLeaf.ShouldBe(e.IsLeaf, $"tree topology diverged ({context})");
            if (e.IsLeaf)
            {
                a.IsSolid.ShouldBe(e.IsSolid, $"leaf solidity diverged ({context})");
                continue;
            }
            (a.Plane == e.Plane).ShouldBeTrue($"splitter plane diverged ({context}): {e.Plane} vs {a.Plane}");
            stack.Push((e.Front!, a.Front!));
            stack.Push((e.Back!, a.Back!));
        }
    }
}
