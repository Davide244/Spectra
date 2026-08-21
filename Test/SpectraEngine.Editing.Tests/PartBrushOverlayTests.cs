using System;
using System.Numerics;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Viewport;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The part-brush outline. A part and a world brush are drawn from the same
/// planes with the same materials and are indistinguishable at rest, yet they
/// behave differently in ways that read as engine bugs — a part does not carve
/// and is not carved, so it interpenetrates instead of merging and z-fights
/// where a world face would have been welded away. The outline is what tells
/// the author which one they are looking at.
/// </summary>
public sealed class PartBrushOverlayTests
{
    [Fact]
    public void A_part_brush_is_outlined()
    {
        var scene = new Scene("Test");
        AddPart(scene, "part");
        var overlay = new PartBrushOverlay();
        var output = new DebugDraw();

        overlay.Draw(output, scene);

        overlay.DrawnLastDraw.ShouldBe(1);
        output.VertexCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_world_brush_is_not_outlined()
    {
        // World geometry is the default and the overwhelming majority; marking
        // it would make the overlay noise instead of a signal.
        var scene = new Scene("Test");
        SceneNode world = scene.Root.CreateChild("world");
        world.Brush = CreateUnitBrush();
        var overlay = new PartBrushOverlay();
        var output = new DebugDraw();

        overlay.Draw(output, scene);

        overlay.DrawnLastDraw.ShouldBe(0);
        output.VertexCount.ShouldBe(0);
    }

    [Fact]
    public void Converting_a_brush_moves_it_in_and_out_of_the_overlay()
    {
        var scene = new Scene("Test");
        SceneNode node = scene.Root.CreateChild("brush");
        node.Brush = CreateUnitBrush();
        var overlay = new PartBrushOverlay();

        node.BrushKind = BrushKind.Part;
        overlay.Draw(new DebugDraw(), scene);
        overlay.DrawnLastDraw.ShouldBe(1);

        node.BrushKind = BrushKind.World;
        overlay.Draw(new DebugDraw(), scene);
        overlay.DrawnLastDraw.ShouldBe(0);
    }

    [Fact]
    public void The_outline_follows_the_node_rather_than_the_brush()
    {
        // A part's mesh is brush-local and its movement is a matrix write, so
        // the outline has to be transformed the same way — an outline drawn in
        // brush space would sit at the origin while the part flew away.
        var scene = new Scene("Test");
        SceneNode part = AddPart(scene, "part");
        part.LocalPosition = new Vector3(50f, 0f, 0f);
        var output = new DebugDraw();

        new PartBrushOverlay().Draw(output, scene);

        // DebugDraw packs interleaved position+colour floats, so the positions
        // are the first three of every six.
        ReadOnlySpan<float> data = output.Vertices;
        bool anyNearThePart = false;
        for (int i = 0; i + 5 < data.Length; i += 6)
        {
            if (data[i] > 45f)
                anyNearThePart = true;
        }
        anyNearThePart.ShouldBeTrue();
    }

    [Fact]
    public void The_budget_is_disclosed_rather_than_silently_truncating()
    {
        // A cap that quietly drops outlines reads as "every part is outlined"
        // while some are not — the exact class of lie the overlay exists to
        // prevent.
        var scene = new Scene("Test");
        for (int i = 0; i < 10; i++)
            AddPart(scene, $"part{i}");
        var overlay = new PartBrushOverlay { MaxOutlines = 4 };

        overlay.Draw(new DebugDraw(), scene);

        overlay.DrawnLastDraw.ShouldBe(4);
        overlay.SkippedLastDraw.ShouldBe(6);
    }

    [Fact]
    public void A_disabled_overlay_draws_nothing_and_reports_nothing_skipped()
    {
        var scene = new Scene("Test");
        AddPart(scene, "part");
        var overlay = new PartBrushOverlay { Enabled = false };
        var output = new DebugDraw();

        overlay.Draw(output, scene);

        output.VertexCount.ShouldBe(0);
        overlay.DrawnLastDraw.ShouldBe(0);
        overlay.SkippedLastDraw.ShouldBe(0);
    }

    private static SceneNode AddPart(Scene scene, string name)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.BrushKind = BrushKind.Part;
        node.Brush = CreateUnitBrush();
        return node;
    }

    private static Brush CreateUnitBrush() =>
        Brush.CreateBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
}
