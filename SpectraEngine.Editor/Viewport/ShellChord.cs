namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// A keyboard chord that belongs to the shell rather than to the engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the viewport is a native child window.</b> While it
/// has focus the OS delivers the keyboard to it and nothing reaches Avalonia,
/// so a menu accelerator is inert exactly while somebody is working. The
/// viewport therefore intercepts a short, closed list of Ctrl chords and hands
/// them up instead of submitting them to the engine.
/// </para>
/// <para>
/// <b>Everything here is a DOCUMENT verb, and that is the boundary.</b> A chord
/// that means something to the scene belongs to the engine's own keymap, where
/// it already works and where the editor can decide whether a camera is
/// currently driving. Only the verbs with no meaning inside a viewport are
/// taken away from it.
/// </para>
/// </remarks>
public enum ShellChord
{
    /// <summary>Ctrl+N.</summary>
    NewMap,

    /// <summary>Ctrl+O.</summary>
    OpenMap,

    /// <summary>Ctrl+S.</summary>
    SaveMap,

    /// <summary>Ctrl+Shift+S.</summary>
    SaveMapAs,
}
