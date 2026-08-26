using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Physics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Physics.Box3D.Native;

namespace SpectraEngine.Physics.Box3D;

/// <summary>
/// A Box3D-backed <see cref="IScenePhysics"/>: the compiled static world as
/// per-chunk static bodies carrying one convex hull per authored brush.
/// </summary>
/// <remarks>
/// <para>
/// <b>Collision comes from AUTHORED brushes, never from carved surfaces.</b>
/// The carve produces a crack-free skin for rendering; it does not produce
/// convex pieces, and a solver needs convex pieces. The authored brushes
/// already are convex by construction, so the placement list is both the
/// correct input and the cheap one.
/// </para>
/// <para>
/// <b>One static body per occupied chunk cell, positioned at the cell's
/// corner.</b> Hulls attach in cell-local coordinates, which is what keeps
/// collision precision position-independent in an unbounded world: a brush
/// 10 km out is described by numbers no larger than the 32-unit cell it sits
/// in. It also makes the dirty-cell recompile map straight onto physics —
/// rebuilding a cell's body is exactly as scoped as rebuilding its mesh.
/// </para>
/// <para>
/// <b>Synced at the compile-harvest slot, never inside the tick loop.</b>
/// Visible geometry and solid geometry have to change in the same instant, or a
/// player walks into an invisible wall for a frame.
/// </para>
/// </remarks>
public sealed class Box3DScenePhysics : IScenePhysics
{
    private readonly ILogger _logger;
    private readonly Dictionary<ChunkCoord, ChunkBody> _chunkBodies = [];

    // Hulls are cached only for the DURATION of one sync, deliberately. The
    // experiment that settled it: attach a hull to a shape, free the hull, then
    // drop a body on the shape — it lands correctly, so a shape copies its hull
    // into the world rather than referencing ours. Long-lived refcounting would
    // therefore buy nothing but memory across a large map. Within one sync the
    // cache still matters: one Brush instance commonly backs many placements.
    private readonly Dictionary<Brush, nint> _syncHulls = new(BrushReferenceComparer.Instance);
    private readonly List<ChunkCoord> _removalScratch = [];

    private B3WorldId _world;
    private CsgWorld? _syncedWorld;
    private bool _disposed;

    /// <summary>Creates the world. Throws if the loaded library cannot be trusted.</summary>
    public Box3DScenePhysics(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        // The ABI handshake, before anything else. Every managed struct in the
        // binding assumes the float build; a double library silently widens
        // positions and every struct containing one, and no later symptom would
        // point back here.
        if (B3.IsDoublePrecision())
        {
            throw new InvalidOperationException(
                "The loaded box3d library was built with BOX3D_DOUBLE_PRECISION, which invalidates " +
                "every struct layout this binding declares. Rebuild it with native/build-box3d.ps1.");
        }

        // BEFORE DefaultWorldDef, not merely before the world: the default defs
        // bake the length scale into contact speed, maximum linear speed, sleep
        // threshold and density AT CALL TIME.
        B3.SetLengthUnitsPerMeter(PhysicsDefaults.MetresPerUnit);

        B3WorldDef def = B3.DefaultWorldDef();
        def.Gravity = B3Vec3.From(PhysicsDefaults.Gravity);

        _world = B3.CreateWorld(in def);
        if (_world.Index1 == 0)
        {
            throw new InvalidOperationException(
                "Box3D refused to create a world. In a release build a zeroed id is the only " +
                "signal it gives — the usual cause is exceeding the library's world limit.");
        }

        int workers = B3.World_GetWorkerCount(_world);
        B3Version version = B3.GetVersion();
        _logger.LogInformation(
            "Box3D {Version} world created: {Workers} worker(s), gravity {Gravity} sunit/s², " +
            "1 sunit = {Metres} m, fixed tick {Hz} Hz",
            version, workers, PhysicsDefaults.Gravity.Y, PhysicsDefaults.MetresPerUnit,
            PhysicsDefaults.TicksPerSecond);

        if (workers != 1)
        {
            // Not fatal, but it means the library spawned threads we did not ask
            // for — which changes determinism and is worth seeing rather than
            // discovering later through an irreproducible result.
            _logger.LogWarning(
                "Box3D reports {Workers} workers; the serial path was expected. Simulation is no " +
                "longer single-threaded.", workers);
        }
    }

    /// <inheritdoc/>
    public bool IsSimulating => true;

    /// <inheritdoc/>
    public int BodyCount => _chunkBodies.Count;

    /// <inheritdoc/>
    public int StaticShapeCount { get; private set; }

