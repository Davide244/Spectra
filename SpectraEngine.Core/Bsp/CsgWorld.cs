using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using SpectraEngine.Core.Assets;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A compiled static world: the result of running CSG over a set of brushes.
/// Holds the carved visible surfaces and per-cell BSP trees for spatial
/// queries (routed through <see cref="ContainsPoint"/> and
/// <see cref="Raycast"/>), and can produce a render mesh — all from one
/// <see cref="Build"/> call.
/// </summary>
public sealed class CsgWorld
{
    // How far a cell's ray segment is allowed to overshoot its traversal
    // interval when accepting a hit. Guards float rounding at cell boundaries:
    // a crossing landing epsilon past the boundary is accepted in the earlier
    // cell (with its true surface normal) instead of degenerating into the
    // next cell's starts-on-the-surface corner case. MUST stay at most
    // ChunkGrid.WeldBand: every brush with geometry inside the overshoot
    // window is then resident in the current cell too, so the window can never
    // shadow a nearer surface the current cell's tree does not contain. The
    // window really is the whole sampled region: BspTree's segment trace
    // detects solid entry by the leaf containing each sub-segment and probes
    // nothing beyond its clamped segment (see BspTree.TraceSegment), so a
    // cell's answer never depends on space past boundary + WeldBand, where
    // non-resident brushes would make it wrong.
    private const float RaycastIntervalEpsilon = Polygon.Epsilon;

    private CsgWorld(
        IReadOnlyList<BrushPlacement> placements, Polygon[]? surfaces, int surfaceCount,
        ChunkGrid chunks, IReadOnlyList<ChunkMesh> chunkMeshes, IReadOnlyList<ChunkCoord>? dirtyCells,
        CsgWorldCarry carry, bool lazyCaches,
        CsgCompileCache? compileCache, CsgCacheStats? cacheStats,
        CsgWeldCache? weldCache, CsgWeldStats? weldStats,
        CsgBspCache? bspCache, CsgBspStats? bspStats,
        CsgMeshCache? meshCache, CsgMeshStats? meshStats,
        long patchBaseId = 0, IReadOnlyList<(ChunkCoord Coord, ChunkMesh? Mesh)>? chunkMeshDelta = null)
    {
        Placements = placements;
        _surfaces = surfaces;
        SurfaceCount = surfaceCount;
        Chunks = chunks;
        ChunkMeshesPaged = chunkMeshes as PagedArray<ChunkMesh> ?? PagedArray<ChunkMesh>.From(chunkMeshes);
        DirtyCells = dirtyCells;
        Carry = carry;
        _lazyCaches = lazyCaches;
        _compileCache = compileCache;
        CacheStats = cacheStats;
        _weldCache = weldCache;
        WeldStats = weldStats;
        _bspCache = bspCache;
        BspStats = bspStats;
        _meshCache = meshCache;
        MeshStats = meshStats;
        PatchBaseId = patchBaseId;
        ChunkMeshDelta = chunkMeshDelta;
    }

    // Lazily materialized views (see the corresponding properties). Single
    // writer in practice — the one compile or test that first asks — and a
    // duplicated build under a benign race would produce an identical value,
    // so plain compare-exchange publication suffices; no lock.
    private Polygon[]? _surfaces;
    private Brush[]? _brushes;
    private readonly bool _lazyCaches;
    private CsgCompileCache? _compileCache;
    private CsgWeldCache? _weldCache;
    private CsgBspCache? _bspCache;
    private CsgMeshCache? _meshCache;

    // Process-unique world identities, for delta-consumer validity checks
    // (see PatchBaseId) without retaining a reference chain of previous
    // worlds. Interlocked so concurrent background compiles stay unique.
    private static long _nextId;

    /// <summary>Process-unique identity of this compiled world instance.</summary>
    internal long Id { get; } = Interlocked.Increment(ref _nextId);

    /// <summary>
    /// <see cref="Id"/> of the previous world this world was PATCHED from, or
    /// 0 when it was built from scratch (validated or cache-free path). An id,
    /// not a reference, so the chain of superseded worlds stays collectable.
    /// </summary>
    internal long PatchBaseId { get; }

    /// <summary>
    /// The per-cell mesh changes relative to the <see cref="PatchBaseId"/>
    /// world, ascending by coordinate: null mesh = the cell lost its render
    /// geometry, non-null = the cell's artifact was replaced or newly added;
    /// cells absent from the list carried their artifact INSTANCE forward
    /// unchanged. Null for from-scratch worlds. A consumer holding GPU state
    /// keyed per cell (the scene's chunk-mesh map) applies exactly these
    /// entries instead of re-diffing every cell — the O(changed cells) swap —
    /// but only after verifying its current state was built from the
    /// <see cref="PatchBaseId"/> world.
    /// </summary>
    internal IReadOnlyList<(ChunkCoord Coord, ChunkMesh? Mesh)>? ChunkMeshDelta { get; }

    /// <summary>
    /// The per-placement working state this compile retains for the next
    /// incremental compile (see <see cref="CsgWorldCarry"/>); the classic
    /// validation caches are derived views over it.
    /// </summary>
    internal CsgWorldCarry Carry { get; }

    /// <summary>
    /// The source brushes the world was built from, index-aligned with
    /// <see cref="Placements"/>. Materialized lazily — it is a convenience
    /// projection of the placements, and eager construction would be an
    /// O(world) cost on every incremental compile.
    /// </summary>
    public IReadOnlyList<Brush> Brushes
    {
        get
        {
            if (_brushes is null)
            {
                var brushes = new Brush[Placements.Count];
                for (int i = 0; i < brushes.Length; i++)
                    brushes[i] = Placements[i].Brush;
                Interlocked.CompareExchange(ref _brushes, brushes, null);
            }
            return _brushes;
        }
    }

    /// <summary>
    /// The placements the world was built from: each source brush paired with
    /// the world transform it was carved at. Consumers that need world-space
    /// brush data (e.g. debug AABBs) must read these, not
    /// <see cref="Brush.Transform"/> — a snapshot-built world may have been
    /// carved at transforms the live brushes no longer carry.
    /// </summary>
    public IReadOnlyList<BrushPlacement> Placements { get; }

