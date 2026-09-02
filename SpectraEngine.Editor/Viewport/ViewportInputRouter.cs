using SpectraEngine.Core.Input;
using System;
using System.Numerics;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// Everything the viewport does with input that is not a call into a windowing
/// API: the button state, the cursor lock, the right-click-versus-right-drag
/// arbitration, and the short list of chords the shell owns rather than the
/// engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>It names no Win32 and no Avalonia type, and that is the whole point.</b>
/// This is the engine's entire input path in the editor, and until it was pulled
/// out of the window it could not be reached without a real HWND and a message
/// pump, so none of it had ever been under test. The behaviours in here are the
/// kind that fail silently when they regress: a cursor that lands outside the
/// window, a button the engine believes is still held, a context menu that opens
/// only when you click quickly. A host swap must not be the moment any of them
/// is discovered.
/// </para>
/// <para>
/// <b>The state machine runs whether or not there is a sink.</b> Input arriving
/// before the engine exists is dropped rather than queued (a keystroke into a
/// viewport with no scene has nothing to mean), but the lock, the buttons and
/// the press arbitration are the SHELL's bookkeeping and would otherwise come
/// back inconsistent the moment a session started.
/// </para>
/// <para>
/// <b>Threading:</b> UI thread only, exactly as the window procedure it is
/// driven from.
/// </para>
/// </remarks>
internal sealed class ViewportInputRouter
{
    // What SM_CXDRAG returns at 100%, and what the router uses until a press has
    // asked the host for the real one. Never actually read: a press resolves the
    // slack before any movement can accumulate against it.
    private const int DefaultDragSlack = 4;

    private readonly IViewportCursor _cursor;

    private PointerButtons _buttonsDown;
    private bool _cursorLocked;

    // Screen-space point the cursor is pinned to while looking around, and the
    // client-space point it goes back to when the look ends.
    private ViewportPoint _lockAnchor;
    private ViewportPoint _lockRestore;

    // The last real client-space position, tracked exactly as the engine's own
    // input manager tracks it and for the same reason: it is where the cursor
    // goes back to when a look ends. Pinning happens at the viewport's centre,
    // which is not where the user pressed, so restoring to the anchor would
    // teleport the pointer every time a freelook finished.
    private ViewportPoint _lastClientPosition;

    // Right-click-vs-right-drag: where the right button went down (client
    // pixels) and how far the pointer has moved AWAY from it since. The
    // engine's freelook owns a right DRAG, so a context menu can only mean a
    // right press that ended before it became one.
    //
    // NET DISPLACEMENT, not the length of the path travelled. Windows itself
    // draws a rectangle around the press point that the pointer has to LEAVE
    // (SM_CXDRAG, what DragDetect implements), and that has no time
    // component: a press held for a second while the hand shakes is still a
    // click. Summing |dx|+|dy| per message instead makes an ordinary hesitant
    // click on a high-dpi mouse silently open no menu, and pressing faster
    // "fixes" it, which is the signature of a bug nobody can report.
    //
    // Accumulated from both move shapes, because the cursor lock engages
    // within a frame or two of the press and everything after it arrives as a
    // delta rather than a position.
    private ViewportPoint _rightPressPosition;
    private int _rightTravelX;
    private int _rightTravelY;
    private bool _rightPressActive;
    private bool _rightBecameDrag;

    // Half the system's drag width, in this window's DPI: the same slack
    // Explorer gives a click, resolved once per press.
    private int _rightClickSlack = DefaultDragSlack;

    internal ViewportInputRouter(IViewportCursor cursor) => _cursor = cursor;

    /// <summary>
    /// Where submitted input goes, once the engine exists. Null before then, and
    /// input that arrives in that window is dropped rather than queued.
    /// </summary>
    internal IInputSink? Sink { get; set; }

    /// <summary>
    /// Raised for a chord the shell owns rather than the engine.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a short, closed list.</b> Every chord added here is one
    /// the engine can no longer see, so this is not a general keyboard hook: it
    /// is the handful of document verbs that have no meaning inside a viewport
    /// and every meaning outside one. Anything about the SCENE stays with the
    /// engine, where the editor's own keymap already owns it.
    /// </remarks>
    internal event Action<ShellChord>? ShellChord;