    /// <summary>
    /// Brushes whose collision does NOT reflect a subtractive brush cutting
    /// them, as of the last sync.
    /// </summary>
    /// <remarks>
    /// <b>Reported rather than silently shipped, because it is a real
    /// divergence.</b> The compiled solid is
    /// <c>⋃{additive} \ ⋃{subtractive}</c>, but a convex hull per additive
    /// brush cannot express the subtraction — so a doorway you can see through
    /// is currently solid to the solver. Deciding the representation (an exact
    /// plane-set convex decomposition, a per-chunk trimesh, or refusing
    /// collision on cut geometry) is an open call recorded in
    /// <c>docs/physics.md</c>. Until it is made, this counter is how the
    /// discrepancy stays visible instead of arriving as a bug report about
    /// invisible walls.
    /// </remarks>
    public int CutBrushesWithoutCollision { get; private set; }

    /// <summary>The native world handle, for tests and diagnostics.</summary>
    internal B3WorldId World => _world;

    /// <inheritdoc/>
    public void SyncStaticWorld(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ThrowIfDisposed();

        CsgWorld? world = scene.StaticWorld;

        // Identity compare, so the steady state costs one reference check per
        // frame. A compile that lands produces a NEW CsgWorld instance; nothing
        // else can change the static world.
        if (ReferenceEquals(world, _syncedWorld))
            return;

        if (world is null)
        {
            DestroyAllChunkBodies();
            // An empty tree has nothing left to optimise, so churn accrued
            // toward the amortised rebuild is meaningless now and must not
            // leak into the next world's accounting.
            _staticShapeChurnSinceRebuild = 0;
            _syncedWorld = null;
            return;
        }

        IReadOnlyList<ChunkCoord>? dirty = world.DirtyCells;
        try
        {
            if (dirty is null)
            {
                // A full compile: every cell may have changed, and cells may
                // have vanished. Rebuilding wholesale is both correct and rare
                // — it is the load-time and structural-edit path.
                DestroyAllChunkBodies();
                CutBrushesWithoutCollision = 0;
                foreach (WorldChunk chunk in world.Chunks.OrderedChunks)
                    BuildChunkBody(world, chunk);

                // The one intended use of the full rebuild: optimise the tree
                // once after bulk creation. Its cost is O(world log world),
                // which is exactly what a load or a structural edit already
                // paid for the compile itself.
                RebuildStaticTree();
            }
            else
            {
                // An incremental compile: only the dirty cells can differ, so
                // only they are rebuilt. This is what keeps physics on the same
                // world-size-independent footing as the mesh swap, and it is
                // why there is NO tree rebuild down here. Box3D inserts and
                // removes static leaves at shape create/destroy time (see
                // b3CreateShapeProxy), so the tree stays correct without one;
                // a rebuild only restores QUALITY, and calling it per sync was
                // an O(world log world) pass on the render thread every frame
                // a world brush moved, which falsified the sentence above.
                // (docs/physics.md also records the API's own header calling
                // it internal testing, i.e. not the knob for chunk churn.)
                for (int i = 0; i < dirty.Count; i++)
                {
                    ChunkCoord coord = dirty[i];
                    int destroyed = DestroyChunkBody(coord);
                    int created = 0;
                    if (world.Chunks.TryGet(coord, out WorldChunk chunk))
                        created = BuildChunkBody(world, chunk);

                    // Only the NET change counts as tree-quality churn. The
                    // steady state of an animating world brush is this exact
                    // loop rebuilding the SAME cell every landed compile:
                    // leaves removed, near-identical AABBs re-inserted, tree
                    // quality essentially untouched. Counting gross
                    // destroy+create would cross the threshold every few
                    // hundred frames and re-arm a world-sized rebuild forever,
                    // which is the cost this policy exists to remove. Growth
                    // and shrinkage are what actually drift the tree.
                    _staticShapeChurnSinceRebuild += Math.Abs(created - destroyed);
                }

                // Net inserts degrade tree quality gradually, so the rebuild
                // is amortised: once per quarter of the world's shapes changed
                // (floor for small worlds), the per-edit cost stays
                // world-size independent while the tree never drifts far from
                // optimal.
                if (_staticShapeChurnSinceRebuild > Math.Max(RebuildChurnFloor, StaticShapeCount / 4))
                    RebuildStaticTree();
            }
        }
        finally
        {
            ReleaseSyncHulls();
        }

        _syncedWorld = world;
    }

