using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Spectra.Kitchen.Maps;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Maps.Compiled;
using SpectraEngine.Core.Scene;

// ============================================================================
// CsgBench — static-world compile and BSP query benchmark.
//
// Times every pipeline phase (Csg.Carve → VertexSnapper.Snap →
// TJunctionWelder.Weld → BspTree.BuildFromSurfaces → CsgWorld.BuildMesh) over
// synthetic scenes, cross-checked against the end-to-end CsgWorld.Build path.
// Median of 3 timed reps (the perf-gate protocol) after two untimed warmups,
// forced GC between reps, and a managed-allocation delta per configuration.
// The run header prints the measurement protocol and JIT configuration —
// phase medians swing several-fold under different JIT conditions (tiering
// alone was observed to move carve medians ~5x), so recorded baselines are
// only comparable to runs whose protocol lines match. The QUERY scenario is
// the regression guard for BSP splitter and query-routing work (queries go
// through the routed per-cell API since W3): tree SHAPE may change, but the
// inside/hit checksums must stay identical. The INCREMENTAL scenario compares
// the full-recompile cost of a one-brush edit against the incremental path
// through CsgCompileCache, and cross-checks that both produce bit-identical
// mesh arrays. The OPENWORLD scenario is the open-world pillar's proof:
// scattered parts worlds at 1k/10k/50k brushes (constant density, half the
// world around +8000 units to exercise position-independent precision), with
// the HEADLINE number being the full-cache-carry recompile cost of moving ONE
// part — which must stay flat as the world grows — plus the validated
// fallback floor of ADDING one part (a placement-count change takes the
// validated caching path, whose all-hit validation cost is O(world) by
// design and must stay visible to the perf gate), plus the routed
// point->cell / ray->DDA query rates over the 10k world. The BAKE scenario is
// the cook's side of the same question: it splits a clean map cook into the
// compile (cache-free CsgWorld.Build plus the BSP flatten) and the serialize
// (the ScmapBuilder tables and the CMSH/CBSP blobs), times the two halves
// independently, and asserts that the serializer stays a small fraction of the
// compile it writes out.
//
// Usage:
//   dotnet run -c Release --project Benchmarks/CsgBench                # all scenarios
//   dotnet run -c Release --project Benchmarks/CsgBench -- grid query  # a subset
//
// Filters match scenario-name prefixes: grid | floorplan | tower | query |
// incremental | openworld | bake (so "floor" and "incr" work too).
// ============================================================================

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// Timed repetitions per configuration; the reported value is the median.
// Pinned to 3 — the perf-gate protocol — so phase medians recorded as
// baselines are apples-to-apples with gate runs.
const int TimedReps = 3;

