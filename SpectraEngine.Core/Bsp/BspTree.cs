using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A solid-leaf BSP tree built from convex <see cref="Brush"/> solids. It
/// partitions space into solid and empty convex cells, giving fast
/// point-containment and ray queries — the static, query-oriented counterpart
/// to the dynamic scene graph.
/// </summary>
public sealed class BspTree
{
    // Plane-offset agreement threshold used only by splitter SCORING's
    // candidate de-duplication (never by queries): two candidate planes whose
    // D differs by less than this are treated as one candidate slot.
    private const float ScoringPlaneEpsilon = 1e-3f;

    // ---- Build tuning ----------------------------------------------------

    // Nodes at or below this size skip splitter scoring and take the first
    // polygon's plane, exactly as the original builder always did: at this
    // scale a scoring pass costs more than a mediocre split ever could.
    private const int ScoreThreshold = 25;

    // Distinct candidate planes scored per node when picking a splitter.
    private const int MaxCandidates = 12;

    // Upper bound on polygons sampled while scoring one node's candidates.
    // Scoring is a heuristic — a deterministic strided sample of the node's
    // polygons predicts balance and split counts well enough, and it caps the
    // scoring cost of huge nodes.
    private const int MaxScoreSamples = 256;

    // Classic BSP heuristic weight: cutting one polygon in two costs this many
    // units of front/back imbalance.
    private const int SplitPenalty = 8;

    // Front and back children are built as parallel tasks only while each side
    // still holds at least this many polygons; below it the spawn overhead
    // outweighs the remaining subtree work.
    private const int ParallelThreshold = 256;

    // Heuristic-only agreement threshold used by splitter SCORING (candidate
    // de-duplication and the consumed-for-free bonus in ChooseSplitterIndex).
    // The partition loop's actual coplanar consumption requires BIT-IDENTICAL
    // planes — see BuildNode; anything looser there changes query semantics.
    // Scoring only ranks candidates, so the tolerant test is fine for it.
    private const float CoplanarNormalAgreement = 0.999f;

    private BspTree(BspNode root) => Root = root;

    public BspNode Root { get; }

    /// <summary>
    /// Builds a BSP tree from a set of brushes, running CSG first so the tree
    /// is partitioned from a clean, non-overlapping surface set.
    /// </summary>
    public static BspTree Build(IReadOnlyList<Brush> brushes) =>
        BuildFromSurfaces(Csg.Carve(brushes));

    /// <summary>
    /// Builds a BSP tree directly from a set of surface polygons (typically the
    /// output of <see cref="Csg.Carve"/>). The build is deterministic: the same
    /// surface list always produces the same tree, regardless of how the
    /// parallel subtree tasks are scheduled.
    /// </summary>
    public static BspTree BuildFromSurfaces(IReadOnlyList<Polygon> surfaces)
    {
        var polygons = new List<Polygon>(surfaces);
        return new BspTree(BuildNode(polygons, solidIfEmpty: false));
    }

