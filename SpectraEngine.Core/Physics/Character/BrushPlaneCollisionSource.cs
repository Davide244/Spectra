using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Core.Physics.Character;

/// <summary>
/// A character collision source built from authored brush planes, with no
/// native dependency of any kind.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the source that can express subtraction, and that is not a
/// detail.</b> The compiled solid is <c>⋃additive \ ⋃subtractive</c>. A convex
/// hull per additive brush cannot represent the bite a negative takes out of
/// it, so a doorway you can see through stays solid — the divergence the
/// hull-based static collision has to count and report. A plane-set source has
/// no such limit, because <c>A \ N</c> is exactly the union of
/// <c>A ∩ {hₖ ≥ 0}</c> over N's planes: an <em>overlapping cover</em>, where
/// each element is still convex and still just a plane list. Entering the union
/// means entering some element, so a sweep is the minimum over elements and is
/// exact — including its normal.
/// </para>
/// <para>
/// <b>The two lanes never mix, and getting that wrong is not subtle.</b> World
/// geometry comes from the compiled static world, where admission already
/// guarantees the brush is a <see cref="BrushKind.World"/> brush and the
/// placement matches what is drawn. Part brushes come live from the spatial
/// index. A part brush is <em>never</em> cut by anything: a
/// <c>(Part, Subtractive)</c> brush is a legal, inert state — the flying
/// projectile of the destruction design — and letting it into the cover would
/// have it drilling a moving, invisible, capsule-sized hole through every wall
/// it flew past.
/// </para>
/// <para>
/// Render-thread only, like the scene it reads.
/// </para>
/// </remarks>
public sealed class BrushPlaneCollisionSource : ICharacterCollisionSource
{
    /// <summary>Cover elements one additive brush may expand to before it is refused.</summary>
    public const int MaxCoverPieces = 32;

    /// <summary>A cover element thinner than this along its generating plane is dropped.</summary>
    /// <remarks>
    /// Deliberately looser than the carve's own epsilon, and the direction is
    /// chosen: keeping a sliver produces a phantom invisible wall, dropping one
    /// produces at most a millimetre gap that the skin width — ten times larger
    /// — already covers.
    /// </remarks>
    public const float PieceEmptyEpsilon = 1e-3f;

    private const int MaxSweepIterations = 12;

    /// <summary>How far beyond the tick volume the world lane is built.</summary>
    /// <remarks>
    /// <para>
    /// The lane is a REGION, not the world, and the margin is what makes that
    /// affordable rather than thrashing. Building every placement would be
    /// O(world) work on every compile — and in a scene where anything animates,
    /// a compile lands nearly every frame, so a character standing still in one
    /// corner would rebuild the whole level continuously to walk three
    /// spectraunits.
    /// </para>
    /// <para>
    /// 24 units is roughly five seconds of sprinting, so ordinary movement
    /// rebuilds a few times a minute rather than a few hundred times a second,
    /// and a teleport costs exactly one rebuild.
    /// </para>
    /// </remarks>
    public const float RegionMargin = 24f;

    private readonly Scene.Scene _scene;
    private readonly CharacterTuning _tuning;

    // The world lane, rebuilt when a compile lands OR when the character leaves
    // the region it was built for.
    private readonly List<ConvexPiece> _worldPieces = [];
    private int _builtCompileCount = -1;
    private Aabb _builtRegion;
    private bool _hasRegion;

    // What the current lane was built FROM. Compared against a fresh selection
    // whenever a compile lands, so a recompile somewhere else in the world costs
    // a bounds scan instead of a rebuild — see RebuildWorldLaneIfStale.
    private readonly List<BrushPlacement> _builtAdditives = [];
    private readonly List<BrushPlacement> _builtNegatives = [];
    private readonly List<BrushPlacement> _scratchAdditives = [];
    private readonly List<BrushPlacement> _scratchNegatives = [];

    // The part lane plus this tick's candidates, both rebuilt per tick.
    private readonly List<ConvexPiece> _partPieces = [];
    private readonly List<SceneNode> _partScratch = [];
    private readonly List<ConvexPiece> _candidates = [];