    // Amortisation floor: below this much accumulated churn the tree is never
    // rebuilt, because a few hundred inserted leaves cannot degrade a query
    // measurably. Above it, see the quarter-of-the-world rule at the call site.
    private const int RebuildChurnFloor = 256;

    private int _staticShapeChurnSinceRebuild;

    /// <summary>
    /// Full static-tree rebuilds performed, for tests and diagnostics: the
    /// count must track loads and amortisation thresholds, never every sync.
    /// </summary>
    internal int StaticTreeRebuilds { get; private set; }

    private void RebuildStaticTree()
    {
        B3.World_RebuildStaticTree(_world);
        _staticShapeChurnSinceRebuild = 0;
        StaticTreeRebuilds++;
    }

    /// <inheritdoc/>
    public void PushKinematicTargets(float fixedDt)
    {
        ThrowIfDisposed();
        // Nothing is kinematic yet: part brushes and moving platforms take this
        // slot when they gain bodies.
    }

    /// <inheritdoc/>
    public void Step(float fixedDt)
    {
        ThrowIfDisposed();
        B3.World_Step(_world, fixedDt, SubStepCount);
    }

    /// <inheritdoc/>
    public void DrainEvents()
    {
        ThrowIfDisposed();
        // No dynamic bodies yet, so there is nothing to drain. The slot exists
        // and is called in the right place — inside the tick loop, immediately
        // after the step that produced the events — so that filling it in later
        // is not a restructuring.
    }

    /// <inheritdoc/>
    public void PublishRenderPoses(float alpha)
    {
        ThrowIfDisposed();
        // Nothing is simulated yet, so there is nothing to interpolate.
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseSyncHulls();

        // Destroying the world destroys its bodies and shapes, so the chunk map
        // is dropped rather than walked.
        _chunkBodies.Clear();
        StaticShapeCount = 0;

        if (_world.Index1 != 0)
        {
            // Exactly once: the library decrements its global world count
            // BEFORE validating the id, so a double destroy corrupts that count
            // rather than being harmlessly ignored. Clearing the handle is what
            // makes a second Dispose a no-op.
            B3.DestroyWorld(_world);
            _world = default;
        }
    }

    // Sub-steps per tick. Four is the library's own default range and is what
    // keeps a stack of boxes from sinking; it is not a tuning knob anybody has
    // measured here yet.
    private const int SubStepCount = 4;

    // Returns the number of shapes created, which is what the amortised
    // static-tree rebuild counts as churn.
    private int BuildChunkBody(CsgWorld world, WorldChunk chunk)
    {
        IReadOnlyList<int> owned = chunk.OwnedBrushIndices;
        if (owned.Count == 0)
            return 0;

        // The body sits at the cell's corner and every hull is placed relative
        // to it, so no coordinate the solver sees exceeds the cell size however
        // far out the cell is.
        Vector3 origin = chunk.Coord.MinCorner;

        B3BodyDef bodyDef = B3.DefaultBodyDef();
        bodyDef.Type = B3BodyType.Static;
        bodyDef.Position = B3Pos.From(origin);

        B3BodyId body = B3.CreateBody(_world, in bodyDef);
        if (body.Index1 == 0)
        {
            _logger.LogError("Box3D refused a static body for chunk {Coord}.", chunk.Coord);
            return 0;
        }

        B3ShapeDef shapeDef = B3.DefaultShapeDef();
        // A static body has no mass to recompute, and leaving this on would make
        // a hundred-brush cell do a hundred redundant passes.
        shapeDef.UpdateBodyMass = 0;

        int shapes = 0;
        IReadOnlyList<BrushPlacement> placements = world.Placements;

        for (int i = 0; i < owned.Count; i++)
        {
            BrushPlacement placement = placements[owned[i]];
            Brush brush = placement.Brush;

            // A subtractive brush is a hole: it contributes no solid, so it gets
            // no hull. What it does NOT do is remove solid from the brushes it
            // cuts — see CutBrushesWithoutCollision.
            if (brush.Operation == BrushOperation.Subtractive)
                continue;

            if (IsCutBySubtractiveBrush(world, chunk, placement))
                CutBrushesWithoutCollision++;

            nint hull = AcquireHull(brush);
            if (hull == 0)
                continue;

            if (!TryDecomposeRigid(placement.Transform, origin, out B3Transform local))
            {
                _logger.LogError(
                    "Brush placement in chunk {Coord} has a non-rigid transform and was given no " +
                    "collision. Brush node transforms must be rotation and translation only.",
                    chunk.Coord);
                continue;
            }

            B3ShapeId shape = B3.CreateTransformedHullShape(
                body, in shapeDef, hull, local, new B3Vec3(1f, 1f, 1f));

            if (shape.Index1 == 0)
                _logger.LogError("Box3D refused a hull shape in chunk {Coord}.", chunk.Coord);
            else
                shapes++;
        }

        if (shapes == 0)
        {
            // A body with no shapes collides with nothing and costs a broadphase
            // entry, so it is not kept.
            B3.DestroyBody(body);
            return 0;
        }

        _chunkBodies[chunk.Coord] = new ChunkBody(body, shapes);
        StaticShapeCount += shapes;
        return shapes;
    }

