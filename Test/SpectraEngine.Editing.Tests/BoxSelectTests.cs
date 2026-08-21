using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Box select, checked against an oracle that shares none of its machinery: the
/// implementation asks the BVH for a sub-frustum's occupants and refines them,
/// while the oracle walks every node in the scene, projects its bounds straight
/// from the brush and the world matrix, and compares rectangles. Anything the
/// acceleration structure drops — or invents — shows up as a disagreement.
/// </summary>
/// <remarks>
/// The grid is deliberately dense enough that most rectangles cut <em>through</em>
/// nodes rather than cleanly around them: a marquee that only ever fully
/// contained or fully missed its targets would not exercise the difference
/// between the conservative frustum test and the exact screen-rectangle one,
/// which is the whole reason the refinement pass exists.
/// </remarks>
public sealed class BoxSelectTests
{
    // 7 x 5 nodes, 2 units apart, small enough that the marquee edges cut
    // through them.
    private const int GridWidth = 7;
    private const int GridHeight = 5;
    private const float Spacing = 2f;
    private const float HalfExtent = 0.45f;

    // --- The oracle comparison ----------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public void Box_select_picks_exactly_what_the_oracle_says_it_should(
        float yaw, float pitch, float distance,
        float x0, float y0, float x1, float y1,
        BoxSelectMode mode)
    {
        var harness = BuildGrid(yaw, pitch, distance);
        var rect = ScreenRect.FromCorners(new Vector2(x0, y0), new Vector2(x1, y1));

        var actual = new List<SceneNode>();
        BoxSelectQuery.Query(harness.Scene, in rect, harness.ViewportSize, mode, actual);

        SceneNode[] expected = Oracle(harness, rect, mode);

        actual.ShouldBe(expected, ignoreOrder: true);
    }

    public static TheoryData<float, float, float, float, float, float, float, BoxSelectMode> Cases()
    {
        var data = new TheoryData<float, float, float, float, float, float, float, BoxSelectMode>();
        (float Yaw, float Pitch, float Distance)[] poses =
        [
            (0f, 0f, 30f),
            (-MathF.PI / 2f, 0f, 26f),
            (0.8f, -0.45f, 34f),
            (2.6f, 0.6f, 22f),
            (-1.9f, -0.15f, 40f),
        ];
        (float X0, float Y0, float X1, float Y1)[] rects =
        [
            (250f, 180f, 560f, 430f),   // a box through the middle of the crowd
            (0f, 0f, 800f, 600f),       // the whole viewport
            (395f, 295f, 405f, 305f),   // a tiny box at the centre
            (560f, 430f, 250f, 180f),   // the first one, dragged the other way
            (-200f, -150f, 380f, 290f), // a drag that started off-screen
            (700f, 40f, 790f, 560f),    // a tall thin strip down the right
            (40f, 520f, 760f, 560f),    // a wide flat strip along the bottom
        ];

        foreach ((float yaw, float pitch, float distance) in poses)
        {
            foreach ((float x0, float y0, float x1, float y1) in rects)
            {
                data.Add(yaw, pitch, distance, x0, y0, x1, y1, BoxSelectMode.Intersect);
                data.Add(yaw, pitch, distance, x0, y0, x1, y1, BoxSelectMode.Contain);
            }
        }
        return data;
    }

    [Fact]
    public void Contain_mode_never_picks_more_than_intersect_mode()
    {
        var harness = BuildGrid(0.6f, -0.3f, 28f);
        // Deliberately cutting through the grid rather than around it, so some
        // nodes are half in.
        var rect = ScreenRect.FromCorners(new Vector2(300f, 220f), new Vector2(520f, 400f));

        var loose = new List<SceneNode>();
        var strict = new List<SceneNode>();
        BoxSelectQuery.Query(harness.Scene, in rect, harness.ViewportSize, BoxSelectMode.Intersect, loose);
        BoxSelectQuery.Query(harness.Scene, in rect, harness.ViewportSize, BoxSelectMode.Contain, strict);

        strict.ShouldBeSubsetOf(loose);
        strict.Count.ShouldBeLessThan(loose.Count); // the rect really does straddle some
    }

    [Fact]
    public void Dragging_the_rectangle_backwards_selects_the_same_nodes()
    {
        var harness = BuildGrid(0.4f, -0.2f, 30f);
        var forward = new List<SceneNode>();
        var backward = new List<SceneNode>();

        var a = ScreenRect.FromCorners(new Vector2(220f, 160f), new Vector2(600f, 440f));
        var b = ScreenRect.FromCorners(new Vector2(600f, 440f), new Vector2(220f, 160f));
        BoxSelectQuery.Query(harness.Scene, in a, harness.ViewportSize, BoxSelectMode.Intersect, forward);
        BoxSelectQuery.Query(harness.Scene, in b, harness.ViewportSize, BoxSelectMode.Intersect, backward);

        backward.ShouldBe(forward);
    }

    // --- The controller: modifiers and events --------------------------------

    [Fact]
    public void A_marquee_replaces_the_selection_and_raises_exactly_one_event()
    {
        var harness = BuildGrid(0f, 0f, 30f);
        SceneNode outsider = harness.Scene.Root.Children[0];
        harness.Scene.Selection.Select(outsider);

        int fired = 0;
        harness.Scene.Selection.SelectionChanged += () => fired++;

        DragMarquee(harness, new Vector2(250f, 180f), new Vector2(560f, 430f));

        fired.ShouldBe(1);
        harness.Scene.Selection.Items.ShouldBe(harness.Viewport.BoxSelect.LastResult, ignoreOrder: true);
        harness.Viewport.BoxSelect.LastResult.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Shift_adds_the_marquee_to_what_is_already_selected()
    {
        var harness = BuildGrid(0f, 0f, 30f);
        SceneNode kept = harness.Scene.Root.Children[^1];
        harness.Scene.Selection.Select(kept);

        int fired = 0;
        harness.Scene.Selection.SelectionChanged += () => fired++;

        DragMarquee(harness, new Vector2(250f, 180f), new Vector2(560f, 430f), KeyModifiers.Shift);

        fired.ShouldBe(1);
        harness.Scene.Selection.Contains(kept).ShouldBeTrue();
        foreach (SceneNode node in harness.Viewport.BoxSelect.LastResult)
            harness.Scene.Selection.Contains(node).ShouldBeTrue();
        // Stable order: the pre-existing selection stays first.
        harness.Scene.Selection.Items[0].ShouldBeSameAs(kept);
    }

    [Fact]
    public void Ctrl_toggles_the_marquee_against_what_is_already_selected()
    {
        var harness = BuildGrid(0f, 0f, 30f);
        // Smaller than the grid's projection, so there is an untouched node to
        // prove the toggle leaves everything outside the marquee alone.
        var from = new Vector2(350f, 250f);
        var to = new Vector2(460f, 340f);
        var rect = ScreenRect.FromCorners(from, to);
        var covered = new List<SceneNode>();
        BoxSelectQuery.Query(harness.Scene, in rect, harness.ViewportSize, BoxSelectMode.Intersect, covered);
        covered.Count.ShouldBeGreaterThan(2);

        // Pre-select half of what the marquee will cover, plus one node it will not.
        SceneNode outsider = harness.Scene.Root.Children.First(node => !covered.Contains(node));
        var preselected = covered.Take(covered.Count / 2).Append(outsider).ToArray();
        harness.Scene.Selection.SetRange(preselected);

        int fired = 0;
        harness.Scene.Selection.SelectionChanged += () => fired++;

        DragMarquee(harness, from, to, KeyModifiers.Control);

        fired.ShouldBe(1);
        harness.Scene.Selection.Contains(outsider).ShouldBeTrue(); // untouched by the marquee
        for (int i = 0; i < covered.Count; i++)
        {
            bool wasSelected = preselected.Contains(covered[i]);
            harness.Scene.Selection.Contains(covered[i]).ShouldBe(!wasSelected);
        }
    }

    [Fact]
    public void Selecting_five_hundred_nodes_raises_one_event_not_five_hundred()
    {
        var harness = new ViewportHarness();
        harness.Orbit(Vector3.Zero, 400f, -MathF.PI / 2f, 0f);
        for (int i = 0; i < 500; i++)
            harness.AddBrush(new Vector3(i % 25 * 4f - 48f, i / 25 * 4f - 38f, 0f), 0.5f, $"N{i}");

        int fired = 0;
        harness.Scene.Selection.SelectionChanged += () => fired++;

        DragMarquee(harness, new Vector2(2f, 2f), new Vector2(798f, 598f));

        fired.ShouldBe(1);
        harness.Scene.Selection.Count.ShouldBe(500);
    }

    [Fact]
    public void A_marquee_that_covers_the_same_nodes_twice_raises_nothing_the_second_time()
    {
        var harness = BuildGrid(0f, 0f, 30f);
        DragMarquee(harness, new Vector2(250f, 180f), new Vector2(560f, 430f));

        int fired = 0;
        harness.Scene.Selection.SelectionChanged += () => fired++;

        DragMarquee(harness, new Vector2(250f, 180f), new Vector2(560f, 430f));

        fired.ShouldBe(0);
    }

    [Fact]
    public void A_click_on_empty_space_clears_the_selection()
    {
        var harness = BuildGrid(0f, 0f, 30f);
        harness.Scene.Selection.Select(harness.Scene.Root.Children[0]);

        Vector2 empty = EmptyPixel(harness);
        harness.Press(empty);
        harness.Release(empty);

        harness.Viewport.BoxSelect.LastResult.ShouldBeEmpty();
        harness.Scene.Selection.Count.ShouldBe(0);
    }

    [Fact]
    public void A_modified_click_on_empty_space_keeps_the_selection()
    {
        var harness = BuildGrid(0f, 0f, 30f);
        SceneNode kept = harness.Scene.Root.Children[0];
        harness.Scene.Selection.Select(kept);

        Vector2 empty = EmptyPixel(harness);
        harness.Press(empty, KeyModifiers.Shift);
        harness.Release(empty, KeyModifiers.Shift);

        // A slipped Shift+click must not throw away a carefully built selection.
        harness.Scene.Selection.Items.ShouldBe(new[] { kept });
    }

    [Fact]
    public void Cancelling_a_marquee_leaves_the_selection_untouched()
    {
        var harness = BuildGrid(0f, 0f, 30f);
        SceneNode kept = harness.Scene.Root.Children[0];
        harness.Scene.Selection.Select(kept);

        harness.Press(new Vector2(250f, 180f));
        harness.Drag(new Vector2(560f, 430f));
        harness.Viewport.Update(harness.Frame(new Vector2(560f, 430f), down: PointerButtons.Left), cancelRequested: true);

        harness.Viewport.BoxSelect.IsActive.ShouldBeFalse();
        harness.Scene.Selection.Items.ShouldBe(new[] { kept });
    }

    // --- The overlay path ----------------------------------------------------

    [Fact]
    public void The_marquee_draws_four_lines_that_reproject_onto_the_rectangle()
    {
        var harness = BuildGrid(0.5f, -0.3f, 30f);
        var from = new Vector2(220f, 160f);
        var to = new Vector2(600f, 440f);
        harness.Press(from);
        harness.Drag(to);

        var output = new DebugDraw();
        harness.Viewport.Draw(output, harness.ViewportSize);

        // The gizmo draws nothing (nothing is selected), so every vertex here is
        // the marquee: four lines, two vertices each, six floats per vertex.
        output.VertexCount.ShouldBe(8);

        ScreenRect rect = harness.Viewport.BoxSelect.Rect;
        foreach (Vector3 vertex in DebugLineVertices(output))
        {
            Vector2 pixel = harness.WorldToScreen(vertex);
            // The whole claim of the unprojected-overlay approach: what lands on
            // screen is exactly the rectangle the user is dragging.
            pixel.X.ShouldBeOneOf(rect.Min.X, rect.Max.X, 0.5f);
            pixel.Y.ShouldBeOneOf(rect.Min.Y, rect.Max.Y, 0.5f);
        }
    }

    [Fact]
    public void A_marquee_still_within_the_click_threshold_draws_nothing()
    {
        var harness = BuildGrid(0f, 0f, 30f);
        // From empty space, so this is a marquee rather than an object drag.
        harness.Press(EmptyPixel(harness));
        harness.Drag(EmptyPixel(harness) + new Vector2(1f, 1f));

        var output = new DebugDraw();
        harness.Viewport.Draw(output, harness.ViewportSize);

        output.VertexCount.ShouldBe(0);
    }

    // --- Fixture and oracle --------------------------------------------------

    private static ViewportHarness BuildGrid(float yaw, float pitch, float distance)
    {
        var harness = new ViewportHarness();
        for (int y = 0; y < GridHeight; y++)
        {
            for (int x = 0; x < GridWidth; x++)
            {
                harness.AddBrush(
                    new Vector3(
                        (x - (GridWidth - 1) * 0.5f) * Spacing,
                        (y - (GridHeight - 1) * 0.5f) * Spacing,
                        0f),
                    HalfExtent,
                    $"N{x}_{y}");
            }
        }

        harness.Orbit(Vector3.Zero, distance, yaw, pitch);
        return harness;
    }

    /// <summary>
    /// The independent answer: walk every node, rebuild its world bounds from
    /// the brush and the node's matrix (never from the spatial index), project
    /// the eight corners with the camera's own view-projection, and compare
    /// screen rectangles.
    /// </summary>
    private static SceneNode[] Oracle(ViewportHarness harness, in ScreenRect rect, BoxSelectMode mode)
    {
        Matrix4x4 viewProjection = harness.Scene.Camera.GetViewProjection();
        Vector2 viewport = harness.ViewportSize;
        var hits = new List<SceneNode>();

        foreach (SceneNode node in harness.Scene.Nodes)
        {
            if (node.Brush is not { } brush)
                continue;

            Aabb bounds = brush.LocalBounds.Transform(node.WorldMatrix);
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);

            for (int corner = 0; corner < 8; corner++)
            {
                var world = new Vector3(
                    (corner & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                    (corner & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                    (corner & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);

                Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
                // The fixture keeps everything comfortably in front of the
                // camera; if that ever stops being true the oracle's own
                // definition would go undefined, so it is asserted rather than
                // silently handled.
                clip.W.ShouldBeGreaterThan(0.01f, $"'{node.Name}' straddles the eye plane");

                var pixel = new Vector2(
                    (clip.X / clip.W + 1f) * 0.5f * viewport.X,
                    (1f - clip.Y / clip.W) * 0.5f * viewport.Y);
                min = Vector2.Min(min, pixel);
                max = Vector2.Max(max, pixel);
            }

            var projected = new ScreenRect(min, max);
            bool covered = mode == BoxSelectMode.Contain
                ? rect.Contains(in projected)
                : rect.Intersects(in projected);

            if (covered)
                hits.Add(node);
        }

        return [.. hits];
    }

    private static void DragMarquee(
        ViewportHarness harness, Vector2 from, Vector2 to, KeyModifiers modifiers = KeyModifiers.None)
    {
        harness.Press(from, modifiers);
        harness.Viewport.DragMode.ShouldBe(Editing.Viewport.ViewportDragMode.BoxSelect);
        harness.Drag(to, modifiers);
        harness.Release(to, modifiers);
    }

    // A pixel with no node under it and no gizmo handle near it: the far corner
    // of the viewport, well outside the grid's projection.
    private static Vector2 EmptyPixel(ViewportHarness harness) => new(6f, 6f);

    private static IEnumerable<Vector3> DebugLineVertices(DebugDraw output)
    {
        ReadOnlySpan<float> data = output.Vertices;
        var result = new List<Vector3>();
        for (int i = 0; i + 5 < data.Length; i += 6)
            result.Add(new Vector3(data[i], data[i + 1], data[i + 2]));
        return result;
    }
}

/// <summary>Assertions the box-select suite shares.</summary>
internal static class BoxSelectAssertions
{
    /// <summary>Asserts the value is within tolerance of one of two expected values.</summary>
    public static void ShouldBeOneOf(this float actual, float first, float second, float tolerance)
    {
        if (MathF.Abs(actual - first) <= tolerance || MathF.Abs(actual - second) <= tolerance)
            return;

        throw new Shouldly.ShouldAssertException(
            $"Expected {actual} to be within {tolerance} of {first} or {second}.");
    }
}
