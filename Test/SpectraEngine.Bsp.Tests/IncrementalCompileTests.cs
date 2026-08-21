using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The incremental (previous-world) compile —
/// <see cref="CsgWorld.Build(System.Collections.Generic.IReadOnlyList{BrushPlacement}, System.Collections.Generic.IReadOnlyList{ChunkCoord}, CsgWorld)"/>
/// backed by <c>CsgIncrementalCompiler</c>. The load-bearing property is the
/// arc's oracle: an incrementally derived world must be indistinguishable
/// from a from-scratch compile of the same placements — same surface list and
/// mesh arrays bit for bit (the accepted gestures re-run the identical stage
/// cores), and same routed query answers as a monolithic tree — while its
/// per-cell artifacts outside the edit's neighbourhood are carried forward as
/// the SAME instances (the GPU swap path's "cell unchanged" signal). The
/// fallback tests then pin that structural edits (count changes, new overlap
/// pairs) transparently resolve through the validated path.
/// <para>
/// Every edit here produces a FRESH placement list (see <see cref="With"/>):
/// a compiled world owns the list it was built from — exactly the scene
/// pump's per-launch snapshot semantics — and mutating it afterwards would
/// violate that ownership, not exercise the compiler.
/// </para>
/// </summary>
public sealed class IncrementalCompileTests
{
    // ------------------------------------------------------------------
    // (a) No-op recompile: everything carries forward, including instances.
    // ------------------------------------------------------------------

    [Fact]
    public void Noop_recompile_shares_every_artifact_instance()
    {
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        // Fresh list instance, identical contents, empty dirty set — the
        // scene pump's unchanged-scene recompile.
        CsgWorld second = CsgWorld.Build([.. placements], [], first);

        second.CacheStats.ShouldBe(new CsgCacheStats(Hits: placements.Count, Misses: 0));
        second.WeldStats.ShouldBe(new CsgWeldStats(Reused: placements.Count, Welded: 0));
        second.BspStats.ShouldNotBeNull().Built.ShouldBe(0);
        second.MeshStats.ShouldNotBeNull().Built.ShouldBe(0);

        // The grid and every mesh artifact are the previous world's very
        // instances — nothing was copied, let alone rebuilt.
        ReferenceEquals(second.Chunks, first.Chunks).ShouldBeTrue("grid instance not shared");
        second.ChunkMeshes.Count.ShouldBe(first.ChunkMeshes.Count);
        for (int i = 0; i < first.ChunkMeshes.Count; i++)
            ReferenceEquals(second.ChunkMeshes[i], first.ChunkMeshes[i]).ShouldBeTrue($"chunk mesh #{i} not shared");

        second.SurfaceCount.ShouldBe(first.Surfaces.Count);
        ShouldBeIdenticalSurfaces(first.Surfaces, second.Surfaces, "noop");
    }

    // ------------------------------------------------------------------
    // (b) THE LOAD-BEARING TEST: a fixed-seed walk of single-part moves over
    // a multi-cell scattered world, every incrementally derived world
    // compared bit-for-bit against a from-scratch compile — and the walk is
    // long enough to drive the layered chunk map through real cell churn.
    // ------------------------------------------------------------------

    [Fact]
    public void Random_single_part_walk_stays_bit_identical_to_from_scratch_compiles()
    {
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld incremental = CsgWorld.Build(placements, previousCache: null);

        ulong state = 0xD1FF00D5EEDF00D1UL;
        float NextFloat01()
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (state >> 40) * (1.0f / (1 << 24));
        }

