namespace SpectraEngine.Core.Windowing;

/// <summary>
/// The window a <see cref="WindowModeLatch"/> drives, reduced to the four
/// things a borderless-fullscreen transition actually needs. <b>Every member is
/// window-thread affine</b> — the latch only ever touches this from the thread
/// that owns the window.
/// </summary>
/// <remarks>
/// This exists so the transition itself is a pure state machine over a tiny
/// surface: the engine supplies <c>SilkWindowModeTarget</c>, a headless test
/// supplies a fake, and neither the latch nor its tests name a windowing
/// backend.
/// </remarks>
public interface IWindowModeTarget
{
    /// <summary>Position and size of the window in virtual-screen pixels.</summary>
    WindowRect Bounds { get; set; }

    /// <summary>
    /// Whether the window wears the OS title bar and frame. Clearing it is what
    /// makes borderless fullscreen borderless.
    /// </summary>
    bool Decorated { get; set; }

    /// <summary>
    /// Whether the window is maximized. Going fullscreen clears it first,
    /// because a maximized window's <see cref="Bounds"/> are the restore
    /// geometry rather than what is on screen, and restoring it afterwards is
    /// what puts the user back exactly where they were.
    /// </summary>
    bool IsMaximized { get; set; }

    /// <summary>
    /// The bounds of the display this window currently sits on. Returns
    /// <c>false</c> when the backend cannot name one (headless, or a monitor
    /// that vanished mid-call), in which case the fullscreen request is
    /// refused rather than guessed at.
    /// </summary>
    bool TryGetDisplayBounds(out WindowRect bounds);
}
