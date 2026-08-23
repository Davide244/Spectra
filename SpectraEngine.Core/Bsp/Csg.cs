using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Constructive Solid Geometry over convex brushes. Carving removes the parts
/// of each brush's faces that are buried inside other brushes, leaving only the
/// visible exterior skin of the solid union — the crack-free, non-overlapping
/// surface set a BSP needs to build a correct partition.
/// </summary>
/// <remarks>
/// Each brush's faces are carved in <em>its own local frame</em>: the carver's
/// planes and bounds are transformed into the carved brush's space before
/// clipping. Vertex magnitudes during splits are bounded by the brush's extent,
/// so a brush 10 km from the world origin has the same numerical accuracy as
/// one at the origin. Local fragments are pushed to world coordinates only at
/// the very end, in one matrix multiply per vertex.
/// </remarks>
public static class Csg
{
    private const float NormalEpsilon = 1e-4f;
    private const float OffsetEpsilon = 1e-3f;

    /// <summary>
    /// Carves a set of brushes placed by their own <see cref="Brush.Transform"/>,
    /// returning the visible surface polygons of their union in world space.
    /// Convenience overload for standalone/test use; scene compiles go through
    /// the <see cref="BrushPlacement"/> overload with snapshot transforms.
    /// </summary>
    public static Polygon[] Carve(IReadOnlyList<Brush> brushes)
        => Carve(ToPlacements(brushes));

    /// <summary>
    /// Carves a set of placed brushes, returning the visible surface polygons
    /// of their union in world space. Reads only the placements (and each
    /// brush's immutable local geometry) — never <see cref="Brush.Transform"/> —
    /// so a snapshot can be carved on a background thread while the live scene
    /// keeps moving. One <see cref="Brush"/> instance may back any number of
    /// placements: its local geometry, including every face's eagerly-computed
    /// <see cref="Polygon.Bounds"/>, is immutable after construction, so the
    /// parallel per-placement workers below (and concurrent Carve calls) can
    /// share it freely.
    /// </summary>
    public static Polygon[] Carve(IReadOnlyList<BrushPlacement> placements)
        => Concatenate(CarvePerBrush(placements));

    /// <summary>
    /// Incremental variant of <see cref="Carve(IReadOnlyList{BrushPlacement})"/>:
    /// consumes the cache a previous compile produced, reuses each brush's
    /// cached surfaces when its placement and carver sequence are bitwise
    /// unchanged (the contract is documented on <see cref="CsgCompileCache"/>),
    /// and produces the cache for the next compile. Output is bit-identical to
    /// the cache-free overload for the same placements, whatever mix of hits
    /// and misses occurred — surfaces are always emitted in placement order,
    /// and a hit only ever substitutes the exact polygons a fresh carve of
    /// that brush would compute. <paramref name="previousCache"/> is read-only
    /// here and stays valid for further use.
    /// </summary>
    public static Polygon[] Carve(
        IReadOnlyList<BrushPlacement> placements,
        CsgCompileCache? previousCache,
        out CsgCompileCache nextCache,
        out CsgCacheStats stats)
        => Concatenate(CarvePerBrush(placements, previousCache, out nextCache, out stats));

    /// <summary>
    /// Like <see cref="Carve(IReadOnlyList{BrushPlacement})"/> but keeps the
    /// result grouped per placement (index-aligned with
    /// <paramref name="placements"/>) instead of concatenating — the shape the
    /// chunked compile needs to bucket each brush's surfaces under its owner
    /// cell. <see cref="Concatenate(Polygon[][])"/> of the result equals the
    /// flat overload's output bit for bit.
    /// </summary>
    internal static Polygon[][] CarvePerBrush(IReadOnlyList<BrushPlacement> placements)
        => CarveCore(placements, previousCache: null, cacheEntries: null, out _, out _);

    /// <summary>Cache-free variant that also reports the broadphase neighbour lists (see the caching counterpart).</summary>
    internal static Polygon[][] CarvePerBrush(IReadOnlyList<BrushPlacement> placements, out int[][] neighbors)
        => CarveCore(placements, previousCache: null, cacheEntries: null, out _, out neighbors);