    // Recursively partitions polygons. An exhausted polygon list means the
    // region is unbounded by any further face: it is solid when reached down a
    // back edge (behind a face) and empty when reached down a front edge.
    //
    // Solid-marking semantics (pinned by Test/SpectraEngine.Bsp.Tests, do not
    // change): the front recursion passes solidIfEmpty:false and the back
    // recursion solidIfEmpty:true; coplanar polygons are sided by whether
    // their normal agrees with the splitter (dot >= 0 is the front side).
    private static BspNode BuildNode(List<Polygon> polygons, bool solidIfEmpty)
    {
        if (polygons.Count == 0)
            return BspNode.Leaf(solidIfEmpty);

        int splitterIndex = ChooseSplitterIndex(polygons);
        Plane splitter = polygons[splitterIndex].Surface;

        // Capacity hint: a scored splitter aims for balance, so half the
        // parent count is the expected side size; List doubling absorbs the
        // remainder without churn.
        int hint = polygons.Count / 2 + 4;
        var front = new List<Polygon>(hint);
        var back = new List<Polygon>(hint);

        for (int i = 0; i < polygons.Count; i++)
        {
            // The splitter's own polygon is consumed unconditionally — never
            // classified against its own plane. This mirrors the original
            // builder (which always dropped polygons[0]) and is what
            // guarantees termination: every node retires at least one
            // polygon, even if vertex snapping drifted it off its stored
            // Surface plane by more than the classification epsilon.
            if (i == splitterIndex)
                continue;

            Polygon poly = polygons[i];
            switch (poly.Classify(splitter))
            {
                case PolygonClassification.Front:
                    front.Add(poly);
                    break;
                case PolygonClassification.Back:
                    back.Add(poly);
                    break;
                case PolygonClassification.Coplanar:
                    // Same plane: side it by whether its normal agrees with
                    // the splitter (>= 0 is the front side, < 0 the back —
                    // the original sidedness convention, preserved exactly).
                    //
                    // Plane bucketing: a face whose Surface plane is
                    // BIT-IDENTICAL to the splitter's is consumed here
                    // instead of routed front. Routed front (the old
                    // behaviour), it would later split the front region with
                    // this very plane again, producing a node whose back
                    // child — a solid leaf — is unreachable: every
                    // point/segment in the front region already satisfied
                    // d >= 0 against the identical plane at this node, so the
                    // same comparison can never send it back. Dropping the
                    // duplicate changes the tree shape but not query results.
                    // Brush worlds are massively coplanar (fragments of one
                    // wall share one exact plane), which is what made the old
                    // one-node-per-coplanar-face chains quadratic.
                    //
                    // Bit-equality is load-bearing, not pedantry: Classify
                    // accepts vertices within Polygon.Epsilon of the
                    // splitter, and VertexSnapper moves vertices onto the
                    // snap grid while PRESERVING each polygon's pre-snap
                    // Surface plane — so a face can classify Coplanar here
                    // while its own plane is offset from the splitter's by up
                    // to Epsilon in D (or tilted within the tolerance). That
                    // face bounds a slightly DIFFERENT half-space; consuming
                    // it would delete its brush's boundary from the tree and
                    // flip ContainsPoint/Raycast results everywhere in the
                    // sliver between the two planes. Near-coincident planes
                    // therefore keep the original front/back routing, where
                    // the unreachability argument above does not apply but
                    // query semantics are untouched.
                    Plane surface = poly.Surface;
                    if (surface.Normal.Equals(splitter.Normal) && surface.D == splitter.D)
                        continue; // consumed by this node
                    if (Vector3.Dot(surface.Normal, splitter.Normal) >= 0f)
                        front.Add(poly);
                    else
                        back.Add(poly);
                    break;
                case PolygonClassification.Spanning:
                    poly.Split(splitter, out Polygon? f, out Polygon? b);
                    if (f is not null) front.Add(f);
                    if (b is not null) back.Add(b);
                    break;
            }
        }

        // The input list is dead past this point; the children own front/back
        // exclusively, so the subtrees can build in parallel with no shared
        // mutable state (Polygon is deeply immutable). Scheduling cannot
        // affect the result — each subtree is a pure function of its list —
        // so the build stays deterministic. The current thread takes the back
        // side and only then joins the front task; TPL inlines an unstarted
        // task into the joining thread, so blocked-worker pile-ups don't
        // starve the pool even with nested spawns.
        if (front.Count >= ParallelThreshold && back.Count >= ParallelThreshold)
        {
            List<Polygon> frontList = front;
            Task<BspNode> frontTask = Task.Run(() => BuildNode(frontList, solidIfEmpty: false));
            BspNode backChild = BuildNode(back, solidIfEmpty: true);
            return BspNode.Split(splitter, frontTask.GetAwaiter().GetResult(), backChild);
        }

        BspNode frontChild = BuildNode(front, solidIfEmpty: false);
        BspNode backChildSeq = BuildNode(back, solidIfEmpty: true);
        return BspNode.Split(splitter, frontChild, backChildSeq);
    }

