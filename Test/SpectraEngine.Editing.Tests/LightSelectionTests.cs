using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Selection;
using SpectraEngine.Editing.Viewport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Lights can be seen and picked in the viewport, which they could not be at
/// all: they carry no mesh and no brush, and the scene deliberately keeps them
/// out of the spatial index, so a lamp was findable only by name in the tree
/// and a marquee dragged across one CLEARED the selection.
/// </summary>
/// <remarks>
/// <b>The marquee cases carry their own oracle</b>, computed from the camera's
/// view-projection and the icon's documented pixel radius and sharing nothing
/// with <see cref="BoxSelectQuery"/>'s implementation. Extending the cases
/// without extending the oracle would prove only that the code agrees with
/// itself, which is the failure mode the box-select suite was built to avoid.
/// </remarks>
public sealed class LightSelectionTests
{
    private static ViewportHarness BuildLitScene(params Vector3[] positions)
    {
        var harness = new ViewportHarness();

        for (int i = 0; i < positions.Length; i++)
        {
            var node = new SceneNode($"Lamp{i}")
            {
                LocalPosition = positions[i],
                Light = new Light { Kind = LightKind.Point, Range = 6f },
            };

            harness.Scene.Root.AddChild(node);
        }

        // Looking down -Z from a little way back, so every lamp is comfortably
        // in front of the eye plane and the projection is well defined.
        harness.Scene.Camera.Position = new Vector3(0f, 0f, 12f);
        harness.Scene.Camera.LookAt(Vector3.Zero);
        return harness;
    }

    // --- Clicking ------------------------------------------------------------

    [Fact]
    public void A_ray_through_a_lamps_icon_finds_it()
    {
        ViewportHarness harness = BuildLitScene(Vector3.Zero);
        SceneNode lamp = harness.Scene.Root.Children[0];

        Ray3 ray = harness.Scene.Camera.ScreenPointToRay(harness.CenterPixel, harness.ViewportSize);

        LightPicking.TryPick(
            harness.Scene, harness.Scene.Camera, in ray, harness.ViewportSize,
            out SceneNode? hit, out float distance).ShouldBeTrue();

        hit.ShouldBeSameAs(lamp);
        distance.ShouldBeGreaterThan(0f);
    }

    [Fact]
    public void A_ray_well_clear_of_every_icon_finds_nothing()
    {
        ViewportHarness harness = BuildLitScene(Vector3.Zero);

        // The far corner: the lamp projects to the centre, and the icon is nine
        // pixels.
        Ray3 ray = harness.Scene.Camera.ScreenPointToRay(
            new Vector2(4f, 4f), harness.ViewportSize);

        LightPicking.TryPick(
            harness.Scene, harness.Scene.Camera, in ray, harness.ViewportSize,
            out SceneNode? hit, out _).ShouldBeFalse();

        hit.ShouldBeNull();
    }

    [Fact]
    public void The_nearest_of_two_stacked_lamps_wins()
    {
        // Both project to the same pixel; only their depth differs.
        ViewportHarness harness = BuildLitScene(
            new Vector3(0f, 0f, -6f),
            new Vector3(0f, 0f, 2f));

        SceneNode near = harness.Scene.Root.Children[1];

        Ray3 ray = harness.Scene.Camera.ScreenPointToRay(harness.CenterPixel, harness.ViewportSize);

        LightPicking.TryPick(
            harness.Scene, harness.Scene.Camera, in ray, harness.ViewportSize,
            out SceneNode? hit, out _).ShouldBeTrue();

        hit.ShouldBeSameAs(near);
    }

    [Fact]
    public void The_icon_holds_a_constant_screen_size_however_far_away_it_is()
    {
        ViewportHarness harness = BuildLitScene(Vector3.Zero);
        Camera camera = harness.Scene.Camera;

        // The pick radius has to grow with distance in WORLD units for the icon
        // to stay the same size in pixels, which is the whole reason it is
        // computed rather than fixed. Twice as far, twice as big.
        float near = LightPicking.WorldRadius(camera, harness.ViewportSize, Vector3.Zero);
        float far = LightPicking.WorldRadius(camera, harness.ViewportSize, new Vector3(0f, 0f, -12f));

        far.ShouldBe(near * 2f, tolerance: near * 0.02f);
    }

    [Fact]
    public void A_lamp_behind_the_camera_is_never_picked()
    {
        ViewportHarness harness = BuildLitScene(new Vector3(0f, 0f, 40f));

        Ray3 ray = harness.Scene.Camera.ScreenPointToRay(harness.CenterPixel, harness.ViewportSize);

        LightPicking.TryPick(
            harness.Scene, harness.Scene.Camera, in ray, harness.ViewportSize,
            out _, out _).ShouldBeFalse();
    }