    /// <summary>
    /// The visible exterior surfaces of the brush union, in world space, in
    /// placement order. Fully compiled worlds carry this list eagerly; an
    /// incrementally compiled world materializes it on first access (from the
    /// per-placement carry — flattening the whole world eagerly would be an
    /// O(world) cost on every one-brush edit). Engine paths that only need
    /// the size use <see cref="SurfaceCount"/>, which never materializes.
    /// </summary>
    public IReadOnlyList<Polygon> Surfaces
    {
        get
        {
            if (_surfaces is null)
                Interlocked.CompareExchange(ref _surfaces, Csg.Concatenate(Carry.WeldedPerBrush, SurfaceCount), null);
            return _surfaces;
        }
    }

    /// <summary>
    /// Number of surfaces in <see cref="Surfaces"/>, known without
    /// materializing the flat list.
    /// </summary>
    public int SurfaceCount { get; }

    /// <summary>
    /// The sparse chunk partition of this world: every placement bucketed into
    /// the cells its weld-band-inflated AABB touches, with its carved surfaces
    /// and its snapped+welded surfaces stored under its single owner cell (see
    /// <see cref="WorldChunk"/> for the owner/resident distinction). Since W2
    /// the snap+weld stage runs per cell against these buckets
    /// (<see cref="ChunkWelder"/>), and since W3 each cell also carries its
    /// own solid-leaf BSP tree over its residents' welded surfaces
    /// (<see cref="ChunkBspBuilder"/>, queried through
    /// <see cref="ContainsPoint"/>/<see cref="Raycast"/>), and since W4 the
    /// render mesh is per cell too (<see cref="ChunkMeshBuilder"/>, exposed
    /// through <see cref="ChunkMeshes"/>); the flat <see cref="Surfaces"/>
    /// list is assembled from the per-cell results in placement order, and the
    /// monolithic <see cref="BuildMesh"/> remains as the oracle/benchmark
    /// reference over it.
    /// </summary>
    public ChunkGrid Chunks { get; }

    /// <summary>
    /// The per-cell render meshes (W4): one <see cref="ChunkMesh"/> per cell
    /// that owns render geometry, in ascending cell order, each split per face
    /// material (<see cref="ChunkMesh.Submeshes"/>). Their triangle union
    /// equals the monolithic <see cref="BuildMesh"/> of this world, triangle
    /// for triangle; caching compiles reuse clean cells' artifact INSTANCES,
    /// which is what lets the render thread swap GPU meshes for only the cells
    /// an edit actually touched.
    /// </summary>
    public IReadOnlyList<ChunkMesh> ChunkMeshes => ChunkMeshesPaged;

    // The paged backing of ChunkMeshes — what the incremental compile splices
    // by copy-on-write instead of copying the whole list per edit.
    internal PagedArray<ChunkMesh> ChunkMeshesPaged { get; }

    /// <summary>
    /// The dirty-cell set this compile was invoked with — the cells whose
    /// brush contents changed since the previous compile, as tracked by the
    /// scene's footprint diffing (sorted ascending). Null when the world was
    /// built through a dirty-agnostic overload (standalone/test builds and the
    /// synchronous scene rebuild), which compile everything regardless.
    /// Recorded for observability (logs, tests, editors) only: NO per-chunk
    /// stage consumes it — the weld, per-cell BSP, and per-cell mesh rebuild
    /// scopes all come from <see cref="CsgWeldCache"/>,
    /// <see cref="CsgBspCache"/>, and <see cref="CsgMeshCache"/> validation,
    /// which covers a strict superset of the dirty cells and cannot go stale
    /// (see <see cref="ChunkWelder"/>, <see cref="ChunkBspBuilder"/>, and
    /// <see cref="ChunkMeshBuilder"/> — the last documents why the dirty set
    /// alone would be an UNSAFE mesh scope).
    /// </summary>
    public IReadOnlyList<ChunkCoord>? DirtyCells { get; }

    /// <summary>
    /// The carve cache this compile produced, for the next incremental compile
    /// to consume — see <see cref="Build(IReadOnlyList{BrushPlacement}, CsgCompileCache?)"/>.
    /// Null when the world was built through a cache-free overload. Note the
    /// cache retains every brush's carved (pre-snap) surfaces for as long as
    /// the world lives — the memory price of making the next edit cheap.
    /// Incrementally compiled worlds materialize it lazily from the carry
    /// (an O(world) dictionary build, paid only when a fallback compile —
    /// or a direct consumer — actually asks).
    /// </summary>
    public CsgCompileCache? CompileCache
    {
        get
        {
            if (_compileCache is null && _lazyCaches)
            {
                int n = Placements.Count;
                var entries = new CsgCompileCache.Entry?[n];
                for (int i = 0; i < n; i++)
                    entries[i] = CsgCompileCache.Entry.Create(Placements, i, Carry.CarveNeighbors[i], Carry.CarvedPerBrush[i]);
                Interlocked.CompareExchange(ref _compileCache, CsgCompileCache.FromEntries(Placements, entries), null);
            }
            return _compileCache;
        }
    }

    /// <summary>
    /// Carve hit/miss counters when the world was built through the caching
    /// overload (a first compile with no previous cache reports all misses);
    /// null when it was built cache-free.
    /// </summary>
    public CsgCacheStats? CacheStats { get; }

    /// <summary>
    /// The per-cell weld cache this compile produced, for the next incremental
    /// compile to consume — the weld-stage counterpart of
    /// <see cref="CompileCache"/>, and null under the same conditions (weld
    /// reuse rides on carve-array identity, so it only ever pays off alongside
    /// the carve cache).
    /// </summary>
    public CsgWeldCache? WeldCache
    {
        get
        {
            if (_weldCache is null && _lazyCaches)
            {
                int n = Placements.Count;
                var perBrush = new Polygon[n][];
                for (int i = 0; i < n; i++)
                    perBrush[i] = Carry.CarvedPerBrush[i];

                // Candidate-carve lists are shared per distinct candidate set,
                // mirroring the eager weld path (reference-keyed memo).
                var carvesMemo = new Dictionary<int[], Polygon[][]>(ReferenceEqualityComparer.Instance);
                var entries = new CsgWeldCache.Entry[n];
                for (int i = 0; i < n; i++)
                {
                    int[] candidateSet = Carry.WeldCandidates[i];
                    if (!carvesMemo.TryGetValue(candidateSet, out Polygon[][]? carves))
                    {
                        carves = new Polygon[candidateSet.Length][];
                        for (int k = 0; k < carves.Length; k++)
                            carves[k] = perBrush[candidateSet[k]];
                        carvesMemo[candidateSet] = carves;
                    }
                    entries[i] = new CsgWeldCache.Entry(carves, Carry.WeldedPerBrush[i]);
                }
                Interlocked.CompareExchange(ref _weldCache, CsgWeldCache.FromEntries(perBrush, entries), null);
            }
            return _weldCache;
        }
    }