string[] scenarios = ["grid", "floorplan", "tower", "query", "incremental", "openworld", "bake"];
foreach (string arg in args)
{
    if (!scenarios.Any(s => s.StartsWith(arg, StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine($"Unknown scenario filter '{arg}'. Filters (prefixes ok): {string.Join(" | ", scenarios)}");
        return 2;
    }
}

bool ShouldRun(string scenario) =>
    args.Length == 0 || args.Any(a => scenario.StartsWith(a, StringComparison.OrdinalIgnoreCase));

Console.WriteLine($"ProcessorCount: {Environment.ProcessorCount}");
Console.WriteLine($"GC server mode: {System.Runtime.GCSettings.IsServerGC}");

// The measurement protocol is part of every number this program prints.
// A baseline recorded without these lines cannot be audited later; a
// baseline recorded WITH them is only comparable to runs where they match.
// (The csproj sets TieredCompilation=false; the runtime knob is echoed here
// so a stray environment override is visible in the output it skewed.)
object? tieredKnob = AppContext.GetData("System.Runtime.TieredCompilation");
Console.WriteLine($"TieredCompilation: {tieredKnob ?? "runtime default (enabled)"}");
Console.WriteLine($"Protocol: median of {TimedReps} timed reps, 2 untimed warmups, forced blocking GC between reps");

if (ShouldRun("grid") || ShouldRun("floorplan") || ShouldRun("tower"))
{
    Console.WriteLine();
    Console.WriteLine("scenario   | brushes | surfaces | meshVerts | meshIdx  | carve ms | snap ms | weld ms | bsp ms  | mesh ms | sum ms  | e2e ms   | alloc MiB");
    Console.WriteLine("-----------|---------|----------|-----------|----------|----------|---------|---------|---------|---------|---------|----------|----------");
}

if (ShouldRun("grid"))
    foreach (int k in new[] { 2, 3, 4, 6, 8, 10, 13 })
        RunConfig($"grid k={k}", MakeGrid(k));

if (ShouldRun("floorplan"))
    foreach (int k in new[] { 5, 10, 20, 32, 48 })
        RunConfig($"floor k={k}", MakeFloorplan(k));

if (ShouldRun("tower"))
    foreach (int n in new[] { 2, 4, 8, 16, 32 })
        RunConfig($"tower N={n}", MakeTower(n));

if (ShouldRun("query"))
    RunQueryBench();

if (ShouldRun("incremental"))
    RunIncrementalBench();

if (ShouldRun("openworld"))
    RunOpenWorldBench();

if (ShouldRun("bake"))
    RunBakeBench();

return 0;

// =====================================================================
// Scenario generators
// =====================================================================

static List<BrushPlacement> MakeGrid(int k)
{
    // k*k*k cubes, size 2.0, spacing 1.8 -> each overlaps its 6 neighbors by 0.2.
    const float size = 2.0f, spacing = 1.8f, half = size * 0.5f;
    var list = new List<BrushPlacement>(k * k * k);
    for (int x = 0; x < k; x++)
        for (int y = 0; y < k; y++)
            for (int z = 0; z < k; z++)
            {
                var brush = Brush.CreateBox(new Vector3(-half), new Vector3(half));
                var t = Matrix4x4.CreateTranslation(x * spacing, y * spacing, z * spacing);
                list.Add(new BrushPlacement(brush, t));
            }
    return list;
}

static List<BrushPlacement> MakeFloorplan(int k)
{
    // k*k floor tiles 4x0.5x4 at spacing 3.9 (0.1 pairwise overlap) plus a
    // perimeter of wall boxes (one per perimeter tile, 4k walls total).
    const float tile = 4.0f, spacing = 3.9f, tHalf = tile * 0.5f;
    const float floorHalfH = 0.25f;
    const float wallH = 3.0f, wallHalfH = wallH * 0.5f, wallHalfT = 0.25f;

    var list = new List<BrushPlacement>(k * k + 4 * k);

    for (int i = 0; i < k; i++)
        for (int j = 0; j < k; j++)
        {
            var brush = Brush.CreateBox(
                new Vector3(-tHalf, -floorHalfH, -tHalf),
                new Vector3(tHalf, floorHalfH, tHalf));
            list.Add(new BrushPlacement(brush, Matrix4x4.CreateTranslation(i * spacing, 0f, j * spacing)));
        }

    float minEdge = -tHalf;                       // outer edge of tile row 0
    float maxEdge = (k - 1) * spacing + tHalf;    // outer edge of last tile row
    // Wall bottoms at y=0 -> overlap the floor slab (top at +0.25) by 0.25.
    float wallY = wallHalfH;

    for (int i = 0; i < k; i++)
    {
        // walls along Z-min and Z-max edges, running in X
        var wx = Brush.CreateBox(
            new Vector3(-tHalf, -wallHalfH, -wallHalfT),
            new Vector3(tHalf, wallHalfH, wallHalfT));
        list.Add(new BrushPlacement(wx, Matrix4x4.CreateTranslation(i * spacing, wallY, minEdge)));
        var wx2 = Brush.CreateBox(
            new Vector3(-tHalf, -wallHalfH, -wallHalfT),
            new Vector3(tHalf, wallHalfH, wallHalfT));
        list.Add(new BrushPlacement(wx2, Matrix4x4.CreateTranslation(i * spacing, wallY, maxEdge)));

        // walls along X-min and X-max edges, running in Z
        var wz = Brush.CreateBox(
            new Vector3(-wallHalfT, -wallHalfH, -tHalf),
            new Vector3(wallHalfT, wallHalfH, tHalf));
        list.Add(new BrushPlacement(wz, Matrix4x4.CreateTranslation(minEdge, wallY, i * spacing)));
        var wz2 = Brush.CreateBox(
            new Vector3(-wallHalfT, -wallHalfH, -tHalf),
            new Vector3(wallHalfT, wallHalfH, tHalf));
        list.Add(new BrushPlacement(wz2, Matrix4x4.CreateTranslation(maxEdge, wallY, i * spacing)));
    }

    return list;
}

static List<BrushPlacement> MakeTower(int n)
{
    // N size-2 cubes all overlapping the same region, each shifted 0.1 on Y —
    // the pathological deep-overlap case for the carve phase.
    var list = new List<BrushPlacement>(n);
    for (int i = 0; i < n; i++)
    {
        var brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
        list.Add(new BrushPlacement(brush, Matrix4x4.CreateTranslation(0f, i * 0.1f, 0f)));
    }
    return list;
}

// =====================================================================
// Compile benchmark
// =====================================================================

static void RunConfig(string name, List<BrushPlacement> placements)
{
    // Warmup: whole pipeline twice (phases + end-to-end), untimed. Two rounds
    // because the Parallel.For phases need the thread pool spun up before the
    // first timed rep, and one round is not always enough on wide machines.
    for (int warm = 0; warm < 2; warm++)
    {
        var s = Csg.Carve(placements);
        s = VertexSnapper.Snap(s);
        s = TJunctionWelder.Weld(s);
        _ = BspTree.BuildFromSurfaces(s);
        var w = CsgWorld.Build(placements);
        _ = w.BuildMesh();
    }

    const int reps = TimedReps;
    var carve = new double[reps];
    var snap = new double[reps];
    var weld = new double[reps];
    var bsp = new double[reps];
    var mesh = new double[reps];
    var e2e = new double[reps];
    int surfaces = 0, meshVerts = 0, meshIdx = 0;

    for (int r = 0; r < reps; r++)
    {
        Collect();

        var sw = Stopwatch.StartNew();
        Polygon[] carved = Csg.Carve(placements);
        sw.Stop(); carve[r] = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        Polygon[] snapped = VertexSnapper.Snap(carved);
        sw.Stop(); snap[r] = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        Polygon[] welded = TJunctionWelder.Weld(snapped);
        sw.Stop(); weld[r] = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        BspTree tree = BspTree.BuildFromSurfaces(welded);
        sw.Stop(); bsp[r] = sw.Elapsed.TotalMilliseconds;

        // End-to-end cross-check (carve+snap+weld+bsp inside CsgWorld.Build).
        Collect();
        sw.Restart();
        CsgWorld world = CsgWorld.Build(placements);
        sw.Stop(); e2e[r] = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        (float[] verts, uint[] idx) = world.BuildMesh();
        sw.Stop(); mesh[r] = sw.Elapsed.TotalMilliseconds;

        surfaces = world.Surfaces.Count;
        meshVerts = verts.Length / 8;
        meshIdx = idx.Length;
        GC.KeepAlive(tree);
    }

    // Managed-allocation delta for one full compile (Build + BuildMesh),
    // measured outside the timed reps so the accounting itself is not timed.
    Collect();
    long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
    {
        var w = CsgWorld.Build(placements);
        _ = w.BuildMesh();
    }
    long allocBytes = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;

    Collect();

    double carveMed = Median(carve), snapMed = Median(snap), weldMed = Median(weld);
    double bspMed = Median(bsp), meshMed = Median(mesh), e2eMed = Median(e2e);
    double sum = carveMed + snapMed + weldMed + bspMed + meshMed;

    Console.WriteLine(
        $"{name,-10} | {placements.Count,7} | {surfaces,8} | {meshVerts,9} | {meshIdx,8} | " +
        $"{carveMed,8:F2} | {snapMed,7:F2} | {weldMed,7:F2} | {bspMed,7:F2} | {meshMed,7:F2} | {sum,7:F2} | {e2eMed,8:F2} | {allocBytes / (1024.0 * 1024.0),8:F1}");
}

// =====================================================================
// Query benchmark — the regression guard for BSP splitter changes.
// =====================================================================

static void RunQueryBench()
{
    Console.WriteLine();
    Console.WriteLine("QUERY (floorplan k=20) — splitter/routing changes may reshape the per-cell trees; the checksums must not change.");

    List<BrushPlacement> placements = MakeFloorplan(20);
    CsgWorld world = CsgWorld.Build(placements);

    // Queries route to per-cell BSP trees since W3; the stats aggregate every
    // occupied cell's tree (resident surfaces appear in several cells' trees,
    // so node totals are not comparable to pre-W3 global-tree baselines — the
    // semantic checksums below are the values that must not move).
    TreeStats stats = MeasureCellTrees(world);
    Console.WriteLine(
        $"  trees: {world.Chunks.Count:N0} cells, {stats.Nodes:N0} nodes, {stats.Leaves:N0} leaves, " +
        $"avg leaf depth {stats.AvgLeafDepth:F1}, max depth {stats.MaxDepth}");

    // Query volume: the brush bounds plus a margin, so points land inside
    // solid, in enclosed empty space, and outside the world.
    Aabb bounds = WorldBounds(placements).Expanded(2f);
    Vector3 min = bounds.Min, size = bounds.Size;

    const int PointCount = 1_000_000;
    const int RayCount = 100_000;
    const float RayMaxDistance = 40f;

    // Deterministic inputs from a fixed-seed LCG (System.Random's algorithm is
    // not guaranteed stable across runtimes), pre-generated so the timed loops
    // measure pure query cost.
    var lcg = new Lcg(0x5EEDC0DE12345678UL);
    var points = new Vector3[PointCount];
    for (int i = 0; i < points.Length; i++)
        points[i] = min + size * new Vector3(lcg.NextFloat01(), lcg.NextFloat01(), lcg.NextFloat01());

    var origins = new Vector3[RayCount];
    var directions = new Vector3[RayCount];
    for (int i = 0; i < RayCount; i++)
    {
        origins[i] = min + size * new Vector3(lcg.NextFloat01(), lcg.NextFloat01(), lcg.NextFloat01());
        directions[i] = lcg.NextDirection();
    }

    (double pointSeconds, long inside) = MeasurePointQueries(world, points);
    Console.WriteLine(
        $"  ContainsPoint: {PointCount:N0} calls in {pointSeconds * 1000:F1} ms -> " +
        $"{PointCount / pointSeconds / 1e6:F2} M calls/s | checksum inside={inside:N0} ({100.0 * inside / PointCount:F2} %)");

    (double raySeconds, long hits) = MeasureRayQueries(world, origins, directions, RayMaxDistance);
    Console.WriteLine(
        $"  Raycast:       {RayCount:N0} calls in {raySeconds * 1000:F1} ms -> " +
        $"{RayCount / raySeconds / 1e6:F2} M calls/s | checksum hits={hits:N0} ({100.0 * hits / RayCount:F2} %)");
}

// Times ContainsPoint over pre-generated inputs — one untimed warmup pass
// fixes the checksum, then the standard median-of-reps protocol with forced
// GC between reps. Shared by the QUERY scenario and OPENWORLD's query section.
static (double Seconds, long Inside) MeasurePointQueries(CsgWorld world, Vector3[] points)
{
    long inside = 0;
    for (int i = 0; i < points.Length; i++)       // warmup, also fixes the checksum
        if (world.ContainsPoint(points[i])) inside++;

    var elapsed = new double[TimedReps];
    for (int r = 0; r < TimedReps; r++)
    {
        Collect();
        long count = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < points.Length; i++)
            if (world.ContainsPoint(points[i])) count++;
        sw.Stop();
        elapsed[r] = sw.Elapsed.TotalSeconds;
        if (count != inside)
            Console.WriteLine($"  WARNING: ContainsPoint checksum unstable across reps ({count} vs {inside})!");
    }
    return (Median(elapsed), inside);
}

// Raycast counterpart of MeasurePointQueries — same protocol, same sharing.
static (double Seconds, long Hits) MeasureRayQueries(
    CsgWorld world, Vector3[] origins, Vector3[] directions, float maxDistance)
{
    long hits = 0;
    for (int i = 0; i < origins.Length; i++)      // warmup, also fixes the checksum
        if (world.Raycast(origins[i], directions[i], maxDistance, out _)) hits++;

    var elapsed = new double[TimedReps];
    for (int r = 0; r < TimedReps; r++)
    {
        Collect();
        long count = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < origins.Length; i++)
            if (world.Raycast(origins[i], directions[i], maxDistance, out _)) count++;
        sw.Stop();
        elapsed[r] = sw.Elapsed.TotalSeconds;
        if (count != hits)
            Console.WriteLine($"  WARNING: Raycast checksum unstable across reps ({count} vs {hits})!");
    }
    return (Median(elapsed), hits);
}