    /// <summary>Per-brush-grouped variant of the caching <see cref="Carve(IReadOnlyList{BrushPlacement}, CsgCompileCache?, out CsgCompileCache, out CsgCacheStats)"/> overload.</summary>
    internal static Polygon[][] CarvePerBrush(
        IReadOnlyList<BrushPlacement> placements,
        CsgCompileCache? previousCache,
        out CsgCompileCache nextCache,
        out CsgCacheStats stats)
        => CarvePerBrush(placements, previousCache, out nextCache, out stats, out _);

    /// <summary>
    /// Like the caching <see cref="CarvePerBrush(IReadOnlyList{BrushPlacement}, CsgCompileCache?, out CsgCompileCache, out CsgCacheStats)"/>
    /// but also reports each brush's broadphase neighbour list in carve order —
    /// the carry the incremental (previous-world) compile patches instead of
    /// re-running the whole broadphase (see <see cref="CsgIncrementalCompiler"/>).
    /// </summary>
    internal static Polygon[][] CarvePerBrush(
        IReadOnlyList<BrushPlacement> placements,
        CsgCompileCache? previousCache,
        out CsgCompileCache nextCache,
        out CsgCacheStats stats,
        out int[][] neighbors)
    {
        var entries = new CsgCompileCache.Entry?[placements.Count];
        Polygon[][] perBrush = CarveCore(placements, previousCache, entries, out int hits, out neighbors);
        nextCache = CsgCompileCache.FromEntries(placements, entries);
        stats = new CsgCacheStats(hits, placements.Count - hits);
        return perBrush;
    }

    // Shared carve core. `previousCache` enables per-brush reuse; a non-null
    // `cacheEntries` array is filled with one entry per placement (reused on
    // hit, freshly captured on miss) for the caller to assemble into the next
    // cache. `hitCount` is 0 when no cache was consulted. `neighbors` reports
    // the broadphase result (per-brush lists in carve order) for callers that
    // retain it as incremental-compile carry.
    private static Polygon[][] CarveCore(
        IReadOnlyList<BrushPlacement> placements,
        CsgCompileCache? previousCache,
        CsgCompileCache.Entry?[]? cacheEntries,
        out int hitCount,
        out int[][] neighborsOut)
    {
        hitCount = 0;
        int n = placements.Count;
        if (n == 0)
        {
            neighborsOut = [];
            return [];
        }

        // Broadphase works in world space so it remains scale-independent.
        var worldBounds = new Aabb[n];
        for (int i = 0; i < n; i++)
            worldBounds[i] = placements[i].WorldBounds;

        int[][] neighbors = BrushBroadphase.FindOverlaps(worldBounds);
        neighborsOut = neighbors;

        // Neighbour lists are consumed in the broadphase's sweep-discovery
        // order — the clip order this engine has always carved in. Clip order
        // changes the fragment decomposition bit-wise (A-then-B splits a face
        // along A's planes first; B-then-A covers the same area with
        // different polygons), so reordering here — e.g. canonicalising to
        // ascending placement index — would change the emitted surfaces and
        // the final mesh arrays for the same scene, which the determinism
        // gate forbids: only BSP tree shape may change, never output.
        // Discovery order is itself a deterministic function of the placement
        // list (the broadphase is sequential and its sort is unchanged from
        // the original engine), so recompiles of the same scene still match
        // bit for bit. Cache reuse stays sound without a canonical order
        // because each entry records the exact ordered carver sequence it was
        // carved under and TryGetValid compares it element-wise: a reshuffled
        // list is a miss (wasted work), never a wrong hit.

        bool[]? hitFlags = previousCache is not null ? new bool[n] : null;
        var perBrush = new Polygon[n][];

        // The thread-local overload gives each worker one CarveScratch reused
        // across every brush it processes, so the per-brush list/buffer churn
        // of the plain overload disappears. Output is written to perBrush[b],
        // which depends only on b's own input — partitioning cannot affect
        // the result.
        Parallel.For(0, n,
            static () => new CarveScratch(),
            (b, _, scratch) =>
            {
                int[] neighborIndices = neighbors[b];

                // Cache hit: this brush's placement and carver sequence are
                // bitwise identical to the previous compile's, so its cached
                // surfaces ARE what the carve below would produce — reuse them
                // verbatim (the array is immutable and never written through).
                if (previousCache is not null &&
                    previousCache.TryGetValid(placements, b, neighborIndices, out CsgCompileCache.Entry? cachedEntry))
                {
                    perBrush[b] = cachedEntry.Surfaces;
                    if (cacheEntries is not null)
                        cacheEntries[b] = cachedEntry;
                    hitFlags![b] = true;
                    return scratch;
                }

                Polygon[] worldSurfaces = CarveSingle(placements, b, neighborIndices, scratch);
                perBrush[b] = worldSurfaces;
                if (cacheEntries is not null)
                    cacheEntries[b] = CsgCompileCache.Entry.Create(placements, b, neighborIndices, worldSurfaces);
                return scratch;
            },
            static _ => { });

        if (hitFlags is not null)
        {
            foreach (bool hit in hitFlags)
            {
                if (hit)
                    hitCount++;
            }
        }

        return perBrush;
    }