    /// <summary>
    /// Weld reused/welded counters when the world was built through the
    /// caching overload (a first compile with no previous weld cache reports
    /// all welded); null when it was built cache-free.
    /// </summary>
    public CsgWeldStats? WeldStats { get; }

    /// <summary>
    /// The per-cell BSP cache this compile produced, for the next incremental
    /// compile to consume — the BSP-stage counterpart of
    /// <see cref="WeldCache"/>, and null under the same conditions (tree reuse
    /// rides on welded-array identity, so it only ever pays off alongside the
    /// carve and weld caches).
    /// </summary>
    public CsgBspCache? BspCache
    {
        get
        {
            if (_bspCache is null && _lazyCaches)
            {
                IReadOnlyList<WorldChunk> cells = Chunks.OrderedChunks;
                var entries = new CsgBspCache.Entry[cells.Count];
                for (int c = 0; c < cells.Count; c++)
                {
                    WorldChunk chunk = cells[c];
                    IReadOnlyList<int> residents = chunk.ResidentBrushIndices;
                    var residentWelded = new Polygon[residents.Count][];
                    for (int k = 0; k < residentWelded.Length; k++)
                        residentWelded[k] = Carry.WeldedPerBrush[residents[k]];
                    entries[c] = new CsgBspCache.Entry(residentWelded, chunk.Bsp);
                }
                Interlocked.CompareExchange(ref _bspCache, CsgBspCache.FromEntries(cells, entries), null);
            }
            return _bspCache;
        }
    }

    /// <summary>
    /// Per-cell tree reused/built counters when the world was built through
    /// the caching overload (a first compile with no previous BSP cache
    /// reports all built); null when it was built cache-free.
    /// </summary>
    public CsgBspStats? BspStats { get; }

    /// <summary>
    /// The per-cell mesh cache this compile produced, for the next incremental
    /// compile to consume — the mesh-stage counterpart of
    /// <see cref="BspCache"/>, and null under the same conditions (artifact
    /// reuse rides on welded-array identity, so it only ever pays off
    /// alongside the carve and weld caches).
    /// </summary>
    public CsgMeshCache? MeshCache
    {
        get
        {
            if (_meshCache is null && _lazyCaches)
            {
                // Geometry cells align 1:1 with ChunkMeshes (both ascending,
                // both built from cells with owned render geometry).
                var geometryCells = new List<WorldChunk>(ChunkMeshes.Count);
                foreach (WorldChunk chunk in Chunks.OrderedChunks)
                {
                    if (chunk.WeldedSurfaces.Count > 0)
                        geometryCells.Add(chunk);
                }

                var entries = new CsgMeshCache.Entry[geometryCells.Count];
                for (int c = 0; c < geometryCells.Count; c++)
                {
                    WorldChunk chunk = geometryCells[c];
                    IReadOnlyList<int> owned = chunk.OwnedBrushIndices;
                    var ownedWelded = new Polygon[owned.Count][];
                    for (int k = 0; k < ownedWelded.Length; k++)
                        ownedWelded[k] = Carry.WeldedPerBrush[owned[k]];
                    entries[c] = new CsgMeshCache.Entry(ownedWelded, ChunkMeshes[c]);
                }
                Interlocked.CompareExchange(ref _meshCache, CsgMeshCache.FromEntries(geometryCells, entries), null);
            }
            return _meshCache;
        }
    }

    /// <summary>
    /// Per-cell mesh reused/built counters when the world was built through
    /// the caching overload (a first compile with no previous mesh cache
    /// reports all built); null when it was built cache-free.
    /// </summary>
    public CsgMeshStats? MeshStats { get; }

    /// <summary>
    /// Carves the brushes at their own <see cref="Brush.Transform"/> and builds
    /// the BSP tree from the result. Convenience overload for standalone/test
    /// use; scene compiles go through the <see cref="BrushPlacement"/> overload.
    /// </summary>
    public static CsgWorld Build(IReadOnlyList<Brush> brushes)
        => Build(Csg.ToPlacements(brushes));

    /// <summary>
    /// Carves the placed brushes and builds the BSP tree from the result. Pure
    /// CPU work over the placements and immutable brush geometry — safe to run
    /// on a background thread as long as no other thread is concurrently
    /// carving the same <see cref="Brush"/> instances (see
    /// <see cref="Csg.Carve(IReadOnlyList{BrushPlacement})"/>).
    /// </summary>
    public static CsgWorld Build(IReadOnlyList<BrushPlacement> placements)
    {
        Polygon[][] perBrushSurfaces = Csg.CarvePerBrush(placements, out int[][] neighbors);
        return Assemble(
            placements, perBrushSurfaces, neighbors, dirtyCells: null, compileCache: null, cacheStats: null,
            previousWeldCache: null, previousBspCache: null, previousMeshCache: null);
    }

    /// <summary>
    /// Incremental compile: like
    /// <see cref="Build(IReadOnlyList{BrushPlacement})"/>, but the carve
    /// consumes <paramref name="previousCache"/> (pass null on the first
    /// compile of a session) and the result carries the produced
    /// <see cref="CompileCache"/> and <see cref="CacheStats"/>. The built
    /// world is bit-identical to a cache-free build of the same placements —
    /// only the carve cost changes.
    /// </summary>
    public static CsgWorld Build(IReadOnlyList<BrushPlacement> placements, CsgCompileCache? previousCache)
        => Build(placements, dirtyCells: null, previousCache, previousWeldCache: null);

