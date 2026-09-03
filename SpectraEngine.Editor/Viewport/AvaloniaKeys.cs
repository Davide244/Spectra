using Avalonia.Input;
using SpectraEngine.Core.Input;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;
using EngineKeyModifiers = SpectraEngine.Core.Input.KeyModifiers;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// Avalonia's input vocabulary translated into the engine's.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third hand-written table of this kind, and the third for the same
/// reason.</b> <c>SilkInputKeys</c> serves the standalone window and
/// <c>Win32Keys</c> the native child; a composited viewport is an ordinary
/// Avalonia control, so the keyboard arrives as <see cref="Key"/> and somebody
/// has to say what each one means. <b>A transposition in a table like this is
/// invisible</b> - the wrong key simply stops working, or quietly fires a
/// different tool - so the letter, digit and function rows are range arithmetic
/// rather than sixty cases that could each be mistyped one at a time, and the
/// rest is checked by NAME in a test rather than by eye.
/// </para>
/// <para>
/// <b>Physical keys, and the engine's names are a US layout's.</b> Avalonia
/// reports the same physical-key model, which is what makes the mapping a
/// rename rather than a reinterpretation - <see cref="Key.D2"/> is the key
/// above W whatever it prints, exactly as <see cref="InputKey.Number2"/> is.
/// </para>
/// <para>
/// <b>Anything the engine does not name becomes
/// <see cref="InputKey.Unknown"/>, never something nearby.</b> A keypad 7
/// mapped onto the number row would fire whatever tool 7 binds, from a key the
/// user pressed for a completely different reason.
/// </para>
/// </remarks>
internal static class AvaloniaKeys
{
    /// <summary>The engine's name for an Avalonia key, or <see cref="InputKey.Unknown"/>.</summary>
    internal static InputKey ToInputKey(Key key)
    {
        // Contiguous in both enums, so the whole of each row is one range test
        // rather than a case per member. The test asserts the arithmetic lands
        // where both enums' own orderings say it should.
        if (key is >= Key.A and <= Key.Z)
            return InputKey.A + (key - Key.A);
        if (key is >= Key.D0 and <= Key.D9)
            return InputKey.Number0 + (key - Key.D0);
        if (key is >= Key.F1 and <= Key.F12)
            return InputKey.F1 + (key - Key.F1);

        return key switch
        {
            // Navigation and editing. Avalonia spells several of these the way
            // Win32 does rather than the way the engine does, which is the
            // whole content of this table.
            Key.Escape => InputKey.Escape,
            Key.Return => InputKey.Enter,
            Key.Tab => InputKey.Tab,
            Key.Back => InputKey.Backspace,
            Key.Insert => InputKey.Insert,
            Key.Delete => InputKey.Delete,
            Key.Space => InputKey.Space,
            Key.Right => InputKey.Right,
            Key.Left => InputKey.Left,
            Key.Down => InputKey.Down,
            Key.Up => InputKey.Up,
            Key.PageUp => InputKey.PageUp,
            Key.PageDown => InputKey.PageDown,
            Key.Home => InputKey.Home,
            Key.End => InputKey.End,

            // Modifiers. The two sides stay distinct because the engine binds
            // the left ones specifically - left Control descends the fly camera
            // and left Shift boosts it - and Avalonia reports the side, so
            // there is nothing to reconstruct here the way Win32 needs a
            // scancode remap.
            Key.LeftShift => InputKey.ShiftLeft,
            Key.RightShift => InputKey.ShiftRight,
            Key.LeftCtrl => InputKey.ControlLeft,
            Key.RightCtrl => InputKey.ControlRight,
            Key.LeftAlt => InputKey.AltLeft,
            Key.RightAlt => InputKey.AltRight,
            Key.LWin => InputKey.SuperLeft,
            Key.RWin => InputKey.SuperRight,
            Key.Apps => InputKey.Menu,

            // Punctuation. Named for a US layout on both sides, and the OEM
            // spellings are the easiest entries in the whole table to get
            // wrong: the snap ladder lives on [ and ], which are OemOpenBrackets
            // and OemCloseBrackets rather than anything resembling a bracket.
            Key.OemQuotes => InputKey.Apostrophe,
            Key.OemComma => InputKey.Comma,
            Key.OemMinus => InputKey.Minus,
            Key.OemPeriod => InputKey.Period,
            Key.OemQuestion => InputKey.Slash,
            Key.OemSemicolon => InputKey.Semicolon,
            Key.OemPlus => InputKey.Equal,
            Key.OemOpenBrackets => InputKey.LeftBracket,
            Key.OemPipe => InputKey.BackSlash,
            Key.OemCloseBrackets => InputKey.RightBracket,
            Key.OemTilde => InputKey.GraveAccent,

            // Locks and system.
            Key.CapsLock => InputKey.CapsLock,
            Key.Scroll => InputKey.ScrollLock,
            Key.NumLock => InputKey.NumLock,
            Key.PrintScreen => InputKey.PrintScreen,
            Key.Pause => InputKey.Pause,

            _ => InputKey.Unknown,
        };
    }

