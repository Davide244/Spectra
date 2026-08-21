using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Input;
using Silk.NET.Maths;
using SpectraEngine.Core.Input;
using SpectraEngine.Editing.Input;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The Silk-to-editing input adapter: it must read the viewport from the
/// renderer's framebuffer latch (never from the window, which only the main
/// thread may query), rebase the cursor onto a sub-rect viewport, and carry
/// the input manager's latched edges, modifiers and wheel across the seam in
/// the engine's own backend-neutral vocabulary.
/// </summary>
/// <remarks>
/// The input manager is driven through its OS-event entry points directly —
/// they are internal precisely so a headless suite can exercise the state
/// machine without a real device; the device argument is unused by them.
/// </remarks>
public sealed class EngineEditorInputSourceTests
{
    [Fact]
    public void The_viewport_comes_from_the_renderer_framebuffer_latch()
    {
        var (input, renderer, source) = CreateSource();
        renderer.SetFramebufferSize(new Vector2D<int>(1280, 720));
        input.Update(1.0 / 60.0);

        EditorInputFrame frame = source.CaptureFrame(1f / 60f);

        frame.ViewportSize.ShouldBe(new Vector2(1280f, 720f));
        frame.DeltaTime.ShouldBe(1f / 60f);
    }

    [Fact]
    public void A_resize_is_picked_up_on_the_next_frame()
    {
        var (_, renderer, source) = CreateSource();
        renderer.SetFramebufferSize(new Vector2D<int>(800, 600));
        source.CaptureFrame(0.016f).ViewportSize.ShouldBe(new Vector2(800f, 600f));

        renderer.SetFramebufferSize(new Vector2D<int>(1920, 1080));
        source.CaptureFrame(0.016f).ViewportSize.ShouldBe(new Vector2(1920f, 1080f));
    }

    [Fact]
    public void Cursor_position_carries_across_in_top_left_pixel_coordinates()
    {
        var (input, renderer, source) = CreateSource();
        renderer.SetFramebufferSize(new Vector2D<int>(640, 480));
        input.OnMouseMove(null!, new Vector2(120f, 340f));
        input.Update(0.016);

        EditorInputFrame frame = source.CaptureFrame(0.016f);

        frame.CursorPosition.ShouldBe(new Vector2(120f, 340f));
        frame.IsCursorInsideViewport.ShouldBeTrue();
    }

    [Fact]
    public void A_sub_rect_viewport_rebases_the_cursor_onto_its_own_origin()
    {
        var (input, _, source) = CreateSource();
        input.OnMouseMove(null!, new Vector2(300f, 200f));
        input.Update(0.016);

        // A docked editor viewport: 640x480 panel whose top-left sits at
        // (250, 150) in window client pixels.
        EditorInputFrame frame = source.CaptureFrame(
            0.016f, new Vector2(250f, 150f), new Vector2(640f, 480f));

        frame.CursorPosition.ShouldBe(new Vector2(50f, 50f));
        frame.ViewportSize.ShouldBe(new Vector2(640f, 480f));
        frame.IsCursorInsideViewport.ShouldBeTrue();
    }

    [Fact]
    public void A_cursor_outside_the_sub_rect_still_reports_its_offset()
    {
        var (input, _, source) = CreateSource();
        input.OnMouseMove(null!, new Vector2(10f, 20f));
        input.Update(0.016);

        EditorInputFrame frame = source.CaptureFrame(
            0.016f, new Vector2(250f, 150f), new Vector2(640f, 480f));

        // Negative, not clamped: a drag that runs off the panel must keep
        // tracking.
        frame.CursorPosition.ShouldBe(new Vector2(-240f, -130f));
        frame.IsCursorInsideViewport.ShouldBeFalse();
    }