        int patchedCompiles = 0;
        for (int move = 0; move < 40; move++)
        {
            int index = (int)(NextFloat01() * placements.Count) % placements.Count;
            // Small nudges keep parts inside their own site (no overlap-pair
            // additions -> the patch path engages); larger vertical hops cross
            // the 32-unit cell boundary regularly, exercising cell insertion,
            // removal, and ownership transfer in the layered grid.
            var delta = new Vector3(
                (NextFloat01() - 0.5f) * 2f,
                (NextFloat01() - 0.5f) * (move % 3 == 0 ? 40f : 2f),
                (NextFloat01() - 0.5f) * 2f);

            BrushPlacement before = placements[index];
            BrushPlacement after = before with
            {
                Transform = before.Transform * Matrix4x4.CreateTranslation(delta),
            };
            placements = With(placements, index, after);

            CsgWorld previous = incremental;
            incremental = CsgWorld.Build(placements, DirtyUnion(before, after), incremental);
            CsgWorld scratch = CsgWorld.Build(placements);

            // Patch-path detection: the fallback rebuilds the grid from
            // scratch (all-new WorldChunk instances), the patch carries every
            // untouched cell's instance forward — so shared instances mean
            // this compile actually patched.
            foreach (WorldChunk chunk in previous.Chunks.OrderedChunks)
            {
                if (incremental.Chunks.TryGet(chunk.Coord, out WorldChunk now) && ReferenceEquals(now, chunk))
                {
                    patchedCompiles++;
                    break;
                }
            }

            ShouldBeIdenticalSurfaces(scratch.Surfaces, incremental.Surfaces, $"move #{move}");
            (float[] expectedVertices, uint[] expectedIndices) = scratch.BuildMesh();
            (float[] actualVertices, uint[] actualIndices) = incremental.BuildMesh();
            actualVertices.SequenceEqual(expectedVertices).ShouldBeTrue($"mesh vertices diverged at move #{move}");
            actualIndices.SequenceEqual(expectedIndices).ShouldBeTrue($"mesh indices diverged at move #{move}");

            // Grid parity, cell for cell: same occupied set, same membership
            // (the scratch grid is the ground truth).
            incremental.Chunks.Count.ShouldBe(scratch.Chunks.Count, $"cell count diverged at move #{move}");
            for (int c = 0; c < scratch.Chunks.OrderedChunks.Count; c++)
            {
                WorldChunk expected = scratch.Chunks.OrderedChunks[c];
                WorldChunk actual = incremental.Chunks.OrderedChunks[c];
                actual.Coord.ShouldBe(expected.Coord, $"cell order diverged at move #{move}");
                actual.ResidentBrushIndices.ShouldBe(expected.ResidentBrushIndices, $"residents diverged at move #{move}");
                actual.OwnedBrushIndices.ShouldBe(expected.OwnedBrushIndices, $"owners diverged at move #{move}");
                incremental.Chunks.TryGet(expected.Coord, out WorldChunk lookedUp).ShouldBeTrue();
                ReferenceEquals(lookedUp, actual).ShouldBeTrue("layered lookup disagrees with ordered enumeration");
            }

            // And whichever path ran, the compile stayed incremental in the
            // carve: a one-part move re-carves a handful of brushes at most.
            CsgCacheStats stats = incremental.CacheStats.ShouldNotBeNull();
            stats.Misses.ShouldBeLessThanOrEqualTo(6, $"move #{move} re-carved too much");
        }

