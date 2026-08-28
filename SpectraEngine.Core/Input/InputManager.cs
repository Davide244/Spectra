using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Silk.NET.Input;
using SilkCursorMode = Silk.NET.Input.CursorMode;

namespace SpectraEngine.Core.Input;

/// <summary>
/// Tracks keyboard and mouse state from Silk.NET input events and exposes a
/// pollable query surface. Events fire on the OS-event thread; queries are
/// expected from the render thread, so all shared state is mutated under a
/// single lock.
/// </summary>
/// <remarks>
/// It is also the engine's <see cref="ICursorLock"/>: cursor-mode requests
/// arrive here from any thread and are applied by the main thread in
/// <see cref="ApplyPendingCursorMode"/>. See that method for why the round trip
/// exists at all.
/// <para>
/// Note that <c>CursorMode</c> spelled unqualified in this file is the engine's
/// own <see cref="Input.CursorMode"/> — a type in this namespace outranks one
/// pulled in by a <c>using</c> — and the backend's is spelled
/// <c>SilkCursorMode</c>.
/// </para>
/// </remarks>
public sealed class InputManager : ICursorLock, IInputSink
{
    private readonly ILogger<InputManager> _logger;
    private readonly object _stateLock = new();
    private readonly HashSet<InputKey> _keysDown = [];
    private readonly HashSet<InputKey> _pendingPressed = [];
    private readonly HashSet<InputKey> _pressedThisFrame = [];

    // Buttons are a flag set rather than a HashSet: there are three of them,
    // every query is a mask test, and the neutral vocabulary they are reported
    // in is already a flags enum.
    private PointerButtons _buttonsDown;
    private PointerButtons _pendingPressedButtons;
    private PointerButtons _pressedButtonsThisFrame;
    private PointerButtons _pendingReleasedButtons;
    private PointerButtons _releasedButtonsThisFrame;

    private IInputContext? _inputContext;
    private Vector2 _accumulatedMouseDelta;
    private Vector2 _mouseDelta;
    private Vector2? _lastMousePosition;
    private Vector2 _accumulatedScrollDelta;
    private Vector2 _scrollDelta;

    // ─── Cursor-mode latch ───────────────────────────────────
    // Requested from any thread, applied by the main thread in
    // ApplyPendingCursorMode. Both live under _stateLock like everything else
    // here; _appliedCursorMode is only ever WRITTEN by the main thread, so the
    // lock is purely about torn reads and about the focus-loss override below
    // racing an in-flight request.
    private CursorMode _requestedCursorMode = CursorMode.Normal;
    private CursorMode _appliedCursorMode = CursorMode.Normal;

    // Where the cursor was when the lock was taken, so releasing can put it
    // back. Also what MousePosition reports while locked: GLFW's virtual
    // position runs off to infinity once the cursor is disabled, and a picking
    // ray built from that would aim somewhere absurd.
    private Vector2 _cursorRestorePosition;
    private bool _hasCursorRestorePosition;

    public InputManager(ILogger<InputManager> logger)
    {
        _logger = logger;
    }

    /// <summary>The mouse movement accumulated during the last <see cref="Update"/> interval.</summary>
    public Vector2 MouseDelta
    {
        // Locked like every other accessor: Vector2 is two floats, so an
        // unlocked read could observe a torn value mid-write.
        get { lock (_stateLock) return _mouseDelta; }
    }

    /// <summary>
    /// The cursor's latest position in window client coordinates: pixels,
    /// origin at the top-left, y growing downward — exactly the convention the
    /// camera's picking-ray API expects. Unlike <see cref="MouseDelta"/> this
    /// is not latched per frame: position is absolute, so the freshest value
    /// is always the right one. Zero until the OS reports a first position.
    /// </summary>
    /// <remarks>
    /// <b>Frozen while the cursor is locked.</b> A disabled (captured) cursor
    /// has no meaningful absolute position — GLFW reports an unbounded virtual
    /// one that walks away from the window as you look around — so this reports
    /// the position the lock was taken at, and callers that care read
    /// <see cref="IsCursorLocked"/> and stand down rather than picking through a
    /// stale coordinate.
    /// </remarks>
    public Vector2 MousePosition
    {
        // Locked for the same torn-read reason as MouseDelta.
        get
        {
            lock (_stateLock)
            {
                if (_appliedCursorMode == CursorMode.Locked && _hasCursorRestorePosition)
                    return _cursorRestorePosition;

                return _lastMousePosition ?? Vector2.Zero;
            }
        }
    }