    public BrushPlaneCollisionSource(Scene.Scene scene, CharacterTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(tuning);
        _scene = scene;
        _tuning = tuning;
    }

    /// <inheritdoc/>
    public int Revision { get; private set; }

    /// <inheritdoc/>
    public float SkinWidth => _tuning.SkinWidth;

    /// <inheritdoc/>
    public int DroppedPlanes { get; private set; }

    /// <summary>Additive brushes whose cut could not be represented and were treated as uncut.</summary>
    /// <remarks>
    /// Inherits the hull path's posture: a loud count rather than a silent wrong
    /// answer. Non-zero means somewhere in the level a character collides with
    /// geometry that is not drawn.
    /// </remarks>
    public int UncoveredCutBrushes { get; private set; }

    /// <summary>Cover elements the world lane currently holds.</summary>
    public int WorldPieceCount => _worldPieces.Count;

    /// <summary>Pieces the last <see cref="BeginTick"/> selected as candidates.</summary>
    public int CandidateCount => _candidates.Count;

    /// <summary>Times the world lane has been rebuilt — a compile landing, or the character leaving its region.</summary>
    /// <remarks>
    /// Worth watching: a count that climbs with the frame counter means the
    /// region is not holding, and every tick is paying an O(region) rebuild it
    /// should be amortising over thousands.
    /// </remarks>
    public int WorldLaneRebuilds { get; private set; }

    /// <summary>
    /// Selects the pieces a tick can possibly touch, once, so every sweep and
    /// gather in that tick shares one broad phase.
    /// </summary>
    /// <remarks>
    /// Sound because nothing in the scene moves during a tick: kinematic targets
    /// are pushed before the mover runs, and the static world only ever swaps
    /// between frames.
    /// </remarks>
    public void BeginTick(in Aabb volume, in CharacterQueryFilter filter)
    {
        RebuildWorldLaneIfStale(in volume);
        RebuildPartLane(in volume, in filter);

        _candidates.Clear();
        for (int i = 0; i < _worldPieces.Count; i++)
        {
            if (_worldPieces[i].Bounds.Intersects(volume))
                _candidates.Add(_worldPieces[i]);
        }

        for (int i = 0; i < _partPieces.Count; i++)
        {
            if (_partPieces[i].Bounds.Intersects(volume))
                _candidates.Add(_partPieces[i]);
        }
    }

    /// <inheritdoc/>
    public float SweepCapsule(
        in CharacterCapsule capsule,
        Vector3 translation,
        in CharacterQueryFilter filter,
        out CharacterContactPlane plane,
        out CharacterContactSource source)
    {
        plane = default;
        source = default;

        float best = 1f;
        bool hit = false;

        for (int i = 0; i < _candidates.Count; i++)
        {
            ConvexPiece piece = _candidates[i];
            if (IsSelf(in piece, in filter))
                continue;

            float fraction = CapsuleGeometry.Sweep(
                in capsule, translation, piece.Planes, piece.Faces,
                _tuning.SkinWidth, MaxSweepIterations, CharacterPlaneSolver.Tolerance * 0.25f,
                out Vector3 normal, out Vector3 point);

            if (fraction >= best)
                continue;

            // A hit on a face buried inside a sibling is a hit on nothing: the
            // capsule is entering the union somewhere else, and whichever
            // element owns that part of the boundary will report it.
            if (IsInternalContact(in piece, point))
                continue;

            best = fraction;
            hit = true;

            // The plane's D is baked for the capsule AT THE HIT POSITION, so it
            // feeds the solver directly with no further arithmetic.
            CharacterCapsule atHit = capsule.Translated(translation * fraction);
            float separation = SurfaceSeparation(in atHit, normal, point);
            plane = CharacterContactPlane.Rigid(normal, separation);
            source = new CharacterContactSource
            {
                Node = piece.Node,
                Brush = piece.Brush,
                PlaneIndex = piece.PlaneIndex,
                Point = point,
            };
        }

        return hit ? best : 1f;
    }