    // Carves ONE placed brush against the given carvers, in the given order —
    // the exact per-brush body of the parallel carve loop, factored out so the
    // incremental compile (CsgIncrementalCompiler) re-carves an edit's
    // neighbourhood through the identical code path: same clip order, same
    // arithmetic, bit-identical fragments. `neighborIndices` must be in carve
    // order (broadphase discovery order for full compiles; the patched carry
    // order for incremental ones).
    internal static Polygon[] CarveSingle(
        IReadOnlyList<BrushPlacement> placements, int b, int[] neighborIndices, CarveScratch scratch)
    {
        BrushPlacement placement = placements[b];

        // Pre-transform each neighbour into this brush's local frame
        // once. All carvers' planes are packed back-to-back into the
        // worker's reusable buffer (sized up front so it never grows
        // mid-fill); each CarverInFrame records its slice.
        int planeTotal = 0;
        for (int k = 0; k < neighborIndices.Length; k++)
            planeTotal += placements[neighborIndices[k]].Brush.LocalPlanes.Count;
        scratch.EnsureCarverCapacity(neighborIndices.Length, planeTotal);

        CarverInFrame[] carvers = scratch.Carvers;
        Plane[] planeBuffer = scratch.PlaneBuffer;
        int planeStart = 0;
        for (int k = 0; k < neighborIndices.Length; k++)
        {
            int o = neighborIndices[k];
            carvers[k] = CarverInFrame.Build(placements[o], placement, carverWins: o < b, planeBuffer, planeStart);
            planeStart += carvers[k].PlaneCount;
        }

        List<Polygon> localSurfaces = scratch.LocalSurfaces;
        List<Polygon> current = scratch.Current;
        List<Polygon> next = scratch.Next;
        localSurfaces.Clear();

        // SKIN SUPPRESSION. A subtractive brush emits no outward skin of its
        // own, ever — its contribution is the cavity walls seeded into the
        // brushes it cuts, below. So its carved array is ALWAYS length 0: an
        // invariant, not a case, and a fully supported shape everywhere
        // downstream (Concatenate copies it, ChunkGrid.AddOwned no-ops, and
        // ChunkMeshBuilder's non-empty filter stops a subtractive-only cell
        // ever reaching the artifact bounds seed).
        bool additive = placement.Brush.Operation == BrushOperation.Additive;
        if (additive)
        {
            foreach (Polygon face in placement.Brush.LocalFaces)
            {
                current.Clear();
                current.Add(face);

                for (int k = 0; k < neighborIndices.Length; k++)
                {
                    next.Clear();
                    foreach (Polygon fragment in current)
                        CarveFragment(fragment, carvers, neighborIndices.Length, planeBuffer, next, seedIsWall: false, seedOrigin: -1, carverIndex: k);

                    (current, next) = (next, current);
                    if (current.Count == 0)
                        break;
                }

                localSurfaces.AddRange(current);
            }

            // CAVITY WALLS, attributed to the CUT brush's slot — which is what
            // makes every downstream stage (chunk ownership, weld candidate
            // sets, render bounds) correct with no formula changes: a wall is
            // inside this brush's convex solid, hence inside its LocalBounds.
            //
            // Emitted AFTER every face seed, in carver-list order, and within a
            // carver in the negative's LocalFaces order. Order is output-visible
            // three times over (Concatenate copies verbatim, BspTree's splitter
            // choice strides positionally, BuildMeshArrays packs in array
            // order), so leaving it loose would be a determinism hole — and
            // appending after the faces is what makes the no-subtractive-brush
            // non-regression positional rather than argued.
            for (int k = 0; k < neighborIndices.Length; k++)
            {
                if (!carvers[k].Subtractive || !carvers[k].Bounds.Intersects(placement.Brush.LocalBounds))
                    continue;

                Brush negative = placements[neighborIndices[k]].Brush;
                foreach (Polygon negativeFace in negative.LocalFaces)
                {
                    // (a) into this brush's frame — Transformed maps the
                    // FaceSurface payload too, which is the whole of why a
                    // cavity wears the negative's materials with no plumbing.
                    // (b) flipped HERE, at seed construction: a wall is born as
                    // the boundary of the REMOVED region, so it obeys the same
                    // solid-behind-Surface convention as every other polygon
                    // from the moment it exists. Flipping later would run it
                    // through the clip loops under the wrong convention.
                    Polygon? wall = negativeFace.Transformed(carvers[k].Combined).Flipped();

                    // (c) clip to inside(this brush). Load-bearing twice: it
                    // confines the wall to where solid is actually removed, and
                    // — because Split reports a Coplanar polygon on the FRONT
                    // side, and we keep the back — it kills a wall coincident
                    // with one of this brush's own planes with no special case.
                    IReadOnlyList<Plane> ownPlanes = placement.Brush.LocalPlanes;
                    for (int i = 0; i < ownPlanes.Count && wall is not null; i++)
                    {
                        wall.Split(ownPlanes[i], out _, out Polygon? inside);
                        wall = inside;
                    }

                    if (wall is null)
                        continue;

                    // (d) the same carver ping-pong the faces use, skipping
                    // carver k itself — see the origin-skip row of the table.
                    current.Clear();
                    current.Add(wall);

                    for (int c = 0; c < neighborIndices.Length; c++)
                    {
                        next.Clear();
                        foreach (Polygon fragment in current)
                            CarveFragment(fragment, carvers, neighborIndices.Length, planeBuffer, next, seedIsWall: true, seedOrigin: k, carverIndex: c);

                        (current, next) = (next, current);
                        if (current.Count == 0)
                            break;
                    }

                    localSurfaces.AddRange(current);
                }
            }
        }

        // Push this brush's local fragments out to world coordinates,
        // straight into the exact-size array that becomes part of the
        // result (freshly allocated — never scratch, never pooled).
        var worldSurfaces = new Polygon[localSurfaces.Count];
        for (int i = 0; i < worldSurfaces.Length; i++)
            worldSurfaces[i] = localSurfaces[i].Transformed(placement.Transform);
        return worldSurfaces;
    }

