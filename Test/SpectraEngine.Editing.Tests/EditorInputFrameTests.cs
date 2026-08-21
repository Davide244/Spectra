using SpectraEngine.Core.Input;
using SpectraEngine.Editing.Input;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// <see cref="EditorInputFrame"/> query semantics: multi-flag tests mean "all
/// of these", an empty query is never a match for buttons but always one for
/// modifiers, and viewport containment is left/top inclusive, right/bottom
/// exclusive.
/// </summary>
public sealed class EditorInputFrameTests
{
    [Fact]
    public void Button_queries_report_state_and_both_edges()
    {
        var frame = Build(
            down: PointerButtons.Left | PointerButtons.Middle,
            pressed: PointerButtons.Left,
            released: PointerButtons.Right);

        frame.IsDown(PointerButtons.Left).ShouldBeTrue();
        frame.IsDown(PointerButtons.Middle).ShouldBeTrue();
        frame.IsDown(PointerButtons.Right).ShouldBeFalse();

        frame.WasPressed(PointerButtons.Left).ShouldBeTrue();
        frame.WasPressed(PointerButtons.Middle).ShouldBeFalse();
        frame.WasReleased(PointerButtons.Right).ShouldBeTrue();
    }

    [Fact]
    public void A_multi_button_query_requires_all_of_them()
    {
        var frame = Build(down: PointerButtons.Left | PointerButtons.Middle);

        frame.IsDown(PointerButtons.Left | PointerButtons.Middle).ShouldBeTrue();
        frame.IsDown(PointerButtons.Left | PointerButtons.Right).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_button_query_never_matches()
    {
        var frame = Build(down: PointerButtons.Left);

        // Guards `if (frame.WasPressed(binding))` against an unassigned binding
        // reading as "always pressed".
        frame.IsDown(PointerButtons.None).ShouldBeFalse();
        frame.WasPressed(PointerButtons.None).ShouldBeFalse();
        frame.WasReleased(PointerButtons.None).ShouldBeFalse();
    }

    [Fact]
    public void Modifier_queries_allow_extra_modifiers_but_require_the_asked_for_ones()
    {
        var frame = Build(modifiers: KeyModifiers.Shift | KeyModifiers.Control);

        frame.HasModifiers(KeyModifiers.Shift).ShouldBeTrue();
        frame.HasModifiers(KeyModifiers.Shift | KeyModifiers.Control).ShouldBeTrue();
        frame.HasModifiers(KeyModifiers.Alt).ShouldBeFalse();

        // "Any modifiers" is always satisfied; test the exact value for "none".
        frame.HasModifiers(KeyModifiers.None).ShouldBeTrue();
        (frame.Modifiers == KeyModifiers.None).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0f, 0f, true)]
    [InlineData(639f, 479f, true)]
    [InlineData(640f, 240f, false)]
    [InlineData(320f, 480f, false)]
    [InlineData(-1f, 240f, false)]
    [InlineData(320f, -0.5f, false)]
    public void Cursor_containment_is_top_left_inclusive_and_bottom_right_exclusive(float x, float y, bool inside)
    {
        var frame = new EditorInputFrame(
            new Vector2(x, y),
            new Vector2(640f, 480f),
            PointerButtons.None,
            PointerButtons.None,
            PointerButtons.None,
            KeyModifiers.None,
            Vector2.Zero,
            1f / 60f);

        frame.IsCursorInsideViewport.ShouldBe(inside);
    }

    [Fact]
    public void A_zero_sized_viewport_never_contains_the_cursor()
    {
        var frame = new EditorInputFrame(
            Vector2.Zero,
            Vector2.Zero,
            PointerButtons.None,
            PointerButtons.None,
            PointerButtons.None,
            KeyModifiers.None,
            Vector2.Zero,
            1f / 60f);

        // A panel that has not been laid out yet must not claim a hover.
        frame.IsCursorInsideViewport.ShouldBeFalse();
    }

    [Fact]
    public void The_snapshot_keeps_every_value_it_was_built_with()
    {
        var frame = new EditorInputFrame(
            new Vector2(12f, 34f),
            new Vector2(800f, 600f),
            PointerButtons.Right,
            PointerButtons.Right,
            PointerButtons.Left,
            KeyModifiers.Alt,
            new Vector2(0f, -2f),
            0.016f);

        frame.CursorPosition.ShouldBe(new Vector2(12f, 34f));
        frame.ViewportSize.ShouldBe(new Vector2(800f, 600f));
        frame.ButtonsDown.ShouldBe(PointerButtons.Right);
        frame.ButtonsPressed.ShouldBe(PointerButtons.Right);
        frame.ButtonsReleased.ShouldBe(PointerButtons.Left);
        frame.Modifiers.ShouldBe(KeyModifiers.Alt);
        frame.ScrollDelta.ShouldBe(new Vector2(0f, -2f));
        frame.DeltaTime.ShouldBe(0.016f);
    }

    private static EditorInputFrame Build(
        PointerButtons down = PointerButtons.None,
        PointerButtons pressed = PointerButtons.None,
        PointerButtons released = PointerButtons.None,
        KeyModifiers modifiers = KeyModifiers.None) =>
        new(
            new Vector2(100f, 50f),
            new Vector2(640f, 480f),
            down,
            pressed,
            released,
            modifiers,
            Vector2.Zero,
            1f / 60f);
}
