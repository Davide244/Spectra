using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The asynchronous static-world pipeline driven by
/// <see cref="Scene.ProcessStaticWorldCompilation"/>, exercised headlessly:
/// the test thread plays the render thread (calling the pump exactly as the
/// engine does, once per "frame") and a <see cref="FakeRenderer"/> stands in
/// for the GPU, so only the background CSG compile runs off-thread — the same
/// division of labour as in the engine. Waits poll the pump with a generous
/// ceiling that is only ever reached on genuine failure, so the tests are
/// slow-machine-proof without being timing-sensitive: every assertion below
/// holds regardless of how fast the thread pool finishes a compile, because
/// worlds are only ever swapped in by pump calls on this thread.
/// </summary>
public sealed class SceneAsyncCompileTests
{
    [Fact]
    public void Dirty_scene_launches_a_background_compile_and_swaps_the_world_in()
    {
        var (scene, _, renderer, logger) = CreateSceneWithBrushNode();
        scene.StaticWorldDirty.ShouldBeTrue(); // attaching the brush auto-dirtied
        scene.StaticWorldCompileCount.ShouldBe(0);

        scene.ProcessStaticWorldCompilation(renderer, logger);

        // The pump LAUNCHED the compile rather than running it: the edit is
        // handled (no longer dirty), but no world can have been swapped in yet
        // because harvesting only happens on a later pump call.
        scene.StaticWorldDirty.ShouldBeFalse();
        scene.StaticWorld.ShouldBeNull();
        scene.StaticWorldCompileCount.ShouldBe(0);

        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 1, "the first background compile to land");

        scene.StaticWorld.ShouldNotBeNull();
        scene.StaticWorld.Surfaces.ShouldNotBeEmpty();
        // One brush, one owner cell: the per-chunk map holds exactly one entry
        // and its GPU mesh is the one the fake renderer created.
        StaticWorldChunkMesh chunk = scene.StaticWorldChunkMeshes.ShouldHaveSingleItem();
        renderer.CreatedMeshes.Count.ShouldBe(1);
        chunk.SingleMesh().ShouldBeSameAs(renderer.CreatedMeshes[0]);
        renderer.CreatedMeshes[0].VertexData.ShouldNotBeEmpty();
        scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void Edits_during_an_in_flight_compile_coalesce_into_one_follow_up_compile()
    {
        var (scene, node, renderer, logger) = CreateSceneWithBrushNode();
        scene.ProcessStaticWorldCompilation(renderer, logger); // snapshots at the origin, launches v1

        // Edit while v1 is in flight: the scene is dirty again, but no second
        // compile may start until the first one lands.
        var moved = new Vector3(5f, 0f, 0f);
        node.LocalPosition = moved;
        scene.StaticWorldDirty.ShouldBeTrue();

        // v1 lands with the placement it was snapshotted at — the origin —
        // proving the background compile read the snapshot, not the live node.
        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 1, "the origin-snapshot compile to land");
        scene.StaticWorld.ShouldNotBeNull();
        scene.StaticWorld.Placements[0].Transform.Translation.ShouldBe(Vector3.Zero);