    // Concatenates per-brush surface groups in placement-index order into one
    // exact-size array (a List+ToArray would pay growth doubling plus a full
    // final copy). DETERMINISM: emission order is by placement index
    // regardless of which brushes hit the cache, so cached and fresh compiles
    // of the same placements produce the same surface list bit for bit.
    internal static Polygon[] Concatenate(Polygon[][] perBrush)
    {
        int total = 0;
        foreach (Polygon[] list in perBrush)
            total += list.Length;

        var all = new Polygon[total];
        int offset = 0;
        foreach (Polygon[] list in perBrush)
        {
            Array.Copy(list, 0, all, offset, list.Length);
            offset += list.Length;
        }
        return all;
    }

    /// <summary>
    /// <see cref="Concatenate(Polygon[][])"/> over a paged carry — same
    /// placement-index emission order, same exact-size result. Used by the
    /// lazy flat-surface materialization of incrementally built worlds.
    /// </summary>
    internal static Polygon[] Concatenate(PagedArray<Polygon[]> perBrush, int totalSurfaces)
    {
        var all = new Polygon[totalSurfaces];
        int offset = 0;
        for (int i = 0; i < perBrush.Count; i++)
        {
            Polygon[] list = perBrush[i];
            Array.Copy(list, 0, all, offset, list.Length);
            offset += list.Length;
        }
        return all;
    }