    /// <summary>
    /// Incremental compile with dirty-cell awareness: identical to
    /// <see cref="Build(IReadOnlyList{BrushPlacement}, CsgCompileCache?)"/>
    /// except the built world records <paramref name="dirtyCells"/> — the
    /// sorted set of chunk cells whose brush contents changed since the
    /// previous compile, as diffed by the scene — in
    /// <see cref="DirtyCells"/>. The set is recorded, not consumed (see
    /// <see cref="DirtyCells"/>), so the output is bit-identical whatever set
    /// (or null) is passed.
    /// </summary>
    public static CsgWorld Build(
        IReadOnlyList<BrushPlacement> placements, IReadOnlyList<ChunkCoord>? dirtyCells, CsgCompileCache? previousCache)
        => Build(placements, dirtyCells, previousCache, previousWeldCache: null, previousBspCache: null);

    /// <summary>
    /// Incremental compile carrying the carve and weld caches but no BSP
    /// cache: every cell's tree builds fresh. Kept for callers (and tests)
    /// that predate the per-cell BSP carry; the scene pump uses the
    /// full-carry overload.
    /// </summary>
    public static CsgWorld Build(
        IReadOnlyList<BrushPlacement> placements, IReadOnlyList<ChunkCoord>? dirtyCells,
        CsgCompileCache? previousCache, CsgWeldCache? previousWeldCache)
        => Build(placements, dirtyCells, previousCache, previousWeldCache, previousBspCache: null);

    /// <summary>
    /// Incremental compile carrying the carve, weld, and BSP caches but no
    /// mesh cache: every geometry cell's render arrays build fresh. Kept for
    /// callers (and tests) that predate the per-cell mesh carry; the scene
    /// pump uses the full-carry overload.
    /// </summary>
    public static CsgWorld Build(
        IReadOnlyList<BrushPlacement> placements, IReadOnlyList<ChunkCoord>? dirtyCells,
        CsgCompileCache? previousCache, CsgWeldCache? previousWeldCache, CsgBspCache? previousBspCache)
        => Build(placements, dirtyCells, previousCache, previousWeldCache, previousBspCache, previousMeshCache: null);

    /// <summary>
    /// The fully incremental compile the scene's pump uses: carve consumes
    /// <paramref name="previousCache"/>, snap+weld consumes
    /// <paramref name="previousWeldCache"/>, the per-cell BSP stage consumes
    /// <paramref name="previousBspCache"/>, and the per-cell mesh stage
    /// consumes <paramref name="previousMeshCache"/> (each may be null on the
    /// first compile of a session), so a one-brush edit re-carves, re-welds,
    /// re-partitions, and re-meshes only that brush's neighbourhood — no
    /// remaining per-edit stage scales with the world. The built world is
    /// semantically identical to a cache-free build of the same placements —
    /// the oracle tests pin this per stage — and carries the successor
    /// <see cref="CompileCache"/>, <see cref="WeldCache"/>,
    /// <see cref="BspCache"/>, and <see cref="MeshCache"/> for the next
    /// compile.
    /// </summary>
    public static CsgWorld Build(
        IReadOnlyList<BrushPlacement> placements, IReadOnlyList<ChunkCoord>? dirtyCells,
        CsgCompileCache? previousCache, CsgWeldCache? previousWeldCache, CsgBspCache? previousBspCache,
        CsgMeshCache? previousMeshCache)
    {
        Polygon[][] perBrushSurfaces = Csg.CarvePerBrush(
            placements, previousCache, out CsgCompileCache nextCache, out CsgCacheStats stats, out int[][] neighbors);
        return Assemble(
            placements, perBrushSurfaces, neighbors, dirtyCells, nextCache, stats,
            previousWeldCache, previousBspCache, previousMeshCache);
    }

    /// <summary>
    /// The incremental compile of the open-world editing loop: derives the new
    /// world from <paramref name="previous"/> by re-running only the stages an
    /// edit's neighbourhood actually needs — no per-compile cost scales with
    /// the world (see <see cref="CsgIncrementalCompiler"/> for the mechanism
    /// and its exactness/fallback contract).
    /// <para>
    /// <b>TRUSTED-DIFF CONTRACT</b> — when <paramref name="previous"/> and
    /// <paramref name="dirtyCells"/> are both non-null and the placement count
    /// matches the previous world's, the caller GUARANTEES that (a) the
    /// placement list has the same source order as the previous compile's
    /// (element i describes the same authoring slot), and (b) every placement
    /// that differs from the previous compile's has both its old and its new
    /// residency footprint (<see cref="ChunkGrid.ComputeFootprint"/>) covered
    /// by <paramref name="dirtyCells"/>. The scene's footprint diff produces
    /// exactly this set; a violated guarantee would mean silently stale
    /// geometry (debug builds verify (a) and throw). Callers that cannot
    /// guarantee the contract pass a null <paramref name="dirtyCells"/> (or a
    /// null <paramref name="previous"/>), which compiles through the fully
    /// validated path instead — always correct, but with an O(world)
    /// validation floor.
    /// </para>
    /// </summary>
    public static CsgWorld Build(
        IReadOnlyList<BrushPlacement> placements, IReadOnlyList<ChunkCoord>? dirtyCells, CsgWorld? previous)
    {
        if (previous is not null && dirtyCells is not null &&
            CsgIncrementalCompiler.TryBuild(placements, dirtyCells, previous, out CsgWorld? patched))
        {
            return patched;
        }

        // Fallback: the fully validated caching path, seeded with the previous
        // world's classic caches (materialized from its carry on demand). Any
        // stale-order or structural situation resolves here to fresh, validated
        // artifacts — the fallback re-validates every reuse against a real
        // broadphase run, so it is correct for ANY placement list.
        return Build(
            placements, dirtyCells,
            previous?.CompileCache, previous?.WeldCache, previous?.BspCache, previous?.MeshCache);
    }