    /// <summary>
    /// Wheel movement accumulated during the last <see cref="Update"/> interval,
    /// in notches: <c>Y</c> is the vertical wheel (positive = away from the
    /// user), <c>X</c> the horizontal one. Latched per frame like
    /// <see cref="MouseDelta"/>, because scroll is a delta, not a state.
    /// </summary>
    public Vector2 ScrollDelta
    {
        get { lock (_stateLock) return _scrollDelta; }
    }

    // ─── Backend-neutral view ────────────────────────────────
    // The properties below report the same state as the Silk.NET-typed queries
    // above, but in the engine's own PointerButtons/KeyModifiers vocabulary.
    // That is what lets SpectraEngine.Editing consume live input without
    // referencing Silk.NET at all (see EditorInputFrame): the whole windowing
    // backend stays behind this seam.

    /// <summary>
    /// The mouse buttons currently held, as a backend-neutral flag set.
    /// </summary>
    public PointerButtons PointerButtonsDown
    {
        get { lock (_stateLock) return _buttonsDown; }
    }

    /// <summary>
    /// The mouse buttons that went from up to down on this tick, as a
    /// backend-neutral flag set. Latched by <see cref="Update"/>.
    /// </summary>
    public PointerButtons PointerButtonsPressed
    {
        get { lock (_stateLock) return _pressedButtonsThisFrame; }
    }

    /// <summary>
    /// The mouse buttons that went from down to up on this tick, as a
    /// backend-neutral flag set. Latched by <see cref="Update"/>. A button
    /// pressed and released inside a single tick reports on both edge sets.
    /// </summary>
    public PointerButtons PointerButtonsReleased
    {
        get { lock (_stateLock) return _releasedButtonsThisFrame; }
    }

    /// <summary>
    /// The modifier keys currently held, as a backend-neutral flag set. Left
    /// and right physical keys collapse into one flag each.
    /// </summary>
    public KeyModifiers Modifiers
    {
        get
        {
            lock (_stateLock)
            {
                KeyModifiers modifiers = KeyModifiers.None;
                if (_keysDown.Contains(InputKey.ShiftLeft) || _keysDown.Contains(InputKey.ShiftRight))
                    modifiers |= KeyModifiers.Shift;
                if (_keysDown.Contains(InputKey.ControlLeft) || _keysDown.Contains(InputKey.ControlRight))
                    modifiers |= KeyModifiers.Control;
                if (_keysDown.Contains(InputKey.AltLeft) || _keysDown.Contains(InputKey.AltRight))
                    modifiers |= KeyModifiers.Alt;
                if (_keysDown.Contains(InputKey.SuperLeft) || _keysDown.Contains(InputKey.SuperRight))
                    modifiers |= KeyModifiers.Super;
                return modifiers;
            }
        }
    }

    // ─── Cursor lock (ICursorLock) ───────────────────────────

    /// <inheritdoc/>
    public void RequestCursorMode(CursorMode mode)
    {
        lock (_stateLock)
            _requestedCursorMode = mode;
    }

    /// <inheritdoc/>
    public CursorMode CursorMode
    {
        get { lock (_stateLock) return _appliedCursorMode; }
    }

    /// <inheritdoc/>
    public bool IsCursorLocked
    {
        get { lock (_stateLock) return _appliedCursorMode == CursorMode.Locked; }
    }

    /// <summary>
    /// The cursor mode last asked for, whether or not it has been applied.
    /// </summary>
    /// <remarks>
    /// <b>For an embedded host, which owns the cursor the engine cannot
    /// touch.</b> The standalone path's <see cref="ApplyPendingCursorMode"/>
    /// drives a Silk mouse device; behind a host-supplied surface there is no
    /// device to drive, so a shell polls this, performs its own platform
    /// capture, and calls <see cref="ApplyPendingCursorMode"/> to close the
    /// state machine. The bookkeeping half of that method runs with or without
    /// a device, which is exactly what makes the split work.
    /// </remarks>
    public CursorMode RequestedCursorMode
    {
        get { lock (_stateLock) return _requestedCursorMode; }
    }