    // Worker-local scratch for Carve's Parallel.For. OWNERSHIP RULES: each
    // instance is owned by exactly one worker at a time (Parallel.For never
    // shares thread-local state across concurrently-running workers), and
    // nothing stored here may ever escape into Carve's results — result
    // polygons and per-brush arrays are always freshly allocated at exact
    // size. Buffers only grow; entries past the counts the current brush
    // wrote are stale and must never be read. Internal (not private) so the
    // incremental compile can drive CarveSingle with one too.
    internal sealed class CarveScratch
    {
        public readonly List<Polygon> LocalSurfaces = [];
        public readonly List<Polygon> Current = [];
        public readonly List<Polygon> Next = [];

        /// <summary>
        /// Carver descriptors for the brush currently being carved. Valid only
        /// until the next brush begins on this worker.
        /// </summary>
        public CarverInFrame[] Carvers = [];

        /// <summary>
        /// Backing storage for every current carver's transformed planes,
        /// packed back-to-back; each <see cref="CarverInFrame"/> addresses its
        /// own [PlaneStart, PlaneStart + PlaneCount) slice. Same lifetime as
        /// <see cref="Carvers"/>.
        /// </summary>
        public Plane[] PlaneBuffer = [];

        public void EnsureCarverCapacity(int carverCount, int planeCount)
        {
            // Geometric growth so a worker settles at its high-water mark
            // after a few brushes instead of reallocating per brush.
            if (Carvers.Length < carverCount)
                Carvers = new CarverInFrame[Math.Max(carverCount, Carvers.Length * 2)];
            if (PlaneBuffer.Length < planeCount)
                PlaneBuffer = new Plane[Math.Max(planeCount, PlaneBuffer.Length * 2)];
        }
    }

    /// <summary>Captures each brush's own <see cref="Brush.Transform"/> into a placement list.</summary>
    internal static BrushPlacement[] ToPlacements(IReadOnlyList<Brush> brushes)
    {
        var placements = new BrushPlacement[brushes.Count];
        for (int i = 0; i < brushes.Count; i++)
            placements[i] = new BrushPlacement(brushes[i], brushes[i].Transform);
        return placements;
    }

