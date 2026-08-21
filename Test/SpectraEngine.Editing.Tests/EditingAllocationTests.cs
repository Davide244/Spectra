using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Gizmos;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Selection;
using SpectraEngine.Editing.Undo;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The editing spine sits on the per-frame path (an input snapshot every frame,
/// a coalesced command every frame of a drag), so its steady-state work has to
/// stay allocation-free — same bar as the asset pump and the render-view build.
/// </summary>
public sealed class EditingAllocationTests
{
    [Fact]
    public void Capturing_an_input_frame_allocates_nothing()
    {
        var input = new InputManager(NullLogger<InputManager>.Instance);
        var renderer = new StubRenderer();
        var source = new EngineEditorInputSource(input, renderer);
        input.OnMouseDown(null!, Silk.NET.Input.MouseButton.Left);
        input.OnKeyDown(null!, Silk.NET.Input.Key.ShiftLeft, 0);
        input.Update(0.016);

        // Warm up: JIT the capture path and the flag mapping behind it.
        for (int i = 0; i < 200; i++)
            _ = source.CaptureFrame(0.016f);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            _ = source.CaptureFrame(0.016f);
        long after = GC.GetAllocatedBytesForCurrentThread();

        // EditorInputFrame is a readonly struct returned by value, and the
        // neutral flag mapping is three hash probes per set — nothing boxes.
        (after - before).ShouldBe(0);
    }

    [Fact]
    public void Retargeting_a_held_command_through_a_drag_allocates_nothing()
    {
        var scene = new Scene("Editing");
        var node = scene.Root.CreateChild("Box");
        var stack = new UndoStack(scene);

        // The zero-allocation drag path: the tool creates ONE command on grab
        // and retargets it each frame instead of pushing a new one.
        stack.BeginTransaction("Move");
        var command = SetTransformCommand.Move(node, Vector3.Zero);
        stack.Record(command);

        for (int i = 0; i < 200; i++)
        {
            command.SetAfter(new Vector3(i, 0f, 0f), Quaternion.Identity);
            command.Do(scene);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            command.SetAfter(new Vector3(i, 0f, 0f), Quaternion.Identity);
            command.Do(scene);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);

        stack.CommitTransaction();
        stack.Count.ShouldBe(1);
    }

    [Fact]
    public void Absorbing_a_per_frame_command_allocates_only_that_command()
    {
        var scene = new Scene("Editing");
        var node = scene.Root.CreateChild("Box");
        var stack = new UndoStack(scene);
        stack.BeginTransaction("Move");
        stack.Record(SetTransformCommand.Move(node, Vector3.Zero));

        for (int i = 0; i < 200; i++)
            stack.Record(SetTransformCommand.Move(node, new Vector3(i, 0f, 0f)));

        const int frames = 10_000;

        // Baseline: what the per-frame commands cost on their own, with no
        // recording at all. `escape` keeps the loop from being optimised away.
        SetTransformCommand? escape = null;
        long baselineBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
            escape = SetTransformCommand.Move(node, new Vector3(i, 0f, 0f));
        long baseline = GC.GetAllocatedBytesForCurrentThread() - baselineBefore;
        escape.ShouldNotBeNull();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++)
            stack.Record(SetTransformCommand.Move(node, new Vector3(i, 0f, 0f)));
        long after = GC.GetAllocatedBytesForCurrentThread();

        // The push-per-frame style costs exactly one command object per frame
        // and nothing else: the absorb keeps the transaction list at one entry,
        // so neither it nor the history grows.
        (after - before).ShouldBe(baseline);

        stack.CommitTransaction();
        stack.Count.ShouldBe(1);
    }

    [Fact]
    public void Hovering_the_translate_gizmo_allocates_nothing()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(Vector3.Zero);
        float length = harness.GeometryAt(Vector3.Zero).AxisLength;

        // Aimed at the x arrow, so the hit test runs its whole priority ladder
        // — disc, three quads, three arrows — rather than bailing out early.
        EditorInputFrame frame = harness.Frame(
            harness.WorldToScreen(Vector3.UnitX * (length * 0.8f)));

        for (int i = 0; i < 200; i++)
            harness.Gizmo.Update(in frame);
        harness.Gizmo.HoveredHandle.ShouldBe(GizmoHandle.AxisX);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            harness.Gizmo.Update(in frame);
        long after = GC.GetAllocatedBytesForCurrentThread();

        // Geometry, the picking ray, and the pick result are all structs, and
        // the pivot sum walks the selection by index.
        (after - before).ShouldBe(0);
    }

    [Fact]
    public void Dragging_the_translate_gizmo_allocates_nothing_per_frame()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(Vector3.Zero, "A");
        harness.AddSelectedNode(new Vector3(2f, 1f, 0f), "B");

        // Two nodes, so the per-frame loop really does walk a target list.
        var pivot = new Vector3(1f, 0.5f, 0f);
        float length = harness.GeometryAt(pivot).AxisLength;

        harness.Grab(pivot + Vector3.UnitX * (length * 0.8f))
            .ShouldBe(GizmoUpdateResult.DragBegan);

        // The grab already allocated its one command per node; from here the
        // drag only retargets and re-applies them.
        for (int i = 0; i < 200; i++)
            harness.DragBy(Vector3.UnitX * (i * 0.01f));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            harness.DragBy(Vector3.UnitX * (i * 0.001f));
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);

        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);
        harness.Undo.Count.ShouldBe(1);
    }

    [Fact]
    public void Hovering_the_rotate_gizmo_allocates_nothing()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(Vector3.Zero);
        harness.Use(GizmoMode.Rotate);

        // Aimed at the z ring 45° round, where no other ring passes, so the hit
        // test walks all four — four fixed loops over the same chord count the
        // renderer draws.
        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        EditorInputFrame frame = harness.Frame(harness.WorldToScreen(
            RingPoint(geometry.RingRadius, MathF.PI / 4f)));

        for (int i = 0; i < 200; i++)
            harness.Gizmos.Update(in frame);
        harness.Gizmo.HoveredHandle.ShouldBe(GizmoHandle.AxisZ);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5_000; i++)
            harness.Gizmos.Update(in frame);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }

    [Fact]
    public void Dragging_the_rotate_gizmo_allocates_nothing_per_frame()
    {
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(new Vector3(-1f, 0f, 0f), "A");
        harness.AddSelectedNode(new Vector3(1f, 0f, 0f), "B");
        RotateGizmo rotate = (RotateGizmo)harness.Use(GizmoMode.Rotate);
        rotate.Snap.Enabled = false;

        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        float radius = geometry.RingRadius;
        harness.Grab(RingPoint(radius, MathF.PI / 4f)).ShouldBe(GizmoUpdateResult.DragBegan);

        // The grab already allocated its one command per node; from here the
        // drag only retargets and re-applies them.
        for (int i = 0; i < 200; i++)
            harness.DragTo(RingPoint(radius, MathF.PI / 4f + i * 0.001f));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5_000; i++)
            harness.DragTo(RingPoint(radius, MathF.PI / 4f + i * 0.0001f));
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);

        harness.Release().ShouldBe(GizmoUpdateResult.DragCommitted);
        harness.Undo.Count.ShouldBe(1);
    }

    [Fact]
    public void Dragging_the_scale_gizmo_over_mesh_nodes_allocates_nothing_per_frame()
    {
        // Mesh nodes only. A BRUSH resize deliberately allocates — brushes are
        // immutable, so a new size is a new instance — and ScaleGizmo documents
        // that as the one gesture in the editing layer that cannot be free.
        var harness = GizmoHarness.ThreeQuarterView();
        harness.AddSelectedNode(new Vector3(-1f, 0f, 0f), "A");
        harness.AddSelectedNode(new Vector3(1f, 0f, 0f), "B");
        ScaleGizmo scale = (ScaleGizmo)harness.Use(GizmoMode.Scale);
        scale.Snap.Enabled = false;

        GizmoGeometry geometry = harness.GeometryAt(Vector3.Zero);
        float length = geometry.AxisLength;
        harness.Grab(Vector3.UnitX * length).ShouldBe(GizmoUpdateResult.DragBegan);

        for (int i = 0; i < 200; i++)
            harness.DragTo(Vector3.UnitX * (length * (1f + i * 0.001f)));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5_000; i++)
            harness.DragTo(Vector3.UnitX * (length * (1f + i * 0.0001f)));
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }

    // A point on the world z ring, which lies in the xy plane.
    private static Vector3 RingPoint(float radius, float angle) =>
        new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);

    [Fact]
    public void Steady_state_history_pushes_allocate_nothing_beyond_the_commands()
    {
        var scene = new Scene("Editing");
        var node = scene.Root.CreateChild("Box");
        var stack = new UndoStack(scene, capacity: 16);
        var command = SetTransformCommand.Move(node, Vector3.Zero);

        // Fill the ring so every push from here on also evicts.
        for (int i = 0; i < 200; i++)
            stack.Record(command);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            stack.Record(command);
        long after = GC.GetAllocatedBytesForCurrentThread();

        // Eviction is a head bump in a preallocated ring, not a list shuffle.
        (after - before).ShouldBe(0);
    }

    // --- The viewport per-frame path -----------------------------------------

    [Fact]
    public void Driving_the_editor_camera_allocates_nothing_per_frame()
    {
        // The whole navigation path in one loop: freelook (the unmodified
        // right-drag), the fly axis, the speed trim, and the damping filter.
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, 0.4f, -0.2f);
        harness.EditorCamera.SmoothingTimeConstant = 0.1f; // damping on: the real path
        EditorNavigationInput fly = EditorNavigationInput.FromKeys(
            forward: true, back: false, left: false, right: true, up: true, down: false, boost: true);

        // Warm up the whole filter, including the transcendentals.
        for (int i = 0; i < 200; i++)
            harness.EditorCamera.Update(harness.Frame(
                new Vector2(400f + i % 5, 300f), down: PointerButtons.Right,
                scroll: new Vector2(0f, 1f), navigation: fly));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            harness.EditorCamera.Update(harness.Frame(
                new Vector2(400f + i % 5, 300f), down: PointerButtons.Right,
                scroll: new Vector2(0f, i % 3 - 1), navigation: fly));
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }

    [Fact]
    public void A_locked_look_frame_allocates_nothing_per_frame()
    {
        var harness = new ViewportHarness();
        harness.EditorCamera.SetPose(Vector3.Zero, 0.4f, -0.2f);
        harness.EditorCamera.SmoothingTimeConstant = 0.1f;

        for (int i = 0; i < 200; i++)
            harness.EditorCamera.Update(harness.Frame(
                harness.CenterPixel, down: PointerButtons.Right,
                cursorDelta: new Vector2(i % 3 - 1, i % 5 - 2), locked: true));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            harness.EditorCamera.Update(harness.Frame(
                harness.CenterPixel, down: PointerButtons.Right,
                cursorDelta: new Vector2(i % 3 - 1, i % 5 - 2), locked: true));
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).ShouldBe(0);
    }

    [Fact]
    public void An_idle_viewport_frame_allocates_nothing()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 20f, 0.4f, -0.2f);
        harness.AddSelectedBrush(Vector3.Zero, 1f);

        for (int i = 0; i < 200; i++)
            harness.Move(new Vector2(400f + i % 7, 300f));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            harness.Move(new Vector2(400f + i % 7, 300f));
        long after = GC.GetAllocatedBytesForCurrentThread();

        // Gizmo hover, camera settle check, no picking: the cost of a frame the
        // user is not interacting with must be zero.
        (after - before).ShouldBe(0);
    }

    [Fact]
    public void Querying_a_marquee_allocates_nothing_once_the_result_list_has_grown()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 40f, 0f, 0f);
        for (int i = 0; i < 200; i++)
            harness.AddBrush(new Vector3(i % 20 * 2f - 19f, i / 20 * 2f - 9f, 0f), 0.4f, $"N{i}");

        var rect = new ScreenRect(new Vector2(100f, 80f), new Vector2(700f, 520f));
        var results = new List<SceneNode>();
        for (int i = 0; i < 200; i++)
            BoxSelectQuery.Query(harness.Scene, in rect, harness.ViewportSize, BoxSelectMode.Intersect, results);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2_000; i++)
            BoxSelectQuery.Query(harness.Scene, in rect, harness.ViewportSize, BoxSelectMode.Intersect, results);
        long after = GC.GetAllocatedBytesForCurrentThread();

        // A marquee is re-queried every frame it is dragged, so the query — BVH
        // descent plus the in-place refinement compaction — has to be free.
        (after - before).ShouldBe(0);
    }
}