    /// <summary>
    /// Applies whatever cursor mode was last requested. <b>Main thread only</b>,
    /// once per pass of the OS-event pump — the same slot the window title latch
    /// is applied in, and for the same reason: GLFW cursor calls belong to the
    /// thread that created the window.
    /// </summary>
    /// <remarks>
    /// Taking the lock hides the cursor and remembers where it was; releasing it
    /// puts the cursor back and re-seeds the position tracker, so the teleport
    /// home is not reported to anyone as a mouse movement. Both transitions
    /// clear the accumulated delta, because the driver's own jump at the moment
    /// of capture (and of release) is not user motion.
    /// <para>
    /// Costs two lock acquisitions and a compare on a frame where nothing
    /// changed, which is every frame outside a freelook transition.
    /// </para>
    /// </remarks>
    public void ApplyPendingCursorMode()
    {
        CursorMode requested;
        Vector2 restore;
        bool hasRestore;
        lock (_stateLock)
        {
            if (_requestedCursorMode == _appliedCursorMode)
                return;

            requested = _requestedCursorMode;
            restore = _cursorRestorePosition;
            hasRestore = _hasCursorRestorePosition;

            // Entering the lock: the position the cursor should come back to is
            // the last real one we saw, captured here — before the backend
            // starts reporting virtual coordinates.
            if (requested == CursorMode.Locked && _lastMousePosition is { } live)
            {
                restore = live;
                hasRestore = true;
            }
        }

        // The device calls are the only part that needs a device: with none
        // attached (headless, or before Initialize) the bookkeeping below still
        // runs, so the state machine behaves identically and the request does
        // not sit pending forever, re-attempted on every single pump.
        if (PrimaryMouse() is { } mouse)
        {
            mouse.Cursor.CursorMode = requested switch
            {
                CursorMode.Locked => SilkCursorMode.Disabled,
                CursorMode.Hidden => SilkCursorMode.Hidden,
                _ => SilkCursorMode.Normal,
            };

            // Leaving the lock: put the pointer back where the user left it.
            // This must happen AFTER the mode change — a position written while
            // the cursor is still disabled goes to the virtual cursor, not the
            // real one.
            if (requested != CursorMode.Locked && hasRestore)
                mouse.Position = restore;
        }

        lock (_stateLock)
        {
            _appliedCursorMode = requested;
            _cursorRestorePosition = restore;
            _hasCursorRestorePosition = hasRestore;

            // Either direction, the jump the backend just performed is not
            // something the user did with their hand.
            _accumulatedMouseDelta = Vector2.Zero;
            if (requested != CursorMode.Locked && hasRestore)
                _lastMousePosition = restore;
        }
    }

    /// <summary>
    /// Reacts to the window gaining or losing focus. <b>Main thread only</b> —
    /// the engine wires it to the window's focus event.
    /// </summary>
    /// <remarks>
    /// <b>Losing focus while looking around must not leave the cursor
    /// captured.</b> Alt-tabbing out of a freelook otherwise strands the user
    /// with an invisible, trapped pointer over a window that is no longer even
    /// in front. So a focus loss forces the mode back to
    /// <see cref="Input.CursorMode.Normal"/> — overwriting the pending
    /// <em>request</em> as well, or the render thread's stale "keep it locked"
    /// would simply re-take it on the next pump — and drops every held key and
    /// button, because the releases that ended them were delivered to whichever
    /// window took the focus. Dropping the buttons is also what makes the
    /// release stick: the editor camera sees its look button come up, ends the
    /// gesture, and asks for the unlocked cursor it already has.
    /// </remarks>
    public void OnWindowFocusChanged(bool focused)
    {
        if (!focused)
            Submit(InputEvent.FocusLost());
    }

    // Caller must hold _stateLock. Shared by the window's focus event and by a
    // host's FocusLost submission, because losing focus means the same thing
    // however the engine hears about it.
    private void ReleaseEverything()
    {
        _requestedCursorMode = CursorMode.Normal;

        _keysDown.Clear();
        _pendingPressed.Clear();

        // Held buttons become release edges rather than vanishing, so a
        // gesture watching for its own release edge still gets one.
        _pendingReleasedButtons |= _buttonsDown;
        _buttonsDown = PointerButtons.None;
        _pendingPressedButtons = PointerButtons.None;

        _accumulatedMouseDelta = Vector2.Zero;
        _accumulatedScrollDelta = Vector2.Zero;
    }