    /// <summary>
    /// Raised when a right press ends without having become a drag: a
    /// right-CLICK, in client pixels. The engine has already received the
    /// balanced down/up pair (its freelook legitimately started and ended around
    /// it); what the shell does with the click is the shell's business, exactly
    /// like the document chords.
    /// </summary>
    internal event Action<int, int>? ContextMenuRequested;

    /// <summary>
    /// Whether the pointer is currently pinned for a look. A host answering a
    /// "what shape is the cursor" query needs this: while it is true there is no
    /// cursor to shape.
    /// </summary>
    internal bool IsCursorLocked => _cursorLocked;

    /// <summary>Which buttons the router believes are held.</summary>
    internal PointerButtons ButtonsDown => _buttonsDown;

    // The chord table. Keys are the engine's own, not a platform's virtual-key
    // codes, so a second host inherits the table rather than transcribing it.
    private static ShellChord? ShellChordFor(InputKey key, KeyModifiers modifiers) => key switch
    {
        InputKey.N => Viewport.ShellChord.NewMap,
        InputKey.O => Viewport.ShellChord.OpenMap,
        InputKey.S => (modifiers & KeyModifiers.Shift) != 0
            ? Viewport.ShellChord.SaveMapAs
            : Viewport.ShellChord.SaveMap,

        // The number row only. A keypad digit is deliberately absent: an insert
        // is not a thing anyone reaches for with their right hand while the left
        // is on the movement keys, and claiming those keys would take them away
        // from the engine for nothing.
        InputKey.Number1 => Viewport.ShellChord.InsertBlock,
        InputKey.Number2 => Viewport.ShellChord.InsertPart,
        InputKey.Number3 => Viewport.ShellChord.InsertCut,
        InputKey.Number4 => Viewport.ShellChord.InsertLight,

        _ => null,
    };

    // --- Pointer -------------------------------------------------------------

    /// <summary>
    /// The pointer is at an absolute client-space position.
    /// </summary>
    /// <remarks>
    /// While the cursor is locked this is still the shape the OS reports, and it
    /// is differenced against the anchor rather than passed on: a captured
    /// pointer has no meaningful position, so what the engine gets is raw motion
    /// and the cursor never reaches the edge of the screen.
    /// </remarks>
    internal void OnPointerMove(int clientX, int clientY)
    {
        if (_cursorLocked)
        {
            ViewportPoint screen = _cursor.ClientToScreen(new ViewportPoint(clientX, clientY));

            int dx = screen.X - _lockAnchor.X;
            int dy = screen.Y - _lockAnchor.Y;

            // The re-pin below generates a move of its own, which arrives here
            // as a zero delta and is dropped by this test.
            if (dx == 0 && dy == 0)
                return;

            AccumulateRightTravel(dx, dy);
            Submit(InputEvent.PointerDelta(new Vector2(dx, dy)));
            _cursor.MoveCursor(_lockAnchor.X, _lockAnchor.Y);
            return;
        }

        var position = new ViewportPoint(clientX, clientY);
        AccumulateRightTravel(position.X - _lastClientPosition.X, position.Y - _lastClientPosition.Y);
        _lastClientPosition = position;

        Submit(InputEvent.PointerMove(new Vector2(position.X, position.Y)));
    }

    /// <summary>
    /// The pointer moved by a raw amount, with no meaningful absolute position.
    /// </summary>
    /// <remarks>
    /// The second move shape, and the reason the right-press arbitration
    /// accumulates rather than comparing against the press point: a host that
    /// reports raw motion (or the lock, which turns absolute positions into
    /// motion above) has no position to compare with, and a press that began
    /// before the lock engaged must keep the same budget afterwards.
    /// </remarks>
    internal void OnPointerDelta(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;

        AccumulateRightTravel(dx, dy);
        Submit(InputEvent.PointerDelta(new Vector2(dx, dy)));
    }