// Aggregates the structural stats of every occupied cell's tree — the routed
// counterpart of the old single-tree measurement.
static TreeStats MeasureCellTrees(CsgWorld world)
{
    int nodes = 0, leaves = 0, maxDepth = 0;
    double leafDepthSum = 0;
    foreach (WorldChunk chunk in world.Chunks.OrderedChunks)
    {
        TreeStats s = MeasureTree(chunk.Bsp);
        nodes += s.Nodes;
        leaves += s.Leaves;
        if (s.MaxDepth > maxDepth) maxDepth = s.MaxDepth;
        leafDepthSum += s.AvgLeafDepth * s.Leaves;
    }
    return new TreeStats(nodes, leaves, maxDepth, leaves == 0 ? 0.0 : leafDepthSum / leaves);
}

// Walks the whole tree once with an explicit stack (depth can exceed what
// comfortable recursion allows on degenerate splitter orders).
static TreeStats MeasureTree(BspTree tree)
{
    int nodes = 0, leaves = 0, maxDepth = 0;
    long leafDepthSum = 0;

    var stack = new Stack<(BspNode Node, int Depth)>();
    stack.Push((tree.Root, 0));
    while (stack.Count > 0)
    {
        (BspNode node, int depth) = stack.Pop();
        nodes++;
        if (node.IsLeaf)
        {
            leaves++;
            leafDepthSum += depth;
            if (depth > maxDepth) maxDepth = depth;
        }
        else
        {
            stack.Push((node.Front!, depth + 1));
            stack.Push((node.Back!, depth + 1));
        }
    }

    return new TreeStats(nodes, leaves, maxDepth, leaves == 0 ? 0.0 : (double)leafDepthSum / leaves);
}

static Aabb WorldBounds(List<BrushPlacement> placements)
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

// =====================================================================
// Incremental benchmark — a one-brush edit, full recompile vs the carve cache.
// =====================================================================

static void RunIncrementalBench()
{
    Console.WriteLine();
    Console.WriteLine("INCREMENTAL (grid k=10) — recompile after translating ONE center brush by 0.3.");

    const int K = 10;
    List<BrushPlacement> placements = MakeGrid(K);

    // The editor starting state: a fully compiled world already exists —
    // built through the caching path so its per-brush carve results seed the
    // cache the edited recompile consumes.
    CsgWorld before = CsgWorld.Build(placements, previousCache: null);
    _ = before.BuildMesh();
    CsgCompileCache cache = before.CompileCache!;

    // Nudge the center brush 0.3 on X — the canonical "user drags one brush"
    // edit that incremental compilation must make cheap. Placements are
    // immutable, so the edit is a list copy with one element replaced.
    int center = (K / 2) * K * K + (K / 2) * K + (K / 2);
    var edited = new List<BrushPlacement>(placements);
    edited[center] = edited[center] with
    {
        Transform = edited[center].Transform * Matrix4x4.CreateTranslation(0.3f, 0f, 0f),
    };

    const int reps = TimedReps;

    // --- FULL recompile: the cache-free baseline the cache is measured against.
    for (int warm = 0; warm < 2; warm++)
    { // warmup, untimed
        CsgWorld w = CsgWorld.Build(edited);
        _ = w.BuildMesh();
    }

    var full = new double[reps];
    for (int r = 0; r < reps; r++)
    {
        Collect();
        var sw = Stopwatch.StartNew();
        CsgWorld w = CsgWorld.Build(edited);
        (float[] verts, uint[] idx) = w.BuildMesh();
        sw.Stop();
        full[r] = sw.Elapsed.TotalMilliseconds;
        GC.KeepAlive(verts);
        GC.KeepAlive(idx);
    }

    Collect();
    long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
    {
        CsgWorld w = CsgWorld.Build(edited);
        _ = w.BuildMesh();
    }
    long fullAllocBytes = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;

    // --- CACHED recompile through the real CsgCompileCache. The cache is
    // immutable once built, so every rep consumes the same instance (each rep
    // produces, and discards, a fresh successor cache — exactly what one
    // editor edit does).
    for (int warm = 0; warm < 2; warm++)
    { // warmup, untimed
        CsgWorld w = CsgWorld.Build(edited, cache);
        _ = w.BuildMesh();
    }

    var cachedCarve = new double[reps];
    var cachedTotal = new double[reps];
    CsgCacheStats stats = default;
    for (int r = 0; r < reps; r++)
    {
        Collect();
        var sw = Stopwatch.StartNew();
        Polygon[] carved = Csg.Carve(edited, cache, out _, out stats);
        sw.Stop();
        cachedCarve[r] = sw.Elapsed.TotalMilliseconds;
        GC.KeepAlive(carved);

        Collect();
        sw.Restart();
        CsgWorld w = CsgWorld.Build(edited, cache);
        (float[] verts, uint[] idx) = w.BuildMesh();
        sw.Stop();
        cachedTotal[r] = sw.Elapsed.TotalMilliseconds;
        GC.KeepAlive(verts);
        GC.KeepAlive(idx);
    }

    Collect();
    allocBefore = GC.GetTotalAllocatedBytes(precise: true);
    {
        CsgWorld w = CsgWorld.Build(edited, cache);
        _ = w.BuildMesh();
    }
    long cachedAllocBytes = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;

    // Correctness cross-check: the cached recompile must be bit-identical to
    // the full recompile of the same placements (float.Equals semantics).
    {
        (float[] fullVerts, uint[] fullIdx) = CsgWorld.Build(edited).BuildMesh();
        (float[] cachedVerts, uint[] cachedIdx) = CsgWorld.Build(edited, cache).BuildMesh();
        if (!cachedVerts.SequenceEqual(fullVerts) || !cachedIdx.SequenceEqual(fullIdx))
            Console.WriteLine("  WARNING: cached recompile diverged from the full recompile!");
    }

    Console.WriteLine(
        $"  full recompile:   {Median(full),6:F2} ms | alloc {fullAllocBytes / (1024.0 * 1024.0),5:F1} MiB");
    Console.WriteLine(
        $"  cached recompile: {Median(cachedTotal),6:F2} ms | alloc {cachedAllocBytes / (1024.0 * 1024.0),5:F1} MiB | " +
        $"carve {Median(cachedCarve):F2} ms | {stats.Hits} hits / {stats.Misses} misses");
    GC.KeepAlive(before);
}