    // The device cursor requests are applied to. Silk exposes one cursor per
    // mouse; the window's cursor is the primary mouse's.
    private IMouse? PrimaryMouse()
    {
        IInputContext? context = _inputContext;
        return context is not null && context.Mice.Count > 0 ? context.Mice[0] : null;
    }

    public void Initialize(IInputContext inputContext)
    {
        _inputContext = inputContext;

        for (int i = 0; i < _inputContext.Keyboards.Count; i++)
        {
            var keyboard = _inputContext.Keyboards[i];
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
        }

        for (int i = 0; i < _inputContext.Mice.Count; i++)
        {
            var mouse = _inputContext.Mice[i];
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
        }

        // Seed the pollable cursor position from the first mouse so a click
        // before any MouseMove event picks through the real cursor location
        // instead of (0,0). Initialize runs on the OS-event thread before the
        // render loop starts, so reading the device here is safe.
        if (_inputContext.Mice.Count > 0)
        {
            Vector2 position = _inputContext.Mice[0].Position;
            lock (_stateLock)
                _lastMousePosition ??= position;
        }

        _logger.LogInformation("Input manager initialized ({KeyboardCount} keyboards, {MouseCount} mice)",
            _inputContext.Keyboards.Count, _inputContext.Mice.Count);
    }

    /// <summary>Latches per-frame deltas; call once per game tick before querying.</summary>
    public void Update(double deltaTime)
    {
        lock (_stateLock)
        {
            _mouseDelta = _accumulatedMouseDelta;
            _accumulatedMouseDelta = Vector2.Zero;

            _scrollDelta = _accumulatedScrollDelta;
            _accumulatedScrollDelta = Vector2.Zero;

            _pressedThisFrame.Clear();
            foreach (InputKey k in _pendingPressed)
                _pressedThisFrame.Add(k);
            _pendingPressed.Clear();

            _pressedButtonsThisFrame = _pendingPressedButtons;
            _pendingPressedButtons = PointerButtons.None;

            _releasedButtonsThisFrame = _pendingReleasedButtons;
            _pendingReleasedButtons = PointerButtons.None;
        }
    }

    public bool IsKeyDown(InputKey key)
    {
        lock (_stateLock)
            return _keysDown.Contains(key);
    }

    /// <summary>True for the single tick on which <paramref name="key"/> went from up to down.</summary>
    public bool WasKeyPressed(InputKey key)
    {
        lock (_stateLock)
            return _pressedThisFrame.Contains(key);
    }

    /// <summary>
    /// True while every button in <paramref name="button"/> is held. A single
    /// flag is the ordinary case; a combination asks for all of them.
    /// </summary>
    public bool IsMouseButtonDown(PointerButtons button)
    {
        lock (_stateLock)
            return (_buttonsDown & button) == button;
    }

    /// <summary>True for the single tick on which <paramref name="button"/> went from up to down.</summary>
    public bool WasMouseButtonPressed(PointerButtons button)
    {
        lock (_stateLock)
            return (_pressedButtonsThisFrame & button) == button;
    }

    /// <summary>
    /// True for the single tick on which <paramref name="button"/> went from
    /// down to up. The release edge is what ends a gizmo drag, so it is latched
    /// exactly like the press edge rather than inferred from a state change the
    /// caller might miss between ticks.
    /// </summary>
    public bool WasMouseButtonReleased(PointerButtons button)
    {
        lock (_stateLock)
            return (_releasedButtonsThisFrame & button) == button;
    }

    public void Shutdown()
    {
        if (_inputContext is not null)
        {
            // Give the pointer back before the devices go away: a process that
            // exits mid-freelook must not leave a captured cursor behind.
            RequestCursorMode(CursorMode.Normal);
            ApplyPendingCursorMode();


            // Mirror Initialize: detach our handlers so the devices hold no
            // references to this manager, then release the native context.
            for (int i = 0; i < _inputContext.Keyboards.Count; i++)
            {
                var keyboard = _inputContext.Keyboards[i];
                keyboard.KeyDown -= OnKeyDown;
                keyboard.KeyUp -= OnKeyUp;
            }

            for (int i = 0; i < _inputContext.Mice.Count; i++)
            {
                var mouse = _inputContext.Mice[i];
                mouse.MouseDown -= OnMouseDown;
                mouse.MouseUp -= OnMouseUp;
                mouse.MouseMove -= OnMouseMove;
                mouse.Scroll -= OnScroll;
            }

            _inputContext.Dispose();
            _inputContext = null;
        }

        _logger.LogInformation("Input manager shut down");
    }