    /// <inheritdoc/>
    public int GatherPlanes(
        in CharacterCapsule capsule,
        float maxSeparation,
        in CharacterQueryFilter filter,
        Span<CharacterContactPlane> planes,
        Span<CharacterContactSource> sources)
    {
        int count = 0;

        for (int i = 0; i < _candidates.Count; i++)
        {
            ConvexPiece piece = _candidates[i];
            if (IsSelf(in piece, in filter))
                continue;

            float distance = CapsuleGeometry.Distance(
                in capsule, piece.Planes, piece.Faces, out Vector3 normal, out Vector3 point);

            if (distance > maxSeparation)
                continue;

            if (IsInternalContact(in piece, point))
                continue;

            var candidatePlane = CharacterContactPlane.Rigid(normal, distance);
            var candidateSource = new CharacterContactSource
            {
                Node = piece.Node,
                Brush = piece.Brush,
                PlaneIndex = piece.PlaneIndex,
                Point = point,
            };

            // ONE PHYSICAL SURFACE, ONE PLANE — however many brushes model it.
            //
            // In a CSG world overlapping solids are the normal case, not the
            // exception: a staircase is a stack of boxes each sunk into the
            // floor, a ramp is a wedge buried in the same slab, and a platform
            // sits on it flush. Every one of those puts two or three pieces'
            // faces on the identical plane at the identical separation, and
            // without this the contact budget is spent on copies of the floor
            // while a wall gets dropped. Measured on the demo course before this:
            // a hundred dropped planes in ten seconds of ordinary walking.
            //
            // The deeper of a duplicate pair wins, which matters when they are
            // near-identical rather than exactly equal.
            int duplicate = -1;
            for (int j = 0; j < count; j++)
            {
                if (Vector3.Dot(planes[j].Plane.Normal, normal) > DuplicateContactDot &&
                    MathF.Abs(planes[j].Plane.D - distance) < DuplicateContactOffset)
                {
                    duplicate = j;
                    break;
                }
            }

            if (duplicate >= 0)
            {
                if (distance < planes[duplicate].Plane.D)
                {
                    planes[duplicate] = candidatePlane;
                    sources[duplicate] = candidateSource;
                }
                continue;
            }

            if (count < planes.Length)
            {
                planes[count] = candidatePlane;
                sources[count] = candidateSource;
                count++;
                continue;
            }

            // Full: keep the deepest, and say that something was dropped rather
            // than letting a wall quietly stop existing.
            int shallowest = 0;
            for (int j = 1; j < count; j++)
            {
                if (planes[j].Plane.D > planes[shallowest].Plane.D)
                    shallowest = j;
            }

            DroppedPlanes++;
            if (distance < planes[shallowest].Plane.D)
            {
                planes[shallowest] = candidatePlane;
                sources[shallowest] = candidateSource;
            }
        }

        // Deepest first, so a truncating consumer keeps what matters.
        for (int i = 1; i < count; i++)
        {
            for (int j = i; j > 0 && planes[j].Plane.D < planes[j - 1].Plane.D; j--)
            {
                (planes[j], planes[j - 1]) = (planes[j - 1], planes[j]);
                (sources[j], sources[j - 1]) = (sources[j - 1], sources[j]);
            }
        }

        return count;
    }

    // Whether a contact point lies strictly inside a sibling cover element, and
    // is therefore in the interior of the solid rather than on its surface.
    //
    // The slack is what keeps a point on a SHARED boundary — where two elements
    // meet along the original brush's own face — from being rejected as
    // internal. Only genuinely buried points are dropped.
    private static bool IsInternalContact(in ConvexPiece piece, Vector3 point)
    {
        Plane[][]? siblings = piece.Siblings;
        if (siblings is null)
            return false;

        for (int i = 0; i < siblings.Length; i++)
        {
            if (ReferenceEquals(siblings[i], piece.Planes))
                continue;
            if (CapsuleGeometry.ContainsPoint(point, siblings[i], -InternalContactSlack))
                return true;
        }

        return false;
    }

    /// <summary>How far inside a sibling a contact must be before it is judged internal.</summary>
    private const float InternalContactSlack = 1e-3f;

