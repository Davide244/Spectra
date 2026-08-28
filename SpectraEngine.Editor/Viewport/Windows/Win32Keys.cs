using SpectraEngine.Core.Input;

namespace SpectraEngine.Editor.Viewport.Windows;

/// <summary>
/// Virtual-key codes to the engine's <see cref="InputKey"/>.
/// </summary>
/// <remarks>
/// <b>The left and right modifiers are the only hard part, and they are not
/// optional.</b> Windows reports a bare <c>VK_SHIFT</c> / <c>VK_CONTROL</c> /
/// <c>VK_MENU</c> for both sides, while the engine binds the left ones
/// specifically (left Control descends the fly camera, left Shift boosts it).
/// Shift is separated by re-mapping its scancode; Control and Alt by the
/// extended-key bit, which is set for the right-hand one. Skip that and the
/// right-hand modifiers either do nothing or, worse, do the left one's job.
/// </remarks>
internal static class Win32Keys
{
    /// <summary>
    /// The engine's name for a virtual key, or <see cref="InputKey.Unknown"/>.
    /// </summary>
    /// <param name="virtualKey">The <c>wParam</c> of a key message.</param>
    /// <param name="lParam">
    /// The message's <c>lParam</c>, which carries the scancode in bits 16..23
    /// and the extended-key flag in bit 24.
    /// </param>
    internal static InputKey ToInputKey(int virtualKey, nint lParam)
    {
        uint scanCode = (uint)(((long)lParam >> 16) & 0xFF);
        bool extended = (((long)lParam >> 24) & 1) != 0;

        switch (virtualKey)
        {
            case Win32Interop.VK_SHIFT:
                // MapVirtualKey with the extended mapping is the documented way
                // to get back the side, because shift does not set the
                // extended-key bit the way control and alt do.
                uint side = Win32Interop.MapVirtualKey(scanCode, Win32Interop.MAPVK_VSC_TO_VK_EX);
                return side == Win32Interop.VK_RSHIFT ? InputKey.ShiftRight : InputKey.ShiftLeft;

            case Win32Interop.VK_CONTROL:
                return extended ? InputKey.ControlRight : InputKey.ControlLeft;

            case Win32Interop.VK_MENU:
                return extended ? InputKey.AltRight : InputKey.AltLeft;
        }

        // Letters and digits are contiguous ASCII in the virtual-key space, so
        // the whole of both rows is two range tests rather than thirty-six
        // cases that could each be mistyped.
        if (virtualKey is >= 'A' and <= 'Z')
            return InputKey.A + (virtualKey - 'A');
        if (virtualKey is >= '0' and <= '9')
            return InputKey.Number0 + (virtualKey - '0');

        return virtualKey switch
        {
            0x70 => InputKey.F1,
            0x71 => InputKey.F2,
            0x72 => InputKey.F3,
            0x73 => InputKey.F4,
            0x74 => InputKey.F5,
            0x75 => InputKey.F6,
            0x76 => InputKey.F7,
            0x77 => InputKey.F8,
            0x78 => InputKey.F9,
            0x79 => InputKey.F10,
            0x7A => InputKey.F11,
            0x7B => InputKey.F12,

            0x1B => InputKey.Escape,
            0x0D => InputKey.Enter,
            0x09 => InputKey.Tab,
            0x08 => InputKey.Backspace,
            0x2D => InputKey.Insert,
            0x2E => InputKey.Delete,
            0x20 => InputKey.Space,
            0x27 => InputKey.Right,
            0x25 => InputKey.Left,
            0x28 => InputKey.Down,
            0x26 => InputKey.Up,
            0x21 => InputKey.PageUp,
            0x22 => InputKey.PageDown,
            0x24 => InputKey.Home,
            0x23 => InputKey.End,

            0x5B => InputKey.SuperLeft,
            0x5C => InputKey.SuperRight,
            0x5D => InputKey.Menu,

            0x14 => InputKey.CapsLock,
            0x91 => InputKey.ScrollLock,
            0x90 => InputKey.NumLock,
            0x2C => InputKey.PrintScreen,
            0x13 => InputKey.Pause,

            // OEM keys, named for a US layout exactly as InputKey is.
            0xDE => InputKey.Apostrophe,
            0xBC => InputKey.Comma,
            0xBD => InputKey.Minus,
            0xBE => InputKey.Period,
            0xBF => InputKey.Slash,
            0xBA => InputKey.Semicolon,
            0xBB => InputKey.Equal,
            0xDB => InputKey.LeftBracket,
            0xDC => InputKey.BackSlash,
            0xDD => InputKey.RightBracket,
            0xC0 => InputKey.GraveAccent,

            _ => InputKey.Unknown,
        };
    }
}
