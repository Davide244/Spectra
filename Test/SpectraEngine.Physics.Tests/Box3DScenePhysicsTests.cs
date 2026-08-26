using Xunit;
using System;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Bsp.Tests;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Physics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Physics.Box3D;
using SpectraEngine.Physics.Box3D.Native;

namespace SpectraEngine.Physics.Tests;

/// <summary>
/// The compiled static world becoming collision: per-chunk static bodies
/// carrying one hull per authored brush, synced at the harvest slot.
/// </summary>
[Collection(NativeWorldCollection.Name)]
public sealed class Box3DScenePhysicsTests
{
    private static bool NativeAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "box3d.dll"));

    private static void RequireNative() =>
        Assert.SkipWhen(
            !NativeAvailable,
            "box3d.dll is not present beside the test binary — build it with: native/build-box3d.ps1");

    [Fact]
    public void A_world_with_no_static_geometry_syncs_to_nothing()
    {
        RequireNative();
        var scene = new Scene("Test");
        using var physics = new Box3DScenePhysics(NullLogger.Instance);

        physics.SyncStaticWorld(scene);

        physics.BodyCount.ShouldBe(0);
        physics.StaticShapeCount.ShouldBe(0);
        physics.IsSimulating.ShouldBeTrue("a real backend says so even when the world is empty");
    }

    [Fact]
    public void Each_occupied_chunk_gets_one_static_body()
    {
        RequireNative();
        var scene = new Scene("Test");
        AddWorldBrush(scene, "a", Vector3.Zero, new Vector3(2f, 1f, 2f));
        scene.RebuildStaticWorld(new FakeRenderer());

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);

        physics.BodyCount.ShouldBeGreaterThan(0);
        physics.StaticShapeCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Two_brushes_far_apart_land_in_separate_chunk_bodies()
    {
        // The per-cell design, observable: cells are 32 units, so brushes 200
        // units apart cannot share a body. This is what keeps collision
        // coordinates small however far out the geometry sits.
        RequireNative();
        var scene = new Scene("Test");
        AddWorldBrush(scene, "near", Vector3.Zero, new Vector3(1f, 1f, 1f));
        AddWorldBrush(scene, "far", new Vector3(200f, 0f, 0f), new Vector3(1f, 1f, 1f));
        scene.RebuildStaticWorld(new FakeRenderer());

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);

        physics.BodyCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Syncing_an_unchanged_world_twice_changes_nothing()
    {
        // The steady state is one reference compare per frame. A sync that
        // rebuilt on every call would churn every body in the map every frame
        // and nothing would look wrong until somebody profiled it.
        RequireNative();
        var scene = new Scene("Test");
        AddWorldBrush(scene, "a", Vector3.Zero, new Vector3(2f, 1f, 2f));
        scene.RebuildStaticWorld(new FakeRenderer());

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);
        int bodies = physics.BodyCount;
        int shapes = physics.StaticShapeCount;

        physics.SyncStaticWorld(scene);
        physics.SyncStaticWorld(scene);

        physics.BodyCount.ShouldBe(bodies);
        physics.StaticShapeCount.ShouldBe(shapes);
    }

    [Fact]
    public void A_part_brush_gets_no_static_collision()
    {
        // A part brush is not in the placement list at all, so physics inherits
        // the world/part split for free by consuming that one list — it never
        // learns what a BrushKind is.
        RequireNative();
        var scene = new Scene("Test");
        SceneNode part = scene.Root.CreateChild("part");
        part.BrushKind = BrushKind.Part;
        part.Brush = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        scene.RebuildStaticWorld(new FakeRenderer());

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);

        physics.StaticShapeCount.ShouldBe(0);
    }

    [Fact]
    public void A_subtractive_brush_gets_no_hull_and_its_victim_is_reported()
    {
        // A hole contributes no solid, so it gets no hull — but a convex hull
        // per additive brush ALSO cannot express the bite taken out of it. That
        // divergence is real and currently unrepresentable, so it is counted
        // rather than shipped silently: a doorway you can see through is solid
        // to the solver until the representation question is decided.
        RequireNative();
        var scene = new Scene("Test");
        AddWorldBrush(scene, "wall", Vector3.Zero, new Vector3(4f, 3f, 0.5f));

        SceneNode cut = scene.Root.CreateChild("doorway");
        cut.Brush = Brush
            .CreateBox(new Vector3(-1f, -3f, -1f), new Vector3(1f, 1f, 1f))
            .WithOperation(BrushOperation.Subtractive);
        scene.RebuildStaticWorld(new FakeRenderer());

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);

        physics.CutBrushesWithoutCollision.ShouldBeGreaterThan(
            0, "the wall is cut by a negative brush and its hull cannot express that");
        physics.StaticShapeCount.ShouldBe(1, "only the additive brush gets a hull");
    }

    [Fact]
    public void An_edit_resyncs_and_the_world_stays_consistent()
    {
        RequireNative();
        var scene = new Scene("Test");
        SceneNode node = AddWorldBrush(scene, "a", Vector3.Zero, new Vector3(2f, 1f, 2f));
        var renderer = new FakeRenderer();
        scene.RebuildStaticWorld(renderer);

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);
        int shapesBefore = physics.StaticShapeCount;

        node.LocalPosition = new Vector3(3f, 0f, 0f);
        scene.RebuildStaticWorldIfDirty(renderer);
        physics.SyncStaticWorld(scene);

        physics.StaticShapeCount.ShouldBe(
            shapesBefore, "the brush moved but there is still exactly one of it");
        physics.BodyCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Removing_all_geometry_removes_all_collision()
    {
        RequireNative();
        var scene = new Scene("Test");
        SceneNode node = AddWorldBrush(scene, "a", Vector3.Zero, new Vector3(2f, 1f, 2f));
        var renderer = new FakeRenderer();
        scene.RebuildStaticWorld(renderer);

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);
        physics.StaticShapeCount.ShouldBeGreaterThan(0);

        scene.Root.RemoveChild(node);
        scene.RebuildStaticWorldIfDirty(renderer);
        physics.SyncStaticWorld(scene);

        physics.BodyCount.ShouldBe(0);
        physics.StaticShapeCount.ShouldBe(0);
    }

    [Fact]
    public void A_body_dropped_onto_scene_geometry_lands_on_it()
    {
        // End to end, through the engine's own types: a brush authored on a
        // scene node, compiled by the CSG pipeline, synced into physics, and
        // something falling onto the result. If chunk-local placement were
        // wrong the box would land at the wrong height or miss entirely.
        RequireNative();
        var scene = new Scene("Test");
        AddWorldBrush(scene, "floor", new Vector3(0f, -0.5f, 0f), new Vector3(8f, 0.5f, 8f));
        scene.RebuildStaticWorld(new FakeRenderer());

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);
        physics.StaticShapeCount.ShouldBeGreaterThan(0);

        // Drop a half-unit cube from 4 units up onto a floor whose top is y = 0.
        Brush boxBrush = Brush.CreateBox(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f));
        BrushHullBuilder.TryCreate(boxBrush, out nint boxHull, out string detail)
            .ShouldBe(HullRefusal.None, detail);

        try
        {
            B3BodyDef boxDef = B3.DefaultBodyDef();
            boxDef.Type = B3BodyType.Dynamic;
            boxDef.Position = new B3Pos(0f, 4f, 0f);
            B3BodyId box = B3.CreateBody(physics.World, in boxDef);
            B3ShapeDef boxShape = B3.DefaultShapeDef();
            B3.CreateHullShape(box, in boxShape, boxHull).Index1.ShouldNotBe(0);
            B3.Body_ApplyMassFromShapes(box);

            for (int tick = 0; tick < PhysicsDefaults.TicksPerSecond * 3; tick++)
                physics.Step(PhysicsDefaults.FixedDeltaTime);

            B3.Body_GetTransform(box).P.Y.ShouldBeInRange(0.4f, 0.6f);
        }
        finally
        {
            BrushHullBuilder.Destroy(boxHull);
        }
    }

    [Fact]
    public void A_full_compile_optimises_the_static_tree_exactly_once()
    {
        // The full rebuild's one intended use: after bulk creation. Load time
        // and structural edits already pay O(world) for the compile itself, so
        // the tree optimisation rides along; re-syncing an unchanged world must
        // not repeat it.
        RequireNative();
        var scene = new Scene("Test");
        AddWorldBrush(scene, "a", Vector3.Zero, new Vector3(2f, 1f, 2f));
        scene.RebuildStaticWorld(new FakeRenderer());

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);
        physics.SyncStaticWorld(scene);

        physics.StaticTreeRebuilds.ShouldBe(1);
    }

    [Fact]
    public void Incremental_syncs_do_not_rebuild_the_static_tree()
    {
        // b3World_RebuildStaticTree is O(world log world) over every static
        // hull, and the sync runs on the render thread once per landed compile,
        // which is once per frame while a world brush is dragged. Box3D inserts
        // and removes static leaves at shape create/destroy time, so skipping
        // the rebuild loses nothing but tree QUALITY; that is amortised over
        // accumulated churn instead. This is what keeps physics on the same
        // world-size-independent footing as the mesh swap, and it is asserted
        // here because the call that broke it looked like a harmless closing
        // line (docs/physics.md row: the API's own header says internal testing).
        RequireNative();
        var scene = new Scene("Test");
        SceneNode node = AddWorldBrush(scene, "a", Vector3.Zero, new Vector3(2f, 1f, 2f));
        var renderer = new FakeRenderer();
        PumpUntil(scene, renderer, () => scene.StaticWorld is not null, "the initial compile");

        using var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.SyncStaticWorld(scene);
        int rebuildsAfterLoad = physics.StaticTreeRebuilds;

        // 160 in-place edits: enough that GROSS destroy+create churn (2 per
        // sync) would cross the 256 amortisation floor, so this pins that
        // in-place cell rebuilds count as NET zero. The movements cycle inside
        // one cell on purpose; a cell-ownership crossing is a real net change
        // and may legitimately accrue.
        for (int i = 1; i <= 160; i++)
        {
            node.LocalPosition = new Vector3(0.01f * (i % 8), 0f, 0f);
            int landed = scene.StaticWorldCompileCount;
            PumpUntil(scene, renderer,
                () => scene.StaticWorldCompileCount > landed, "an incremental compile to land");
            scene.StaticWorld!.DirtyCells.ShouldNotBeNull(
                "the steady-state edit must reach physics through the incremental branch");
            physics.SyncStaticWorld(scene);
        }

        physics.StaticTreeRebuilds.ShouldBe(
            rebuildsAfterLoad,
            "an animating brush rebuilds the same cell in place, which is net-zero churn and must never re-arm the rebuild");
    }

    // Same shape as SceneAsyncCompileTests.PumpUntil: the test thread plays the
    // render thread and only the CSG compile runs off-thread.
    private static void PumpUntil(
        Scene scene, FakeRenderer renderer, Func<bool> condition, string description)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            scene.ProcessStaticWorldCompilation(renderer, NullLogger.Instance);
            if (condition())
                return;
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"Timed out waiting for {description}.");
            System.Threading.Thread.Sleep(1);
        }
    }

    [Fact]
    public void Disposing_twice_is_safe()
    {
        // The library decrements its global world count BEFORE validating the
        // id, so a double destroy corrupts that count rather than being
        // ignored. Clearing the handle is what makes the second call a no-op.
        RequireNative();
        int worldsBefore = B3.GetWorldCount();

        var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.Dispose();
        physics.Dispose();

        B3.GetWorldCount().ShouldBe(worldsBefore);
    }

    [Fact]
    public void Using_a_disposed_backend_throws_rather_than_calling_into_freed_memory()
    {
        RequireNative();
        var physics = new Box3DScenePhysics(NullLogger.Instance);
        physics.Dispose();

        Should.Throw<ObjectDisposedException>(() => physics.Step(PhysicsDefaults.FixedDeltaTime));
    }

    private static SceneNode AddWorldBrush(Scene scene, string name, Vector3 center, Vector3 halfExtent)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.LocalPosition = center;
        node.Brush = Brush.CreateBox(-halfExtent, halfExtent);
        return node;
    }
}