    // Appends to `output` the parts of `fragment` that lie OUTSIDE the carver.
    // `planes` is the worker's packed plane buffer; the carver's planes occupy
    // [carver.PlaneStart, carver.PlaneStart + carver.PlaneCount).
    //
    // `seedIsWall` says whether the fragment descends from a cavity-wall seed
    // rather than one of the carved brush's own faces; `seedOrigin` is the
    // carver-list position of the subtractive brush that produced that wall
    // (-1 for a face seed); `carverIndex` is this carver's list position.
    //
    // THE VERDICT TABLE, five rows:
    //
    //   FACE / ADDITIVE      today's code, character for character.
    //   FACE / SUBTRACTIVE   bypass CoplanarOrientation; drop the footprint on
    //                        Split-Coplanar AND same-facing, else generic.
    //   WALL / its ORIGIN    skip the carver (mandatory, not an optimisation).
    //   WALL / ADDITIVE      Wins ? generic : skip the carver.
    //   WALL / other SUBTR.  as FACE/SUBTRACTIVE, plus an opposite-facing
    //                        coincidence tie-break on list position.
    private static void CarveFragment(
        Polygon fragment, CarverInFrame[] carvers, int carverCount, Plane[] planes, List<Polygon> output,
        bool seedIsWall, int seedOrigin, int carverIndex)
    {
        ref readonly CarverInFrame carver = ref carvers[carverIndex];

        // Whole fragment clear of the carver: nothing to remove.
        if (!fragment.Bounds.Intersects(carver.Bounds))
        {
            output.Add(fragment);
            return;
        }

        if (seedIsWall)
        {
            // A wall meeting its OWN negative would find that negative's plane
            // coincident-and-opposite and the wall/wall tie-break would compare
            // the origin position against itself with a strict <, deleting the
            // wall. Skipping is mandatory.
            //
            // A wall meeting an ADDITIVE carver it does not lose to is also
            // skipped: by the wall/face partition theorem every point of a
            // surviving wall is in the OPEN INTERIOR of the brush it was seeded
            // into, so a coincident additive carver's plane only touches the
            // wall's boundary and can remove nothing. The carver's own face is
            // deleted at exactly the wall's points by the ordinary path, so
            // precisely one surface exists on that plane everywhere.
            if (carverIndex == seedOrigin || (!carver.Subtractive && !carver.Wins))
            {
                output.Add(fragment);
                return;
            }
        }

        Polygon? remaining = fragment;
        bool onCarverPlane = false;
        int end = carver.PlaneStart + carver.PlaneCount;
        for (int p = carver.PlaneStart; p < end; p++)
        {
            if (remaining is null)
                return;

            Plane plane = planes[p];

            if (carver.Subtractive)
            {
                // DELIBERATELY NOT CoplanarOrientation. That predicate accepts
                // |dD| < 1e-3 while Polygon.Split classifies at 1e-4 — ten
                // times looser, and per-plane rather than per-vertex. In the
                // band where they disagree the rule's own premise ("the
                // fragment lies ON this plane") is false, and the footprint
                // would be dropped while the replacement wall, sitting delta
                // away, survives its own clip — leaving the boundary open in a
                // delta-tall ring all round the cavity mouth. Using Split's own
                // classification is not merely tighter, it is the SAME
                // tolerance that decides whether the wall survives, so the two
                // decisions cannot disagree.
                if (remaining.Classify(plane) == PolygonClassification.Coplanar)
                {
                    bool sameFacing = Vector3.Dot(remaining.Surface.Normal, plane.Normal) > 0f;

                    // Same-facing: the negative's interior is behind this
                    // surface, so it removes exactly the material this surface
                    // bounded. Drop the footprint — this is the flush
                    // through-cut, and both sides of a slab hit it at once.
                    //
                    // Opposite-facing: the negative merely RESTS on this
                    // surface and removes nothing, so the fragment survives
                    // whole. (Unmodified code fails precisely here:
                    // CoplanarOrientation returns -1 for that pair and the
                    // interior-interface rule would delete a face under a
                    // negative that took nothing away — an open solid.)
                    // Two coincident negatives produce two identical walls and
                    // exactly one must emit: list position breaks the tie, and
                    // it decides only which negative's material paints the
                    // shared patch, never a topology.
                    if (sameFacing)
                        continue;

                    if (seedIsWall && seedOrigin >= carverIndex)
                        continue;

                    output.Add(remaining);
                    return;
                }

                remaining.Split(plane, out Polygon? outside, out Polygon? behind);
                if (outside is not null)
                    output.Add(outside);
                remaining = behind;
                continue;
            }

            int orientation = CoplanarOrientation(remaining.Surface, plane);
            if (orientation != 0)
            {
                // Opposite-facing coincidence is an interior interface — drop the
                // shared footprint from both brushes. Same-facing coincidence is
                // a duplicate surface — resolved by brush precedence.
                bool removeFootprint = orientation < 0 || carver.Wins;
                if (!removeFootprint)
                {
                    output.Add(remaining);
                    return;
                }

                // Skip the coincident plane; the remaining planes carve the
                // footprint, and whatever survives behind them is dropped.
                onCarverPlane = true;
                continue;
            }

            remaining.Split(plane, out Polygon? front, out Polygon? back);
            if (front is not null)
                output.Add(front);   // in front of an outward plane => outside the carver
            remaining = back;
        }

        // Anything still behind every plane is buried inside the carver:
        // dropped — with ONE exception, the interior interface over a hollow.
        //
        // The rule above deletes the shared footprint of an opposite-facing
        // coincidence because the carver's own face on that plane covers exactly
        // it, so precisely one surface survives there. That pairing breaks when a
        // negative cuts flush through the carver ON THAT PLANE: the carver's own
        // face is deleted over the cut by the flush-through-cut rule, and the
        // cavity wall that would replace it is killed at seeding by the clip to
        // inside(cut brush) — Split reports a coplanar seed on the front. Nobody
        // is left to bound the cavity, the compiled skin is open, and the solid
        // reconstruction leaks through the hole. Re-emit the hollowed part.
        //
        // Only after a coincident-plane skip: a fragment buried in the carver's
        // OPEN interior is bounded by a cavity wall the seeding kept (it is not
        // coplanar with any of the cut brush's own planes there), and re-emitting
        // would duplicate that wall.
        if (onCarverPlane && remaining is not null && !carver.Subtractive)
            EmitHollowedRemainder(remaining, 0, carvers, carverCount, carverIndex, planes, output);
    }

