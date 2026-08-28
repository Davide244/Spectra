namespace SpectraEngine.Core.Input;

/// <summary>
/// The engine's own keyboard vocabulary: physical keys, named by what is
/// printed on a US layout, independent of any windowing backend.
/// </summary>
/// <remarks>
/// <b>This exists because a host cannot be asked to speak Silk.NET.</b> The
/// engine's input state machine used to store <c>Silk.NET.Input.Key</c>, which
/// made that enum the vocabulary every consumer named — so an Avalonia shell
/// feeding the engine would have had to reference the windowing backend of a
/// window it does not own, purely to say "the user pressed W".
/// <para>
/// <b>Names match Silk.NET's spelling deliberately</b> (<c>Number2</c>,
/// <c>ShiftLeft</c>, <c>LeftBracket</c>), so the standalone path's translation
/// table is a one-to-one mapping that a test can verify by name rather than a
/// hand-checked list of a hundred pairs. Where a name is a judgement call, the
/// tie goes to the existing spelling: the point of this enum is to move the
/// vocabulary, not to rename it.
/// </para>
/// <para>
/// <b>Physical keys, not characters.</b> <see cref="Number2"/> is the key above
/// W on a US layout whatever it types on an AZERTY one, which is what a
/// shortcut wants; text entry is a separate problem and is not this enum's job.
/// </para>
/// <para>
/// The set is deliberately not exhaustive. It covers what an editor and a game
/// bind — the letter row, the digits, the function row, the arrows, the
/// modifiers and the common punctuation — and grows when something needs a key
/// that is missing, rather than mirroring every scancode a keyboard can emit.
/// </para>
/// </remarks>
public enum InputKey
{
    /// <summary>A key the source could not name. Never matches a binding.</summary>
    Unknown = 0,

    // ─── Letters ─────────────────────────────────────────────
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    // ─── Digits (the number row, not the keypad) ─────────────
    Number0, Number1, Number2, Number3, Number4,
    Number5, Number6, Number7, Number8, Number9,

    // ─── Function row ────────────────────────────────────────
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    // ─── Navigation and editing ──────────────────────────────
    Escape,
    Enter,
    Tab,
    Backspace,
    Insert,
    Delete,
    Space,
    Right,
    Left,
    Down,
    Up,
    PageUp,
    PageDown,
    Home,
    End,

    // ─── Modifiers ───────────────────────────────────────────
    // Left and right stay distinct: the engine collapses them into
    // KeyModifiers where a chord is what matters, but a binding that wants the
    // left Control specifically (the fly camera's descend) must be able to say
    // so.
    ShiftLeft, ShiftRight,
    ControlLeft, ControlRight,
    AltLeft, AltRight,
    SuperLeft, SuperRight,

    // ─── Punctuation ─────────────────────────────────────────
    Apostrophe,
    Comma,
    Minus,
    Period,
    Slash,
    Semicolon,
    Equal,
    LeftBracket,
    BackSlash,
    RightBracket,
    GraveAccent,

    // ─── Locks and system ────────────────────────────────────
    CapsLock,
    ScrollLock,
    NumLock,
    PrintScreen,
    Pause,
    Menu,
}