        // The walk must actually exercise the patch path, not degrade to
        // permanent fallbacks that are trivially identical.
        patchedCompiles.ShouldBeGreaterThan(25, "the incremental path barely engaged");
    }

    // ------------------------------------------------------------------
    // (c) Routed queries on a patched world match a monolithic tree.
    // ------------------------------------------------------------------

    [Fact]
    public void Patched_world_answers_queries_like_a_monolithic_tree()
    {
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld world = CsgWorld.Build(placements, previousCache: null);

        // Nudge one part, patch, then interrogate the patched world.
        BrushPlacement before = placements[3];
        BrushPlacement after = before with
        {
            Transform = before.Transform * Matrix4x4.CreateTranslation(0.4f, 0.2f, -0.3f),
        };
        placements = With(placements, 3, after);
        world = CsgWorld.Build(placements, DirtyUnion(before, after), world);

        BspTree mono = BspTree.BuildFromSurfaces(world.Surfaces);
        Aabb bounds = WorldBounds(placements).Expanded(4f);
        Vector3 size = bounds.Size;
        float maxDistance = size.Length() + 8f;

        ulong state = 0xBADC0DE5EEDBEEFUL;
        float NextFloat01()
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (state >> 40) * (1.0f / (1 << 24));
        }

        int inside = 0;
        for (int i = 0; i < 20_000; i++)
        {
            Vector3 point = bounds.Min + size * new Vector3(NextFloat01(), NextFloat01(), NextFloat01());
            bool expected = mono.ContainsPoint(point);
            world.ContainsPoint(point).ShouldBe(expected, $"ContainsPoint diverged at {point}");
            if (expected)
                inside++;
        }
        inside.ShouldBeGreaterThan(0, "no probe landed in solid — vacuous oracle");

        int hits = 0;
        for (int r = 0; r < 2_000; r++)
        {
            Vector3 origin = bounds.Min + size * new Vector3(NextFloat01(), NextFloat01(), NextFloat01());
            Vector3 direction;
            while (true)
            {
                var v = new Vector3(NextFloat01() * 2f - 1f, NextFloat01() * 2f - 1f, NextFloat01() * 2f - 1f);
                float lengthSquared = v.LengthSquared();
                if (lengthSquared is > 1e-4f and <= 1f)
                {
                    direction = v / MathF.Sqrt(lengthSquared);
                    break;
                }
            }

            bool expected = mono.Raycast(origin, direction, maxDistance, out BspRaycastHit expectedHit);
            bool actual = world.Raycast(origin, direction, maxDistance, out BspRaycastHit actualHit);
            actual.ShouldBe(expected, $"Raycast hit flag diverged for ray #{r} from {origin} along {direction}");
            if (!expected)
                continue;
            hits++;
            actualHit.Distance.ShouldBe(expectedHit.Distance, 1e-4f, $"ray #{r} distance diverged");
        }
        hits.ShouldBeGreaterThan(0, "no ray hit solid — vacuous oracle");
    }

    // ------------------------------------------------------------------
    // (d) Instance carrying: an isolated move replaces exactly the touched
    // cells' artifacts; every other cell keeps its instances.
    // ------------------------------------------------------------------

    [Fact]
    public void Isolated_move_carries_every_untouched_cell_instance_forward()
    {
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        const int moved = 0;
        BrushPlacement before = placements[moved];
        BrushPlacement after = before with
        {
            Transform = before.Transform * Matrix4x4.CreateTranslation(0.3f, 0f, 0f),
        };
        List<BrushPlacement> edited = With(placements, moved, after);
        CsgWorld second = CsgWorld.Build(edited, DirtyUnion(before, after), first);

        // The ground truth for "which cells' artifacts must change" is the
        // classic validated path over the same edit and the same previous
        // caches: its mesh cache re-validates every cell, so the set of cells
        // it rebuilds is exactly the mesh-stage miss set. The patch path must
        // replace those coords and no others.
        CsgWorld classic = CsgWorld.Build(
            edited, dirtyCells: null, first.CompileCache, first.WeldCache, first.BspCache, first.MeshCache);
        HashSet<ChunkCoord> expectedReplaced = ReplacedCoords(first, classic);
        HashSet<ChunkCoord> actualReplaced = ReplacedCoords(first, second);

        actualReplaced.ShouldBe(expectedReplaced, ignoreOrder: true);
        actualReplaced.Count.ShouldBeGreaterThan(0, "the edit replaced no artifact — vacuous");
        actualReplaced.Count.ShouldBeLessThanOrEqualTo(4, "an isolated move rebuilt more than its neighbourhood");

        // The per-cell tree rebuild scope matches the classic path's BSP-cache
        // miss set exactly, and every cell the patch did NOT rebuild kept its
        // whole chunk instance (tree included).
        second.BspStats.ShouldBe(classic.BspStats);
        int carriedChunks = 0;
        foreach (WorldChunk chunk in first.Chunks.OrderedChunks)
        {
            if (second.Chunks.TryGet(chunk.Coord, out WorldChunk carried) && ReferenceEquals(carried, chunk))
                carriedChunks++;
        }
        CsgBspStats bspStats = second.BspStats.ShouldNotBeNull();
        carriedChunks.ShouldBe(second.Chunks.Count - bspStats.Built, "a non-rebuilt cell lost its chunk instance");
    }

    // The coords whose mesh artifact in `next` is NOT the same instance the
    // `previous` world carried for that cell (removed cells count as replaced).
    private static HashSet<ChunkCoord> ReplacedCoords(CsgWorld previous, CsgWorld next)
    {
        var previousByCoord = new Dictionary<ChunkCoord, ChunkMesh>();
        foreach (ChunkMesh mesh in previous.ChunkMeshes)
            previousByCoord[mesh.Coord] = mesh;

        var replaced = new HashSet<ChunkCoord>();
        foreach (ChunkMesh mesh in next.ChunkMeshes)
        {
            if (!previousByCoord.TryGetValue(mesh.Coord, out ChunkMesh? old) || !ReferenceEquals(old, mesh))
                replaced.Add(mesh.Coord);
            previousByCoord.Remove(mesh.Coord);
        }
        foreach (ChunkCoord coord in previousByCoord.Keys)
            replaced.Add(coord); // cells that lost their mesh
        return replaced;
    }

    // ------------------------------------------------------------------
    // (e) Determinism: identical inputs -> bit-identical artifacts.
    // ------------------------------------------------------------------

    [Fact]
    public void Identical_inputs_produce_bit_identical_patched_worlds()
    {
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        BrushPlacement before = placements[5];
        BrushPlacement after = before with
        {
            Transform = before.Transform * Matrix4x4.CreateTranslation(-0.7f, 0.1f, 0.2f),
        };
        List<BrushPlacement> edited = With(placements, 5, after);
        ChunkCoord[] dirty = DirtyUnion(before, after);

        CsgWorld a = CsgWorld.Build([.. edited], dirty, first);
        CsgWorld b = CsgWorld.Build([.. edited], dirty, first);

        (float[] verticesA, uint[] indicesA) = a.BuildMesh();
        (float[] verticesB, uint[] indicesB) = b.BuildMesh();
        verticesA.SequenceEqual(verticesB).ShouldBeTrue("patched compiles of identical inputs diverged (vertices)");
        indicesA.SequenceEqual(indicesB).ShouldBeTrue("patched compiles of identical inputs diverged (indices)");

        a.ChunkMeshes.Count.ShouldBe(b.ChunkMeshes.Count);
        for (int i = 0; i < a.ChunkMeshes.Count; i++)
        {
            a.ChunkMeshes[i].Coord.ShouldBe(b.ChunkMeshes[i].Coord);
            IReadOnlyList<ChunkSubmesh> subsA = a.ChunkMeshes[i].Submeshes;
            IReadOnlyList<ChunkSubmesh> subsB = b.ChunkMeshes[i].Submeshes;
            subsA.Count.ShouldBe(subsB.Count, $"per-cell submesh count diverged at {a.ChunkMeshes[i].Coord}");
            for (int s = 0; s < subsA.Count; s++)
            {
                subsA[s].Material.ShouldBe(subsB[s].Material);
                subsA[s].Vertices.SequenceEqual(subsB[s].Vertices).ShouldBeTrue(
                    $"per-cell vertices diverged at {a.ChunkMeshes[i].Coord}");
            }
        }
    }

    // ------------------------------------------------------------------
    // (f) Fallback triggers resolve through the validated path, correctly.
    // ------------------------------------------------------------------

    [Fact]
    public void Overlap_forming_move_falls_back_and_stays_correct()
    {
        // Drop one isolated part exactly onto another — a new overlap pair,
        // which the patch path must refuse (its clip-order position inside
        // existing carver sequences depends on global sweep history) and the
        // validated fallback must resolve.
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld firstWorld = CsgWorld.Build(placements, previousCache: null);

        BrushPlacement before = placements[10];
        BrushPlacement joined = before with { Transform = placements[0].Transform };
        List<BrushPlacement> joinedList = With(placements, 10, joined);

        CsgWorld incremental = CsgWorld.Build(joinedList, DirtyUnion(before, joined), firstWorld);
        CsgWorld scratch = CsgWorld.Build(joinedList);

        ShouldBeIdenticalSurfaces(scratch.Surfaces, incremental.Surfaces, "overlap-forming move");

        // And separating them again patches (pair removal is exact).
        List<BrushPlacement> separatedList = With(joinedList, 10, before);
        CsgWorld separated = CsgWorld.Build(separatedList, DirtyUnion(joined, before), incremental);
        ShouldBeIdenticalSurfaces(CsgWorld.Build(separatedList).Surfaces, separated.Surfaces, "separating move");
    }

    [Fact]
    public void Shared_neighbour_rank_crossing_falls_back_and_stays_identical()
    {
        // Regression: j is a slab overlapped by BOTH k and c; c and k never
        // overlap each other, and c's rank against j is stable — but dragging
        // c from x[2,4] to x[7,9] crosses k's min-X rank (5). A fresh sweep
        // then discovers j's pairs in the order [k, c] instead of the carried
        // [c, k], so a patched j re-carved with the carried order would emit
        // bitwise different fragmentation (vertex counts differ — the pinned
        // triangle-multiset oracle breaks, not just ordering). The two-hop
        // rank gate must refuse the patch and resolve through the validated
        // fallback, bit-identical to a from-scratch compile.
        List<BrushPlacement> placements =
        [
            // j: slab x[0,10] y[0,2] z[0,2] — min-X rank 0.
            new BrushPlacement(
                Brush.CreateBox(new Vector3(-5f, -1f, -1f), new Vector3(5f, 1f, 1f)),
                Matrix4x4.CreateTranslation(5f, 1f, 1f)),
            // k: box x[5,6] y[1,3] z[0,2] — cuts j's top face; min-X rank 5.
            new BrushPlacement(
                Brush.CreateBox(new Vector3(-0.5f, -1f, -1f), new Vector3(0.5f, 1f, 1f)),
                Matrix4x4.CreateTranslation(5.5f, 2f, 1f)),
            // c: box x[2,4] y[1,3] z[0,2] — also cuts j's top face; rank 2.
            new BrushPlacement(
                Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f)),
                Matrix4x4.CreateTranslation(3f, 2f, 1f)),
        ];
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        // Drag c to x[7,9]: still overlapping j (rank vs j unchanged), still
        // disjoint from k — every single-hop gate passes — but its min-X
        // rank (7) is now past k's (5).
        BrushPlacement before = placements[2];
        BrushPlacement after = before with { Transform = Matrix4x4.CreateTranslation(8f, 2f, 1f) };
        List<BrushPlacement> edited = With(placements, 2, after);

        CsgWorld incremental = CsgWorld.Build(edited, DirtyUnion(before, after), first);
        CsgWorld scratch = CsgWorld.Build(edited);

        ShouldBeIdenticalSurfaces(scratch.Surfaces, incremental.Surfaces, "shared-neighbour rank crossing");
        (float[] expectedVertices, uint[] expectedIndices) = scratch.BuildMesh();
        (float[] actualVertices, uint[] actualIndices) = incremental.BuildMesh();
        actualVertices.SequenceEqual(expectedVertices).ShouldBeTrue("mesh vertices diverged after the rank crossing");
        actualIndices.SequenceEqual(expectedIndices).ShouldBeTrue("mesh indices diverged after the rank crossing");

        // And the drift must not appear on LATER patched compiles either: a
        // follow-up rank-stable nudge of c patches on top of the fallback's
        // carry and must stay bit-identical to from-scratch.
        BrushPlacement nudged = after with { Transform = Matrix4x4.CreateTranslation(8.3f, 2f, 1f) };
        List<BrushPlacement> edited2 = With(edited, 2, nudged);
        CsgWorld incremental2 = CsgWorld.Build(edited2, DirtyUnion(after, nudged), incremental);
        ShouldBeIdenticalSurfaces(CsgWorld.Build(edited2).Surfaces, incremental2.Surfaces, "post-fallback nudge");
    }

    [Fact]
    public void Placement_count_change_falls_back_and_stays_correct()
    {
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        List<BrushPlacement> edited = [.. placements];
        edited.Add(new BrushPlacement(
            Brush.CreateBox(new Vector3(-1f), new Vector3(1f)),
            Matrix4x4.CreateTranslation(100f, 50f, -70f)));

        // The added part's footprint is the honest dirty set for an addition.
        BrushPlacement added = edited[^1];
        CsgWorld incremental = CsgWorld.Build(edited, ChunkGrid.ComputeFootprint(in added), first);
        CsgWorld scratch = CsgWorld.Build(edited);

        ShouldBeIdenticalSurfaces(scratch.Surfaces, incremental.Surfaces, "added part");
        incremental.Chunks.Count.ShouldBe(scratch.Chunks.Count);
    }

