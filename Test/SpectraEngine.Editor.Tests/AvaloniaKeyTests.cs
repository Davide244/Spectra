using Avalonia.Input;
using SpectraEngine.Core.Input;
using SpectraEngine.Editor.Viewport;
using System.Linq;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;
using EngineKeyModifiers = SpectraEngine.Core.Input.KeyModifiers;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// Avalonia's keys to the engine's vocabulary: the composited viewport's whole
/// keyboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Checked by NAME, because a wrong entry is otherwise invisible.</b> The
/// engine's own enum spells its members the way Silk.NET does, so the standalone
/// window's table can be verified with <c>Enum.TryParse</c> and nothing else.
/// Avalonia spells a third of them differently - <c>Back</c>, <c>Return</c>,
/// <c>D0</c>, <c>LeftCtrl</c>, <c>OemOpenBrackets</c> - so the names cannot be
/// an oracle on their own, and the renamed pairs are written out here as
/// STRINGS instead: a second, independent transcription that has to agree with
/// the first. A transposition survives one list and not two.
/// </para>
/// <para>
/// <b>And the table must be onto.</b> A pair that agrees perfectly but was
/// never written down at all is a key that silently does nothing, so the last
/// test walks the engine's enum rather than Avalonia's and fails on anything the
/// composited viewport could not produce.
/// </para>
/// </remarks>
public sealed class AvaloniaKeyTests
{
    /// <summary>
    /// Every key whose two enums disagree about the spelling, as text.
    /// </summary>
    /// <remarks>
    /// Resolved through <c>Enum.Parse</c> on both sides rather than written as
    /// enum members, so this really is a second transcription and not the same
    /// one twice: a name that exists on neither side fails here rather than
    /// compiling into agreement.
    /// </remarks>
    private static readonly (string Avalonia, string Engine)[] Renamed =
        [
            // The digit row is the only range the two enums disagree about, and
            // it is worth writing out one at a time rather than trusting the
            // arithmetic twice: Ctrl+1 to Ctrl+4 are the insert verbs, so an
            // off-by-one here inserts the wrong thing rather than nothing.
            ("D0", "Number0"),
            ("D1", "Number1"),
            ("D2", "Number2"),
            ("D3", "Number3"),
            ("D4", "Number4"),
            ("D5", "Number5"),
            ("D6", "Number6"),
            ("D7", "Number7"),
            ("D8", "Number8"),
            ("D9", "Number9"),

            ("Return", "Enter"),
            ("Back", "Backspace"),
            ("Scroll", "ScrollLock"),
            ("LWin", "SuperLeft"),
            ("RWin", "SuperRight"),
            ("Apps", "Menu"),
            ("LeftShift", "ShiftLeft"),
            ("RightShift", "ShiftRight"),
            ("LeftCtrl", "ControlLeft"),
            ("RightCtrl", "ControlRight"),
            ("LeftAlt", "AltLeft"),
            ("RightAlt", "AltRight"),
            ("OemQuotes", "Apostrophe"),
            ("OemComma", "Comma"),
            ("OemMinus", "Minus"),
            ("OemPeriod", "Period"),
            ("OemQuestion", "Slash"),
            ("OemSemicolon", "Semicolon"),
            ("OemPlus", "Equal"),
            ("OemOpenBrackets", "LeftBracket"),
            ("OemPipe", "BackSlash"),
            ("OemCloseBrackets", "RightBracket"),
            ("OemTilde", "GraveAccent"),
        ];

    /// <summary>The same list, as theory rows.</summary>
    public static TheoryData<string, string> RenamedKeys
    {
        get
        {
            var rows = new TheoryData<string, string>();
            foreach ((string avalonia, string engine) in Renamed)
                rows.Add(avalonia, engine);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(RenamedKeys))]
    public void A_renamed_key_maps_to_the_engine_name_this_test_says_it_should(
        string avaloniaName, string engineName)
    {
        Enum.TryParse(avaloniaName, out Key key).ShouldBeTrue(
            $"Avalonia has no key named {avaloniaName}");
        Enum.TryParse(engineName, out InputKey expected).ShouldBeTrue(
            $"the engine has no key named {engineName}");

        AvaloniaKeys.ToInputKey(key).ShouldBe(expected);
    }

    [Fact]
    public void A_key_both_enums_spell_the_same_way_keeps_its_name()
    {
        // Everything the renamed table does not cover, checked the way the
        // standalone window's table is: if both enums say "Escape", the mapping
        // has exactly one correct answer and no judgement is involved.
        foreach (InputKey engineKey in Enum.GetValues<InputKey>())
        {
            if (engineKey is InputKey.Unknown ||
                Renamed.Any(row => row.Engine == engineKey.ToString()))
            {
                continue;
            }

            Enum.TryParse(engineKey.ToString(), out Key key).ShouldBeTrue(
                $"InputKey.{engineKey} has no Avalonia key of the same name, so it belongs in the " +
                "renamed table rather than being left out of both");
            AvaloniaKeys.ToInputKey(key).ShouldBe(engineKey);
        }
    }

