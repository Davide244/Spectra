using System.Diagnostics;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The W4 scene swap path: a landing background compile must create GPU
/// meshes ONLY for the chunks an edit actually changed and destroy ONLY the
/// replaced/removed chunks' meshes (dirty-cell-only churn, observed through
/// <see cref="FakeRenderer"/>), and a CreateMesh failure mid-swap must leave
/// the previous world — every chunk of it — renderable (create-before-destroy,
/// per cell). Headless like the other scene compile suites: the test thread
/// plays the render thread and pumps
/// <see cref="Scene.ProcessStaticWorldCompilation"/> exactly as the engine
/// does. Cell landmarks: with a 2-unit cube brush, position (16,16,16) sits
/// mid-cell (0,0,0), (80,16,16) mid-cell (2,0,0), and (144,16,16) mid-cell
/// (4,0,0).
/// </summary>
public sealed class SceneChunkMeshTests
{
    [Fact]
    public void Editing_one_brush_swaps_only_its_cells_gpu_mesh()
    {
        var (scene, renderer, logger) = CreateScene();
        SceneNode edited = AddUnitBoxNode(scene, "edited", new Vector3(16f, 16f, 16f)); // cell (0,0,0)
        AddUnitBoxNode(scene, "mid", new Vector3(80f, 16f, 16f));                       // cell (2,0,0)
        AddUnitBoxNode(scene, "far", new Vector3(144f, 16f, 16f));                      // cell (4,0,0)

        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);
        renderer.CreatedMeshes.Count.ShouldBe(3); // one GPU mesh per occupied cell
        scene.StaticWorldChunkMeshes.Count.ShouldBe(3);
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh editedBefore).ShouldBeTrue();
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(2, 0, 0), out StaticWorldChunkMesh midBefore).ShouldBeTrue();
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(4, 0, 0), out StaticWorldChunkMesh farBefore).ShouldBeTrue();

        edited.LocalPosition = new Vector3(18f, 16f, 16f); // still mid-cell (0,0,0)
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 2);

        // Exactly one cell re-meshed and re-uploaded; the other two chunks
        // kept their artifact AND their GPU mesh, untouched.
        scene.StaticWorld.ShouldNotBeNull().MeshStats.ShouldBe(new CsgMeshStats(Reused: 2, Built: 1));
        renderer.CreatedMeshes.Count.ShouldBe(4);
        editedBefore.SingleFakeMesh().Disposed.ShouldBeTrue();

        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh editedAfter).ShouldBeTrue();
        editedAfter.SingleMesh().ShouldBeSameAs(renderer.CreatedMeshes[3]);
        editedAfter.SingleMesh().ShouldNotBeSameAs(editedBefore.SingleMesh());

        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(2, 0, 0), out StaticWorldChunkMesh midAfter).ShouldBeTrue();
        midAfter.SingleMesh().ShouldBeSameAs(midBefore.SingleMesh());
        midAfter.Artifact.ShouldBeSameAs(midBefore.Artifact);
        midBefore.SingleFakeMesh().Disposed.ShouldBeFalse();

        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(4, 0, 0), out StaticWorldChunkMesh farAfter).ShouldBeTrue();
        farAfter.SingleMesh().ShouldBeSameAs(farBefore.SingleMesh());
        farBefore.SingleFakeMesh().Disposed.ShouldBeFalse();
    }

    [Fact]
    public void Moving_a_brush_across_a_border_creates_the_new_cell_and_removes_the_emptied_one()
    {
        var (scene, renderer, logger) = CreateScene();
        SceneNode mover = AddUnitBoxNode(scene, "mover", new Vector3(16f, 16f, 16f)); // cell (0,0,0)
        AddUnitBoxNode(scene, "anchor", new Vector3(80f, 16f, 16f));                  // cell (2,0,0)

        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out StaticWorldChunkMesh oldCell).ShouldBeTrue();
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(2, 0, 0), out StaticWorldChunkMesh anchorBefore).ShouldBeTrue();

        mover.LocalPosition = new Vector3(48f, 16f, 16f); // into cell (1,0,0)
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 2);

        // The departed cell's mesh is destroyed (nothing owns geometry there
        // any more), the arrival cell gets a fresh one, the anchor is carried.
        scene.StaticWorldChunkMeshes.Count.ShouldBe(2);
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(0, 0, 0), out _).ShouldBeFalse();
        oldCell.SingleFakeMesh().Disposed.ShouldBeTrue();
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(1, 0, 0), out _).ShouldBeTrue();
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(2, 0, 0), out StaticWorldChunkMesh anchorAfter).ShouldBeTrue();
        anchorAfter.SingleMesh().ShouldBeSameAs(anchorBefore.SingleMesh());
        anchorBefore.SingleFakeMesh().Disposed.ShouldBeFalse();
    }

    [Fact]
    public void Failed_chunk_mesh_creation_keeps_every_old_chunk_renderable_and_recovers()
    {
        var (scene, renderer, logger) = CreateScene();
        SceneNode a = AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));   // cell (0,0,0)
        AddUnitBoxNode(scene, "b", new Vector3(80f, 16f, 16f));                 // cell (2,0,0)
        SceneNode c = AddUnitBoxNode(scene, "c", new Vector3(144f, 16f, 16f));  // cell (4,0,0)

        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);
        CsgWorld previousWorld = scene.StaticWorld.ShouldNotBeNull();
        StaticWorldChunkMesh[] before = [.. scene.StaticWorldChunkMeshes];
        before.Length.ShouldBe(3);

        // Two cells go dirty, but the swap's budget covers only ONE creation:
        // the second CreateMesh throws mid-batch, exercising the rollback of
        // an already-created replacement.
        a.LocalPosition = new Vector3(18f, 16f, 16f);
        c.LocalPosition = new Vector3(146f, 16f, 16f);
        renderer.CreateMeshBudget = 1;
        PumpUntilCreateMeshFails(scene, renderer, logger);

        // The previous world — every chunk of it — is still what renders:
        // same world, same map entries, no old mesh destroyed.
        scene.StaticWorld.ShouldBeSameAs(previousWorld);
        scene.StaticWorldChunkMeshes.Count.ShouldBe(3);
        for (int i = 0; i < before.Length; i++)
        {
            scene.StaticWorldChunkMeshes[i].SingleMesh().ShouldBeSameAs(before[i].SingleMesh());
            before[i].SingleFakeMesh().Disposed.ShouldBeFalse();
        }

        // The one replacement that WAS created got rolled back, not leaked.
        renderer.CreatedMeshes.Count.ShouldBe(4);
        renderer.CreatedMeshes[3].Disposed.ShouldBeTrue();

        // With the budget restored, a re-armed pump completes the swap.
        renderer.CreateMeshBudget = int.MaxValue;
        scene.MarkStaticWorldDirty();
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 2);

        scene.StaticWorldChunkMeshes.Count.ShouldBe(3);
        before[0].SingleFakeMesh().Disposed.ShouldBeTrue(); // cell (0,0,0): replaced now
        before[2].SingleFakeMesh().Disposed.ShouldBeTrue(); // cell (4,0,0): replaced now
        scene.TryGetStaticWorldChunkMesh(new ChunkCoord(2, 0, 0), out StaticWorldChunkMesh untouched).ShouldBeTrue();
        untouched.SingleMesh().ShouldBeSameAs(before[1].SingleMesh()); // cell (2,0,0): still carried
        untouched.SingleFakeMesh().Disposed.ShouldBeFalse();
    }

    [Fact]
    public void Failed_swap_restores_the_consumed_dirty_cells_for_the_next_compile()
    {
        // Regression: the harvest used to commit its compile bookkeeping
        // (dirty cells consumed, carry advanced to the landed-but-unpublished
        // world) BEFORE the fallible GPU swap. After a mid-swap CreateMesh
        // throw the edits' dirty cells were silently dropped: the published
        // world still lacked them, and the fault-restoration path could later
        // re-pair the OLD world with a dirty set that no longer covered the
        // changed brushes — stale geometry (or a debug trusted-diff fault
        // loop). The swap failure must fold the consumed dirty cells back so
        // the next compile re-covers them against the still-published world.
        var (scene, renderer, logger) = CreateScene();
        SceneNode a = AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));   // cell (0,0,0)
        AddUnitBoxNode(scene, "b", new Vector3(80f, 16f, 16f));                 // cell (2,0,0)
        SceneNode c = AddUnitBoxNode(scene, "c", new Vector3(144f, 16f, 16f));  // cell (4,0,0)
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);

        a.LocalPosition = new Vector3(18f, 16f, 16f);
        c.LocalPosition = new Vector3(146f, 16f, 16f);
        renderer.CreateMeshBudget = 1;
        PumpUntilCreateMeshFails(scene, renderer, logger);

        // Re-arm with NO further edits: the next compile's dirty set must be
        // exactly the failed swap's restored cells, proving they were folded
        // back rather than dropped (an unchanged scene would otherwise report
        // an empty set).
        renderer.CreateMeshBudget = int.MaxValue;
        scene.MarkStaticWorldDirty();
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 2);

        scene.LastCompileDirtyCells.ShouldBe(
            new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(4, 0, 0) });

        // And the recovered world actually carries the edits: a and c render
        // at their new positions (fresh meshes for their cells), b untouched.
        CsgWorld world = scene.StaticWorld.ShouldNotBeNull();
        world.ContainsPoint(new Vector3(18f, 16f, 16f)).ShouldBeTrue();
        world.ContainsPoint(new Vector3(146f, 16f, 16f)).ShouldBeTrue();
        world.ContainsPoint(new Vector3(15.5f, 16f, 16f)).ShouldBeFalse(); // vacated by a's move
    }

    // --- Helpers ------------------------------------------------------------

    private static (Scene Scene, FakeRenderer Renderer, CapturingLogger Logger) CreateScene() =>
        (new Scene("Test"), new FakeRenderer(), new CapturingLogger());

    // A 2-unit cube brush node at `position` — comfortably inside one cell
    // unless placed within a brush-half-extent (plus weld band) of a border.
    private static SceneNode AddUnitBoxNode(Scene scene, string name, Vector3 position)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.LocalPosition = position;
        node.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
        return node;
    }

    // Same slow-machine-proof pump loop as the other scene compile suites.
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(30);

    private static void PumpUntil(Scene scene, FakeRenderer renderer, CapturingLogger logger, Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            scene.ProcessStaticWorldCompilation(renderer, logger);
            if (condition())
                return;
            if (stopwatch.Elapsed > CompileTimeout)
                throw new TimeoutException(
                    $"Timed out waiting for a compile to land. Captured log:{Environment.NewLine}{logger.Describe()}");
            Thread.Sleep(1);
        }
    }

    // Pumps until the harvest attempts its GPU swap and the budgeted
    // CreateMesh throws out of the pump — the failure mode under test.
    private static void PumpUntilCreateMeshFails(Scene scene, FakeRenderer renderer, CapturingLogger logger)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                scene.ProcessStaticWorldCompilation(renderer, logger);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Simulated CreateMesh failure"))
            {
                return;
            }
            if (stopwatch.Elapsed > CompileTimeout)
                throw new TimeoutException(
                    $"Timed out waiting for the simulated CreateMesh failure. Captured log:{Environment.NewLine}{logger.Describe()}");
            Thread.Sleep(1);
        }
    }
}
