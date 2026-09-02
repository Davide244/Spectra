namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// A point in viewport pixels, or in screen pixels: whichever the member using
/// it names. Integer, deliberately.
/// </summary>
/// <remarks>
/// The anchor differencing that drives a freelook compares two positions for
/// exact equality, because the re-pin the lock performs generates a move of its
/// own and a zero delta is how that echo is recognised. A float point would make
/// that comparison a question about rounding.
/// </remarks>
internal readonly record struct ViewportPoint(int X, int Y);

/// <summary>The viewport's client area, in pixels.</summary>
internal readonly record struct ViewportSize(int Width, int Height);

/// <summary>
/// The platform half of the viewport's input path: the handful of things the
/// arbitration in <see cref="ViewportInputRouter"/> cannot do for itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so that the router can be tested, and so that the next host
/// can reuse it.</b> Everything about what a press MEANS (a click or the start
/// of a look, a document chord or a key for the engine, how far a pointer may
/// wander and still be a click) is policy and lives in the router. Everything
/// that requires an actual window (where it is on screen, moving the OS cursor,
/// fencing it, holding the capture) is here, and is the only part a new host has
/// to write.
/// </para>
/// <para>
/// <b>Every member is window-thread work</b>, which is what makes the split
/// worth drawing at all: the engine asks for a cursor mode from its render
/// thread and the shell applies it here, once per pass of its own pump.
/// </para>
/// </remarks>
internal interface IViewportCursor
{
    /// <summary>The client area's size, which is also the framebuffer's.</summary>
    ViewportSize ClientSize { get; }

    /// <summary>
    /// How far a press may travel and still count as a click, in this window's
    /// own DPI.
    /// </summary>
    /// <remarks>
    /// <b>Per-window DPI, so the slack is the same physical distance on a 200%
    /// display as on a 100% one.</b> The router reads it once per press rather
    /// than caching it, because a window can be dragged between monitors of
    /// different scaling between one press and the next.
    /// </remarks>
    int DragSlack { get; }

    /// <summary>Turns a client-space point into a screen-space one.</summary>
    ViewportPoint ClientToScreen(ViewportPoint client);

    /// <summary>Puts the OS cursor at a screen-space point.</summary>
    void MoveCursor(int screenX, int screenY);

    /// <summary>
    /// Fences the cursor inside the client area, or gives it back the screen.
    /// </summary>
    /// <remarks>
    /// The whole client rect rather than a band around the anchor, deliberately:
    /// a tight fence SHRINKS the headroom the anchor differencing survives a UI
    /// stall with, which is the opposite of hardening.
    /// </remarks>
    void ClipToClient(bool clip);

    /// <summary>Hides the pointer for the duration of a look, or shows it again.</summary>
    void SetCursorHidden(bool hidden);

    /// <summary>
    /// Takes or gives back the pointer capture that keeps a drag leaving the
    /// viewport still ending when the user lets go.
    /// </summary>
    void SetPointerCapture(bool captured);
}
