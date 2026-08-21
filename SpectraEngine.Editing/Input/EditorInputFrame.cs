using SpectraEngine.Core.Input;
using System.Numerics;

namespace SpectraEngine.Editing.Input;

/// <summary>
/// An immutable snapshot of everything the editing layer is allowed to know
/// about input for one frame: where the cursor is, how big the viewport is,
/// which mouse buttons are held and which changed this frame, which modifiers
/// are down, how far the wheel turned, and how long the frame was.
/// </summary>
/// <remarks>
/// <b>This type is the seam.</b> Editor tools — gizmos, selection, drag
/// handling — consume an <c>EditorInputFrame</c> and never touch
/// <c>InputManager</c>, <c>IWindow</c>, or any Silk.NET type directly. That is
/// what keeps re-hosting the editor viewport inside an Uno/WinUI shell a swap
/// rather than a rewrite: the host builds the frame from whatever input stack
/// it has (see <see cref="IEditorInputSource"/>;
/// <see cref="EngineEditorInputSource"/> is the Silk-backed one) and every tool
/// above the seam is unchanged. Consequently nothing in this assembly may
/// reference Silk.NET — the button and modifier vocabulary
/// (<see cref="PointerButtons"/>, <see cref="KeyModifiers"/>) is the engine's
/// own, not the backend's.
/// <para>
/// It is a readonly struct passed by value, so producing one frame's snapshot
/// allocates nothing.
/// </para>
/// <para>
/// <b>Threading:</b> built and consumed on the render thread, which owns scene
/// mutation — the same rule as <c>Scene</c>. The snapshot itself is immutable,
/// so handing a copy to another thread is safe; acting on it is not.
/// </para>
/// </remarks>
public readonly struct EditorInputFrame
{
    /// <summary>Creates a snapshot from already-gathered per-frame state.</summary>
    /// <param name="cursorPosition">Cursor position in viewport pixels, origin top-left, y growing downward.</param>
    /// <param name="viewportSize">Viewport size in pixels.</param>
    /// <param name="buttonsDown">Buttons held at snapshot time.</param>
    /// <param name="buttonsPressed">Buttons that went down on this frame.</param>
    /// <param name="buttonsReleased">Buttons that went up on this frame.</param>
    /// <param name="modifiers">Modifier keys held at snapshot time.</param>
    /// <param name="scrollDelta">Wheel movement over this frame, in notches (y = vertical).</param>
    /// <param name="deltaTime">Frame duration in seconds.</param>
    public EditorInputFrame(
        Vector2 cursorPosition,
        Vector2 viewportSize,
        PointerButtons buttonsDown,
        PointerButtons buttonsPressed,
        PointerButtons buttonsReleased,
        KeyModifiers modifiers,
        Vector2 scrollDelta,
        float deltaTime)
    {
        CursorPosition = cursorPosition;
        ViewportSize = viewportSize;
        ButtonsDown = buttonsDown;
        ButtonsPressed = buttonsPressed;
        ButtonsReleased = buttonsReleased;
        Modifiers = modifiers;
        ScrollDelta = scrollDelta;
        DeltaTime = deltaTime;
    }

    /// <summary>
    /// The cursor's position in viewport pixels: origin at the top-left, y
    /// growing downward — exactly the convention
    /// <c>Camera.ScreenPointToRay</c> expects, so a picking ray needs no
    /// conversion. May fall outside the viewport when the cursor has left it
    /// (see <see cref="IsCursorInsideViewport"/>); that is deliberate, since a
    /// drag that runs off the edge must keep tracking.
    /// </summary>
    public Vector2 CursorPosition { get; }

    /// <summary>
    /// The viewport's size in pixels — the framebuffer today, an arbitrary
    /// docked panel rect once the editor is re-hosted. Tools that need a
    /// projection or an aspect ratio take it from here rather than from the
    /// window.
    /// </summary>
    public Vector2 ViewportSize { get; }

    /// <summary>Mouse buttons held down at snapshot time.</summary>
    public PointerButtons ButtonsDown { get; }

    /// <summary>
    /// Mouse buttons that went from up to down on this frame — the grab edge
    /// for a gizmo drag.
    /// </summary>
    public PointerButtons ButtonsPressed { get; }

    /// <summary>
    /// Mouse buttons that went from down to up on this frame — the release
    /// edge that commits a gizmo drag. A button pressed and released inside a
    /// single frame appears in both edge sets.
    /// </summary>
    public PointerButtons ButtonsReleased { get; }

    /// <summary>Modifier keys held at snapshot time.</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary>
    /// Wheel movement over this frame in notches: <c>Y</c> is the vertical
    /// wheel (positive = away from the user), <c>X</c> the horizontal one.
    /// Zero on frames with no wheel activity.
    /// </summary>
    public Vector2 ScrollDelta { get; }

    /// <summary>The frame's duration in seconds.</summary>
    public float DeltaTime { get; }

    /// <summary>
    /// True when <see cref="CursorPosition"/> lies inside the viewport rect
    /// (left/top inclusive, right/bottom exclusive). False for a zero-sized
    /// viewport, so a not-yet-sized panel never claims a hover.
    /// </summary>
    public bool IsCursorInsideViewport =>
        CursorPosition.X >= 0f && CursorPosition.X < ViewportSize.X &&
        CursorPosition.Y >= 0f && CursorPosition.Y < ViewportSize.Y;

    /// <summary>
    /// True when <em>every</em> button in <paramref name="buttons"/> is held.
    /// <see cref="PointerButtons.None"/> returns false — "no buttons" is not a
    /// button that can be down.
    /// </summary>
    public bool IsDown(PointerButtons buttons) => Matches(ButtonsDown, buttons);

    /// <summary>
    /// True when <em>every</em> button in <paramref name="buttons"/> went down
    /// on this frame. Same <see cref="PointerButtons.None"/> rule as
    /// <see cref="IsDown"/>.
    /// </summary>
    public bool WasPressed(PointerButtons buttons) => Matches(ButtonsPressed, buttons);

    /// <summary>
    /// True when <em>every</em> button in <paramref name="buttons"/> went up on
    /// this frame. Same <see cref="PointerButtons.None"/> rule as
    /// <see cref="IsDown"/>.
    /// </summary>
    public bool WasReleased(PointerButtons buttons) => Matches(ButtonsReleased, buttons);

    /// <summary>
    /// True when every modifier in <paramref name="modifiers"/> is held; other
    /// modifiers may also be held. <see cref="KeyModifiers.None"/> returns true
    /// — asking for no modifiers is always satisfied. Use
    /// <c>Modifiers == KeyModifiers.None</c> to test for "nothing held".
    /// </summary>
    public bool HasModifiers(KeyModifiers modifiers) => (Modifiers & modifiers) == modifiers;

    // Shared "all of these flags are set" test. The None guard is what makes
    // `if (frame.WasPressed(binding))` safe when a binding is unassigned.
    private static bool Matches(PointerButtons state, PointerButtons query) =>
        query != PointerButtons.None && (state & query) == query;
}