    /// <summary>Two contact normals closer than this are the same surface.</summary>
    private const float DuplicateContactDot = 0.999f;

    /// <summary>...and only if their separations agree to this, so a step above a floor stays two planes.</summary>
    private const float DuplicateContactOffset = 1e-3f;

    // The self test needs BOTH sides to be a real node. A world piece has no
    // node, and so does a filter that excludes nothing — so a bare reference
    // compare makes every piece of world geometry look like the character
    // itself and the character falls through the entire level.
    private static bool IsSelf(in ConvexPiece piece, in CharacterQueryFilter filter) =>
        filter.Self is not null && ReferenceEquals(piece.Node, filter.Self);

    // The capsule's surface separation from a contact, expressed so that
    // Plane.DotCoordinate against a translation gives the separation after it.
    private static float SurfaceSeparation(in CharacterCapsule capsule, Vector3 normal, Vector3 point)
    {
        float d1 = Vector3.Dot(normal, capsule.Center1 - point);
        float d2 = Vector3.Dot(normal, capsule.Center2 - point);
        return MathF.Min(d1, d2) - capsule.Radius;
    }

    private void RebuildWorldLaneIfStale(in Aabb volume)
    {
        int compileCount = _scene.StaticWorldCompileCount;
        bool covered = _hasRegion && Contains(in _builtRegion, in volume);
        if (compileCount == _builtCompileCount && covered)
            return;

        // Keep the existing region when the character is still inside it, so a
        // recompile does not silently re-centre the lane and make the region
        // cache useless the moment anything animates.
        Aabb region = covered ? _builtRegion : volume.Expanded(RegionMargin);

        SelectPlacements(in region, _scratchAdditives, _scratchNegatives);

        // A COMPILE LANDING IS NOT A REASON TO REBUILD — only a compile that
        // changed something this lane is built from is.
        //
        // In any scene where something animates, a compile lands nearly every
        // frame: the demo alone recompiles about seven hundred times a second
        // because one pillar bobs. Invalidating on the compile counter meant the
        // character rebuilt its entire neighbourhood sixty times a second to
        // stand still next to geometry that had not moved since it spawned.
        //
        // Comparing the SELECTION rather than the counter costs one bounds scan
        // over the placement list — the same scan the rebuild would do anyway —
        // and answers the real question. Brushes compare by reference and
        // transforms by value, which is exactly what every other change detector
        // in the engine does: a brush is immutable, so a new reference IS the
        // edit.
        if (covered &&
            SameSelection(_builtAdditives, _scratchAdditives) &&
            SameSelection(_builtNegatives, _scratchNegatives))
        {
            _builtCompileCount = compileCount;
            return;
        }

        _builtCompileCount = compileCount;
        _builtRegion = region;
        _hasRegion = true;
        WorldLaneRebuilds++;
        Revision++;
        _worldPieces.Clear();
        UncoveredCutBrushes = 0;

        _builtAdditives.Clear();
        _builtAdditives.AddRange(_scratchAdditives);
        _builtNegatives.Clear();
        _builtNegatives.AddRange(_scratchNegatives);

        for (int i = 0; i < _builtAdditives.Count; i++)
            AddCoveredPieces(_builtAdditives[i], _builtNegatives);
    }

    // The additive brushes inside the region, and EVERY negative in the world.
    //
    // The asymmetry is deliberate. An additive brush outside the region is
    // skipped because nothing will ever query it; a negative outside it may
    // still cut a brush that straddles the boundary, and dropping that negative
    // would restore the solid it removed — a doorway that seals itself as you
    // walk away from it. Negatives are a handful of placement references, so
    // keeping all of them costs a pointer each.
    private void SelectPlacements(in Aabb region, List<BrushPlacement> additives, List<BrushPlacement> negatives)
    {
        additives.Clear();
        negatives.Clear();

        if (_scene.StaticWorld is not { } world)
            return;

        IReadOnlyList<BrushPlacement> placements = world.Placements;
        for (int i = 0; i < placements.Count; i++)
        {
            BrushPlacement placement = placements[i];
            if (placement.Brush.Operation == BrushOperation.Subtractive)
            {
                negatives.Add(placement);
                continue;
            }

            if (placement.WorldBounds.Intersects(region))
                additives.Add(placement);
        }
    }