// =====================================================================
// Open-world benchmark — the open-world pillar's proof. Scattered "parts"
// worlds at three sizes with constant density; the headline is the cost of
// recompiling after moving ONE part with the full cache carry, which must be
// a function of the part's neighbourhood, not of world size.
// =====================================================================

static void RunOpenWorldBench()
{
    Console.WriteLine();
    Console.WriteLine("OPENWORLD — scattered parts at constant density (85% isolated / 10% touching pairs / 5% overlapping clusters),");
    Console.WriteLine("half of every world placed around +8,000 units (position-independent precision). HEADLINE: 'edit' columns —");
    Console.WriteLine("recompile after moving ONE isolated part by 0.3 with the previous-world carry (incremental compile), no monolithic mesh.");
    Console.WriteLine("'add' is the OTHER common gesture: appending one isolated part, which the patch path refuses (count change) and the");
    Console.WriteLine("validated caching fallback resolves — its O(world) validation floor is a real per-placement cost, measured here.");
    Console.WriteLine("parts  | cells  | surfaces | initial ms | init MiB | edit ms | noop ms | add ms  | edit MiB | dirty | carveMiss | weld | bspBuilt | meshBuilt");
    Console.WriteLine("-------|--------|----------|------------|----------|---------|---------|---------|----------|-------|-----------|------|----------|----------");

    int[] sizes = [1_000, 10_000, 50_000];
    var editMedians = new double[sizes.Length];
    var noopMedians = new double[sizes.Length];
    var addMedians = new double[sizes.Length];
    CsgWorld? world10k = null;
    OpenWorld? open10k = null;

    for (int i = 0; i < sizes.Length; i++)
    {
        (editMedians[i], noopMedians[i], addMedians[i], CsgWorld world, OpenWorld open) = RunOpenWorldSize(sizes[i]);
        if (sizes[i] == 10_000)
        {
            // Kept alive for the routed-query section below — ~10k parts of
            // world data, cheap next to the 50k configuration's own peak.
            world10k = world;
            open10k = open;
        }
    }

    // The world-size-independence verdict — scoped to what it actually
    // proves: the footprint-stable one-part MOVE gesture at 50k vs 1k.
    // Medians of 3 reps jitter; 1.5x is the noise band that separates "flat
    // with jitter" from "scaling with world size" (a true O(world) stage
    // would show up as ~50x here, not ~1.5x). Add/remove gestures are NOT
    // covered by this verdict — they pay the validated fallback floor in the
    // 'add' column, which grows with the world by design.
    double ratio = editMedians[^1] / editMedians[0];
    Console.WriteLine(
        $"  verdict (MOVE gesture): one-part edit at 50k parts = {ratio:F2}x the edit at 1k parts -> " +
        (ratio <= 1.5
            ? "world-size independent (within noise)"
            : "NOT world-size independent — investigate before accepting this as a baseline"));

    // Attribution, honestly labeled: the no-op build short-circuits through
    // the patch path's changed-empty branch (every artifact carries forward
    // by reference — O(1), no grid re-bucketing, no cache validation), so it
    // measures the per-compile bookkeeping floor of the PATCH path only.
    // edit - noop is the edit-scoped stage work. The validated caching path's
    // own O(world) floor — what every placement-count change pays — is the
    // 'add' column above, not this number.
    Console.WriteLine(
        $"  attribution: patch-path no-op (changed-empty short-circuit) {string.Join(" / ", noopMedians.Select(m => $"{m:F2}"))} ms; " +
        $"edit-scoped work (edit - noop) {string.Join(" / ", sizes.Select((_, i) => $"{editMedians[i] - noopMedians[i]:F2}"))} ms; " +
        $"validated fallback floor (add one part) {string.Join(" / ", addMedians.Select(m => $"{m:F2}"))} ms " +
        "at 1k / 10k / 50k parts");

    RunOpenWorldQuery(world10k!, open10k!);
}

