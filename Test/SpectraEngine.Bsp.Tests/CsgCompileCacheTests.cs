using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Incremental carve caching (<see cref="CsgCompileCache"/>). The load-bearing
/// property is bit-identity: however the cache is threaded through a sequence
/// of edits, every incremental compile must produce exactly the surface list
/// and mesh arrays a from-scratch compile of the same placements produces —
/// a cache hit may only ever substitute the polygons a fresh carve would have
/// computed. The invalidation tests then pin the cache's granularity: an edit
/// re-carves the edited brush and its overlap neighbours, nothing else.
/// </summary>
public sealed class CsgCompileCacheTests
{
    // ------------------------------------------------------------------
    // (a) THE LOAD-BEARING TEST: a fixed-seed random walk of single-brush
    // moves, each incremental compile compared bit-for-bit against scratch.
    // ------------------------------------------------------------------

    [Fact]
    public void Twenty_random_single_brush_moves_stay_bit_identical_to_from_scratch_compiles()
    {
        // 4x4x4 grid of overlapping cubes; the cache chains through every edit
        // exactly as the scene pump chains it through background compiles.
        List<BrushPlacement> placements = MakeGrid(4);
        CsgWorld incremental = CsgWorld.Build(placements, previousCache: null);
        incremental.CacheStats.ShouldBe(new CsgCacheStats(Hits: 0, Misses: placements.Count));

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

            // Chained through the chunked path exactly as the scene pump
            // chains it: the carve cache AND the per-cell weld cache both
            // travel from compile to compile. (This tight grid occupies a
            // single cell, so every brush is a weld candidate of every other
            // and weld reuse is legitimately zero here — the spread-world
            // reuse walk lives in ChunkWeldEquivalenceTests.)
            incremental = CsgWorld.Build(placements, dirtyCells: null, incremental.CompileCache, incremental.WeldCache);
            CsgWorld scratch = CsgWorld.Build(placements);

            // Surface list AND mesh arrays, bit for bit, after every move.
            ShouldBeIdenticalSurfaces(scratch.Surfaces, incremental.Surfaces, $"move #{move}");
            (float[] expectedVertices, uint[] expectedIndices) = scratch.BuildMesh();
            (float[] actualVertices, uint[] actualIndices) = incremental.BuildMesh();
            actualVertices.SequenceEqual(expectedVertices).ShouldBeTrue($"mesh vertices diverged at move #{move}");
            actualIndices.SequenceEqual(expectedIndices).ShouldBeTrue($"mesh indices diverged at move #{move}");

            // The walk must actually exercise the cache, not degrade to
            // full recompiles that are trivially identical.
            CsgCacheStats stats = incremental.CacheStats.ShouldNotBeNull();
            stats.Hits.ShouldBeGreaterThan(0, $"no cache hits at move #{move}");
            stats.Total.ShouldBe(placements.Count);
        }
    }

    // ------------------------------------------------------------------
    // (b) Granularity: one move re-carves exactly the moved brush plus its
    // overlap neighbours (old and new positions).
    // ------------------------------------------------------------------

    [Fact]
    public void Moving_one_brush_recarves_exactly_itself_and_its_overlap_neighbours()
    {
        // A row of 8 boxes overlapping only their immediate neighbours, so the
        // expected invalidation set is small and sharply defined.
        List<BrushPlacement> placements = MakeRow(8);
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        const int moved = 4;
        List<BrushPlacement> edited = [.. placements];
        edited[moved] = edited[moved] with
        {
            Transform = edited[moved].Transform * Matrix4x4.CreateTranslation(0.1f, 0f, 0f),
        };

        // Expected misses, computed from the broadphase itself rather than
        // hard-coded: the moved brush, plus every brush that overlapped it
        // before or after the move (their carver sequences reference its
        // matrix). Everything else must hit.
        HashSet<int> expectedMisses = [moved];
        expectedMisses.UnionWith(NeighborsOf(placements, moved));
        expectedMisses.UnionWith(NeighborsOf(edited, moved));
        expectedMisses.Count.ShouldBe(3); // itself + left + right — sanity-check the geometry

        CsgWorld second = CsgWorld.Build(edited, first.CompileCache);

        second.CacheStats.ShouldBe(new CsgCacheStats(
            Hits: placements.Count - expectedMisses.Count,
            Misses: expectedMisses.Count));
    }

    // ------------------------------------------------------------------
    // (c) Removal prunes the entry and invalidates former neighbours.
    // ------------------------------------------------------------------

    [Fact]
    public void Removing_a_brush_prunes_its_entry_and_invalidates_former_neighbours()
    {
        List<BrushPlacement> placements = MakeRow(5);
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        const int removed = 2;
        Brush removedBrush = placements[removed].Brush;
        List<BrushPlacement> edited = [.. placements];
        edited.RemoveAt(removed);

        CsgWorld second = CsgWorld.Build(edited, first.CompileCache);

        // Former neighbours (1 and 3) lost a carver — miss; the ends (0 and 4)
        // still see the identical carver sequence — hit. (Index shifts alone
        // don't invalidate: precedence and order are compared per carver, and
        // both are preserved for the survivors here.)
        second.CacheStats.ShouldBe(new CsgCacheStats(Hits: 2, Misses: 2));

        // The departed brush must not be retained by the produced cache.
        CsgCompileCache next = second.CompileCache.ShouldNotBeNull();
        next.Contains(removedBrush).ShouldBeFalse();
        next.Count.ShouldBe(edited.Count);

        // And the world is exactly the from-scratch world of the survivors.
        ShouldBeIdenticalSurfaces(CsgWorld.Build(edited).Surfaces, second.Surfaces, "removal");
    }

    // ------------------------------------------------------------------
    // (d) Addition invalidates the brushes the newcomer overlaps.
    // ------------------------------------------------------------------

    [Fact]
    public void Adding_a_brush_invalidates_its_new_neighbours()
    {
        List<BrushPlacement> placements = MakeRow(4);
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        // Append a box overlapping only the last one (appending keeps every
        // existing index — and so every precedence flag — unchanged).
        List<BrushPlacement> edited = [.. placements];
        var newcomer = new BrushPlacement(
            CreateUnitBox(),
            Matrix4x4.CreateTranslation(3 * RowSpacing + 1.0f, 0f, 0f));
        edited.Add(newcomer);

        int[] newcomerNeighbors = NeighborsOf(edited, edited.Count - 1);
        newcomerNeighbors.ShouldBe(new[] { 3 }); // sanity-check the geometry

        CsgWorld second = CsgWorld.Build(edited, first.CompileCache);

        // Misses: the newcomer (never cached) plus its new neighbours.
        second.CacheStats.ShouldBe(new CsgCacheStats(Hits: 3, Misses: 2));
        second.CompileCache.ShouldNotBeNull().Contains(newcomer.Brush).ShouldBeTrue();

        ShouldBeIdenticalSurfaces(CsgWorld.Build(edited).Surfaces, second.Surfaces, "addition");
    }

    // ------------------------------------------------------------------
    // (e) The cache-free paths are unaffected, and a first caching compile
    // matches them exactly.
    // ------------------------------------------------------------------

    [Fact]
    public void First_compile_with_no_cache_matches_the_cache_free_path_bit_for_bit()
    {
        List<BrushPlacement> placements = MakeGrid(3);

        CsgWorld cacheFree = CsgWorld.Build(placements);
        CsgWorld caching = CsgWorld.Build(placements, previousCache: null);

        // The cache-free overload neither consumes nor produces cache state.
        cacheFree.CompileCache.ShouldBeNull();
        cacheFree.CacheStats.ShouldBeNull();

        // The caching overload's first compile is all misses and captures
        // every brush — but the built world is indistinguishable.
        caching.CacheStats.ShouldBe(new CsgCacheStats(Hits: 0, Misses: placements.Count));
        caching.CompileCache.ShouldNotBeNull().Count.ShouldBe(placements.Count);

        ShouldBeIdenticalSurfaces(cacheFree.Surfaces, caching.Surfaces, "first compile");
        (float[] expectedVertices, uint[] expectedIndices) = cacheFree.BuildMesh();
        (float[] actualVertices, uint[] actualIndices) = caching.BuildMesh();
        actualVertices.SequenceEqual(expectedVertices).ShouldBeTrue();
        actualIndices.SequenceEqual(expectedIndices).ShouldBeTrue();
    }

    [Fact]
    public void Unchanged_placements_hit_for_every_brush_and_reproduce_the_world()
    {
        List<BrushPlacement> placements = MakeGrid(3);
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        CsgWorld second = CsgWorld.Build(placements, first.CompileCache);

        second.CacheStats.ShouldBe(new CsgCacheStats(Hits: placements.Count, Misses: 0));
        ShouldBeIdenticalSurfaces(first.Surfaces, second.Surfaces, "unchanged recompile");
    }

    [Fact]
    public void Duplicate_brush_references_across_placements_stay_correct()
    {
        // One Brush instance backing three placements: the cache can hold only
        // one entry per instance, so at most one occurrence can ever hit — the
        // others must miss and re-carve, never share the wrong surfaces.
        Brush shared = CreateUnitBox();
        List<BrushPlacement> placements =
        [
            new(shared, Matrix4x4.CreateTranslation(0f, 0f, 0f)),
            new(shared, Matrix4x4.CreateTranslation(1.5f, 0f, 0f)),
            new(shared, Matrix4x4.CreateTranslation(3.0f, 0f, 0f)),
        ];
        CsgWorld first = CsgWorld.Build(placements, previousCache: null);

        List<BrushPlacement> edited = [.. placements];
        edited[2] = edited[2] with { Transform = Matrix4x4.CreateTranslation(3.2f, 0f, 0f) };

        CsgWorld incremental = CsgWorld.Build(edited, first.CompileCache);
        CsgWorld scratch = CsgWorld.Build(edited);

        ShouldBeIdenticalSurfaces(scratch.Surfaces, incremental.Surfaces, "duplicate brush references");
    }

    // ------------------------------------------------------------------
    // Fixtures and helpers
    // ------------------------------------------------------------------

    private const float RowSpacing = 1.8f;

    private static Brush CreateUnitBox() =>
        Brush.CreateBox(new Vector3(-1f), new Vector3(1f));

    // k³ size-2 cubes at spacing 1.8: every axis-adjacent pair overlaps by 0.2
    // (the CsgBench grid), so single-brush moves genuinely re-carve regions.
    private static List<BrushPlacement> MakeGrid(int k)
    {
        var list = new List<BrushPlacement>(k * k * k);
        for (int x = 0; x < k; x++)
            for (int y = 0; y < k; y++)
                for (int z = 0; z < k; z++)
                    list.Add(new BrushPlacement(
                        CreateUnitBox(),
                        Matrix4x4.CreateTranslation(x * RowSpacing, y * RowSpacing, z * RowSpacing)));
        return list;
    }

    // A 1D row of size-2 cubes at spacing 1.8: each overlaps only its
    // immediate neighbours, giving invalidation tests a crisp expected set.
    private static List<BrushPlacement> MakeRow(int count)
    {
        var list = new List<BrushPlacement>(count);
        for (int i = 0; i < count; i++)
            list.Add(new BrushPlacement(
                CreateUnitBox(), Matrix4x4.CreateTranslation(i * RowSpacing, 0f, 0f)));
        return list;
    }

    // The broadphase's own answer for one brush's overlap neighbours — the
    // same source of truth the carve uses, so expectations track geometry
    // instead of hard-coded adjacency.
    private static int[] NeighborsOf(List<BrushPlacement> placements, int index)
    {
        var bounds = new Aabb[placements.Count];
        for (int i = 0; i < bounds.Length; i++)
            bounds[i] = placements[i].WorldBounds;
        return BrushBroadphase.FindOverlaps(bounds)[index];
    }

    // Bit-exact comparison (same rationale as PlacementEquivalenceTests): both
    // paths run the same code over the same float inputs, so any drift is a
    // real divergence — a stale cache hit — not FP noise.
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