    // The shared post-carve pipeline: bucket the carved per-brush surfaces
    // into the chunk grid, snap+weld per owner cell (reusing carried results
    // where the weld cache validates), reassemble the flat surface list in
    // placement order, build each cell's BSP tree (reusing carried trees where
    // the BSP cache validates) and each geometry cell's render mesh (reusing
    // carried artifacts where the mesh cache validates), then the world object.
    private static CsgWorld Assemble(
        IReadOnlyList<BrushPlacement> placements, Polygon[][] perBrushSurfaces, int[][] neighbors,
        IReadOnlyList<ChunkCoord>? dirtyCells, CsgCompileCache? compileCache, CsgCacheStats? cacheStats,
        CsgWeldCache? previousWeldCache, CsgBspCache? previousBspCache, CsgMeshCache? previousMeshCache)
    {
        ChunkGrid chunks = ChunkGrid.Build(placements, perBrushSurfaces);

        // Per-cell snap+weld (W2). Producing the successor weld cache only
        // makes sense alongside a carve cache — weld reuse validates by
        // carve-array identity, which only a carve-cache hit chain preserves —
        // so the cache-free overloads stay cache-free here too.
        bool caching = compileCache is not null;
        Polygon[][] weldedPerBrush = ChunkWelder.Weld(
            placements, perBrushSurfaces, chunks, previousWeldCache, produceCache: caching,
            out CsgWeldCache? weldCache, out CsgWeldStats weldStats, out int[][] candidates);
        chunks.AttachWeldedSurfaces(placements, weldedPerBrush);

        // Per-cell BSP (W3): every cell gets a tree over its residents' welded
        // surfaces; queries route through ContainsPoint/Raycast. The successor
        // cache rides the same caching condition as the weld cache — tree
        // reuse validates by welded-array identity, which only a weld-cache
        // hit chain preserves.
        ChunkBspBuilder.Build(
            chunks, weldedPerBrush, previousBspCache, produceCache: caching,
            out CsgBspCache? bspCache, out CsgBspStats bspStats);

        // Per-cell render mesh (W4): every geometry-owning cell gets its own
        // vertex/index arrays; the render thread uploads them per cell and
        // swaps only what changed. The successor cache rides the same caching
        // condition as the others — artifact reuse validates by welded-array
        // identity, which only a weld-cache hit chain preserves.
        IReadOnlyList<ChunkMesh> chunkMeshes = ChunkMeshBuilder.Build(
            chunks, weldedPerBrush, previousMeshCache, produceCache: caching,
            out CsgMeshCache? meshCache, out CsgMeshStats meshStats);

        // Reassembled in placement order — the same order the old global
        // snap → weld pipeline emitted, so the flat list (and everything built
        // from it) is bit-identical to a monolithic compile.
        Polygon[] surfaces = Csg.Concatenate(weldedPerBrush);

        // The carry for the next incremental compile: the same arrays the
        // stages just produced, repackaged into paged storage (an O(n)
        // reference copy — noise next to the full compile that just ran).
        var carry = new CsgWorldCarry(
            PagedArray<Polygon[]>.From(perBrushSurfaces),
            PagedArray<Polygon[]>.From(weldedPerBrush),
            PagedArray<int[]>.From(neighbors),
            PagedArray<int[]>.From(candidates));

        return new CsgWorld(
            placements, surfaces, surfaces.Length, chunks, chunkMeshes, dirtyCells,
            carry, lazyCaches: false, compileCache, cacheStats,
            weldCache, caching ? weldStats : null, bspCache, caching ? bspStats : null,
            meshCache, caching ? meshStats : null);
    }

    // The incremental compiler's assembly entry: everything is already
    // computed and scoped; this just wraps it in a world instance with lazy
    // flat-list/cache materialization plus the per-cell mesh delta relative
    // to the patched-from world (see ChunkMeshDelta). Internal — see
    // CsgIncrementalCompiler.
    internal static CsgWorld CreatePatched(
        IReadOnlyList<BrushPlacement> placements, int surfaceCount,
        ChunkGrid chunks, IReadOnlyList<ChunkMesh> chunkMeshes, IReadOnlyList<ChunkCoord> dirtyCells,
        CsgWorldCarry carry, CsgWorld patchedFrom,
        IReadOnlyList<(ChunkCoord Coord, ChunkMesh? Mesh)> chunkMeshDelta,
        CsgCacheStats cacheStats, CsgWeldStats weldStats, CsgBspStats bspStats, CsgMeshStats meshStats)
        => new(
            placements, surfaces: null, surfaceCount, chunks, chunkMeshes, dirtyCells,
            carry, lazyCaches: true, compileCache: null, cacheStats,
            weldCache: null, weldStats, bspCache: null, bspStats,
            meshCache: null, meshStats, patchedFrom.Id, chunkMeshDelta);

    /// <summary>
    /// True when the point lies inside solid space. Routed to the point's
    /// cell: an unoccupied cell is open air by construction (any brush whose
    /// volume could reach a point of the cell would be resident there, and
    /// residency creates the chunk), and an occupied cell's tree answers
    /// correctly for every point inside the cell (see
    /// <see cref="ChunkBspBuilder"/> for the closure argument).
    /// </summary>
    /// <remarks>
    /// <b>SCOPE: the compiled authored static world, and nothing else.</b> Not
    /// part brushes (a <c>BrushKind.Part</c> brush is not in the placement list,
    /// so this structure cannot see one at all), not dynamic bodies, not model
    /// colliders, not the character. It is the right answer for the editor, for
    /// cook-time verification, and as the reference side of a determinism
    /// oracle — and it is the wrong answer for gameplay, which needs everything
    /// this list excludes.
    /// <para>
    /// It also disagrees with <see cref="Scene.Scene.Raycast"/> by design and
    /// in both directions: that one tests <em>authored</em> brush planes, so it
    /// reports solid inside a doorway this one correctly reports as empty; this
    /// one sees only admitted world brushes, so it reports empty where a part
    /// brush stands. Neither is a bug; picking one per call site is the
    /// contract until the two unify behind physics.
    /// </para>
    /// </remarks>
    public bool ContainsPoint(Vector3 point) =>
        Chunks.TryGet(ChunkCoord.FromPosition(point), out WorldChunk chunk) && chunk.Bsp.ContainsPoint(point);

