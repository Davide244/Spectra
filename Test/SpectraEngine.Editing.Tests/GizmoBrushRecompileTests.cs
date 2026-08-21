using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// A gizmo drag of a brush node must be indistinguishable, to the static-world
/// compile, from a scripted assignment to the same node's
/// <c>LocalPosition</c> — same final placement, same dirty cells, same
/// incremental recompile.
/// </summary>
/// <remarks>
/// This is the load-bearing claim behind the whole design: the gizmo moves
/// nodes through <c>SetTransformCommand</c> and therefore through the ordinary
/// transform setters, so the scene's node-scoped dirtying
/// (<c>MarkBrushSubtreeDirty</c>) and its chunked footprint diff see a gizmo
/// edit as just another edit. If the gizmo ever grew a "fast path" that wrote
/// placements or transforms directly, the per-edit recompile cost would stop
/// being O(edit neighbourhood) and these tests would catch it.
/// <para>
/// Cell landmarks match <c>SceneDirtyCellTests</c>: a 2-unit cube at
/// (16,16,16) sits mid-cell (0,0,0), and at (48,16,16) mid-cell (1,0,0).
/// </para>
/// </remarks>
public sealed class GizmoBrushRecompileTests
{
    private const float AlongAxis = 0.8f;

    private static readonly Vector3 Start = new(16f, 16f, 16f);

    [Fact]
    public void A_gizmo_drag_lands_a_brush_on_exactly_the_scripted_position()
    {
        (GizmoHarness scripted, SceneNode scriptedNode, CompilingRenderer scriptedRenderer) = BrushScene();
        scripted.Scene.RebuildStaticWorld(scriptedRenderer);
        scriptedNode.LocalPosition = new Vector3(48f, 16f, 16f);

        (GizmoHarness dragged, SceneNode draggedNode, CompilingRenderer draggedRenderer) = BrushScene();
        dragged.Scene.RebuildStaticWorld(draggedRenderer);
        DragBrushToX(dragged, 48f);

        // Bit-for-bit, not approximately: the snapped result of an axis drag is
        // an exact grid multiple and the untouched axes keep the pivot's own
        // floats, so the two paths produce identical placements — which is what
        // makes the dirty-cell comparison below meaningful rather than lucky.
        draggedNode.LocalPosition.ShouldBe(scriptedNode.LocalPosition);
    }