    /// <summary>The engine's modifier set for Avalonia's.</summary>
    /// <remarks>
    /// <b>Avalonia's <c>Meta</c> is Super</b>, which is the same
    /// collapse the engine's own enum documents: left and right fold together
    /// because a binding cares that the key is held, not which one.
    /// </remarks>
    internal static EngineKeyModifiers ToModifiers(AvaloniaKeyModifiers modifiers)
    {
        EngineKeyModifiers result = EngineKeyModifiers.None;

        if ((modifiers & AvaloniaKeyModifiers.Shift) != 0)
            result |= EngineKeyModifiers.Shift;
        if ((modifiers & AvaloniaKeyModifiers.Control) != 0)
            result |= EngineKeyModifiers.Control;
        if ((modifiers & AvaloniaKeyModifiers.Alt) != 0)
            result |= EngineKeyModifiers.Alt;
        if ((modifiers & AvaloniaKeyModifiers.Meta) != 0)
            result |= EngineKeyModifiers.Super;

        return result;
    }

    /// <summary>
    /// Which button a pointer update is about, or
    /// <see cref="PointerButtons.None"/> for one the engine does not bind.
    /// </summary>
    /// <remarks>
    /// <b>Avalonia reports a press and a release as separate KINDS of the same
    /// update</b>, so the button and the direction arrive together; the caller
    /// already knows which of the two it is handling and only needs the button.
    /// The extra buttons map to nothing rather than to something nearby, for
    /// the same reason a keypad digit does.
    /// </remarks>
    internal static PointerButtons ToPointerButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased =>
            PointerButtons.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased =>
            PointerButtons.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased =>
            PointerButtons.Middle,
        _ => PointerButtons.None,
    };

    /// <summary>
    /// The nearest standard cursor to a shape the engine asked for.
    /// </summary>
    /// <remarks>
    /// <b>Every degradation lives here, exactly as the Win32 window's does.</b>
    /// There is no rotate cursor in the standard set and the nearest thing to a
    /// grab is the hand, so a tool asking for either gets the closest available
    /// shape and never learns that it did - which is the whole reason the
    /// vocabulary is the engine's rather than a platform's.
    /// </remarks>
    internal static StandardCursorType ToStandardCursor(CursorShape shape) => shape switch
    {
        CursorShape.Crosshair => StandardCursorType.Cross,
        CursorShape.Grab => StandardCursorType.Hand,
        CursorShape.Grabbing => StandardCursorType.Hand,
        CursorShape.SizeWestEast => StandardCursorType.SizeWestEast,
        CursorShape.SizeNorthSouth => StandardCursorType.SizeNorthSouth,
        CursorShape.SizeNorthWestSouthEast => StandardCursorType.TopLeftCorner,
        CursorShape.SizeNorthEastSouthWest => StandardCursorType.TopRightCorner,
        CursorShape.SizeAll or CursorShape.Rotate => StandardCursorType.SizeAll,
        CursorShape.No => StandardCursorType.No,
        _ => StandardCursorType.Arrow,
    };
}
