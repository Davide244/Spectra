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
/// <b>Everything here is a verb the ENGINE KEYMAP does not own.</b> A chord the
/// keymap already answers belongs to the keymap, where it works and where the
/// editor can decide whether a camera is currently driving; taking one away
/// from it would be a second path free to drift. The document verbs qualify
/// because a viewport has no opinion about files. So do the four inserts: they
/// are shell verbs (<c>SceneEditorHost.Insert</c> is called from the shell, and
/// the keymap has no chord for it), so without an interception here they would
/// work only while an Avalonia control had focus - which is to say, only while
/// the user was NOT looking at the thing they wanted to insert into. A shortcut
/// that works sometimes is worse than one that does not exist.
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

    /// <summary>Ctrl+1: a block, at the centre of the view.</summary>
    InsertBlock,

    /// <summary>Ctrl+2: a part.</summary>
    InsertPart,

    /// <summary>Ctrl+3: a cut.</summary>
    InsertCut,

    /// <summary>Ctrl+4: a light.</summary>
    InsertLight,
}