    /// <summary>
    /// Casts a ray against solid space. Returns true and reports the first
    /// surface entered in <paramref name="hit"/>; false if the ray stays in
    /// empty space for the whole distance. Routed: a 3D-DDA walk visits the
    /// grid cells along the ray front-to-back, querying each occupied cell's
    /// tree with the segment clamped to that cell's traversal interval — so a
    /// resident surface extending beyond the cell can never report a hit
    /// outside the interval (the same surface belongs to the next cell's tree
    /// and is found there, in order). The first accepted hit is therefore the
    /// global nearest.
    /// </summary>
    /// <remarks>
    /// <b>SCOPE: the compiled authored static world, and nothing else</b> — see
    /// <see cref="ContainsPoint"/> for the full statement of what that excludes
    /// and why this disagrees with <see cref="Scene.Scene.Raycast"/> in both
    /// directions.
    /// </remarks>
    /// <summary>
    /// The compiled surface a point lies on, matched by plane and containment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all:</b> the per-cell BSP reports the splitter
    /// PLANE a ray crossed, not the polygon lying on it, because a node keeps
    /// its splitter and hands its coplanar faces to its children. So a raycast
    /// knows exactly where and which way a surface faces, and nothing about what
    /// that surface is made of. Gameplay needs the material for footstep sounds,
    /// impact decals and surface properties, so it is resolved here instead.
    /// </para>
    /// <para>
    /// <b>Resolved rather than stored, deliberately.</b> Making the BSP carry
    /// its coplanar faces would change the compiled artifact that the
    /// determinism and chunked-versus-monolithic oracles compare, for a lookup
    /// that only gameplay rays perform. The scan is bounded by one 32-unit
    /// cell's surface list, and it is exact: a hit point lies on exactly one
    /// surface.
    /// </para>
    /// <para>
    /// Returns false when the point is not on a surface of the chunk that
    /// contains it, which is what a caller sees for a hit exactly on a cell
    /// boundary. The default material is the right answer there, not a wrong
    /// one from the neighbouring cell.
    /// </para>
    /// </remarks>
    public bool TryResolveSurface(Vector3 point, Vector3 normal, out FaceSurface face)
    {
        face = default;

        if (!Chunks.TryGet(ChunkCoord.FromPosition(point), out WorldChunk chunk))
            return false;

        IReadOnlyList<Polygon> surfaces = chunk.WeldedSurfaces.Count > 0
            ? chunk.WeldedSurfaces
            : chunk.Surfaces;

        const float PlaneEpsilon = 1e-3f;
        const float NormalAgreement = 0.9f;

        for (int i = 0; i < surfaces.Count; i++)
        {
            Polygon polygon = surfaces[i];

            // Normal first: it is one dot product and rejects the overwhelming
            // majority, including the opposite face of the same thin brush.
            if (Vector3.Dot(polygon.Surface.Normal, normal) < NormalAgreement)
                continue;

            if (MathF.Abs(Plane.DotCoordinate(polygon.Surface, point)) > PlaneEpsilon)
                continue;

            if (!ContainsProjected(polygon, point))
                continue;

            face = polygon.Face;
            return true;
        }

        return false;
    }

    // Point-in-polygon on the polygon's own plane, by the sign of the cross
    // product against every edge. Convex by construction (every surface is a
    // clipped face), so one disagreeing edge is a rejection.
    private static bool ContainsProjected(Polygon polygon, Vector3 point)
    {
        ReadOnlySpan<Vector3> vertices = polygon.VertexSpan;
        if (vertices.Length < 3)
            return false;

        Vector3 normal = polygon.Surface.Normal;

        // A small outward tolerance, because the hit point comes from a plane
        // intersection and may land a hair outside an edge it is exactly on.
        const float EdgeEpsilon = -1e-3f;

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 a = vertices[i];
            Vector3 b = vertices[(i + 1) % vertices.Length];
            if (Vector3.Dot(Vector3.Cross(b - a, point - a), normal) < EdgeEpsilon)
                return false;
        }