    [Fact]
    public void Every_key_the_engine_names_can_actually_be_produced()
    {
        // The direction that catches an omission rather than a transposition. A
        // key nobody wrote down maps to Unknown, which never matches a binding:
        // the shortcut simply stops working and nothing logs.
        var reachable = new HashSet<InputKey>();
        foreach (Key key in Enum.GetValues<Key>())
        {
            InputKey mapped = AvaloniaKeys.ToInputKey(key);
            if (mapped is not InputKey.Unknown)
                reachable.Add(mapped);
        }

        foreach (InputKey engineKey in Enum.GetValues<InputKey>())
        {
            if (engineKey is InputKey.Unknown)
                continue;

            reachable.ShouldContain(engineKey, $"no Avalonia key maps to InputKey.{engineKey}");
        }
    }

    [Theory]
    [InlineData(Key.A, InputKey.A)]
    [InlineData(Key.W, InputKey.W)]
    [InlineData(Key.Z, InputKey.Z)]
    public void The_letter_row_maps_by_range(Key key, InputKey expected) =>
        AvaloniaKeys.ToInputKey(key).ShouldBe(expected);

    [Theory]
    [InlineData(Key.D0, InputKey.Number0)]
    [InlineData(Key.D3, InputKey.Number3)]
    [InlineData(Key.D9, InputKey.Number9)]
    public void The_digit_row_maps_by_range(Key key, InputKey expected) =>
        // The number row, never the keypad: Ctrl+1 through Ctrl+4 insert, and a
        // keypad digit firing them would be a shortcut from a key nobody meant.
        AvaloniaKeys.ToInputKey(key).ShouldBe(expected);

    [Theory]
    [InlineData(Key.F1, InputKey.F1)]
    [InlineData(Key.F8, InputKey.F8)]
    [InlineData(Key.F12, InputKey.F12)]
    public void The_function_row_maps_by_range(Key key, InputKey expected) =>
        AvaloniaKeys.ToInputKey(key).ShouldBe(expected);

    [Fact]
    public void A_key_the_engine_does_not_name_is_unknown_rather_than_nearby()
    {
        AvaloniaKeys.ToInputKey(Key.NumPad7).ShouldBe(InputKey.Unknown);
        AvaloniaKeys.ToInputKey(Key.F20).ShouldBe(InputKey.Unknown);
        AvaloniaKeys.ToInputKey(Key.None).ShouldBe(InputKey.Unknown);
    }

    [Fact]
    public void Modifiers_translate_including_the_super_rename()
    {
        AvaloniaKeys.ToModifiers(AvaloniaKeyModifiers.None).ShouldBe(EngineKeyModifiers.None);
        AvaloniaKeys.ToModifiers(AvaloniaKeyModifiers.Shift).ShouldBe(EngineKeyModifiers.Shift);
        AvaloniaKeys.ToModifiers(AvaloniaKeyModifiers.Control).ShouldBe(EngineKeyModifiers.Control);
        AvaloniaKeys.ToModifiers(AvaloniaKeyModifiers.Alt).ShouldBe(EngineKeyModifiers.Alt);
        AvaloniaKeys.ToModifiers(AvaloniaKeyModifiers.Meta).ShouldBe(EngineKeyModifiers.Super);

        AvaloniaKeys.ToModifiers(AvaloniaKeyModifiers.Control | AvaloniaKeyModifiers.Shift)
            .ShouldBe(EngineKeyModifiers.Control | EngineKeyModifiers.Shift);
    }

    [Fact]
    public void The_three_mouse_buttons_map_and_the_rest_do_not()
    {
        AvaloniaKeys.ToPointerButton(PointerUpdateKind.LeftButtonPressed)
            .ShouldBe(PointerButtons.Left);
        AvaloniaKeys.ToPointerButton(PointerUpdateKind.LeftButtonReleased)
            .ShouldBe(PointerButtons.Left);
        AvaloniaKeys.ToPointerButton(PointerUpdateKind.RightButtonPressed)
            .ShouldBe(PointerButtons.Right);
        AvaloniaKeys.ToPointerButton(PointerUpdateKind.MiddleButtonPressed)
            .ShouldBe(PointerButtons.Middle);

        // A move is not a button, and neither is the back button on a mouse the
        // engine has no binding for.
        AvaloniaKeys.ToPointerButton(PointerUpdateKind.Other).ShouldBe(PointerButtons.None);
        AvaloniaKeys.ToPointerButton(PointerUpdateKind.XButton1Pressed).ShouldBe(PointerButtons.None);
    }

    [Fact]
    public void Cursor_shapes_degrade_to_the_nearest_thing_the_platform_has()
    {
        AvaloniaKeys.ToStandardCursor(CursorShape.Arrow).ShouldBe(StandardCursorType.Arrow);
        AvaloniaKeys.ToStandardCursor(CursorShape.Crosshair).ShouldBe(StandardCursorType.Cross);

        // The two degradations, stated rather than discovered: there is no
        // rotate cursor in the standard set and the nearest thing to a grab is
        // the hand. A tool asks for a meaning and never learns what it got.
        AvaloniaKeys.ToStandardCursor(CursorShape.Grab).ShouldBe(StandardCursorType.Hand);
        AvaloniaKeys.ToStandardCursor(CursorShape.Rotate).ShouldBe(StandardCursorType.SizeAll);
    }
}