    // --- Marquee -------------------------------------------------------------

    [Theory]
    [InlineData(BoxSelectMode.Intersect)]
    [InlineData(BoxSelectMode.Contain)]
    public void A_marquee_picks_exactly_the_lamps_the_oracle_says_it_should(BoxSelectMode mode)
    {
        ViewportHarness harness = BuildLitScene(
            new Vector3(-4f, 2f, 0f),
            new Vector3(0f, 0f, 0f),
            new Vector3(4f, -2f, 0f),
            new Vector3(-1f, -3f, 0f));

        // Deliberately cutting through the middle of the field rather than
        // around it, so containment and intersection disagree.
        var rect = ScreenRect.FromCorners(new Vector2(220f, 180f), new Vector2(560f, 430f));

        var actual = new List<SceneNode>();
        BoxSelectQuery.Query(harness.Scene, in rect, harness.ViewportSize, mode, actual);

        SceneNode[] expected = Oracle(harness, in rect, mode);

        actual.Select(n => n.Name).OrderBy(n => n)
            .ShouldBe(expected.Select(n => n.Name).OrderBy(n => n));
    }

    [Fact]
    public void A_marquee_across_a_room_of_lamps_no_longer_selects_nothing()
    {
        ViewportHarness harness = BuildLitScene(
            new Vector3(-3f, 0f, 0f),
            new Vector3(0f, 0f, 0f),
            new Vector3(3f, 0f, 0f));

        // The whole viewport. Before lights were pickable this returned an
        // empty list, and the marquee then CLEARED the selection - the specific
        // behaviour that reads as the marquee being broken.
        var rect = ScreenRect.FromCorners(Vector2.Zero, harness.ViewportSize);

        var actual = new List<SceneNode>();
        BoxSelectQuery.Query(harness.Scene, in rect, harness.ViewportSize, BoxSelectMode.Intersect, actual);

        actual.Count.ShouldBe(3);
    }

    [Fact]
    public void A_node_carrying_both_a_light_and_a_brush_is_reported_once()
    {
        var harness = new ViewportHarness();

        var node = new SceneNode("LampBlock")
        {
            Brush = Brush.CreateBox(Vector3.Zero, new Vector3(1f, 1f, 1f)),
            Light = new Light { Kind = LightKind.Point, Range = 4f },
        };

        harness.Scene.Root.AddChild(node);
        harness.Scene.Camera.Position = new Vector3(0f, 0f, 12f);
        harness.Scene.Camera.LookAt(Vector3.Zero);

        var rect = ScreenRect.FromCorners(Vector2.Zero, harness.ViewportSize);

        var actual = new List<SceneNode>();
        BoxSelectQuery.Query(harness.Scene, in rect, harness.ViewportSize, BoxSelectMode.Intersect, actual);

        // It is already in the spatial index through its brush; adding it again
        // for its light would put the same node in the selection twice, which
        // an additive Ctrl-drag would then toggle straight back out.
        actual.Count.ShouldBe(1);
    }

    /// <summary>
    /// The independent answer: project each lamp's origin with the camera's own
    /// view-projection, build the icon rectangle from the documented pixel
    /// radius, and compare.
    /// </summary>
    private static SceneNode[] Oracle(ViewportHarness harness, in ScreenRect rect, BoxSelectMode mode)
    {
        Matrix4x4 viewProjection = harness.Scene.Camera.GetViewProjection();
        Vector2 viewport = harness.ViewportSize;
        var hits = new List<SceneNode>();

        foreach (SceneNode node in harness.Scene.Root.Children)
        {
            if (node.Light is null)
                continue;

            Vector4 clip = Vector4.Transform(new Vector4(node.WorldPosition, 1f), viewProjection);
            clip.W.ShouldBeGreaterThan(0.01f, $"'{node.Name}' straddles the eye plane");

            var pixel = new Vector2(
                ((clip.X / clip.W) + 1f) * 0.5f * viewport.X,
                (1f - (clip.Y / clip.W)) * 0.5f * viewport.Y);

            const float R = LightOverlay.IconPixels;
            var icon = new ScreenRect(
                new Vector2(pixel.X - R, pixel.Y - R),
                new Vector2(pixel.X + R, pixel.Y + R));

            bool covered = mode == BoxSelectMode.Contain
                ? rect.Contains(in icon)
                : rect.Intersects(in icon);

            if (covered)
                hits.Add(node);
        }

        return [.. hits];
    }
}