    private static bool SameSelection(List<BrushPlacement> built, List<BrushPlacement> fresh)
    {
        if (built.Count != fresh.Count)
            return false;

        for (int i = 0; i < built.Count; i++)
        {
            if (!ReferenceEquals(built[i].Brush, fresh[i].Brush) ||
                built[i].Transform != fresh[i].Transform)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(in Aabb outer, in Aabb inner) =>
        inner.Min.X >= outer.Min.X && inner.Max.X <= outer.Max.X &&
        inner.Min.Y >= outer.Min.Y && inner.Max.Y <= outer.Max.Y &&
        inner.Min.Z >= outer.Min.Z && inner.Max.Z <= outer.Max.Z;

    private void AddCoveredPieces(in BrushPlacement placement, List<BrushPlacement> negatives)
    {
        Brush brush = placement.Brush;
        Matrix4x4 transform = placement.Transform;
        Aabb bounds = placement.WorldBounds;

        // Which negatives actually reach this brush.
        var cutters = new List<BrushPlacement>();
        for (int i = 0; i < negatives.Count; i++)
        {
            if (negatives[i].WorldBounds.Intersects(bounds))
                cutters.Add(negatives[i]);
        }

        Plane[] basePlanes = WorldPlanes(brush, transform);

        if (cutters.Count == 0)
        {
            // The overwhelmingly common case: an uncut brush is one piece, and
            // its faces are the ones the renderer already built.
            _worldPieces.Add(new ConvexPiece(
                basePlanes, WorldFaces(brush, transform), bounds, brush, null, -1));
            return;
        }

        // The cover: A \ (N1 ∪ N2 ∪ …) is the intersection over cutters of
        // (A \ Ni), and each of those is the union over Ni's planes of
        // A ∩ {flipped plane}. Composing them is the Cartesian product, pruned
        // hard by emptiness — most combinations describe nothing.
        var pieces = new List<List<Plane>> { new(basePlanes) };

        for (int c = 0; c < cutters.Count; c++)
        {
            Plane[] cutterPlanes = WorldPlanes(cutters[c].Brush, cutters[c].Transform);
            var next = new List<List<Plane>>();

            for (int p = 0; p < pieces.Count; p++)
            {
                for (int k = 0; k < cutterPlanes.Length; k++)
                {
                    // Flipped: "outside this face of the negative", which is the
                    // half-space the solid survives in.
                    var flipped = new Plane(-cutterPlanes[k].Normal, -cutterPlanes[k].D);

                    var combined = new List<Plane>(pieces[p].Count + 1);
                    combined.AddRange(pieces[p]);

                    // A flush cut makes the flipped plane IDENTICAL to one the
                    // brush already has, in which case it constrains nothing and
                    // the element is just the parent. Adding it anyway leaves a
                    // duplicate that the face builder has to defend against, so
                    // it is dropped here where the reason is visible.
                    bool redundant = false;
                    for (int e = 0; e < combined.Count; e++)
                    {
                        if (ConvexFaceBuilder.SameDirectedPlane(combined[e], flipped))
                        {
                            redundant = true;
                            break;
                        }
                    }

                    if (!redundant)
                        combined.Add(flipped);

                    next.Add(combined);
                }
            }

            pieces = next;
            if (pieces.Count > MaxCoverPieces * cutterPlanes.Length)
                break;
        }

        // Build the surviving elements first, then hand every one of them the
        // whole set: a piece cannot know which of its faces are real until it
        // knows what the others cover.
        var survivingPlanes = new List<Plane[]>();
        var survivingFaces = new List<Polygon[]>();

        for (int p = 0; p < pieces.Count && survivingPlanes.Count < MaxCoverPieces; p++)
        {
            Plane[] planes = [.. pieces[p]];
            Polygon[] faces = ConvexFaceBuilder.Build(planes, PieceEmptyEpsilon);
            if (faces.Length < 4)
                continue;   // empty or degenerate: this combination describes nothing

            survivingPlanes.Add(planes);
            survivingFaces.Add(faces);
        }

        int emitted = survivingPlanes.Count;
        Plane[][] siblings = [.. survivingPlanes];

        for (int p = 0; p < emitted; p++)
        {
            _worldPieces.Add(new ConvexPiece(
                survivingPlanes[p], survivingFaces[p],
                ConvexFaceBuilder.Bounds(survivingFaces[p]), brush, null, -1)
            {
                Siblings = siblings,
            });
        }

        if (emitted == 0)
        {
            // Fully annihilated — the negative swallowed the brush. Correct, and
            // exactly what a hull-per-brush design cannot express.
            return;
        }

        if (emitted >= MaxCoverPieces)
        {
            // Refused loudly rather than approximated: a brush cut by this many
            // overlapping negatives is a content pathology, and silently
            // dropping elements would open holes nobody authored.
            UncoveredCutBrushes++;
        }
    }

    private void RebuildPartLane(in Aabb volume, in CharacterQueryFilter filter)
    {
        _partPieces.Clear();
        if (!filter.IncludeParts)
            return;

        _partScratch.Clear();
        _scene.GetPartBoundsInBox(in volume, _partScratch);

        for (int i = 0; i < _partScratch.Count; i++)
        {
            SceneNode node = _partScratch[i];
            if (node.Brush is not { } brush)
                continue;

            // BOTH of these rejections are load-bearing. Without the kind test
            // every world brush is gathered twice — once from the compiled lane
            // and once from here — doubling its contact planes and disagreeing
            // with itself mid-edit. Without the operation test a legal, inert
            // (Part, Subtractive) brush becomes a moving hole in solid walls.
            if (node.BrushKind != BrushKind.Part)
                continue;
            if (brush.Operation == BrushOperation.Subtractive)
                continue;
            if (!node.CanCollide)
                continue;

            Matrix4x4 world = node.WorldMatrix;
            _partPieces.Add(new ConvexPiece(
                WorldPlanes(brush, world),
                WorldFaces(brush, world),
                brush.LocalBounds.Transform(world),
                brush,
                node,
                -1));
        }
    }

    private static Plane[] WorldPlanes(Brush brush, Matrix4x4 transform)
    {
        IReadOnlyList<Plane> local = brush.LocalPlanes;
        var planes = new Plane[local.Count];
        for (int i = 0; i < planes.Length; i++)
            planes[i] = Plane.Transform(local[i], transform);
        return planes;
    }

    private static Polygon[] WorldFaces(Brush brush, Matrix4x4 transform)
    {
        IReadOnlyList<Polygon> local = brush.LocalFaces;
        var faces = new Polygon[local.Count];
        for (int i = 0; i < faces.Length; i++)
            faces[i] = local[i].Transformed(transform);
        return faces;
    }

    /// <summary>One convex element of the collision world.</summary>
    private readonly struct ConvexPiece(
        Plane[] planes, Polygon[] faces, Aabb bounds, Brush? brush, SceneNode? node, int planeIndex)
    {
        /// <summary>
        /// The other cover elements of the same brush, or null when this piece
        /// is a whole uncut brush.
        /// </summary>
        /// <remarks>
        /// <b>Needed because a cover element has faces that are not surfaces.</b>
        /// The elements of <c>A \ N</c> overlap, so each one's cut face lies in
        /// the INTERIOR of the union rather than on its boundary — an invisible
        /// wall standing exactly where a doorway was carved. The union's real
        /// boundary is the part of each element's faces that no sibling
        /// contains, so a contact has to be tested against the siblings before
        /// it is believed.
        /// </remarks>
        public Plane[][]? Siblings { get; init; }

        public Plane[] Planes { get; } = planes;

        public Polygon[] Faces { get; } = faces;

        public Aabb Bounds { get; } = bounds;

        public Brush? Brush { get; } = brush;

        public SceneNode? Node { get; } = node;

        public int PlaneIndex { get; } = planeIndex;
    }
}
