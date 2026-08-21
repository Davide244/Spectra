using System.Diagnostics;
using System.Numerics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Dirty-cell tracking through the scene's footprint diffing: each compile
/// (synchronous rebuild or background launch) receives exactly the chunk cells
/// whose brush contents changed since the previous compile, observable via
/// <see cref="Scene.LastCompileDirtyCells"/> and, for background compiles, via
/// <see cref="CsgWorld.DirtyCells"/> on the world they build. Cell landmarks:
/// with a 2-unit cube brush, position (16,16,16) sits mid-cell (0,0,0) and
/// (48,16,16) mid-cell (1,0,0).
/// </summary>
public sealed class SceneDirtyCellTests
{
    [Fact]
    public void First_compile_marks_every_covered_cell_dirty()
    {
        var (scene, renderer, _) = CreateScene();
        AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));   // cell (0,0,0)
        AddUnitBoxNode(scene, "b", new Vector3(80f, 16f, 16f));   // cell (2,0,0)

        scene.RebuildStaticWorld(renderer);

        scene.LastCompileDirtyCells.ShouldBe(
            new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(2, 0, 0) });
    }

    [Fact]
    public void Unchanged_recompile_dirties_no_cells()
    {
        var (scene, renderer, _) = CreateScene();
        AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));
        scene.RebuildStaticWorld(renderer);

        scene.MarkStaticWorldDirty(); // nothing actually changed
        scene.RebuildStaticWorld(renderer);

        scene.LastCompileDirtyCells.ShouldBeEmpty();
    }

    [Fact]
    public void Move_within_a_cell_dirties_only_that_cell()
    {
        var (scene, renderer, _) = CreateScene();
        SceneNode node = AddUnitBoxNode(scene, "a", new Vector3(10f, 10f, 10f));
        AddUnitBoxNode(scene, "far", new Vector3(80f, 16f, 16f)); // proves the diff is per-node
        scene.RebuildStaticWorld(renderer);

        node.LocalPosition = new Vector3(20f, 20f, 20f); // still inside cell (0,0,0)
        scene.RebuildStaticWorld(renderer);

        scene.LastCompileDirtyCells.ShouldBe(new[] { new ChunkCoord(0, 0, 0) });
    }

    [Fact]
    public void Move_across_a_border_dirties_the_old_and_new_footprints()
    {
        var (scene, renderer, _) = CreateScene();
        SceneNode node = AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));
        scene.RebuildStaticWorld(renderer);

        node.LocalPosition = new Vector3(48f, 16f, 16f); // cell (0,0,0) → (1,0,0)
        scene.RebuildStaticWorld(renderer);

        scene.LastCompileDirtyCells.ShouldBe(
            new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(1, 0, 0) });
    }

    [Fact]
    public void Move_into_the_weld_band_of_a_border_dirties_the_neighbour_cell_too()
    {
        var (scene, renderer, _) = CreateScene();
        SceneNode node = AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));
        scene.RebuildStaticWorld(renderer);

        // World AABB 30..32 on x: the brush stays inside cell 0, but its
        // weld-band-inflated footprint now reaches cell 1 — that cell's future
        // weld results depend on this brush, so it must be dirtied.
        node.LocalPosition = new Vector3(31f, 16f, 16f);
        scene.RebuildStaticWorld(renderer);

        scene.LastCompileDirtyCells.ShouldBe(
            new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(1, 0, 0) });
    }

    [Fact]
    public void Attaching_a_brush_dirties_only_its_own_cells()
    {
        var (scene, renderer, _) = CreateScene();
        AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));
        scene.RebuildStaticWorld(renderer);

        AddUnitBoxNode(scene, "b", new Vector3(80f, 16f, 16f)); // cell (2,0,0)
        scene.RebuildStaticWorld(renderer);

        scene.LastCompileDirtyCells.ShouldBe(new[] { new ChunkCoord(2, 0, 0) });
    }

    [Fact]
    public void Detaching_a_brush_dirties_the_cells_it_departed()
    {
        var (scene, renderer, _) = CreateScene();
        AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));
        SceneNode b = AddUnitBoxNode(scene, "b", new Vector3(80f, 16f, 16f));
        scene.RebuildStaticWorld(renderer);

        b.Brush = null;
        scene.RebuildStaticWorld(renderer);

        scene.LastCompileDirtyCells.ShouldBe(new[] { new ChunkCoord(2, 0, 0) });
    }

    [Fact]
    public void Detaching_the_last_brush_dirties_its_cells_through_the_synchronous_clear_path()
    {
        var (scene, renderer, logger) = CreateScene();
        SceneNode node = AddUnitBoxNode(scene, "a", new Vector3(48f, 16f, 16f)); // cell (1,0,0)
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);

        node.Brush = null;
        scene.ProcessStaticWorldCompilation(renderer, logger); // resolves synchronously

        scene.StaticWorld.ShouldBeNull();
        scene.LastCompileDirtyCells.ShouldBe(new[] { new ChunkCoord(1, 0, 0) });
    }

    [Fact]
    public void Background_compile_carries_its_dirty_set_into_the_built_world()
    {
        var (scene, renderer, logger) = CreateScene();
        SceneNode node = AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));

        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);
        CsgWorld first = scene.StaticWorld.ShouldNotBeNull();
        first.DirtyCells.ShouldNotBeNull();
        first.DirtyCells.ShouldBe(new[] { new ChunkCoord(0, 0, 0) }); // first compile: all covered cells
        scene.LastCompileDirtyCells.ShouldBe(first.DirtyCells);

        node.LocalPosition = new Vector3(48f, 16f, 16f); // into cell (1,0,0)
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 2);

        CsgWorld second = scene.StaticWorld.ShouldNotBeNull();
        second.DirtyCells.ShouldNotBeNull();
        second.DirtyCells.ShouldBe(new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(1, 0, 0) });
    }

    [Fact]
    public void Retexturing_a_face_dirties_its_cell_and_reaches_the_compiled_world()
    {
        // A brush-for-brush swap that changes ONLY a face material must still
        // travel the whole async patch path: same footprint, so the diff has
        // nothing geometric to notice, and a compile that skipped the cell
        // would leave the old material silently on screen.
        var (scene, renderer, logger) = CreateScene();
        SceneNode node = AddUnitBoxNode(scene, "a", new Vector3(48f, 16f, 16f)); // cell (1,0,0)
        AddUnitBoxNode(scene, "far", new Vector3(144f, 16f, 16f));               // cell (4,0,0)
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 1);

        MaterialRef accent = MaterialRegistry.Intern("Materials/scene_accent.spectramat");
        scene.StaticWorld.ShouldNotBeNull().Surfaces
            .ShouldNotContain(s => s.Face.Material == accent);

        node.Brush = node.Brush.ShouldNotBeNull().WithFaceMaterial(0, accent);
        PumpUntil(scene, renderer, logger, () => scene.StaticWorldCompileCount == 2);

        CsgWorld world = scene.StaticWorld.ShouldNotBeNull();
        world.DirtyCells.ShouldNotBeNull().ShouldBe(new[] { new ChunkCoord(1, 0, 0) });

        // The retextured face is present in the compiled surfaces...
        world.Surfaces.Count(s => s.Face.Material == accent).ShouldBe(1);

        // ...and in the render artifact of that cell, which is what the render
        // thread would resolve materials from at upload time.
        ChunkMesh cell = world.ChunkMeshes.Single(m => m.Coord == new ChunkCoord(1, 0, 0));
        cell.Submeshes.ShouldContain(s => s.Material == accent);
    }

    [Fact]
    public void Synchronously_built_worlds_carry_no_dirty_set()
    {
        // The synchronous path compiles everything by contract, so its worlds
        // are built dirty-agnostic; only Scene.LastCompileDirtyCells reports
        // the diff there.
        var (scene, renderer, _) = CreateScene();
        AddUnitBoxNode(scene, "a", new Vector3(16f, 16f, 16f));

        scene.RebuildStaticWorld(renderer);

        scene.StaticWorld.ShouldNotBeNull().DirtyCells.ShouldBeNull();
        scene.LastCompileDirtyCells.ShouldBe(new[] { new ChunkCoord(0, 0, 0) });
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

    // Same slow-machine-proof pump loop as SceneAsyncCompileTests: worlds only
    // ever land on this thread's pump calls, so assertions after PumpUntil are
    // never timing-sensitive.
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
}