    // Emits the parts of a buried fragment that lie inside one of the
    // subtractive carvers at [first, carverCount) — i.e. the parts the carver
    // that buried it no longer actually fills. Parts inside none of them stay
    // buried and are dropped, exactly as before.
    //
    // A part strictly inside a negative is boundary the negative itself removes,
    // and the ordinary ping-pong deletes it again when the fragment meets that
    // negative (or already did, if it came earlier in the list), so this cannot
    // resurrect material: what survives is the patch RESTING on the negative's
    // own face, which is precisely the cavity floor nothing else emits.
    private static void EmitHollowedRemainder(
        Polygon buried, int first, CarverInFrame[] carvers, int carverCount, int carverIndex,
        Plane[] planes, List<Polygon> output)
    {
        for (int s = first; s < carverCount; s++)
        {
            ref readonly CarverInFrame negative = ref carvers[s];
            if (s == carverIndex || !negative.Subtractive)
                continue;
            if (!negative.Bounds.Intersects(carvers[carverIndex].Bounds) || !negative.Bounds.Intersects(buried.Bounds))
                continue;

            // The patch has to REST on one of the negative's own faces —
            // coplanar with it and facing into the cavity — because that face
            // is the cavity wall the seeding would have emitted, and this patch
            // is the only other surface that can bound the same hollow.
            // Classified with Polygon's per-vertex tolerance, not
            // CoplanarOrientation's ten-times-looser one, for the reason the
            // FACE/SUBTRACTIVE row states: the decision has to be taken at the
            // same tolerance that decides whether the seed survives.
            int rest = -1;
            int end = negative.PlaneStart + negative.PlaneCount;
            for (int p = negative.PlaneStart; p < end; p++)
            {
                if (Vector3.Dot(buried.Surface.Normal, planes[p].Normal) < 0f
                    && buried.Classify(planes[p]) == PolygonClassification.Coplanar)
                {
                    rest = p;
                    break;
                }
            }

            // And that face has to lie on one of the carver's OWN planes, which
            // is exactly when the seeding clip killed the cavity wall. A cut
            // that stops inside the carver keeps its wall, and re-emitting here
            // would put two coincident surfaces on the plane.
            if (rest < 0 || !LiesOnOwnPlane(in carvers[carverIndex], planes, planes[rest]))
                continue;

            Polygon? inside = buried;
            for (int p = negative.PlaneStart; p < end && inside is not null; p++)
            {
                if (p == rest)
                    continue;

                inside.Split(planes[p], out Polygon? outside, out Polygon? behind);
                if (outside is not null)
                    EmitHollowedRemainder(outside, s + 1, carvers, carverCount, carverIndex, planes, output);
                inside = behind;
            }

            if (inside is not null)
                output.Add(inside);
            return;
        }
    }