    [Fact]
    public void Press_and_release_edges_cross_the_seam_on_the_frame_they_happen()
    {
        var (input, _, source) = CreateSource();

        input.OnMouseDown(null!, MouseButton.Left);
        input.Update(0.016);
        EditorInputFrame pressFrame = source.CaptureFrame(0.016f);
        pressFrame.WasPressed(PointerButtons.Left).ShouldBeTrue();
        pressFrame.IsDown(PointerButtons.Left).ShouldBeTrue();
        pressFrame.WasReleased(PointerButtons.Left).ShouldBeFalse();

        // Held, no edges.
        input.Update(0.016);
        EditorInputFrame heldFrame = source.CaptureFrame(0.016f);
        heldFrame.WasPressed(PointerButtons.Left).ShouldBeFalse();
        heldFrame.IsDown(PointerButtons.Left).ShouldBeTrue();

        input.OnMouseUp(null!, MouseButton.Left);
        input.Update(0.016);
        EditorInputFrame releaseFrame = source.CaptureFrame(0.016f);
        releaseFrame.WasReleased(PointerButtons.Left).ShouldBeTrue();
        releaseFrame.IsDown(PointerButtons.Left).ShouldBeFalse();

        // The edge is one frame wide.
        input.Update(0.016);
        source.CaptureFrame(0.016f).WasReleased(PointerButtons.Left).ShouldBeFalse();
    }

    [Fact]
    public void A_press_and_release_inside_one_frame_reports_both_edges()
    {
        var (input, _, source) = CreateSource();

        input.OnMouseDown(null!, MouseButton.Right);
        input.OnMouseUp(null!, MouseButton.Right);
        input.Update(0.016);

        EditorInputFrame frame = source.CaptureFrame(0.016f);
        frame.WasPressed(PointerButtons.Right).ShouldBeTrue();
        frame.WasReleased(PointerButtons.Right).ShouldBeTrue();
        frame.IsDown(PointerButtons.Right).ShouldBeFalse();
    }

    [Fact]
    public void Modifiers_collapse_left_and_right_keys_into_one_flag()
    {
        var (input, _, source) = CreateSource();
        input.OnKeyDown(null!, Key.ShiftRight, 0);
        input.OnKeyDown(null!, Key.ControlLeft, 0);
        input.Update(0.016);

        EditorInputFrame frame = source.CaptureFrame(0.016f);

        frame.Modifiers.ShouldBe(KeyModifiers.Shift | KeyModifiers.Control);
        frame.HasModifiers(KeyModifiers.Shift).ShouldBeTrue();
        frame.HasModifiers(KeyModifiers.Alt).ShouldBeFalse();

        input.OnKeyUp(null!, Key.ShiftRight, 0);
        input.Update(0.016);
        source.CaptureFrame(0.016f).Modifiers.ShouldBe(KeyModifiers.Control);
    }

    [Fact]
    public void Scroll_accumulates_within_a_frame_and_resets_on_the_next_latch()
    {
        var (input, _, source) = CreateSource();

        input.OnScroll(null!, new ScrollWheel(0f, 1f));
        input.OnScroll(null!, new ScrollWheel(0f, 2f));
        input.Update(0.016);
        source.CaptureFrame(0.016f).ScrollDelta.ShouldBe(new Vector2(0f, 3f));

        // Scroll is a delta, not a state: an idle frame reports zero.
        input.Update(0.016);
        source.CaptureFrame(0.016f).ScrollDelta.ShouldBe(Vector2.Zero);
    }

    [Fact]
    public void An_untouched_input_manager_produces_an_empty_frame()
    {
        var (_, _, source) = CreateSource();

        EditorInputFrame frame = source.CaptureFrame(0.016f);

        frame.ButtonsDown.ShouldBe(PointerButtons.None);
        frame.ButtonsPressed.ShouldBe(PointerButtons.None);
        frame.ButtonsReleased.ShouldBe(PointerButtons.None);
        frame.Modifiers.ShouldBe(KeyModifiers.None);
        frame.ScrollDelta.ShouldBe(Vector2.Zero);
        frame.CursorPosition.ShouldBe(Vector2.Zero);
    }

    private static (InputManager Input, StubRenderer Renderer, EngineEditorInputSource Source) CreateSource()
    {
        var input = new InputManager(NullLogger<InputManager>.Instance);
        var renderer = new StubRenderer();
        return (input, renderer, new EngineEditorInputSource(input, renderer));
    }
}