static (double EditMedianMs, double NoopMedianMs, double AddMedianMs, CsgWorld World, OpenWorld Open) RunOpenWorldSize(int parts)
{
    OpenWorld open = MakeOpenWorld(parts);
    List<BrushPlacement> placements = open.Placements;

    // --- Initial full compile through the caching path — how an editor
    // session starts, and the producer of the caches the edit consumes. One
    // untimed warmup per size (the scenario runs sizes ascending, so the code
    // paths are already hot from the smaller sizes; a second warmup at 50k
    // would only stretch the run).
    CsgWorld world = OpenWorldCompile(placements);

    var initial = new double[TimedReps];
    for (int r = 0; r < TimedReps; r++)
    {
        Collect();
        var sw = Stopwatch.StartNew();
        world = OpenWorldCompile(placements);
        sw.Stop();
        initial[r] = sw.Elapsed.TotalMilliseconds;
    }

    Collect();
    long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
    _ = OpenWorldCompile(placements);
    long initialAlloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
    Collect();

    // --- The headline edit: move one isolated part 0.3 on X — the canonical
    // "user drags one part" gesture. Placements are immutable, so the edit is
    // a list copy with one element replaced.
    BrushPlacement before = placements[open.EditIndex];
    BrushPlacement after = before with
    {
        Transform = before.Transform * Matrix4x4.CreateTranslation(0.3f, 0f, 0f),
    };
    var edited = new List<BrushPlacement>(placements);
    edited[open.EditIndex] = after;

    // Dirty cells exactly as the scene's footprint diff computes them: the
    // union of the moved part's old and new residency footprints, sorted.
    var dirtySet = new HashSet<ChunkCoord>(ChunkGrid.ComputeFootprint(in before));
    dirtySet.UnionWith(ChunkGrid.ComputeFootprint(in after));
    var dirtyCells = new ChunkCoord[dirtySet.Count];
    dirtySet.CopyTo(dirtyCells);
    Array.Sort(dirtyCells);

    // Deliberately NO monolithic BuildMesh in the timed section: the editor
    // path renders the per-cell meshes built inside Build and uploads only
    // the changed cells — a whole-world mesh flatten would be a synthetic
    // O(world) cost the engine no longer pays. The carry is the previous
    // WORLD (per-brush and per-cell state included), exactly what the scene
    // pump hands each background compile.
    //
    // An incremental edit compile costs ~0.1 ms — far below this machine's
    // scheduling/cache jitter for a single timed call — so each rep times a
    // BURST of back-to-back recompiles (the shape of a real drag: one compile
    // per frame) and reports the per-compile mean; the median-of-reps
    // protocol above is unchanged. Every burst iteration derives from the
    // same previous world, so each one is the identical measurement.
    const int BurstIterations = 16;
    for (int warm = 0; warm < 2; warm++)
        _ = CsgWorld.Build(edited, dirtyCells, world);

    var edit = new double[TimedReps];
    CsgWorld editedWorld = null!;
    for (int r = 0; r < TimedReps; r++)
    {
        Collect();
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < BurstIterations; k++)
            editedWorld = CsgWorld.Build(edited, dirtyCells, world);
        sw.Stop();
        edit[r] = sw.Elapsed.TotalMilliseconds / BurstIterations;
    }

    Collect();
    allocBefore = GC.GetTotalAllocatedBytes(precise: true);
    _ = CsgWorld.Build(edited, dirtyCells, world);
    long editAlloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
    Collect();

    // No-op recompile: identical placements, full carry, empty dirty set.
    // This short-circuits through the PATCH path's changed-empty branch —
    // every artifact carries forward by reference, no grid re-bucketing, no
    // cache validation, no flat-list assembly runs — so it measures only the
    // patch path's per-compile bookkeeping floor (scanning the empty dirty
    // set, wrapping the carried state in a world object). That floor must
    // NOT scale with world size; subtracting it from the edit time isolates
    // the edit-scoped stage work. It says NOTHING about the validated
    // fallback path's floor — that is the 'add' measurement below.
    for (int warm = 0; warm < 2; warm++)
        _ = CsgWorld.Build(placements, [], world);

    var noop = new double[TimedReps];
    for (int r = 0; r < TimedReps; r++)
    {
        Collect();
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < BurstIterations; k++)
            _ = CsgWorld.Build(placements, [], world);
        sw.Stop();
        noop[r] = sw.Elapsed.TotalMilliseconds / BurstIterations;
    }
    Collect();

    // ADD gesture: append one isolated part far from everything. The
    // placement count changes, so the patch path refuses the edit and the
    // compile falls back to the fully validated caching path — the same path
    // every part placement/deletion/reparent takes in the editor. Its cost
    // has an O(world) validation floor even with every cache hitting; that
    // floor is a real per-placement latency and must be visible to the perf
    // gate. `previous` is the PATCHED edited world, the editor's actual
    // mid-session state, so the warmup also pays (once) the lazy
    // classic-cache materialization a patched world defers; the timed reps
    // then measure the steady validated floor. No burst: this path is
    // milliseconds, far above scheduling jitter.
    var addedPlacements = new List<BrushPlacement>(edited)
    {
        new BrushPlacement(
            Brush.CreateBox(new Vector3(-1f), new Vector3(1f)),
            Matrix4x4.CreateTranslation(-400f, 120f, -400f)),
    };
    BrushPlacement addedPart = addedPlacements[^1];
    ChunkCoord[] addDirty = ChunkGrid.ComputeFootprint(in addedPart);

    for (int warm = 0; warm < 2; warm++)
        _ = CsgWorld.Build(addedPlacements, addDirty, editedWorld);

    var add = new double[TimedReps];
    for (int r = 0; r < TimedReps; r++)
    {
        Collect();
        var sw = Stopwatch.StartNew();
        _ = CsgWorld.Build(addedPlacements, addDirty, editedWorld);
        sw.Stop();
        add[r] = sw.Elapsed.TotalMilliseconds;
    }
    Collect();

    // Correctness spot-check at the smallest size only (the oracle suites pin
    // the equivalence exhaustively; this guards the benchmark's own wiring):
    // the cached edited world must match a cache-free build bit for bit.
    if (parts == 1_000)
    {
        (float[] freshVerts, uint[] freshIdx) = CsgWorld.Build(edited).BuildMesh();
        (float[] cachedVerts, uint[] cachedIdx) = editedWorld.BuildMesh();
        if (!cachedVerts.SequenceEqual(freshVerts) || !cachedIdx.SequenceEqual(freshIdx))
            Console.WriteLine("  WARNING: cached edit recompile diverged from the cache-free recompile!");
    }

    CsgCacheStats carveStats = editedWorld.CacheStats ?? default;
    CsgWeldStats weldStats = editedWorld.WeldStats ?? default;
    CsgBspStats bspStats = editedWorld.BspStats ?? default;
    CsgMeshStats meshStats = editedWorld.MeshStats ?? default;
    Console.WriteLine(
        $"{parts,6} | {world.Chunks.Count,6} | {world.Surfaces.Count,8} | {Median(initial),10:F1} | " +
        $"{initialAlloc / (1024.0 * 1024.0),8:F1} | {Median(edit),7:F2} | {Median(noop),7:F2} | {Median(add),7:F2} | " +
        $"{editAlloc / (1024.0 * 1024.0),8:F2} | " +
        $"{dirtyCells.Length,5} | {carveStats.Misses,9} | {weldStats.Welded,4} | {bspStats.Built,8} | {meshStats.Built,9}");

    return (Median(edit), Median(noop), Median(add), world, open);
}

// The initial-compile shape of an editor session: cache-producing (all four
// successor caches), no previous caches to consume.
static CsgWorld OpenWorldCompile(List<BrushPlacement> placements) =>
    CsgWorld.Build(
        placements, dirtyCells: null, previousCache: null,
        previousWeldCache: null, previousBspCache: null, previousMeshCache: null);

// Routed queries over the 10k openworld: points hash to their cell, rays walk
// cells via 3D-DDA. Samples alternate between the near-origin and +8,000
// regions, so the numbers cover both the dense-cell path and the far-from-
// origin precision pillar; the inter-region void is deliberately not sampled
// (it would only measure dictionary misses).
static void RunOpenWorldQuery(CsgWorld world, OpenWorld open)
{
    Console.WriteLine();
    Console.WriteLine("  QUERY (openworld 10k) — routed point->cell / ray->DDA; samples alternate near-origin and +8,000 regions.");

    Aabb near = open.NearBounds.Expanded(2f);
    Aabb far = open.FarBounds.Expanded(2f);

    const int PointCount = 1_000_000;
    const int RayCount = 100_000;
    const float RayMaxDistance = 40f;

    var lcg = new Lcg(0xB0B0F0FA57F00D5EUL);
    var points = new Vector3[PointCount];
    for (int i = 0; i < points.Length; i++)
    {
        Aabb box = (i & 1) == 0 ? near : far;
        points[i] = box.Min + box.Size * new Vector3(lcg.NextFloat01(), lcg.NextFloat01(), lcg.NextFloat01());
    }

    var origins = new Vector3[RayCount];
    var directions = new Vector3[RayCount];
    for (int i = 0; i < RayCount; i++)
    {
        Aabb box = (i & 1) == 0 ? near : far;
        origins[i] = box.Min + box.Size * new Vector3(lcg.NextFloat01(), lcg.NextFloat01(), lcg.NextFloat01());
        directions[i] = lcg.NextDirection();
    }

    (double pointSeconds, long inside) = MeasurePointQueries(world, points);
    Console.WriteLine(
        $"  ContainsPoint: {PointCount:N0} calls in {pointSeconds * 1000:F1} ms -> " +
        $"{PointCount / pointSeconds / 1e6:F2} M calls/s | checksum inside={inside:N0} ({100.0 * inside / PointCount:F2} %)");

    (double raySeconds, long hits) = MeasureRayQueries(world, origins, directions, RayMaxDistance);
    Console.WriteLine(
        $"  Raycast:       {RayCount:N0} calls in {raySeconds * 1000:F1} ms -> " +
        $"{RayCount / raySeconds / 1e6:F2} M calls/s | checksum hits={hits:N0} ({100.0 * hits / RayCount:F2} %)");
}

