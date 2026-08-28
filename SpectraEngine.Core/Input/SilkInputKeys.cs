using Silk.NET.Input;

namespace SpectraEngine.Core.Input;

/// <summary>
/// Translates the standalone window's Silk.NET input vocabulary into the
/// engine's own. The one place in the engine where the two meet.
/// </summary>
/// <remarks>
/// <b>This is an adapter, not a seam.</b> The standalone path owns a Silk
/// window and therefore receives Silk devices; that is a fact about how the
/// engine creates its own window, and it stops here. An embedded host submits
/// <see cref="InputKey"/> values directly and never reaches this file.
/// <para>
/// <b>Names match on both sides deliberately</b>, so the table below is a
/// mechanical one-to-one and a test can check every pair by name instead of a
/// human reading a hundred lines for a transposition. A Silk key with no
/// counterpart maps to <see cref="InputKey.Unknown"/>, which matches no binding
/// — dropping a key the engine has no name for is correct, and it is why
/// <see cref="InputKey.Unknown"/> exists at all.
/// </para>
/// </remarks>
internal static class SilkInputKeys
{
    /// <summary>The engine's name for a Silk key, or <see cref="InputKey.Unknown"/>.</summary>
    internal static InputKey ToInputKey(Key key) => key switch
    {
        Key.A => InputKey.A,
        Key.B => InputKey.B,
        Key.C => InputKey.C,
        Key.D => InputKey.D,
        Key.E => InputKey.E,
        Key.F => InputKey.F,
        Key.G => InputKey.G,
        Key.H => InputKey.H,
        Key.I => InputKey.I,
        Key.J => InputKey.J,
        Key.K => InputKey.K,
        Key.L => InputKey.L,
        Key.M => InputKey.M,
        Key.N => InputKey.N,
        Key.O => InputKey.O,
        Key.P => InputKey.P,
        Key.Q => InputKey.Q,
        Key.R => InputKey.R,
        Key.S => InputKey.S,
        Key.T => InputKey.T,
        Key.U => InputKey.U,
        Key.V => InputKey.V,
        Key.W => InputKey.W,
        Key.X => InputKey.X,
        Key.Y => InputKey.Y,
        Key.Z => InputKey.Z,

        Key.Number0 => InputKey.Number0,
        Key.Number1 => InputKey.Number1,
        Key.Number2 => InputKey.Number2,
        Key.Number3 => InputKey.Number3,
        Key.Number4 => InputKey.Number4,
        Key.Number5 => InputKey.Number5,
        Key.Number6 => InputKey.Number6,
        Key.Number7 => InputKey.Number7,
        Key.Number8 => InputKey.Number8,
        Key.Number9 => InputKey.Number9,

        Key.F1 => InputKey.F1,
        Key.F2 => InputKey.F2,
        Key.F3 => InputKey.F3,
        Key.F4 => InputKey.F4,
        Key.F5 => InputKey.F5,
        Key.F6 => InputKey.F6,
        Key.F7 => InputKey.F7,
        Key.F8 => InputKey.F8,
        Key.F9 => InputKey.F9,
        Key.F10 => InputKey.F10,
        Key.F11 => InputKey.F11,
        Key.F12 => InputKey.F12,

        Key.Escape => InputKey.Escape,
        Key.Enter => InputKey.Enter,
        Key.Tab => InputKey.Tab,
        Key.Backspace => InputKey.Backspace,
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

        Key.ShiftLeft => InputKey.ShiftLeft,
        Key.ShiftRight => InputKey.ShiftRight,
        Key.ControlLeft => InputKey.ControlLeft,
        Key.ControlRight => InputKey.ControlRight,
        Key.AltLeft => InputKey.AltLeft,
        Key.AltRight => InputKey.AltRight,
        Key.SuperLeft => InputKey.SuperLeft,
        Key.SuperRight => InputKey.SuperRight,

        Key.Apostrophe => InputKey.Apostrophe,
        Key.Comma => InputKey.Comma,
        Key.Minus => InputKey.Minus,
        Key.Period => InputKey.Period,
        Key.Slash => InputKey.Slash,
        Key.Semicolon => InputKey.Semicolon,
        Key.Equal => InputKey.Equal,
        Key.LeftBracket => InputKey.LeftBracket,
        Key.BackSlash => InputKey.BackSlash,
        Key.RightBracket => InputKey.RightBracket,
        Key.GraveAccent => InputKey.GraveAccent,

        Key.CapsLock => InputKey.CapsLock,
        Key.ScrollLock => InputKey.ScrollLock,
        Key.NumLock => InputKey.NumLock,
        Key.PrintScreen => InputKey.PrintScreen,
        Key.Pause => InputKey.Pause,
        Key.Menu => InputKey.Menu,

        _ => InputKey.Unknown,
    };

    /// <summary>
    /// The engine's name for a Silk mouse button, or
    /// <see cref="PointerButtons.None"/> for the extra buttons the engine binds
    /// nothing to.
    /// </summary>
    internal static PointerButtons ToPointerButton(MouseButton button) => button switch
    {
        MouseButton.Left => PointerButtons.Left,
        MouseButton.Right => PointerButtons.Right,
        MouseButton.Middle => PointerButtons.Middle,
        _ => PointerButtons.None,
    };
}