    /// <summary>A pointer button went down.</summary>
    internal void OnPointerDown(PointerButtons button)
    {
        if (_buttonsDown is PointerButtons.None)
            _cursor.SetPointerCapture(true);

        _buttonsDown |= button;

        if (button == PointerButtons.Right)
        {
            _rightPressActive = true;
            _rightBecameDrag = false;
            _rightTravelX = 0;
            _rightTravelY = 0;
            _rightPressPosition = _lastClientPosition;
            _rightClickSlack = _cursor.DragSlack;
        }

        Submit(InputEvent.PointerDown(button));
    }

    /// <summary>A pointer button came up.</summary>
    internal void OnPointerUp(PointerButtons button)
    {
        _buttonsDown &= ~button;

        // Capture is what makes a drag that leaves the viewport still end when
        // the user lets go, so it is held until the last button comes up. Not
        // while the cursor is locked, which needs it for its own reasons.
        if (_buttonsDown is PointerButtons.None && !_cursorLocked)
            _cursor.SetPointerCapture(false);

        Submit(InputEvent.PointerUp(button));

        // AFTER the up is submitted, so the engine's freelook has ended and
        // released its cursor request before the shell opens anything over the
        // viewport.
        if (button == PointerButtons.Right && _rightPressActive)
        {
            _rightPressActive = false;
            if (!_rightBecameDrag)
                ContextMenuRequested?.Invoke(_rightPressPosition.X, _rightPressPosition.Y);
        }
    }

    /// <summary>The wheel turned, in notches.</summary>
    internal void OnScroll(float x, float y) =>
        Submit(InputEvent.Scroll(new Vector2(x, y)));

    // Leaving the rectangle is what makes a press a drag, and it is one-way:
    // coming back inside afterwards does not turn a freelook into a click.
    private void AccumulateRightTravel(int dx, int dy)
    {
        if (!_rightPressActive || _rightBecameDrag)
            return;

        _rightTravelX += dx;
        _rightTravelY += dy;

        if (Math.Abs(_rightTravelX) > _rightClickSlack || Math.Abs(_rightTravelY) > _rightClickSlack)
            _rightBecameDrag = true;
    }

    // --- Keyboard ------------------------------------------------------------

    /// <summary>
    /// A key went down. Returns whether the router claimed it, which a host uses
    /// to decide whether the platform still gets a look at the message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A chord the SHELL owns never reaches the engine.</b> The viewport is a
    /// native child window, so while it has focus the UI framework sees no
    /// keyboard at all and a menu accelerator is simply inert, which for Ctrl+S
    /// is the worst possible failure: it is the one chord people press without
    /// looking and trust to have worked.
    /// </para>
    /// <para>
    /// <b>Never while the cursor is locked.</b> During a freelook Ctrl is the
    /// descend key and S flies backwards, so the chord would fire from the
    /// ordinary descend-while-reversing gesture, pop a save dialog mid-flight
    /// and eat the movement key. The same rule the editor's own Ctrl chords
    /// follow while a camera claims the letters.
    /// </para>
    /// <para>
    /// <b>Alt is claimed even though nothing here acts on it</b>, because a
    /// platform left to handle it opens its window menu and eats the next
    /// keystroke, which is the difference between Alt-orbit working and the
    /// viewport silently going deaf mid-gesture.
    /// </para>
    /// </remarks>
    internal bool OnKeyDown(InputKey key, KeyModifiers modifiers)
    {
        if (!_cursorLocked
            && (modifiers & KeyModifiers.Control) != 0
            && ShellChordFor(key, modifiers) is { } chord)
        {
            ShellChord?.Invoke(chord);
            return true;
        }

        Submit(InputEvent.KeyDown(key));

        // The key itself as well as the modifier, because a host that reports
        // the modifiers as they were BEFORE the key it is reporting would
        // otherwise let the Alt press through unclaimed.
        return (modifiers & KeyModifiers.Alt) != 0
            || key is InputKey.AltLeft or InputKey.AltRight;
    }

    /// <summary>A key came up.</summary>
    internal void OnKeyUp(InputKey key) => Submit(InputEvent.KeyUp(key));

    // --- Focus and the cursor lock -------------------------------------------