// Deterministic scattered-parts world. Sites live on a fixed grid (spacing 20)
// split into two equal regions — one centred on the origin, one around
// +8,000 on X and Z — and every site's geometry is confined to its own square
// (jitter [5,11] plus a max construct reach of 6 keeps everything within
// [3,17] of a 20-unit site), so nothing ever touches across sites and the
// 85/10/5 mix is exact, not probabilistic. Per 100 parts: 85 isolated boxes,
// 5 touching pairs (coincident faces — the classic CSG case), and 1 cluster
// of 5 mutually overlapping boxes; part counts must be multiples of 200 so
// both the mix and the region split come out whole.
static OpenWorld MakeOpenWorld(int partCount)
{
    const int PartsPerBlock = 100;
    const int SitesPerBlock = 91;       // 85 isolated + 5 pairs + 1 cluster
    const int IsolatedPerBlock = 85;
    const int PairsPerBlock = 5;
    const float SiteSpacing = 20f;      // ~1.1 parts per 400 units^2, every size
    const float FarOffset = 8_000f;

    if (partCount % 200 != 0)
        throw new ArgumentException("openworld part counts must be multiples of 200", nameof(partCount));

    int totalSites = partCount / PartsPerBlock * SitesPerBlock;
    int sitesPerRegion = totalSites / 2;
    int sideSites = (int)MathF.Ceiling(MathF.Sqrt(sitesPerRegion));
    float nearOrigin = -sideSites * SiteSpacing * 0.5f;   // centre region A on the origin

    // The edited part: the isolated site nearest the middle of the near
    // region's site range — far from any pair/cluster, so the headline
    // measures the canonical isolated-part edit.
    int editSite = sitesPerRegion / 2;
    while (editSite % SitesPerBlock >= IsolatedPerBlock)
        editSite--;

    var lcg = new Lcg(0x9E3779B97F4A7C15UL);
    var placements = new List<BrushPlacement>(partCount);
    int editIndex = -1;
    var nearMin = new Vector3(float.MaxValue);
    var nearMax = new Vector3(float.MinValue);
    var farMin = new Vector3(float.MaxValue);
    var farMax = new Vector3(float.MinValue);

    for (int site = 0; site < totalSites; site++)
    {
        bool farRegion = site >= sitesPerRegion;
        int local = farRegion ? site - sitesPerRegion : site;
        float baseX = (farRegion ? FarOffset : nearOrigin) + local % sideSites * SiteSpacing;
        float baseZ = (farRegion ? FarOffset : nearOrigin) + local / sideSites * SiteSpacing;

        if (site == editSite)
            editIndex = placements.Count;

        float cx = baseX + 5f + lcg.NextFloat01() * 6f;
        float cz = baseZ + 5f + lcg.NextFloat01() * 6f;
        Vector3 halfA = RandomHalfExtent(ref lcg);
        float bottomA = lcg.NextFloat01() * 2f;
        var centerA = new Vector3(cx, bottomA + halfA.Y, cz);
        AddPart(centerA, halfA);

        int kind = site % SitesPerBlock;
        if (kind < IsolatedPerBlock)
        {
            // isolated: the one box above is the whole site
        }
        else if (kind < IsolatedPerBlock + PairsPerBlock)
        {
            // Touching pair: exactly coincident faces on the +X side, same
            // bottom so the y ranges (heights >= 1) always overlap.
            Vector3 halfB = RandomHalfExtent(ref lcg);
            AddPart(new Vector3(cx + halfA.X + halfB.X, bottomA + halfB.Y, cz), halfB);
        }
        else
        {
            // Cluster: four satellites overlapping the base box. Offsets
            // (0.3..0.9 per horizontal axis, 0..0.5 vertical) are strictly
            // below the minimum half-extent sum of 1.0, so every satellite
            // genuinely interpenetrates the base.
            for (int i = 0; i < 4; i++)
            {
                Vector3 h = RandomHalfExtent(ref lcg);
                float ox = (0.3f + lcg.NextFloat01() * 0.6f) * (i % 2 == 0 ? 1f : -1f);
                float oz = (0.3f + lcg.NextFloat01() * 0.6f) * (i < 2 ? 1f : -1f);
                float oy = lcg.NextFloat01() * 0.5f;
                AddPart(centerA + new Vector3(ox, oy, oz), h);
            }
        }

        void AddPart(Vector3 center, Vector3 half)
        {
            var brush = Brush.CreateBox(-half, half);
            placements.Add(new BrushPlacement(brush, Matrix4x4.CreateTranslation(center)));
            if (farRegion)
            {
                farMin = Vector3.Min(farMin, center - half);
                farMax = Vector3.Max(farMax, center + half);
            }
            else
            {
                nearMin = Vector3.Min(nearMin, center - half);
                nearMax = Vector3.Max(nearMax, center + half);
            }
        }
    }

    return new OpenWorld(placements, editIndex, new Aabb(nearMin, nearMax), new Aabb(farMin, farMax));

    // Half-extents in [0.5, 2.0] per axis — parts stay small next to the
    // 32-unit cells, like Roblox parts next to their world.
    static Vector3 RandomHalfExtent(ref Lcg lcg) => new(
        0.5f + lcg.NextFloat01() * 1.5f,
        0.5f + lcg.NextFloat01() * 1.5f,
        0.5f + lcg.NextFloat01() * 1.5f);
}

// =====================================================================
// Bake benchmark - what a CLEAN cook of a map costs, split into the two
// halves a cook pays and timed independently. The deliverable is the
// verdict: the serializer must stay a small fraction of the compile.
// =====================================================================