    // Whether any subtractive brush resident in this cell overlaps the given
    // placement — i.e. whether this brush's compiled solid has a bite taken out
    // of it that its convex hull cannot express. Bounds-level and conservative:
    // it can over-report a brush whose AABB overlaps a negative that does not
    // actually cut it, which is the right direction for a warning.
    private static bool IsCutBySubtractiveBrush(CsgWorld world, WorldChunk chunk, BrushPlacement placement)
    {
        Aabb bounds = placement.WorldBounds;
        IReadOnlyList<int> resident = chunk.ResidentBrushIndices;
        IReadOnlyList<BrushPlacement> placements = world.Placements;

        for (int i = 0; i < resident.Count; i++)
        {
            BrushPlacement other = placements[resident[i]];
            if (other.Brush.Operation == BrushOperation.Subtractive && other.WorldBounds.Intersects(bounds))
                return true;
        }

        return false;
    }

    private nint AcquireHull(Brush brush)
    {
        if (_syncHulls.TryGetValue(brush, out nint cached))
            return cached;

        HullRefusal refusal = BrushHullBuilder.TryCreate(brush, out nint hull, out string detail);
        if (refusal != HullRefusal.None)
        {
            // Loudly, and never simplified: a reduced collision hull is a player
            // clipping through a wall that renders correctly.
            _logger.LogError("Brush has no collision ({Refusal}). {Detail}", refusal, detail);
            hull = 0;
        }

        // Cached either way, including the failure, so one bad brush produces
        // one log line per sync rather than one per placement of it.
        _syncHulls[brush] = hull;
        return hull;
    }

    private void ReleaseSyncHulls()
    {
        foreach (nint hull in _syncHulls.Values)
            BrushHullBuilder.Destroy(hull);
        _syncHulls.Clear();
    }

    // Returns the number of shapes destroyed, counted as churn like creation.
    private int DestroyChunkBody(ChunkCoord coord)
    {
        if (!_chunkBodies.Remove(coord, out ChunkBody entry))
            return 0;

        // Destroying a body destroys its shapes with it.
        B3.DestroyBody(entry.Body);
        StaticShapeCount -= entry.ShapeCount;
        return entry.ShapeCount;
    }

    private void DestroyAllChunkBodies()
    {
        _removalScratch.Clear();
        foreach (ChunkCoord coord in _chunkBodies.Keys)
            _removalScratch.Add(coord);

        for (int i = 0; i < _removalScratch.Count; i++)
            DestroyChunkBody(_removalScratch[i]);

        _removalScratch.Clear();
    }

    // Splits a rigid world matrix into a translation relative to the chunk
    // origin and a rotation. Rigid only — the scene's snapshot rejects a scaled
    // brush placement before it reaches a compile, so a non-rigid matrix here
    // means something bypassed that and is reported rather than approximated.
    private static bool TryDecomposeRigid(Matrix4x4 world, Vector3 origin, out B3Transform local)
    {
        local = default;

        if (!Matrix4x4.Decompose(world, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
            return false;

        const float scaleTolerance = 1e-3f;
        if (MathF.Abs(scale.X - 1f) > scaleTolerance ||
            MathF.Abs(scale.Y - 1f) > scaleTolerance ||
            MathF.Abs(scale.Z - 1f) > scaleTolerance)
        {
            return false;
        }

        local = new B3Transform
        {
            P = B3Vec3.From(translation - origin),
            Q = B3Quat.From(Quaternion.Normalize(rotation)),
        };
        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct ChunkBody(B3BodyId Body, int ShapeCount);

    // Brush identity, not equality: two structurally identical brushes are still
    // two independent hull builds, and Brush deliberately does not define value
    // equality.
    private sealed class BrushReferenceComparer : IEqualityComparer<Brush>
    {
        public static BrushReferenceComparer Instance { get; } = new();

        public bool Equals(Brush? x, Brush? y) => ReferenceEquals(x, y);

        public int GetHashCode(Brush obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
