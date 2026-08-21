using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The world/part split. <see cref="BrushKind"/> decides whether a brush is
/// admitted to the fused static world, and the whole point of the bit is one
/// checkable invariant:
/// <para>
/// <b>After ANY sequence of writes to nodes whose kind is
/// <see cref="BrushKind.Part"/> — attaching, swapping or detaching a brush,
/// writing any transform, reparenting, adding to or removing from the scene —
/// the scene must still be clean and no compile may have been launched. The
/// only write on a part node permitted to disturb either is the
/// <see cref="SceneNode.BrushKind"/> setter itself.</b>
/// </para>
/// <para>
/// This matters because the carve is union-skin extraction, not subtraction: a
/// brush merely sitting in the world is harmless, but a brush that MOVES under
/// simulation changes the overlap set every tick, which the incremental
/// compiler cannot carry — so it would bail to the fully-validated O(world)
/// compile every tick, forever, while everything still rendered correctly.
/// These tests are the net under that silence.
/// </para>
/// </summary>
public sealed class BrushKindTests
{
    [Fact]
    public void A_brush_is_world_geometry_unless_it_says_otherwise()
    {
        var node = new SceneNode("brush") { Brush = CreateUnitBrush() };

        node.BrushKind.ShouldBe(BrushKind.World);
        node.IsStaticWorldBrush.ShouldBeTrue();
    }

    [Fact]
    public void A_part_brush_is_not_a_static_world_brush()
    {
        var node = new SceneNode("part") { BrushKind = BrushKind.Part, Brush = CreateUnitBrush() };

        node.IsStaticWorldBrush.ShouldBeFalse();
    }