        // The harvesting pump call relaunches for the coalesced edit; v2 lands
        // with the moved placement.
        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 2, "the follow-up compile to land");
        scene.StaticWorld.ShouldNotBeNull();
        scene.StaticWorld.Placements[0].Transform.Translation.ShouldBe(moved);
        scene.StaticWorldDirty.ShouldBeFalse();

        // Exactly two compiles: the mid-flight edit coalesced instead of queueing.
        scene.ProcessStaticWorldCompilation(renderer, logger);
        scene.StaticWorldCompileCount.ShouldBe(2);
    }

    [Fact]
    public void Defective_snapshot_logs_once_keeps_the_previous_world_and_recovers()
    {
        var (scene, node, renderer, logger) = CreateSceneWithBrushNode();
        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 1, "the initial compile to land");
        CsgWorld previousWorld = scene.StaticWorld.ShouldNotBeNull();
        FakeMesh previousMesh = scene.StaticWorldChunkMeshes.ShouldHaveSingleItem().SingleFakeMesh();

        node.LocalScale = new Vector3(2f, 1f, 1f); // non-rigid: the snapshot must reject it
        scene.StaticWorldDirty.ShouldBeTrue();

        // The pump must swallow the defect (log, don't throw) and keep rendering
        // the last good world.
        Should.NotThrow(() => scene.ProcessStaticWorldCompilation(renderer, logger));
        logger.MessagesAt(LogLevel.Error).ShouldHaveSingleItem().ShouldContain("non-rigid");
        scene.StaticWorld.ShouldBeSameAs(previousWorld);
        scene.StaticWorldChunkMeshes.ShouldHaveSingleItem().SingleMesh().ShouldBeSameAs(previousMesh);
        previousMesh.Disposed.ShouldBeFalse();
        scene.StaticWorldCompileCount.ShouldBe(1);
        scene.StaticWorldDirty.ShouldBeFalse(); // defect marked handled — no retry

        // Later frames stay quiet: no retry-spam, no extra error per pump call.
        for (int i = 0; i < 5; i++)
            scene.ProcessStaticWorldCompilation(renderer, logger);
        logger.MessagesAt(LogLevel.Error).Count.ShouldBe(1);
        scene.StaticWorldCompileCount.ShouldBe(1);

        // Fixing the transform re-arms the pump and the world recovers. The
        // recovered geometry is identical to the pre-defect snapshot, so the
        // carried caches validate all the way through and the chunk's GPU
        // mesh is CARRIED, not rebuilt — dirty-cell-only churn even across a
        // defect episode.
        node.LocalScale = Vector3.One;
        scene.StaticWorldDirty.ShouldBeTrue();
        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 2, "the recovery compile to land");
        scene.StaticWorld.ShouldNotBeNull();
        scene.StaticWorld.ShouldNotBeSameAs(previousWorld);
        scene.StaticWorldChunkMeshes.ShouldHaveSingleItem().SingleMesh().ShouldBeSameAs(previousMesh);
        previousMesh.Disposed.ShouldBeFalse();
        renderer.CreatedMeshes.Count.ShouldBe(1); // nothing new was uploaded
    }

    [Fact]
    public void Pump_on_a_clean_scene_does_nothing()
    {
        var scene = new Scene("Test");
        scene.Root.CreateChild("plain");
        var renderer = new FakeRenderer();
        var logger = new CapturingLogger();

        scene.ProcessStaticWorldCompilation(renderer, logger);

        scene.StaticWorldCompileCount.ShouldBe(0);
        scene.StaticWorld.ShouldBeNull();
        renderer.CreatedMeshes.ShouldBeEmpty();
    }

    [Fact]
    public void Dirty_scene_without_brush_nodes_clears_the_world_without_compiling()
    {
        // The documented cheap path: no brushes means no carve, so the pump
        // resolves the edit synchronously instead of bothering the thread pool.
        var scene = new Scene("Test");
        var renderer = new FakeRenderer();
        var logger = new CapturingLogger();
        scene.MarkStaticWorldDirty();

        scene.ProcessStaticWorldCompilation(renderer, logger);

        scene.StaticWorldCompileCount.ShouldBe(1);
        scene.StaticWorld.ShouldBeNull();
        scene.StaticWorldDirty.ShouldBeFalse();
        renderer.CreatedMeshes.ShouldBeEmpty();
    }

    [Fact]
    public void Detaching_the_last_brush_clears_the_world_and_destroys_its_mesh()
    {
        var (scene, node, renderer, logger) = CreateSceneWithBrushNode();
        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 1, "the initial compile to land");
        FakeMesh mesh = scene.StaticWorldChunkMeshes.ShouldHaveSingleItem().SingleFakeMesh();

        node.Brush = null;
        scene.StaticWorldDirty.ShouldBeTrue();

        // The no-brushes path is synchronous, so one pump call fully resolves it.
        scene.ProcessStaticWorldCompilation(renderer, logger);

        scene.StaticWorldCompileCount.ShouldBe(2);
        scene.StaticWorld.ShouldBeNull();
        scene.StaticWorldChunkMeshes.ShouldBeEmpty();
        mesh.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Recompiling_an_unchanged_scene_reuses_every_chunk_gpu_mesh()
    {
        // Two overlapping brushes so the compile does a real carve, not a
        // pass-through: the cache chain must hold through split/snap/weld too.
        var scene = new Scene("Test");
        SceneNode a = scene.Root.CreateChild("a");
        a.Brush = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        SceneNode b = scene.Root.CreateChild("b");
        b.LocalPosition = new Vector3(1.25f, 0.5f, 0f);
        b.Brush = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        var renderer = new FakeRenderer();
        var logger = new CapturingLogger();

        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 1, "the first compile to land");
        int createdAfterFirst = renderer.CreatedMeshes.Count;
        FakeMesh[] firstMeshes = [.. scene.StaticWorldChunkMeshes.Select(c => c.SingleFakeMesh())];
        firstMeshes.ShouldNotBeEmpty();

        scene.MarkStaticWorldDirty(); // nothing changed — identical snapshot
        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 2, "the second compile to land");

        // The whole cache chain validated, so every chunk artifact — and with
        // it every GPU mesh — was carried forward: zero creates, zero
        // destroys. (Bit-identical rebuild determinism is pinned separately by
        // ChunkMeshEquivalenceTests over cache-free builds.)
        renderer.CreatedMeshes.Count.ShouldBe(createdAfterFirst);
        scene.StaticWorldChunkMeshes.Count.ShouldBe(firstMeshes.Length);
        for (int i = 0; i < firstMeshes.Length; i++)
        {
            scene.StaticWorldChunkMeshes[i].SingleMesh().ShouldBeSameAs(firstMeshes[i]);
            firstMeshes[i].Disposed.ShouldBeFalse();
        }
    }

    [Fact]
    public void Weld_cache_travels_through_the_pump_so_an_edit_reuses_far_welds()
    {
        // Two brush nodes many cells apart: editing one must leave the other's
        // carve AND weld untouched on the next background compile — proving
        // both caches ride the harvest → relaunch handoff.
        var scene = new Scene("Test");
        SceneNode near = scene.Root.CreateChild("near");
        near.LocalPosition = new Vector3(16f, 16f, 16f);
        near.Brush = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        SceneNode far = scene.Root.CreateChild("far");
        far.LocalPosition = new Vector3(200f, 16f, 16f);
        far.Brush = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        var renderer = new FakeRenderer();
        var logger = new CapturingLogger();

        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 1, "the first compile to land");
        // A cold start caches nothing yet: everything carved and welded fresh.
        CsgWorld first = scene.StaticWorld.ShouldNotBeNull();
        first.CacheStats.ShouldBe(new CsgCacheStats(Hits: 0, Misses: 2));
        first.WeldStats.ShouldBe(new CsgWeldStats(Reused: 0, Welded: 2));

        near.LocalPosition = new Vector3(18f, 16f, 16f);
        PumpUntil(scene, renderer, logger,
            () => scene.StaticWorldCompileCount == 2, "the post-edit compile to land");

        CsgWorld second = scene.StaticWorld.ShouldNotBeNull();
        second.CacheStats.ShouldBe(new CsgCacheStats(Hits: 1, Misses: 1));
        second.WeldStats.ShouldBe(new CsgWeldStats(Reused: 1, Welded: 1));
    }

    // Ceiling for a background compile of a handful of boxes; normally met
    // within a few pump iterations. Only a hung or lost compile ever runs the
    // clock out, so the size of this value never slows a passing run.
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(30);

    // Drives the pump like the engine's render loop until `condition` holds,
    // sleeping minimally between frames. Fails loudly (with the captured log)
    // instead of hanging when the condition never becomes true.
    private static void PumpUntil(
        Scene scene, FakeRenderer renderer, CapturingLogger logger,
        Func<bool> condition, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            scene.ProcessStaticWorldCompilation(renderer, logger);
            if (condition())
                return;
            if (stopwatch.Elapsed > CompileTimeout)
                throw new TimeoutException(
                    $"Timed out waiting for {description}. Captured log:{Environment.NewLine}{logger.Describe()}");
            Thread.Sleep(1);
        }
    }

    private static (Scene Scene, SceneNode Node, FakeRenderer Renderer, CapturingLogger Logger) CreateSceneWithBrushNode()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("brush");
        node.Brush = Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        return (scene, node, new FakeRenderer(), new CapturingLogger());
    }
}