    // ─── Event handlers (OS-event thread) ────────────────────
    //
    // Internal rather than private so the headless suites can drive the state
    // machine — edges, latching, the neutral flag mapping — without a real
    // input device. The device parameter is part of Silk.NET's event signature
    // and is unused, so a test may pass null for it.

    internal void OnKeyDown(IKeyboard keyboard, Key key, int keyCode) =>
        Submit(InputEvent.KeyDown(SilkInputKeys.ToInputKey(key)));

    internal void OnKeyUp(IKeyboard keyboard, Key key, int keyCode) =>
        Submit(InputEvent.KeyUp(SilkInputKeys.ToInputKey(key)));

    internal void OnMouseDown(IMouse mouse, MouseButton button) =>
        Submit(InputEvent.PointerDown(SilkInputKeys.ToPointerButton(button)));

    internal void OnMouseUp(IMouse mouse, MouseButton button) =>
        Submit(InputEvent.PointerUp(SilkInputKeys.ToPointerButton(button)));

    internal void OnScroll(IMouse mouse, ScrollWheel wheel) =>
        Submit(InputEvent.Scroll(new Vector2(wheel.X, wheel.Y)));

    internal void OnMouseMove(IMouse mouse, Vector2 position) =>
        Submit(InputEvent.PointerMove(position));

    // ─── Submitted input (IInputSink) ────────────────────────

    /// <summary>
    /// Applies one event to the input state. Called by the standalone window's
    /// own device handlers above and by an embedded host through
    /// <c>EngineHost.SubmitInput</c>.
    /// </summary>
    /// <remarks>
    /// <b>Both paths land here, which is the point.</b> Edge detection, the
    /// auto-repeat filter, the delta accumulator and the focus-loss release are
    /// one implementation, so an embedded viewport cannot drift from the
    /// standalone window in how a press or a drag behaves — and the headless
    /// tests that pin those rules cover both.
    /// </remarks>
    public void Submit(in InputEvent input)
    {
        bool focusLost = false;

        lock (_stateLock)
        {
            switch (input.Kind)
            {
                case InputEventKind.KeyDown:
                    // A key the source could not name matches no binding, so it
                    // must not enter the held set either: it would collide with
                    // every other unnameable key and one release would clear
                    // them all.
                    if (input.Key is not InputKey.Unknown && _keysDown.Add(input.Key))
                        _pendingPressed.Add(input.Key);
                    break;

                case InputEventKind.KeyUp:
                    _keysDown.Remove(input.Key);
                    break;

                case InputEventKind.PointerDown:
                    // Same edge rule as the keyboard: only buttons that were
                    // not already held arm the press edge.
                    PointerButtons pressed = input.Button & ~_buttonsDown;
                    _buttonsDown |= input.Button;
                    _pendingPressedButtons |= pressed;
                    break;

                case InputEventKind.PointerUp:
                    // Only a real down-to-up transition arms the release edge,
                    // so a stray release for a button never seen to go down
                    // reports nothing.
                    PointerButtons released = input.Button & _buttonsDown;
                    _buttonsDown &= ~input.Button;
                    _pendingReleasedButtons |= released;
                    break;

                case InputEventKind.PointerMove:
                    if (_lastMousePosition.HasValue)
                        _accumulatedMouseDelta += input.Value - _lastMousePosition.Value;
                    _lastMousePosition = input.Value;
                    break;

                case InputEventKind.PointerDelta:
                    // No position to update: a captured cursor has none, and
                    // writing one would corrupt the restore point the lock is
                    // holding on the caller's behalf.
                    _accumulatedMouseDelta += input.Value;
                    break;

                case InputEventKind.Scroll:
                    _accumulatedScrollDelta += input.Value;
                    break;

                case InputEventKind.FocusLost:
                    ReleaseEverything();
                    focusLost = true;
                    break;
            }
        }

        // Outside the lock, and only on the one event that needs it: a focus
        // loss must give the cursor back immediately rather than a frame later,
        // or alt-tabbing out of a freelook strands the user with an invisible,
        // trapped pointer over a window that is no longer even in front. Costs
        // nothing on every other event.
        if (focusLost)
            ApplyPendingCursorMode();
    }
}