    [Fact]
    public void Attaching_a_part_brush_does_not_mark_the_world_dirty()
    {
        // The gate the entire zero-cost claim rests on, and the one that fails
        // against an ungated Brush setter: MarkStaticWorldDirty sets the
        // force-full flag, so an ungated attach makes every spawned part cost
        // an O(world) walk.
        var (scene, _, _) = CreateCleanSceneWithWorldBrush();
        SceneNode part = scene.Root.CreateChild("part");
        part.BrushKind = BrushKind.Part;

        part.Brush = CreateUnitBrush();

        scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void Moving_a_part_brush_does_not_mark_the_world_dirty()
    {
        var (scene, part, _) = CreateCleanSceneWithPartBrush();

        part.LocalPosition = new Vector3(3f, 0f, 0f);

        scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void Moving_a_group_whose_only_brushes_are_parts_does_not_mark_the_world_dirty()
    {
        var (scene, _, _) = CreateCleanSceneWithWorldBrush();
        SceneNode group = scene.Root.CreateChild("group");
        SceneNode part = group.CreateChild("part");
        part.BrushKind = BrushKind.Part;
        part.Brush = CreateUnitBrush();
        scene.RebuildStaticWorld(new FakeRenderer());

        group.LocalPosition = new Vector3(0f, 0f, 4f);

        scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void Reparenting_a_part_brush_does_not_mark_the_world_dirty()
    {
        var (scene, part, _) = CreateCleanSceneWithPartBrush();
        SceneNode elsewhere = scene.Root.CreateChild("elsewhere");
        scene.RebuildStaticWorld(new FakeRenderer());

        elsewhere.AddChild(part);

        scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void Removing_a_part_brush_subtree_does_not_mark_the_world_dirty()
    {
        var (scene, part, _) = CreateCleanSceneWithPartBrush();

        scene.Root.RemoveChild(part);

        scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void Two_hundred_ticks_of_a_moving_part_brush_launch_no_compile()
    {
        // Pin (a): the simulated-brush case, which is the reason the kind
        // exists. A world brush doing this would launch 200 compiles.
        var (scene, part, renderer) = CreateCleanSceneWithPartBrush();
        int compilesBefore = scene.StaticWorldCompileCount;

        for (int i = 0; i < 200; i++)
        {
            part.LocalPosition = new Vector3(i * 0.05f, MathF.Sin(i * 0.1f), 0f);
            part.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * 0.01f);
            scene.StaticWorldDirty.ShouldBeFalse();
        }

        scene.RebuildStaticWorldIfDirty(renderer);
        scene.StaticWorldCompileCount.ShouldBe(compilesBefore);
    }

    [Fact]
    public void Two_hundred_attach_detach_and_swap_cycles_on_part_nodes_launch_no_compile()
    {
        // Pin (b): the attach half. This is the test that fails against an
        // ungated Brush setter — the transform half above passes there.
        var (scene, part, renderer) = CreateCleanSceneWithPartBrush();
        int compilesBefore = scene.StaticWorldCompileCount;

        for (int i = 0; i < 200; i++)
        {
            part.Brush = CreateUnitBrush();   // swap: a new instance every time
            scene.StaticWorldDirty.ShouldBeFalse();
            part.Brush = null;                // detach
            scene.StaticWorldDirty.ShouldBeFalse();
            part.Brush = CreateUnitBrush();   // re-attach
            scene.StaticWorldDirty.ShouldBeFalse();
        }

        scene.RebuildStaticWorldIfDirty(renderer);
        scene.StaticWorldCompileCount.ShouldBe(compilesBefore);
    }

    [Fact]
    public void Spawning_and_destroying_part_nodes_launches_no_compile()
    {
        var (scene, _, renderer) = CreateCleanSceneWithWorldBrush();
        int compilesBefore = scene.StaticWorldCompileCount;

        for (int i = 0; i < 200; i++)
        {
            SceneNode spawned = scene.Root.CreateChild($"part{i}");
            spawned.BrushKind = BrushKind.Part;
            spawned.Brush = CreateUnitBrush();
            scene.StaticWorldDirty.ShouldBeFalse();
            scene.Root.RemoveChild(spawned);
            scene.StaticWorldDirty.ShouldBeFalse();
        }

        scene.RebuildStaticWorldIfDirty(renderer);
        scene.StaticWorldCompileCount.ShouldBe(compilesBefore);
    }

    [Fact]
    public void Either_assignment_order_is_safe()
    {
        // Pin (c): the Brush setter reads the CURRENT kind, so neither order
        // corrupts. Stamping the kind first costs nothing at all; attaching
        // first costs one dirty plus one admission bump. Both must end in the
        // same state, and neither may be a refusal.
        var (kindFirstScene, _, _) = CreateCleanSceneWithWorldBrush();
        SceneNode kindFirst = kindFirstScene.Root.CreateChild("kind-first");
        kindFirst.BrushKind = BrushKind.Part;
        kindFirst.Brush = CreateUnitBrush();
        kindFirstScene.StaticWorldDirty.ShouldBeFalse();

        var (brushFirstScene, _, _) = CreateCleanSceneWithWorldBrush();
        SceneNode brushFirst = brushFirstScene.Root.CreateChild("brush-first");
        brushFirst.Brush = CreateUnitBrush();
        brushFirst.BrushKind = BrushKind.Part;

        kindFirst.IsStaticWorldBrush.ShouldBeFalse();
        brushFirst.IsStaticWorldBrush.ShouldBeFalse();
        kindFirst.SubtreeBrushCount.ShouldBe(brushFirst.SubtreeBrushCount);
        kindFirst.SubtreeStaticWorldBrushCount.ShouldBe(brushFirst.SubtreeStaticWorldBrushCount);
    }

    [Fact]
    public void Stamping_a_kind_on_a_brushless_node_signals_nothing()
    {
        var (scene, _, _) = CreateCleanSceneWithWorldBrush();
        SceneNode empty = scene.Root.CreateChild("empty");

        empty.BrushKind = BrushKind.Part;

        scene.StaticWorldDirty.ShouldBeFalse();
        empty.SubtreeBrushCount.ShouldBe(0);
        empty.SubtreeStaticWorldBrushCount.ShouldBe(0);
    }

    [Fact]
    public void Converting_a_world_brush_to_a_part_marks_the_world_dirty()
    {
        // The one write on the admission axis that MUST signal: the brush is
        // leaving the fused world, so the placement count changed and every
        // later slot shifted.
        var (scene, node, _) = CreateCleanSceneWithWorldBrush();

        node.BrushKind = BrushKind.Part;

        scene.StaticWorldDirty.ShouldBeTrue();
    }

    [Fact]
    public void Converting_a_part_back_to_world_geometry_marks_the_world_dirty()
    {
        var (scene, part, _) = CreateCleanSceneWithPartBrush();

        part.BrushKind = BrushKind.World;

        scene.StaticWorldDirty.ShouldBeTrue();
    }

    [Fact]
    public void An_equal_kind_write_signals_nothing()
    {
        var (scene, node, _) = CreateCleanSceneWithWorldBrush();

        node.BrushKind = BrushKind.World;

        scene.StaticWorldDirty.ShouldBeFalse();
    }

    [Fact]
    public void A_part_brush_contributes_no_placement_and_no_world_geometry()
    {
        var scene = new Scene("Test");
        SceneNode part = scene.Root.CreateChild("part");
        part.BrushKind = BrushKind.Part;
        part.Brush = CreateUnitBrush();

        scene.RebuildStaticWorld(new FakeRenderer());

        // Nothing was admitted, so there is nothing to compile.
        scene.StaticWorld.ShouldBeNull();
    }

    [Fact]
    public void A_part_brush_does_not_carve_the_world_brush_it_overlaps()
    {
        // The visible difference between the kinds, and the reason the editor
        // must show which is which: two world brushes merge into one skin, but
        // a part sitting inside a world brush leaves it completely untouched.
        var scene = new Scene("Test");
        SceneNode world = scene.Root.CreateChild("world");
        world.Brush = Brush.CreateBox(new Vector3(-4f, -1f, -4f), new Vector3(4f, 1f, 4f));
        scene.RebuildStaticWorld(new FakeRenderer());
        int worldOnlySurfaces = CountSurfaces(scene);

        SceneNode part = scene.Root.CreateChild("part");
        part.BrushKind = BrushKind.Part;
        part.Brush = CreateUnitBrush();
        scene.RebuildStaticWorldIfDirty(new FakeRenderer());

        CountSurfaces(scene).ShouldBe(worldOnlySurfaces);
    }

    [Fact]
    public void The_rigidity_counter_stays_kind_blind()
    {
        // SubtreeBrushCount answers "is there a brush of ANY kind below me?" —
        // rigidity, which a part brush is subject to just as much as a world
        // one. ScaleGizmo's group refusal reads it, and a world-only counter
        // would silently delete that refusal for a group of parts.
        var group = new SceneNode("group");
        SceneNode part = group.CreateChild("part");
        part.BrushKind = BrushKind.Part;
        part.Brush = CreateUnitBrush();

        group.SubtreeBrushCount.ShouldBe(1);
        group.SubtreeStaticWorldBrushCount.ShouldBe(0);
    }

    [Fact]
    public void Both_counter_lanes_survive_a_thousand_random_graph_operations()
    {
        // The two lanes have one writer, so they cannot drift — but "cannot"
        // is a claim, and this is what makes it a fact. Recount both
        // recursively and compare against the incrementally-maintained values.
        var rng = new Random(20260821);
        var scene = new Scene("Test");
        var nodes = new List<SceneNode> { scene.Root };

        for (int i = 0; i < 1000; i++)
        {
            SceneNode target = nodes[rng.Next(nodes.Count)];
            switch (rng.Next(5))
            {
                case 0: // grow the graph
                    nodes.Add(target.CreateChild($"n{i}"));
                    break;
                case 1: // attach or detach a brush
                    target.Brush = target.Brush is null ? CreateUnitBrush() : null;
                    break;
                case 2: // flip the kind
                    target.BrushKind = target.BrushKind == BrushKind.World ? BrushKind.Part : BrushKind.World;
                    break;
                case 3: // reparent, avoiding the cycle a self/descendant move would make
                {
                    SceneNode newParent = nodes[rng.Next(nodes.Count)];
                    if (!ReferenceEquals(target, scene.Root) && !IsAncestorOrSelf(target, newParent))
                        newParent.AddChild(target);
                    break;
                }
                case 4: // detach a subtree, then put it back so the pool stays live
                    if (target.Parent is { } parent && !ReferenceEquals(target, scene.Root))
                    {
                        parent.RemoveChild(target);
                        parent.AddChild(target);
                    }
                    break;
            }
        }

        foreach (SceneNode node in scene.Root.Traverse())
        {
            (int total, int world) = RecountSubtree(node);
            node.SubtreeBrushCount.ShouldBe(total, $"total lane wrong at '{node.Name}'");
            node.SubtreeStaticWorldBrushCount.ShouldBe(world, $"static-world lane wrong at '{node.Name}'");
            node.SubtreeStaticWorldBrushCount.ShouldBeLessThanOrEqualTo(node.SubtreeBrushCount);
        }
    }

    private static (int Total, int World) RecountSubtree(SceneNode node)
    {
        int total = 0, world = 0;
        foreach (SceneNode n in node.Traverse())
        {
            if (n.Brush is null)
                continue;
            total++;
            if (n.BrushKind == BrushKind.World)
                world++;
        }
        return (total, world);
    }

    private static bool IsAncestorOrSelf(SceneNode candidate, SceneNode node)
    {
        for (SceneNode? n = node; n is not null; n = n.Parent)
        {
            if (ReferenceEquals(n, candidate))
                return true;
        }
        return false;
    }

    private static int CountSurfaces(Scene scene)
    {
        return scene.StaticWorld is { } world ? world.Surfaces.Count : 0;
    }

    private static Brush CreateUnitBrush() =>
        Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

    private static (Scene Scene, SceneNode Node, FakeRenderer Renderer) CreateCleanSceneWithWorldBrush()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("world");
        node.Brush = CreateUnitBrush();

        var renderer = new FakeRenderer();
        scene.RebuildStaticWorld(renderer);
        scene.StaticWorldDirty.ShouldBeFalse();
        return (scene, node, renderer);
    }

    // A scene holding one WORLD brush (so a compiled world exists and a stray
    // dirty signal would be visible) plus the part node under test.
    private static (Scene Scene, SceneNode Part, FakeRenderer Renderer) CreateCleanSceneWithPartBrush()
    {
        var scene = new Scene("Test");
        SceneNode world = scene.Root.CreateChild("world");
        world.Brush = Brush.CreateBox(new Vector3(-8f, -1f, -8f), new Vector3(8f, 1f, 8f));

        SceneNode part = scene.Root.CreateChild("part");
        part.BrushKind = BrushKind.Part;
        part.Brush = CreateUnitBrush();

        var renderer = new FakeRenderer();
        scene.RebuildStaticWorld(renderer);
        scene.StaticWorldDirty.ShouldBeFalse();
        return (scene, part, renderer);
    }
}