    /// <summary>
    /// The viewport lost input focus.
    /// </summary>
    /// <remarks>
    /// Everything held goes up and the cursor comes back. The releases that
    /// ended those presses went to whoever took the focus and are never coming,
    /// so the engine is told once and releases them all: <c>FocusLost</c> is the
    /// release-everything event, shared with the standalone window, and
    /// synthesising a button-up per held button here would arm the same release
    /// edges twice.
    /// </remarks>
    internal void OnFocusLost()
    {
        EndCursorLock();
        _buttonsDown = PointerButtons.None;
        _rightPressActive = false;
        Submit(InputEvent.FocusLost());
    }

    /// <summary>
    /// Applies the cursor mode the engine is asking for.
    /// </summary>
    /// <remarks>
    /// The shell's half of the embedded cursor split. The engine has no device
    /// to hide behind a host-supplied surface, so it publishes a request and the
    /// host performs the capture, the pinning and the hide that only the
    /// window's owner can. Calling this when nothing changed is free.
    /// </remarks>
    internal void ApplyCursorMode(CursorMode mode)
    {
        bool wanted = mode == CursorMode.Locked;
        if (wanted == _cursorLocked)
            return;

        if (wanted)
            BeginCursorLock();
        else
            EndCursorLock();
    }

    /// <summary>
    /// The viewport moved on screen without its size changing.
    /// </summary>
    /// <remarks>
    /// <b>The anchor is a SCREEN point derived from the client centre</b>, so a
    /// viewport that moves under a live lock leaves it pointing at where the
    /// pane used to be, and the very next move differences against it and hands
    /// the engine the whole displacement as one frame of look. Nothing in the
    /// current host can reach that state (the pane cannot be re-docked while the
    /// pointer is busy holding a freelook down), so nothing calls this yet; a
    /// dockable or composited viewport, whose surface can move without the OS
    /// telling this code that a window did, will.
    /// </remarks>
    internal void OnViewportMoved()
    {
        if (!_cursorLocked)
            return;

        _lockAnchor = ClientCentreOnScreen();
        _cursor.MoveCursor(_lockAnchor.X, _lockAnchor.Y);
        _cursor.ClipToClient(true);
    }

    private void BeginCursorLock()
    {
        if (_cursorLocked)
            return;

        // The anchor is the middle of the viewport rather than wherever the
        // cursor happens to be: pinning at an edge means half the mouse's
        // travel leaves the window before the teleport catches it.
        _lockRestore = _lastClientPosition;
        _lockAnchor = ClientCentreOnScreen();

        _cursorLocked = true;
        _cursor.SetPointerCapture(true);
        _cursor.SetCursorHidden(true);
        _cursor.MoveCursor(_lockAnchor.X, _lockAnchor.Y);

        // The hidden pointer is fenced to the VIEWPORT for the lock's duration:
        // it cannot drift onto another monitor or another application while
        // invisible, and however close the window sits to a screen edge the
        // anchor keeps at least half the viewport of travel in every direction
        // before the OS would saturate the position. Every release path funnels
        // through EndCursorLock (mode change, focus loss, destruction), so the
        // fence cannot outlive the lock.
        _cursor.ClipToClient(true);
    }

    private void EndCursorLock()
    {
        if (!_cursorLocked)
            return;

        _cursorLocked = false;

        // Before the restore teleport, or the fence would clamp it: a cursor
        // released outside the viewport (the press was near the pane's edge)
        // must land where the press happened, not on the fence line.
        _cursor.ClipToClient(false);

        ViewportPoint restore = _cursor.ClientToScreen(_lockRestore);
        _cursor.MoveCursor(restore.X, restore.Y);
        _cursor.SetCursorHidden(false);

        if (_buttonsDown is PointerButtons.None)
            _cursor.SetPointerCapture(false);
    }

    private ViewportPoint ClientCentreOnScreen()
    {
        ViewportSize size = _cursor.ClientSize;
        return _cursor.ClientToScreen(new ViewportPoint(size.Width / 2, size.Height / 2));
    }

    private void Submit(in InputEvent input) => Sink?.Submit(in input);
}