    // Whether `plane` is one of the carver's own planes, at the tolerance
    // Polygon.Split classifies with — the same decision the cavity-wall clip
    // takes when it kills a seed lying on a plane of the brush it cuts.
    private static bool LiesOnOwnPlane(in CarverInFrame carver, Plane[] planes, in Plane plane)
    {
        int end = carver.PlaneStart + carver.PlaneCount;
        for (int p = carver.PlaneStart; p < end; p++)
        {
            if (Vector3.Dot(planes[p].Normal, plane.Normal) > 1f - NormalEpsilon
                && MathF.Abs(planes[p].D - plane.D) < Polygon.Epsilon)
                return true;
        }

        return false;
    }

    // 0 = not coplanar; +1 = same geometric plane, normals agree;
    // -1 = same geometric plane, normals opposed.
    private static int CoplanarOrientation(Plane a, Plane b)
    {
        float dot = Vector3.Dot(a.Normal, b.Normal);
        if (dot > 1f - NormalEpsilon)
            return MathF.Abs(a.D - b.D) < OffsetEpsilon ? 1 : 0;
        if (dot < -1f + NormalEpsilon)
            return MathF.Abs(a.D + b.D) < OffsetEpsilon ? -1 : 0;
        return 0;
    }

    // A carver brush re-expressed in another brush's local frame so the inner
    // loop never has to think about world coordinates or two transforms at once.
    // Planes live in the owning worker's packed plane buffer at
    // [PlaneStart, PlaneStart + PlaneCount) — this struct is only valid while
    // that buffer segment is, i.e. for the one brush currently being carved
    // (the buffer is overwritten when the worker moves to its next brush).
    // Internal only because CarveScratch (which stores these) is.
    internal readonly struct CarverInFrame
    {
        public int PlaneStart { get; }
        public int PlaneCount { get; }
        public Aabb Bounds { get; }
        public bool Wins { get; }

        /// <summary>Whether this carver removes solid rather than adding it.</summary>
        public bool Subtractive { get; }

        /// <summary>
        /// The carver-local to carved-local matrix. Build already computes it;
        /// cavity-wall seeding needs it to bring a subtractive brush's own
        /// faces into the frame of the brush it cuts.
        /// </summary>
        public Matrix4x4 Combined { get; }

        private CarverInFrame(
            int planeStart, int planeCount, Aabb bounds, bool wins,
            bool subtractive, Matrix4x4 combined)
        {
            PlaneStart = planeStart;
            PlaneCount = planeCount;
            Bounds = bounds;
            Wins = wins;
            Subtractive = subtractive;
            Combined = combined;
        }

        // Writes the carver's transformed planes into `planeBuffer` starting
        // at `planeStart` (the caller sized the buffer for all carvers up
        // front) and returns a descriptor addressing that slice.
        public static CarverInFrame Build(in BrushPlacement carver, in BrushPlacement carved, bool carverWins, Plane[] planeBuffer, int planeStart)
        {
            // Going from carver-local → world → carved-local:
            //   v_carved = v_carver * carver.Transform * Invert(carved.Transform)
            // A singular transform has no carved-local frame to carve in; any
            // fallback would silently produce geometrically wrong output.
            // Scene snapshots reject non-rigid node transforms before they
            // reach here, so this only fires for placements built outside the
            // scene graph.
            if (!Matrix4x4.Invert(carved.Transform, out Matrix4x4 carvedInverse))
                throw new InvalidOperationException(
                    "Carved brush transform is singular and cannot be inverted. " +
                    "Brush transforms must be rigid (rotation and translation only).");
            Matrix4x4 combined = carver.Transform * carvedInverse;

            Brush carverBrush = carver.Brush;
            IReadOnlyList<Plane> localPlanes = carverBrush.LocalPlanes;
            for (int i = 0; i < localPlanes.Count; i++)
                planeBuffer[planeStart + i] = Plane.Transform(localPlanes[i], combined);

            Aabb bounds = carverBrush.LocalBounds.Transform(combined);
            return new CarverInFrame(
                planeStart, localPlanes.Count, bounds, carverWins,
                carverBrush.Operation == BrushOperation.Subtractive, combined);
        }
    }
}