static void RunBakeBench()
{
    Console.WriteLine();
    Console.WriteLine("BAKE - one clean cook of a map, split into COMPILE and SERIALIZE.");
    Console.WriteLine("COMPILE   = cache-free CsgWorld.Build (the overload ScmapBake calls: no previous world, no carve cache,");
    Console.WriteLine("            because a bake must be a pure function of its source) plus BspFlattener.Flatten per cell.");
    Console.WriteLine("SERIALIZE = ScmapBuilder staging (AddNode per brush, AddChunk per cell) plus Build, which emits");
    Console.WriteLine("            STRT/ASTB/META/NODE/CHDR/CMSH/CBSP. The bake's own glue is counted HERE deliberately: it is");
    Console.WriteLine("            bookkeeping paid only because a file is being written, and putting it on this side can only");
    Console.WriteLine("            make the serializer look worse than it is.");
    Console.WriteLine("NEITHER HALF IS INFERRED BY SUBTRACTION. The compile's own cost swings by two orders of magnitude across");
    Console.WriteLine("these content sets, so a serialize time taken as total-minus-compile would be flattered by exactly the");
    Console.WriteLine("content that compiles slowest. A clean cook is legitimately O(world) and nothing here argues otherwise -");
    Console.WriteLine("the incremental story belongs to the cook cache and its own no-op-cook test. What this guards is the SHARE.");
    Console.WriteLine("content     | brushes | cells | surfaces | build ms | flat ms | compile ms | node ms | cell ms | emit ms | serial ms | file MiB | share");
    Console.WriteLine("------------|---------|-------|----------|----------|---------|------------|---------|---------|---------|-----------|----------|------");

    var results = new List<BakeResult>();

    foreach (int k in new[] { 4, 8, 13 })
        results.Add(RunBakeConfig($"grid k={k}", MakeGrid(k)));

    foreach (int parts in new[] { 1_000, 10_000, 50_000 })
        results.Add(RunBakeConfig($"open {parts / 1_000}k", MakeOpenWorld(parts).Placements));

    // The verdict, and it is deliberately NOT about the cook's absolute cost.
    // A clean cook is O(world) by design; what would be a defect is the writer
    // growing into a cost of its own beside the compile whose output it writes.
    // One third is the ceiling because the claim worth holding is that the
    // COMPILE STAYS AT LEAST THREE TIMES THE WRITER; the worst content set
    // measured on the development machine sits at 16 to 22% over five runs, so
    // the band is about 1.5x, which is the shape the openworld verdict uses too.
    // Two things break it and only one is a defect: a serializer that got
    // slower, and a compile that got faster without the writer following, which
    // is a real success and still wants a look.
    const double ShareCeiling = 0.33;

    BakeResult worst = results[0];
    foreach (BakeResult result in results)
        if (result.Share > worst.Share) worst = result;

    Console.WriteLine(
        $"  verdict (SERIALIZE share): worst case {worst.Name} at {worst.Share:P1} of its own compile -> " +
        (worst.Share <= ShareCeiling
            ? $"a small fraction of the compile (ceiling {ShareCeiling:P0})"
            : $"NOT a small fraction (ceiling {ShareCeiling:P0}) - the map writer has become a cost centre in its " +
              "own right, investigate before accepting this as a baseline"));

    // The second verdict, and it exists because the first one cannot see the
    // failure that actually happened here. A SHARE stays healthy while one of
    // its halves grows quadratically, as long as the compile beside it is
    // growing too - which over these content sets it is. So the per-CELL staging
    // cost is asserted directly against the two LARGEST worlds: a term that
    // scales with the world is loudest there, and the small-N configurations
    // measure a couple of microseconds in total and are mostly Stopwatch noise.
    // A pure per-call O(cells) term would make the per-cell cost track the cell
    // COUNT's own growth, which is printed beside it, so the two numbers
    // together say which shape it is. The ceiling sits between the two measured
    // worlds: 0.7 to 1.6x over five runs of the fixed writer, against 4.7x for
    // the linear scan `ScmapBuilder.AddChunk` used to do (1.5 to 7.0 us per cell
    // over this same pair) and 4.9x for the cell growth a pure per-world term
    // would reproduce exactly.
    const double CellCostCeiling = 2.5;

    BakeResult[] bySize = [.. results.OrderByDescending(r => r.Cells)];
    BakeResult largest = bySize[0], next = bySize[1];

    double costGrowth = largest.CostPerCellUs / next.CostPerCellUs;
    double cellGrowth = (double)largest.Cells / next.Cells;
    Console.WriteLine(
        $"  verdict (CELL staging): {largest.CostPerCellUs:F2} us per cell at {largest.Cells} cells = " +
        $"{costGrowth:F2}x the cost at {next.Cells}, over a {cellGrowth:F2}x bigger world -> " +
        (costGrowth <= CellCostCeiling
            ? $"flat per cell (ceiling {CellCostCeiling:F1}x), no term scaling with the world"
            : $"NOT flat per cell (ceiling {CellCostCeiling:F1}x) - the writer has a term that scales with the " +
              "world rather than with the cell, which a share cannot show while the compile grows too"));

    // Attribution, in the shape the openworld scenario reports its own. EMIT is
    // the byte-producing pass, so it is reported per MiB and is expected to be
    // flat; cell staging is per cell, and is what the verdict above asserts.
    Console.WriteLine(
        "  attribution: emit " +
        string.Join(" / ", results.Select(r => $"{r.FileMib / (r.EmitMs / 1000.0):F0}")) +
        " MiB/s; cell staging " +
        string.Join(" / ", results.Select(r => $"{r.CostPerCellUs:F2}")) +
        " us per cell, over " +
        string.Join(" / ", results.Select(r => r.Cells.ToString())) +
        " cells");
}

static BakeResult RunBakeConfig(string name, List<BrushPlacement> placements)
{
    // Node identity, name and local transform are gathered ONCE, outside every
    // timed section. A real bake reads them off scene nodes the binder already
    // built, so producing them here is fixture cost rather than cook cost. Ids
    // are derived from the index rather than drawn from Guid.NewGuid, so two
    // runs of this benchmark write the same bytes.
    var ids = new Guid[placements.Count];
    var names = new string[placements.Count];
    var transforms = new Transform[placements.Count];
    for (int i = 0; i < placements.Count; i++)
    {
        ids[i] = new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
        names[i] = $"brush{i}";
        transforms[i] = new Transform { Position = placements[i].Transform.Translation };
    }

    // Warmup: both halves, twice and untimed, for the same reason RunConfig
    // warms twice - the compile's Parallel.For phases need the thread pool spun
    // up, and one round is not always enough on a wide machine.
    for (int warm = 0; warm < 2; warm++)
    {
        CsgWorld warmWorld = CsgWorld.Build(placements);
        ScmapBuilder warmBuilder = new(name);
        StageNodes(warmBuilder, ids, names, transforms);
        StageCells(warmBuilder, warmWorld, FlattenCells(warmWorld));
        _ = warmBuilder.Build(UInt128.Zero, EngineInfo.MapFormatVersion);
    }

    const int reps = TimedReps;
    var build = new double[reps];
    var flatten = new double[reps];
    var node = new double[reps];
    var cell = new double[reps];
    var emit = new double[reps];
    int cells = 0, surfaces = 0;
    long fileBytes = 0;

    for (int r = 0; r < reps; r++)
    {
        Collect();

        var sw = Stopwatch.StartNew();
        CsgWorld world = CsgWorld.Build(placements);
        sw.Stop(); build[r] = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        FlatCell[] flat = FlattenCells(world);
        sw.Stop(); flatten[r] = sw.Elapsed.TotalMilliseconds;

        // The halves are separated by a collection so the writer is not timed
        // while paying off the compile's garbage, and so neither half's number
        // depends on where a gen0 happened to land in the other.
        Collect();

        ScmapBuilder builder = new(name);

        sw.Restart();
        StageNodes(builder, ids, names, transforms);
        sw.Stop(); node[r] = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        StageCells(builder, world, flat);
        sw.Stop(); cell[r] = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        byte[] file = builder.Build(UInt128.Zero, EngineInfo.MapFormatVersion);
        sw.Stop(); emit[r] = sw.Elapsed.TotalMilliseconds;

        cells = world.Chunks.Count;
        surfaces = world.Surfaces.Count;
        fileBytes = file.Length;
        GC.KeepAlive(file);
    }

    Collect();

    double buildMed = Median(build), flatMed = Median(flatten);
    double nodeMed = Median(node), cellMed = Median(cell), emitMed = Median(emit);
    double compile = buildMed + flatMed;
    double serialize = nodeMed + cellMed + emitMed;
    double fileMib = fileBytes / (1024.0 * 1024.0);

    Console.WriteLine(
        $"{name,-11} | {placements.Count,7} | {cells,5} | {surfaces,8} | {buildMed,8:F2} | {flatMed,7:F2} | " +
        $"{compile,10:F2} | {nodeMed,7:F2} | {cellMed,7:F2} | {emitMed,7:F2} | {serialize,9:F2} | " +
        $"{fileMib,8:F2} | {serialize / compile,5:P1}");

    return new BakeResult(name, cells, serialize / compile, cellMed, emitMed, fileMib);
}