    // Picks the polygon whose plane should split this node. Small nodes take
    // the first polygon (the original builder's rule); larger nodes score a
    // bounded, deterministically strided set of candidate planes and keep the
    // one minimising  splits * SplitPenalty + |front - back|  — few cut
    // polygons, balanced children. Scoring uses a cheap conservative
    // AABB-versus-plane test, not Polygon.Classify: it only has to rank
    // candidates, while the partition loop above does the one exact,
    // semantics-bearing classification per polygon. Ties keep the earliest
    // candidate, so the choice is a pure function of the polygon list.
    private static int ChooseSplitterIndex(List<Polygon> polygons)
    {
        int count = polygons.Count;
        if (count <= ScoreThreshold)
            return 0;

        int candidateStep = Math.Max(1, count / MaxCandidates);
        int sampleStep = Math.Max(1, count / MaxScoreSamples);

        // Planes already scored, so several fragments of one wall don't burn
        // multiple candidate slots on the same answer. Sign-sensitive on
        // purpose: a plane and its flipped twin bound solid on opposite sides
        // and are genuinely different splitters.
        Span<Plane> seen = stackalloc Plane[MaxCandidates];
        int seenCount = 0;

        int bestIndex = 0;
        long bestScore = long.MaxValue;

        for (int c = 0, idx = 0; c < MaxCandidates && idx < count; c++, idx += candidateStep)
        {
            Plane candidate = polygons[idx].Surface;

            bool duplicate = false;
            for (int s = 0; s < seenCount; s++)
            {
                if (Vector3.Dot(seen[s].Normal, candidate.Normal) >= CoplanarNormalAgreement &&
                    MathF.Abs(seen[s].D - candidate.D) <= ScoringPlaneEpsilon)
                {
                    duplicate = true;
                    break;
                }
            }
            if (duplicate)
                continue;
            seen[seenCount++] = candidate;

            Vector3 n = candidate.Normal;
            Vector3 absN = Vector3.Abs(n);
            int frontCount = 0, backCount = 0, splitCount = 0;

            for (int s = 0; s < count; s += sampleStep)
            {
                // Conservative interval test: centre distance vs. the box's
                // projected radius. Slanted coplanar faces (radius above
                // epsilon) land in the split bucket — a deliberate
                // overcount that only biases the heuristic, never the tree's
                // meaning.
                Aabb bounds = polygons[s].Bounds;
                float centerDist = Plane.DotCoordinate(candidate, bounds.Center);
                float radius = Vector3.Dot((bounds.Max - bounds.Min) * 0.5f, absN);

                if (centerDist - radius > Polygon.Epsilon)
                {
                    frontCount++;
                }
                else if (centerDist + radius < -Polygon.Epsilon)
                {
                    backCount++;
                }
                else if (radius <= Polygon.Epsilon)
                {
                    // Coplanar. Approximate the partition loop: same-facing
                    // faces are counted as consumed for free (no count — an
                    // implicit bonus for splitters that retire many coplanar
                    // faces), the rest side by normal agreement. This is
                    // looser than the partition loop's bit-identical
                    // consumption test on purpose — scoring only ranks
                    // candidates and can never affect query semantics.
                    float agreement = Vector3.Dot(polygons[s].Surface.Normal, n);
                    if (agreement >= CoplanarNormalAgreement)
                        continue;
                    if (agreement >= 0f)
                        frontCount++;
                    else
                        backCount++;
                }
                else
                {
                    splitCount++;
                }
            }

            long score = (long)splitCount * SplitPenalty + Math.Abs(frontCount - backCount);
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = idx;
            }
        }