    [Fact]
    public void A_gizmo_drag_across_a_chunk_border_dirties_the_same_cells_as_a_scripted_move()
    {
        var target = new Vector3(48f, 16f, 16f);

        IReadOnlyList<ChunkCoord> scripted = ScriptedMoveDirtyCells(target);
        IReadOnlyList<ChunkCoord> dragged = DraggedMoveDirtyCells(48f);

        // Departed cell and arrival cell, both ways round.
        scripted.ShouldBe(new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(1, 0, 0) });
        dragged.ShouldBe(scripted);
    }

    [Fact]
    public void A_gizmo_drag_within_a_chunk_dirties_only_that_cell()
    {
        IReadOnlyList<ChunkCoord> scripted = ScriptedMoveDirtyCells(new Vector3(20f, 16f, 16f));
        IReadOnlyList<ChunkCoord> dragged = DraggedMoveDirtyCells(20f);

        scripted.ShouldBe(new[] { new ChunkCoord(0, 0, 0) });
        dragged.ShouldBe(scripted);
    }

    [Fact]
    public void A_cancelled_drag_leaves_the_static_world_clean()
    {
        (GizmoHarness harness, SceneNode node, CompilingRenderer renderer) = BrushScene();
        harness.Scene.RebuildStaticWorld(renderer);
        int compilesAfterLoad = harness.Scene.StaticWorldCompileCount;

        float length = harness.GeometryAt(Start).AxisLength;
        harness.Grab(Start + Vector3.UnitX * (length * AlongAxis));
        harness.DragBy(Vector3.UnitX * 32f);
        harness.Scene.StaticWorldDirty.ShouldBeTrue(); // the live drag really did dirty it

        harness.PressEscape();

        // The node is back where it started, so the next compile has nothing to
        // do — the cancel restored the exact placement the last compile saw.
        node.LocalPosition.ShouldBe(Start);
        harness.Scene.RebuildStaticWorld(renderer);
        harness.Scene.LastCompileDirtyCells.ShouldBeEmpty();
        harness.Scene.StaticWorldCompileCount.ShouldBe(compilesAfterLoad + 1);
    }

    [Fact]
    public void Every_frame_of_a_drag_dirties_the_scene_so_the_recompile_can_keep_up()
    {
        // Progressive results while dragging are the point of the async chunked
        // compile; they only happen if each frame's transform write actually
        // marks the world dirty. (A frame that does not move the cursor writes
        // the same value and is correctly free — the setters early-out.)
        (GizmoHarness harness, SceneNode node, CompilingRenderer renderer) = BrushScene();
        harness.Scene.RebuildStaticWorld(renderer);

        float length = harness.GeometryAt(Start).AxisLength;
        harness.Grab(Start + Vector3.UnitX * (length * AlongAxis));

        for (int frame = 1; frame <= 4; frame++)
        {
            harness.Scene.RebuildStaticWorld(renderer); // clears the dirty flag
            harness.Scene.StaticWorldDirty.ShouldBeFalse();

            harness.DragBy(Vector3.UnitX * frame);
            harness.Scene.StaticWorldDirty.ShouldBeTrue();
        }

        harness.Release();
        node.LocalPosition.X.ShouldBe(20f, 1e-3f);
    }

    // --- Helpers -------------------------------------------------------------

    private static IReadOnlyList<ChunkCoord> ScriptedMoveDirtyCells(Vector3 target)
    {
        (GizmoHarness harness, SceneNode node, CompilingRenderer renderer) = BrushScene();
        harness.Scene.RebuildStaticWorld(renderer);

        node.LocalPosition = target;
        harness.Scene.RebuildStaticWorld(renderer);

        return harness.Scene.LastCompileDirtyCells;
    }

    private static IReadOnlyList<ChunkCoord> DraggedMoveDirtyCells(float targetX)
    {
        (GizmoHarness harness, SceneNode _, CompilingRenderer renderer) = BrushScene();
        harness.Scene.RebuildStaticWorld(renderer);

        DragBrushToX(harness, targetX);
        harness.Scene.RebuildStaticWorld(renderer);

        return harness.Scene.LastCompileDirtyCells;
    }

    // Drags the selection along world x until the pivot lands on targetX. Snap
    // is left at its default one-unit grid, which is what makes the landing an
    // exact float and the two paths comparable.
    private static void DragBrushToX(GizmoHarness harness, float targetX)
    {
        float length = harness.GeometryAt(Start).AxisLength;
        Vector3 grabAt = Start + Vector3.UnitX * (length * AlongAxis);

        harness.Grab(grabAt).ShouldBe(GizmoUpdateResult.DragBegan);
        harness.Gizmo.ActiveHandle.ShouldBe(GizmoHandle.AxisX);

        // Deliberately a hair off the target so the snap has something to do.
        harness.DragBy(Vector3.UnitX * (targetX - Start.X + 0.21f));
        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);
    }

    // One 2-unit cube brush node at Start, selected, plus a distant second
    // brush that proves the dirty-cell diff is per-node rather than global.
    private static (GizmoHarness Harness, SceneNode Node, CompilingRenderer Renderer) BrushScene()
    {
        // The camera sits back far enough to see a gizmo sized for a pivot
        // sixteen units out, and off-axis so all three arrows are separated.
        var harness = new GizmoHarness(Start + new Vector3(12f, 9f, 15f), Start);
        var renderer = new CompilingRenderer();

        SceneNode node = harness.Scene.Root.CreateChild("Brush");
        node.LocalPosition = Start;
        node.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));
        harness.Scene.Selection.Add(node);

        SceneNode far = harness.Scene.Root.CreateChild("Far");
        far.LocalPosition = new Vector3(80f, 16f, 16f); // cell (2,0,0)
        far.Brush = Brush.CreateBox(new Vector3(-1f), new Vector3(1f));

        return (harness, node, renderer);
    }
}