// The BSP flatten, counted as COMPILE: it turns a live tree into the array the
// format stores, and the array it produces is a function of the TREE rather
// than of the file. Mirrors the flatten inside ScmapBake.WriteChunks, in the
// same OrderedChunks order the directory is laid out in, so the index it
// returns at is the index the staging pass reads it back at.
static FlatCell[] FlattenCells(CsgWorld world)
{
    IReadOnlyList<WorldChunk> cells = world.Chunks.OrderedChunks;
    var flat = new FlatCell[cells.Count];

    for (int i = 0; i < cells.Count; i++)
    {
        // A cell with no tree gets a null array and keeps the empty-leaf code,
        // which is a different answer from an empty array (a tree that is one
        // bare leaf) and the format tells them apart.
        flat[i] = cells[i].Bsp is { } tree
            ? new FlatCell(BspFlattener.Flatten(tree, out int root), root)
            : new FlatCell(null, FlatBspNode.EmptyLeaf);
    }

    return flat;
}

// The two staging passes MIRROR ScmapBake.WriteNodes/WriteChunks rather than
// calling them: the bake's own walk wants a MapDocument and a bound Scene,
// which these synthetic content sets do not have. Every builder call under them
// is the production one, and the two details that decide bytes are kept - a
// submesh row is an ASSET index and never a MaterialRef.Id, and the submeshes
// are sorted into ascending asset order rather than the ascending material id a
// ChunkMesh arrives in. They are separate functions so the table can report
// them separately: one is per brush and the other is per cell, and a single
// staging number would hide which of the two a change moved.
//
// Every placement here is a world brush, so every node is a brush baked into
// the chunks and no BRSH section is written: the default cook, without
// --keep-brush-source. All roots, because the content sets are flat lists,
// which also makes the pre-order invariant ParentIndex < SelfIndex trivial.
static void StageNodes(ScmapBuilder builder, Guid[] ids, string[] names, Transform[] transforms)
{
    for (int i = 0; i < ids.Length; i++)
    {
        builder.AddNode(new ScmapNodeSource(
            ids[i], names[i], -1, transforms[i], ScmapPayloadKind.StaticWorldBrush));
    }
}

static void StageCells(ScmapBuilder builder, CsgWorld world, FlatCell[] flat)
{
    var materials = new Dictionary<int, uint>();

    var meshes = new Dictionary<ChunkCoord, ChunkMesh>(world.ChunkMeshes.Count);
    for (int i = 0; i < world.ChunkMeshes.Count; i++)
        meshes[world.ChunkMeshes[i].Coord] = world.ChunkMeshes[i];

    IReadOnlyList<WorldChunk> cells = world.Chunks.OrderedChunks;
    for (int i = 0; i < cells.Count; i++)
    {
        WorldChunk cell = cells[i];
        meshes.TryGetValue(cell.Coord, out ChunkMesh? mesh);

        builder.AddChunk(new ScmapChunkSource(
            cell.Coord,

            // The cell's TRUE render bounds where there is geometry to bound;
            // a cell with no mesh is never culled and gets its own cube.
            mesh?.RenderBounds ?? cell.Coord.Bounds,
            BakeSubmeshes(mesh, materials, builder),
            flat[i].Nodes,
            flat[i].RootIndex));
    }
}

// ScmapBake.SubmeshesOf. Every face in these content sets names the default
// material, which has no asset row at all, so every submesh takes the
// NoAssetIndex sentinel and each cell emits exactly one - the shape a default
// brush world produces, and the reason the strict ascending-asset check inside
// AddChunk is never tripped here.
static ScmapSubmeshSource[]? BakeSubmeshes(
    ChunkMesh? mesh, Dictionary<int, uint> materials, ScmapBuilder builder)
{
    if (mesh is null || mesh.Submeshes.Count == 0) return null;

    var submeshes = new ScmapSubmeshSource[mesh.Submeshes.Count];
    for (int i = 0; i < submeshes.Length; i++)
    {
        ChunkSubmesh submesh = mesh.Submeshes[i];
        submeshes[i] = new ScmapSubmeshSource(
            BakeMaterialIndex(submesh.Material, materials, builder), submesh.Vertices, submesh.Indices);
    }

    Array.Sort(submeshes, static (a, b) => a.AssetIndex.CompareTo(b.AssetIndex));
    return submeshes;
}

// ScmapBake.AssetTable.Material: the default material names no path, so it has
// no row and the file says so with a sentinel rather than with row 0, which is
// a real asset.
static uint BakeMaterialIndex(MaterialRef material, Dictionary<int, uint> lookup, ScmapBuilder builder)
{
    if (material.IsDefault) return ScmapFormat.NoAssetIndex;
    if (lookup.TryGetValue(material.Id, out uint existing)) return existing;

    uint index = builder.AddMaterial(material);
    lookup[material.Id] = index;
    return index;
}

// =====================================================================
// Helpers
// =====================================================================

static void Collect()
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
}

static double Median(double[] values)
{
    var c = (double[])values.Clone();
    Array.Sort(c);
    return c[c.Length / 2];
}

/// <summary>Structural shape of a BSP tree, from one full walk.</summary>
internal readonly record struct TreeStats(int Nodes, int Leaves, int MaxDepth, double AvgLeafDepth);

/// <summary>
/// One cell's flattened solid-leaf tree, on its way from the compile half of a
/// bake to the serialize half. A null <paramref name="Nodes"/> means the cell
/// has no tree at all; an EMPTY array is a tree that is one bare leaf, and the
/// format tells the two apart, so the distinction is carried rather than
/// normalised away.
/// </summary>
internal readonly record struct FlatCell(FlatBspNode[]? Nodes, int RootIndex);

/// <summary>
/// One bake configuration's verdict inputs. <paramref name="CellMs"/> and
/// <paramref name="EmitMs"/> are carried separately from the share because they
/// scale against different quantities (cells and bytes), and a per-unit cost is
/// the only form in which a super-linear term is visible at all - a share can
/// stay flat while one of its halves is growing quadratically, as long as the
/// compile beside it is growing too.
/// </summary>
internal readonly record struct BakeResult(
    string Name, int Cells, double Share, double CellMs, double EmitMs, double FileMib)
{
    /// <summary>Microseconds of cell staging per cell.</summary>
    public double CostPerCellUs => CellMs * 1000.0 / Cells;
}

/// <summary>
/// One generated scattered-parts world: the placements, the index of the
/// isolated part the edit benchmark moves, and the tight bounds of the two
/// part regions (near the origin and around +8,000) for query sampling.
/// </summary>
internal sealed record OpenWorld(
    List<BrushPlacement> Placements, int EditIndex, Aabb NearBounds, Aabb FarBounds);

/// <summary>
/// Minimal 64-bit linear congruential generator (Knuth MMIX constants). Used
/// instead of <see cref="Random"/> so benchmark inputs are bit-identical
/// across runs and runtime versions — the query checksums depend on it.
/// </summary>
internal struct Lcg(ulong seed)
{
    private ulong _state = seed;

    /// <summary>Uniform float in [0, 1), from the high 24 bits of the state.</summary>
    public float NextFloat01()
    {
        _state = _state * 6364136223846793005UL + 1442695040888963407UL;
        return (_state >> 40) * (1.0f / (1 << 24));
    }

    /// <summary>
    /// Unit direction via rejection sampling of the cube (keeps the
    /// distribution uniform over the sphere and avoids near-zero vectors).
    /// </summary>
    public Vector3 NextDirection()
    {
        while (true)
        {
            var v = new Vector3(
                NextFloat01() * 2f - 1f,
                NextFloat01() * 2f - 1f,
                NextFloat01() * 2f - 1f);
            float lengthSquared = v.LengthSquared();
            if (lengthSquared is > 1e-4f and <= 1f)
                return v / MathF.Sqrt(lengthSquared);
        }
    }
}