        return bestIndex;
    }

    /// <summary>True when the point lies inside solid space.</summary>
    public bool ContainsPoint(Vector3 point)
    {
        BspNode node = Root;
        while (!node.IsLeaf)
        {
            float d = Plane.DotCoordinate(node.Plane, point);
            node = d >= 0f ? node.Front! : node.Back!;
        }
        return node.IsSolid;
    }

    /// <summary>
    /// Casts a ray against solid space. Returns true and reports the first
    /// surface entered in <paramref name="hit"/>; false if the ray stays in
    /// empty space for the whole distance.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out BspRaycastHit hit)
    {
        hit = default;
        if (direction == Vector3.Zero || maxDistance <= 0f)
            return false;

        direction = Vector3.Normalize(direction);

        // A ray that starts inside solid space hits immediately.
        if (ContainsPoint(origin))
        {
            hit = new BspRaycastHit(origin, -direction, 0f);
            return true;
        }

        Vector3 end = origin + direction * maxDistance;
        if (TraceSegment(Root, origin, end, direction, hasEntry: false, default, out hit))
        {
            hit = hit with { Distance = Vector3.Distance(origin, hit.Point) };
            return true;
        }
        return false;
    }

    // Walks the segment a..b through the tree, returning the point where it
    // first crosses from empty into solid space. Entry into solid is detected
    // EXACTLY, by the leaf that contains each sub-segment: when a sub-segment's
    // start lands in a solid leaf, the last plane crossed on the way there
    // (`entryNormal`, oriented toward the side the ray came from) is the entry
    // surface. This replaces the old fixed-epsilon point probe
    // (ContainsPoint(mid + direction * 1e-3)), which sampled up to 1e-3 world
    // units PAST each crossing: it fabricated hits across genuine air gaps
    // thinner than the probe (reporting the near plane instead of the true far
    // surface) and tunnelled through solids thinner than the probe — and,
    // routed per cell, it silently sampled beyond the cell's weld-band
    // authority window (see CsgWorld.RaycastIntervalEpsilon), making routed
    // and monolithic answers diverge at cell borders. The leaf walk samples
    // nothing outside [a, b], so the routed interval contract holds by
    // construction, and it needs no displaced probe point — which also keeps
    // it exact far from the origin, where a small fixed offset would vanish
    // into float rounding.
    private static bool TraceSegment(
        BspNode node, Vector3 a, Vector3 b, Vector3 direction,
        bool hasEntry, Vector3 entryNormal, out BspRaycastHit hit)
    {
        if (node.IsLeaf)
        {
            if (!node.IsSolid)
            {
                hit = default;
                return false;
            }

            // The sub-segment starts inside solid space. With an entry plane
            // recorded, `a` is the crossing point on that plane — the entry
            // surface. Without one the whole ray started in solid, which the
            // caller's ContainsPoint(origin) check already reports; the
            // defensive fallback mirrors its starts-inside convention.
            hit = new BspRaycastHit(a, hasEntry ? entryNormal : -direction, 0f);
            return true;
        }

        float da = Plane.DotCoordinate(node.Plane, a);
        float db = Plane.DotCoordinate(node.Plane, b);

        if (da >= 0f && db >= 0f)
            return TraceSegment(node.Front!, a, b, direction, hasEntry, entryNormal, out hit);
        if (da < 0f && db < 0f)
            return TraceSegment(node.Back!, a, b, direction, hasEntry, entryNormal, out hit);

        float t = da / (da - db);
        Vector3 mid = Vector3.Lerp(a, b, t);

        BspNode near = da >= 0f ? node.Front! : node.Back!;
        BspNode far = da >= 0f ? node.Back! : node.Front!;

        // Resolve the near side first; the ray reaches it before the plane.
        if (TraceSegment(near, a, mid, direction, hasEntry, entryNormal, out hit))
            return true;

        // The near side is clear up to the plane; continue across it. The
        // crossed plane (oriented toward the incoming side) becomes the
        // candidate entry surface for whatever the far side resolves to.
        Vector3 crossingNormal = da >= 0f ? node.Plane.Normal : -node.Plane.Normal;
        return TraceSegment(far, mid, b, direction, hasEntry: true, crossingNormal, out hit);
    }
}