        return true;
    }

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out BspRaycastHit hit)
    {
        hit = default;
        if (direction == Vector3.Zero || maxDistance <= 0f)
            return false;

        direction = Vector3.Normalize(direction);

        // A ray that starts inside solid space hits immediately — the same
        // contract (and the same reported normal) as the monolithic tree.
        if (ContainsPoint(origin))
        {
            hit = new BspRaycastHit(origin, -direction, 0f);
            return true;
        }

        // TERMINATION BOUND: clip the walk to the grid's occupied-cell box.
        // Without it a miss ray walks one cell per 32 units for the entire
        // maxDistance — O(maxDistance), and for float.MaxValue/Infinity (the
        // natural "unbounded" sentinel, which Scene.Raycast's API already
        // uses) the walk never terminates at all once `tMax += tDelta`
        // saturates in float while the cell integers keep stepping. Every
        // solid surface lives inside an occupied cell, so the ray can only
        // hit within the box: a ray that misses the box misses the world, and
        // once the walk has passed the box's far side every remaining cell is
        // unoccupied. The box is conservative (it may cover vacated cells
        // after edits — see ChunkGrid.TryGetCellBounds), which only costs a
        // few extra lookups, never a wrong answer.
        if (!Chunks.TryGetCellBounds(out ChunkCoord cellMin, out ChunkCoord cellMax))
            return false; // empty world
        float boxEnter = 0f;
        float walkEnd = maxDistance;
        if (!ClipToBox(origin, direction, cellMin.MinCorner, cellMax.MaxCorner, ref boxEnter, ref walkEnd))
            return false; // the ray never reaches an occupied region

        // A ray starting far outside the occupied region fast-forwards to one
        // cell before the box (only when that skips at least a cell, so the
        // common in-world ray keeps bit-identical arithmetic: baseT stays 0
        // and the walk origin IS the origin). The one-cell back-off absorbs
        // slab-vs-DDA rounding, so the entry cell is never skipped; the box
        // is empty space up to boxEnter by construction, so nothing hittable
        // is jumped over.
        float baseT = 0f;
        Vector3 walkOrigin = origin;
        if (boxEnter > ChunkCoord.CellSize)
        {
            baseT = boxEnter - ChunkCoord.CellSize;
            walkOrigin = origin + direction * baseT;
        }
        float maxLocal = maxDistance - baseT;

        // Amanatides–Woo 3D-DDA setup. Zero direction components never step
        // their axis (tMax = +infinity); negative components step down with a
        // positive tDelta; an origin exactly on a cell boundary belongs to the
        // positive-side cell (floor semantics), and heading negative from
        // there yields tMax = 0 — a zero-length first segment that is simply
        // skipped before stepping into the negative neighbour.
        ChunkCoord startCell = ChunkCoord.FromPosition(walkOrigin);
        int x = startCell.X, y = startCell.Y, z = startCell.Z;
        SetupAxis(walkOrigin.X, direction.X, x, out int stepX, out float tMaxX, out float tDeltaX);
        SetupAxis(walkOrigin.Y, direction.Y, y, out int stepY, out float tMaxY, out float tDeltaY);
        SetupAxis(walkOrigin.Z, direction.Z, z, out int stepZ, out float tMaxZ, out float tDeltaZ);

        // The walk stops one cell of slack past the box exit: the slab clip
        // and the DDA compute the same boundary planes through slightly
        // different arithmetic, and the slack absorbs that rounding without
        // ever skipping a cell the box genuinely covers.
        float walkLimit = walkEnd - baseT + ChunkCoord.CellSize;

        float entryT = 0f;
        while (true)
        {
            // The ray occupies this cell for t in [entryT, exitT), relative
            // to the walk origin (global t = baseT + local t).
            float exitT = MathF.Min(MathF.Min(tMaxX, tMaxY), tMaxZ);
            float segmentEnd = MathF.Min(exitT, maxLocal);

            if (segmentEnd > entryT && Chunks.TryGet(new ChunkCoord(x, y, z), out WorldChunk chunk))
            {
                // The interval overshoot applies only at cell boundaries,
                // never past the ray's own end — a hit epsilon beyond
                // maxDistance must stay a miss, exactly as monolithic.
                float window = segmentEnd - entryT +
                    (segmentEnd < maxLocal ? RaycastIntervalEpsilon : 0f);
                Vector3 segmentStart = walkOrigin + direction * entryT;
                if (chunk.Bsp.Raycast(segmentStart, direction, window, out BspRaycastHit local))
                {
                    float distance = baseT + entryT + local.Distance;
                    if (distance > maxDistance)
                        return false; // overshoot found solid just past the ray end
                    hit = new BspRaycastHit(local.Point, local.Normal, distance);
                    return true;
                }
            }

            if (exitT >= maxLocal || entryT > walkLimit)
                return false; // walked the whole ray (or left the occupied box) without entering solid

            // Advance across the nearest cell boundary. Ties (edge/corner
            // crossings) step one axis at a time; the extra zero-length visit
            // is skipped by the segmentEnd > entryT guard above.
            entryT = exitT;
            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                x += stepX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                y += stepY;
                tMaxY += tDeltaY;
            }
            else
            {
                z += stepZ;
                tMaxZ += tDeltaZ;
            }
        }

        // Per-axis DDA parameters: the t at which the ray leaves the start
        // cell on this axis (tMax) and the t a full cell takes to cross
        // (tDelta). The boundary arithmetic matches ChunkCoord.MinCorner/
        // MaxCorner bit for bit, so the walk and the cell classification
        // cannot disagree about where a boundary lies.
        static void SetupAxis(float origin, float direction, int cell,
            out int step, out float tMax, out float tDelta)
        {
            if (direction > 0f)
            {
                step = 1;
                tMax = ((cell + 1) * ChunkCoord.CellSize - origin) / direction;
                tDelta = ChunkCoord.CellSize / direction;
            }
            else if (direction < 0f)
            {
                step = -1;
                tMax = (cell * ChunkCoord.CellSize - origin) / direction;
                tDelta = ChunkCoord.CellSize / -direction;
            }
            else
            {
                step = 0;
                tMax = float.PositiveInfinity;
                tDelta = float.PositiveInfinity;
            }
        }

        // Slab test of the ray against the occupied-cell box, growing `enter`
        // to the box-entry parameter and shrinking `walkEnd` to the box-exit
        // parameter (still capped by its incoming value, the caller's
        // maxDistance). False when the ray misses the box entirely within
        // that budget. Zero direction components reduce to a containment test
        // on their axis; the box bounds are cell-aligned and finite, so a
        // finite exit always comes out of a hit.
        static bool ClipToBox(Vector3 origin, Vector3 direction, Vector3 boxMin, Vector3 boxMax,
            ref float enter, ref float walkEnd)
        {
            return ClipAxis(origin.X, direction.X, boxMin.X, boxMax.X, ref enter, ref walkEnd) &&
                   ClipAxis(origin.Y, direction.Y, boxMin.Y, boxMax.Y, ref enter, ref walkEnd) &&
                   ClipAxis(origin.Z, direction.Z, boxMin.Z, boxMax.Z, ref enter, ref walkEnd);
        }

        static bool ClipAxis(float origin, float direction, float min, float max, ref float enter, ref float exit)
        {
            if (direction == 0f)
                return origin >= min && origin <= max;

            float inverse = 1f / direction;
            float t0 = (min - origin) * inverse;
            float t1 = (max - origin) * inverse;
            if (t0 > t1)
                (t0, t1) = (t1, t0);
            if (t0 > enter)
                enter = t0;
            if (t1 < exit)
                exit = t1;
            return enter <= exit;
        }
    }

    /// <summary>
    /// Builds a position+normal+uv render mesh (8 floats per vertex) over the
    /// ENTIRE world's carved surfaces, matching the engine's standard vertex
    /// layout. Since W4 the engine renders the per-cell
    /// <see cref="ChunkMeshes"/> instead; this monolithic build remains as the
    /// oracle reference (its triangle set equals the union of the per-cell
    /// meshes') and for benchmarks/tools that want one flat mesh. UVs come from
    /// each surface's own <see cref="FaceSurface"/> texture axes; a
    /// world-aligned face (the default) projects on its dominant normal axis,
    /// so adjacent coplanar surfaces tile continuously.
    /// </summary>
    public (float[] Vertices, uint[] Indices) BuildMesh() => BuildMeshArrays(Surfaces);

    /// <summary>
    /// The material runs of the monolithic <see cref="BuildMesh"/>: which slice
    /// of its index array belongs to which material (see
    /// <see cref="MaterialRun"/>). Oracle/tool counterpart of the per-cell
    /// <see cref="ChunkMesh.Submeshes"/> — same attribution, expressed as
    /// ranges over the flat mesh instead of as separate geometry, because the
    /// flat mesh's emission order is fixed by its own bit-identity contract.
    /// </summary>
    public IReadOnlyList<MaterialRun> BuildMaterialRuns() => BuildMaterialRunArray(Surfaces);

    /// <summary>
    /// The shared vertex/index emission over an ordered surface list — used by
    /// the monolithic <see cref="BuildMesh"/> and by the per-cell
    /// <see cref="ChunkMeshBuilder"/>, so both produce bit-identical triangles
    /// for the same polygons by construction. Pure and thread-safe.
    /// </summary>
    internal static (float[] Vertices, uint[] Indices) BuildMeshArrays(IReadOnlyList<Polygon> surfaces)
    {
        // Exact totals up front so both arrays are allocated once at final
        // size (a growth-doubling List plus ToArray roughly triples the
        // allocation for what is the largest single buffer of a compile):
        // 8 floats per vertex, and a fan over n vertices emits 3*(n-2) indices.
        int totalVertices = 0;
        int totalIndices = 0;
        foreach (Polygon poly in surfaces)
        {
            totalVertices += poly.VertexCount;
            // Clamped so a degenerate sub-triangle polygon counts zero indices,
            // exactly as the emit loop below produces zero for it.
            totalIndices += Math.Max(3 * (poly.VertexCount - 2), 0);
        }

        var vertices = new float[totalVertices * 8];
        var indices = new uint[totalIndices];
        int vc = 0;   // write cursor into vertices
        int ic = 0;   // write cursor into indices
        uint baseIndex = 0;

        foreach (Polygon poly in surfaces)
        {
            Vector3 normal = poly.Surface.Normal;

            // UVs come from the face's own texture axes — the exact convention
            // (including what the scale and offset units mean) is documented on
            // FaceSurface:
            //     u = dot(p, UAxis) / UScale + UOffset
            //     v = dot(p, VAxis) / VScale + VOffset
            // The resolution is hoisted out of the per-vertex loop because it is
            // constant across a planar face. A world-aligned face — the default
            // for every brush — resolves to the dominant-normal-axis world
            // projection this method used to hard-code, at scale 1 with zero
            // offsets, so default geometry's UVs are the same numbers as before
            // per-face surfaces existed. (The single representable difference is
            // that a coordinate of -0.0 now emerges as +0.0 through the dot
            // product's zero terms — IEEE-equal, and invisible to both the
            // rasteriser and every determinism comparison.)
            FaceSurface face = poly.Face;
            face.ResolveAxes(normal, out Vector3 uAxis, out Vector3 vAxis, out float uScale, out float vScale);

            // Reciprocals hoisted too: division is the only non-trivial
            // arithmetic in this loop. For the default scale of 1 the reciprocal
            // is exactly 1, so the multiply is bit-identical to the divide.
            float invUScale = 1f / uScale;
            float invVScale = 1f / vScale;
            float uOffset = face.UOffset;
            float vOffset = face.VOffset;

            foreach (Vector3 v in poly.VertexSpan)
            {
                vertices[vc++] = v.X; vertices[vc++] = v.Y; vertices[vc++] = v.Z;
                vertices[vc++] = normal.X; vertices[vc++] = normal.Y; vertices[vc++] = normal.Z;
                vertices[vc++] = Vector3.Dot(v, uAxis) * invUScale + uOffset;
                vertices[vc++] = Vector3.Dot(v, vAxis) * invVScale + vOffset;
            }

            for (uint i = 1; i + 1 < poly.VertexCount; i++)
            {
                indices[ic++] = baseIndex;
                indices[ic++] = baseIndex + i;
                indices[ic++] = baseIndex + i + 1;
            }

            baseIndex += (uint)poly.VertexCount;
        }

        return (vertices, indices);
    }

    /// <summary>
    /// Splits an ordered surface list into <see cref="MaterialRun"/>s over the
    /// index array <see cref="BuildMeshArrays"/> produces for the same list —
    /// the handoff that lets the render thread resolve one material per run at
    /// upload time instead of carrying assets through the compile.
    /// </summary>
    /// <remarks>
    /// Runs are maximal: consecutive surfaces wearing the same material merge
    /// into one. They are NOT sorted or reordered — the surface order is the
    /// compile's deterministic placement order, and reordering it would break
    /// the bit-identity the mesh arrays are pinned to. A world of one material
    /// (the default) therefore yields exactly one run covering every index, and
    /// a mixed world yields one run per material change along the surface list.
    /// Pure and thread-safe, like the array build it accompanies.
    /// </remarks>
    internal static MaterialRun[] BuildMaterialRunArray(IReadOnlyList<Polygon> surfaces)
    {
        // Count first so the result array is allocated once at final size, the
        // same discipline the vertex/index arrays follow.
        int runCount = 0;
        MaterialRef previous = default;
        bool started = false;
        for (int i = 0; i < surfaces.Count; i++)
        {
            if (IndexCountOf(surfaces[i]) == 0)
                continue; // a degenerate sub-triangle face emits no indices to attribute
            MaterialRef material = surfaces[i].Face.Material;
            if (!started || material != previous)
            {
                runCount++;
                started = true;
                previous = material;
            }
        }

        var runs = new MaterialRun[runCount];
        int cursor = 0;      // index-array write position
        int runStart = 0;
        int write = 0;
        started = false;
        previous = default;
        for (int i = 0; i < surfaces.Count; i++)
        {
            int emitted = IndexCountOf(surfaces[i]);
            if (emitted == 0)
                continue;

            MaterialRef material = surfaces[i].Face.Material;
            if (!started)
            {
                runStart = cursor;
                previous = material;
                started = true;
            }
            else if (material != previous)
            {
                runs[write++] = new MaterialRun(previous, runStart, cursor - runStart);
                runStart = cursor;
                previous = material;
            }

            cursor += emitted;
        }

        if (started)
            runs[write] = new MaterialRun(previous, runStart, cursor - runStart);

        return runs;

        // Mirrors the emit loop in BuildMeshArrays exactly: a fan over n
        // vertices emits 3*(n-2) indices, clamped to zero for a degenerate face.
        static int IndexCountOf(Polygon poly) => Math.Max(3 * (poly.VertexCount - 2), 0);
    }
}