#if DEBUG
    [Fact]
    public void Debug_builds_reject_a_change_the_dirty_set_does_not_cover()
    {
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        // Move a part but hand in an empty dirty set — a trusted-diff
        // contract violation that must fail loudly in dev builds instead of
        // silently serving stale geometry.
        List<BrushPlacement> edited = With(placements, 2, placements[2] with
        {
            Transform = placements[2].Transform * Matrix4x4.CreateTranslation(1f, 0f, 0f),
        });

        Should.Throw<InvalidOperationException>(() => CsgWorld.Build(edited, [], first));
    }
#endif

    // ------------------------------------------------------------------
    // (g) The lazily derived classic caches remain valid seeds for the
    // validated path.
    // ------------------------------------------------------------------

    [Fact]
    public void Patched_worlds_lazy_caches_seed_the_validated_path_correctly()
    {
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld world = CsgWorld.Build(placements, previousCache: null);

        // One patched edit first, so the caches must be derived from carry.
        BrushPlacement before = placements[4];
        BrushPlacement after = before with
        {
            Transform = before.Transform * Matrix4x4.CreateTranslation(0.5f, 0f, 0.5f),
        };
        List<BrushPlacement> edited = With(placements, 4, after);
        world = CsgWorld.Build(edited, DirtyUnion(before, after), world);

        // Now compile through the classic validated overload, seeded by the
        // patched world's lazily materialized caches. The unchanged scene must
        // hit for every brush and reproduce the world bit for bit.
        CsgWorld revalidated = CsgWorld.Build(
            edited, dirtyCells: null, world.CompileCache, world.WeldCache, world.BspCache, world.MeshCache);

        revalidated.CacheStats.ShouldBe(new CsgCacheStats(Hits: edited.Count, Misses: 0));
        revalidated.WeldStats.ShouldBe(new CsgWeldStats(Reused: edited.Count, Welded: 0));
        ShouldBeIdenticalSurfaces(world.Surfaces, revalidated.Surfaces, "revalidation");
        revalidated.BspStats.ShouldNotBeNull().Built.ShouldBe(0);
        revalidated.MeshStats.ShouldNotBeNull().Built.ShouldBe(0);
    }

    // ------------------------------------------------------------------
    // (h) Cell-boundary crossings maintain the sparse grid exactly.
    // ------------------------------------------------------------------

    [Fact]
    public void Moving_a_part_across_a_cell_boundary_updates_the_grid_exactly()
    {
        List<BrushPlacement> placements = MakeScatteredWorld();
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        // Part 0 sits near the origin; jump it far into fresh, previously
        // unoccupied cells (and far from every other part).
        BrushPlacement before = placements[0];
        BrushPlacement after = before with
        {
            Transform = Matrix4x4.CreateTranslation(-200f, 90f, -140f),
        };
        List<BrushPlacement> edited = With(placements, 0, after);
        CsgWorld second = CsgWorld.Build(edited, DirtyUnion(before, after), first);
        CsgWorld scratch = CsgWorld.Build(edited);

        second.Chunks.Count.ShouldBe(scratch.Chunks.Count);
        foreach (WorldChunk expected in scratch.Chunks.OrderedChunks)
        {
            second.Chunks.TryGet(expected.Coord, out WorldChunk actual).ShouldBeTrue($"missing cell {expected.Coord}");
            actual.ResidentBrushIndices.ShouldBe(expected.ResidentBrushIndices);
            actual.OwnedBrushIndices.ShouldBe(expected.OwnedBrushIndices);
        }

        // The vacated cells answer empty through the layered lookup.
        foreach (ChunkCoord cell in ChunkGrid.ComputeFootprint(in before))
        {
            if (!scratch.Chunks.TryGet(cell, out _))
                second.Chunks.TryGet(cell, out _).ShouldBeFalse($"vacated cell {cell} still occupied");
        }

        ShouldBeIdenticalSurfaces(scratch.Surfaces, second.Surfaces, "cross-cell move");
    }

    // ------------------------------------------------------------------
    // Fixtures and helpers
    // ------------------------------------------------------------------

    // A deterministic scattered multi-cell world, openworld-style: parts on a
    // 5x5 site grid at spacing 8 with one vertical layer at y=40 (several
    // occupied cells along every axis), plus one touching pair and one
    // overlapping pair so carve interactions exist. Parts never touch across
    // sites (site jitter is bounded), so single-part nudges stay isolated.
    private static List<BrushPlacement> MakeScatteredWorld()
    {
        var placements = new List<BrushPlacement>();
        ulong state = 0x5CA77E12EDB0B5EDUL;
        float NextFloat01()
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (state >> 40) * (1.0f / (1 << 24));
        }

        for (int gx = 0; gx < 5; gx++)
        {
            for (int gz = 0; gz < 5; gz++)
            {
                var half = new Vector3(
                    0.5f + NextFloat01(), 0.5f + NextFloat01(), 0.5f + NextFloat01());
                float y = (gx + gz) % 2 == 0 ? 0f : 40f;
                placements.Add(new BrushPlacement(
                    Brush.CreateBox(-half, half),
                    Matrix4x4.CreateTranslation(
                        gx * 8f + NextFloat01(), y + NextFloat01(), gz * 8f + NextFloat01())));
            }
        }

        // A touching pair (coincident faces) and an overlapping pair, well
        // away from the site grid.
        var pairHalf = new Vector3(1f, 1f, 1f);
        placements.Add(new BrushPlacement(Brush.CreateBox(-pairHalf, pairHalf), Matrix4x4.CreateTranslation(60f, 0f, 0f)));
        placements.Add(new BrushPlacement(Brush.CreateBox(-pairHalf, pairHalf), Matrix4x4.CreateTranslation(62f, 0f, 0f)));
        placements.Add(new BrushPlacement(Brush.CreateBox(-pairHalf, pairHalf), Matrix4x4.CreateTranslation(60f, 0f, 20f)));
        placements.Add(new BrushPlacement(Brush.CreateBox(-pairHalf, pairHalf), Matrix4x4.CreateTranslation(61.2f, 0.4f, 20.6f)));
        return placements;
    }

    // A copy of `source` with slot `index` replaced. A compiled world owns
    // the placement list it was built from (the scene's snapshot-transfer
    // semantics), so every edit derives a fresh list.
    private static List<BrushPlacement> With(List<BrushPlacement> source, int index, BrushPlacement placement)
    {
        List<BrushPlacement> next = [.. source];
        next[index] = placement;
        return next;
    }

    // The scene's dirty rule for one changed placement: the union of its old
    // and new residency footprints, sorted.
    private static ChunkCoord[] DirtyUnion(in BrushPlacement before, in BrushPlacement after)
    {
        var cells = new HashSet<ChunkCoord>(ChunkGrid.ComputeFootprint(in before));
        cells.UnionWith(ChunkGrid.ComputeFootprint(in after));
        var dirty = new ChunkCoord[cells.Count];
        cells.CopyTo(dirty);
        Array.Sort(dirty);
        return dirty;
    }

    private static Aabb WorldBounds(List<BrushPlacement> placements)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (BrushPlacement p in placements)
        {
            Aabb b = p.WorldBounds;
            min = Vector3.Min(min, b.Min);
            max = Vector3.Max(max, b.Max);
        }
        return new Aabb(min, max);
    }

    // Bit-exact comparison (same rationale as CsgCompileCacheTests): both
    // paths run the same code over the same float inputs, so any drift is a
    // real divergence — a stale carried artifact — not FP noise.
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
}
