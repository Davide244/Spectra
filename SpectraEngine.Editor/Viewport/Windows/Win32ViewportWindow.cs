using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Input;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SpectraEngine.Editor.Viewport.Windows;

/// <summary>
/// A real Win32 child window the engine renders into, and the whole of the
/// shell's input path.
/// </summary>
/// <remarks>
/// <b>Why the shell creates this rather than letting Avalonia supply one.</b> A
/// native child window is where the OS delivers the mouse: messages over it go
/// to its own window procedure and do not bubble up to the parent, so a viewport
/// hosted as somebody else's default child would render perfectly and never
/// respond to a click. Owning the class also means owning
/// <c>CS_OWNDC</c>, which an embedded OpenGL context would need and which costs
/// a swap chain nothing.
/// <para>
/// <b>It is an <see cref="IRenderSurface"/> itself</b>, because that is all it
/// is: a handle, a size and a resize event. The engine asks for nothing else,
/// and everything the standalone path additionally owns (title, cursor, event
/// pump, lifetime) belongs to the shell around this.
/// </para>
/// <para>
/// <b>Airspace is the cost of v1, and it is a real one.</b> This window sits
/// above the XAML rather than inside it, so Avalonia cannot draw over the
/// viewport, and it cannot be rotated or given opacity. For a docked pane that
/// is the accepted trade; removing it is <c>H3</c>'s composition path.
/// </para>
/// <para>
/// <b>Threading:</b> UI thread only. The window procedure runs on whichever
/// thread pumped the message, which for a child of an Avalonia window is always
/// the UI thread, and that is exactly the position the standalone window's own
/// device callbacks are in.
/// </para>
/// </remarks>
internal sealed class Win32ViewportWindow : IRenderSurface, IDisposable
{
    private const string ClassName = "SpectraViewportWindow";

    private static ushort _classAtom;
    private static nint _arrowCursor;

    // Held in a field for the window's whole life: the delegate is marshalled
    // to a raw function pointer inside the class registration, and a collected
    // one is a call into freed memory the next time the mouse moves.
    private static Win32Interop.WndProc? _sharedProc;

    private nint _hwnd;
    private Vector2D<int> _size;

    private PointerButtons _buttonsDown;
    private bool _cursorLocked;

    // Screen-space point the cursor is pinned to while looking around, and the
    // client-space point it goes back to when the look ends.
    private Win32Interop.POINT _lockAnchor;
    private Win32Interop.POINT _lockRestore;

    // The last real client-space position, tracked exactly as the engine's own
    // input manager tracks it and for the same reason: it is where the cursor
    // goes back to when a look ends. Pinning happens at the viewport's centre,
    // which is not where the user pressed, so restoring to the anchor would
    // teleport the pointer every time a freelook finished.
    private Win32Interop.POINT _lastClientPosition;

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
    // "fixes" it - the signature of a bug nobody can report.
    //
    // Accumulated from both move shapes, because the cursor lock engages
    // within a frame or two of the press and everything after it arrives as a
    // delta rather than a position.
    private Win32Interop.POINT _rightPressPosition;
    private int _rightTravelX;
    private int _rightTravelY;
    private bool _rightPressActive;
    private bool _rightBecameDrag;

    // Half the system's drag width, in this window's DPI: the same slack
    // Explorer gives a click, resolved once per press.
    private int _rightClickSlack = 4;

    /// <summary>Creates the child window under <paramref name="parent"/>.</summary>
    internal Win32ViewportWindow(nint parent)
    {
        EnsureClassRegistered();

        _hwnd = Win32Interop.CreateWindowEx(
            exStyle: 0,
            ClassName,
            windowName: null,
            Win32Interop.WS_CHILD | Win32Interop.WS_VISIBLE |
            Win32Interop.WS_CLIPSIBLINGS | Win32Interop.WS_CLIPCHILDREN,
            x: 0, y: 0, width: 1, height: 1,
            parent,
            menu: 0,
            Win32Interop.GetModuleHandle(null),
            param: 0);

        if (_hwnd == 0)
            throw new InvalidOperationException(
                $"Could not create the viewport window (Win32 error {Marshal.GetLastWin32Error()}).");

        _windows[_hwnd] = this;
        _size = ReadClientSize();
    }

    // The window procedure is static (one per class), so it needs a way back to
    // the instance. A dictionary rather than GWLP_USERDATA because the first
    // messages arrive during CreateWindowEx, before there is an instance to
    // store, and a lookup miss is then simply "not mine yet".
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, Win32ViewportWindow> _windows = new();

    /// <inheritdoc/>
    public RenderSurfaceKind Kind => RenderSurfaceKind.Win32;

    /// <inheritdoc/>
    public nint NativeHandle => _hwnd;

    /// <inheritdoc/>
    /// <remarks>
    /// Always null. An embedded OpenGL viewport has to create its own WGL
    /// context against this window and supply a proc-address loader, which is
    /// the arc's largest single piece of remaining work; until then the shell
    /// runs a D3D backend and this surface offers a handle only.
    /// </remarks>
    public IGLContext? GLContext => null;

    /// <inheritdoc/>
    public Vector2D<int> PixelSize => _size;

    /// <inheritdoc/>
    public event Action<Vector2D<int>>? Resized;

    /// <summary>
    /// Raised on the UI thread for a Ctrl chord the shell owns rather than the
    /// engine.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a short, closed list.</b> Every chord added here is one
    /// the engine can no longer see, so this is not a general keyboard hook: it
    /// is the handful of document verbs that have no meaning inside a viewport
    /// and every meaning outside one. Anything about the SCENE stays with the
    /// engine, where the editor's own keymap already owns it.
    /// </remarks>
    public event Action<ShellChord>? ShellChord;

    /// <summary>
    /// Raised on the UI thread when a right press ends without having become a
    /// drag: a right-CLICK, in client pixels. The engine has already received
    /// the balanced down/up pair (its freelook legitimately started and ended
    /// around it); what the shell does with the click - a context menu - is
    /// the shell's business, exactly like the document chords.
    /// </summary>
    public event Action<int, int>? ContextMenuRequested;

    private static ShellChord? ShellChordFor(int virtualKey) => virtualKey switch
    {
        0x4E => Viewport.ShellChord.NewMap,   // N
        0x4F => Viewport.ShellChord.OpenMap,  // O
        0x53 => Win32Interop.IsKeyDown(Win32Interop.VK_SHIFT)
            ? Viewport.ShellChord.SaveMapAs
            : Viewport.ShellChord.SaveMap,    // S

        // The number row only. The numpad's own codes are deliberately absent:
        // an insert is not a thing anyone reaches for with their right hand
        // while the left is on the movement keys, and claiming them would take
        // four more codes away from the engine for nothing.
        0x31 => Viewport.ShellChord.InsertBlock, // 1
        0x32 => Viewport.ShellChord.InsertPart,  // 2
        0x33 => Viewport.ShellChord.InsertCut,   // 3
        0x34 => Viewport.ShellChord.InsertLight, // 4

        _ => null,
    };

    /// <summary>
    /// Where submitted input goes, once the engine exists. Null before then, and
    /// input that arrives in that window is dropped rather than queued: a
    /// keystroke into a viewport that has no scene yet has nothing to mean.
    /// </summary>
    internal EngineHost? Host { get; set; }

    /// <summary>
    /// Applies the cursor mode the engine is asking for. <b>UI thread only</b>,
    /// once per pass of the shell's own pump.
    /// </summary>
    /// <remarks>
    /// This is the shell's half of the embedded cursor split. The engine has no
    /// device to hide behind a host-supplied surface, so it publishes a request
    /// and this performs the capture, the pinning and the hide that only the
    /// window's owner can. The engine's state machine is closed afterwards with
    /// <see cref="EngineHost.ApplyPendingCursorMode"/>, exactly as the
    /// standalone event pump does.
    /// </remarks>
    internal void PumpCursorMode()
    {
        if (Host is not { } host)
            return;

        bool wanted = host.RequestedCursorMode == CursorMode.Locked;
        if (wanted != _cursorLocked)
        {
            if (wanted) BeginCursorLock();
            else EndCursorLock();
        }

        host.ApplyPendingCursorMode();
    }

    /// <summary>Gives this window Win32 keyboard focus, as a click on it would.</summary>
    internal void FocusKeyboard()
    {
        if (_hwnd != 0)
            Win32Interop.SetFocus(_hwnd);
    }

    /// <summary>Destroys the child window.</summary>
    public void Dispose()
    {
        if (_hwnd == 0)
            return;

        EndCursorLock();
        _windows.TryRemove(_hwnd, out _);
        Win32Interop.DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    // --- Window class --------------------------------------------------------

    private static void EnsureClassRegistered()
    {
        if (_classAtom != 0)
            return;

        _sharedProc = StaticWndProc;
        _arrowCursor = Win32Interop.LoadCursor(0, Win32Interop.IDC_ARROW);

        var windowClass = new Win32Interop.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32Interop.WNDCLASSEX>(),
            style = Win32Interop.CS_OWNDC | Win32Interop.CS_HREDRAW | Win32Interop.CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_sharedProc),
            hInstance = Win32Interop.GetModuleHandle(null),
            hCursor = _arrowCursor,

            // No background brush, deliberately: the engine paints every pixel
            // of this window every frame, and letting the OS erase it first is
            // a full-window fill that flashes on every resize.
            hbrBackground = 0,
            lpszClassName = Marshal.StringToHGlobalUni(ClassName),
        };

        _classAtom = Win32Interop.RegisterClassEx(ref windowClass);
        if (_classAtom == 0)
            throw new InvalidOperationException(
                $"Could not register the viewport window class (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private static nint StaticWndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (_windows.TryGetValue(hwnd, out Win32ViewportWindow? window) &&
            window.HandleMessage(message, wParam, lParam, out nint result))
        {
            return result;
        }

        return Win32Interop.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // --- Messages ------------------------------------------------------------

    /// <summary>
    /// What the engine currently wants the pointer to look like, as an OS
    /// cursor handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Loaded on demand and kept, because <c>LoadCursor</c> on a stock
    /// cursor is a cache lookup rather than an allocation</b>, but it is still a
    /// call inside a message that arrives with every mouse move. Stock cursors
    /// are never destroyed, so there is nothing here to release.
    /// </para>
    /// <para>
    /// <b>The degradations live here.</b> Windows has no rotate cursor, and its
    /// closest thing to a grab is the hand. A tool asking for either gets the
    /// nearest available shape and never learns that it did - which is the
    /// whole reason the vocabulary is the engine's rather than the platform's.
    /// </para>
    /// </remarks>
    private nint ResolveCursor()
    {
        CursorShape shape = Host?.RequestedCursorShape ?? CursorShape.Arrow;

        int id = shape switch
        {
            CursorShape.Crosshair => Win32Interop.IDC_CROSS,
            CursorShape.Grab or CursorShape.Grabbing => Win32Interop.IDC_HAND,
            CursorShape.SizeWestEast => Win32Interop.IDC_SIZEWE,
            CursorShape.SizeNorthSouth => Win32Interop.IDC_SIZENS,
            CursorShape.SizeNorthWestSouthEast => Win32Interop.IDC_SIZENWSE,
            CursorShape.SizeNorthEastSouthWest => Win32Interop.IDC_SIZENESW,
            CursorShape.SizeAll or CursorShape.Rotate => Win32Interop.IDC_SIZEALL,
            CursorShape.No => Win32Interop.IDC_NO,
            _ => Win32Interop.IDC_ARROW,
        };

        if (_cursors.TryGetValue(id, out nint handle))
            return handle;

        handle = Win32Interop.LoadCursor(0, id);
        if (handle == 0)
            handle = _arrowCursor;

        _cursors[id] = handle;
        return handle;
    }

    private readonly Dictionary<int, nint> _cursors = [];

    private bool HandleMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;

        switch (message)
        {
            case Win32Interop.WM_ERASEBKGND:
                // Claimed and ignored: the engine owns every pixel here, and an
                // OS erase before the next present is a visible flash.
                result = 1;
                return true;

            case Win32Interop.WM_SETCURSOR:
                // Only over the client area; the frame and scrollbars (which
                // this window has none of) stay the OS's business.
                if ((lParam & 0xFFFF) != Win32Interop.HTCLIENT)
                    return false;

                // HERE, and nowhere else. Windows re-asserts the window class's
                // own cursor on every mouse move, so a SetCursor issued from
                // anywhere but this message is reverted within a frame - which
                // is exactly what produces the "the cursor flickers" report
                // that has no other explanation.
                Win32Interop.SetCursor(_cursorLocked ? 0 : ResolveCursor());
                result = 1;
                return true;

            case Win32Interop.WM_SIZE:
                OnResized();
                return false;

            case Win32Interop.WM_KILLFOCUS:
                // Everything held goes up and the cursor comes back. The keys
                // whose release went to whoever took the focus are never coming.
                EndCursorLock();
                _buttonsDown = PointerButtons.None;
                _rightPressActive = false;
                Submit(InputEvent.FocusLost());
                return false;

            case Win32Interop.WM_MOUSEMOVE:
                OnMouseMove(lParam);
                return false;

            case Win32Interop.WM_LBUTTONDOWN: OnButtonDown(PointerButtons.Left); return false;
            case Win32Interop.WM_RBUTTONDOWN: OnButtonDown(PointerButtons.Right); return false;
            case Win32Interop.WM_MBUTTONDOWN: OnButtonDown(PointerButtons.Middle); return false;

            case Win32Interop.WM_LBUTTONUP: OnButtonUp(PointerButtons.Left); return false;
            case Win32Interop.WM_RBUTTONUP: OnButtonUp(PointerButtons.Right); return false;
            case Win32Interop.WM_MBUTTONUP: OnButtonUp(PointerButtons.Middle); return false;

            case Win32Interop.WM_MOUSEWHEEL:
                Submit(InputEvent.Scroll(new Vector2(0f, Win32Interop.HighInt16(wParam) / (float)Win32Interop.WHEEL_DELTA)));
                return false;

            case Win32Interop.WM_MOUSEHWHEEL:
                Submit(InputEvent.Scroll(new Vector2(Win32Interop.HighInt16(wParam) / (float)Win32Interop.WHEEL_DELTA, 0f)));
                return false;

            case Win32Interop.WM_KEYDOWN:
            case Win32Interop.WM_SYSKEYDOWN:
                // A Ctrl chord the SHELL owns never reaches the engine. The
                // viewport is a native child window, so while it has focus
                // Avalonia sees no keyboard at all and a menu accelerator is
                // simply inert - which for Ctrl+S is the worst possible
                // failure, because it is the one chord people press without
                // looking and trust to have worked.
                //
                // Never while the cursor is locked: during a freelook Ctrl is
                // the descend key and S flies backwards, so the chord fires
                // from the ordinary descend-while-reversing gesture, pops a
                // save dialog mid-flight and eats the movement key. The same
                // rule the editor's own Ctrl chords follow while a camera
                // claims the letters.
                if (!_cursorLocked
                    && Win32Interop.IsKeyDown(Win32Interop.VK_CONTROL)
                    && ShellChordFor((int)wParam) is { } chord)
                {
                    ShellChord?.Invoke(chord);
                    result = 0;
                    return true;
                }

                Submit(InputEvent.KeyDown(Win32Keys.ToInputKey((int)wParam, lParam)));

                // Claimed so the OS does not also treat it as a menu accelerator:
                // Alt alone opens the window menu and eats the next keystroke,
                // which is the difference between Alt-orbit working and the
                // viewport silently going deaf mid-gesture.
                result = 0;
                return message == Win32Interop.WM_SYSKEYDOWN;

            case Win32Interop.WM_KEYUP:
            case Win32Interop.WM_SYSKEYUP:
                Submit(InputEvent.KeyUp(Win32Keys.ToInputKey((int)wParam, lParam)));
                result = 0;
                return message == Win32Interop.WM_SYSKEYUP;

            case Win32Interop.WM_DESTROY:
                _windows.TryRemove(_hwnd, out _);
                return false;

            default:
                return false;
        }
    }

    private void OnResized()
    {
        Vector2D<int> size = ReadClientSize();
        if (size == _size)
            return;

        _size = size;
        Resized?.Invoke(size);
    }

    private void OnMouseMove(nint lParam)
    {
        if (_cursorLocked)
        {
            // Pinned: the pointer is teleported back to the anchor after every
            // real movement, so what the engine gets is raw motion and the
            // cursor never reaches the edge of the screen. The SetCursorPos
            // below generates a move of its own, which arrives here as a zero
            // delta and is dropped by the test.
            var screen = new Win32Interop.POINT
            {
                X = Win32Interop.LowInt16(lParam),
                Y = Win32Interop.HighInt16(lParam),
            };
            Win32Interop.ClientToScreen(_hwnd, ref screen);

            int dx = screen.X - _lockAnchor.X;
            int dy = screen.Y - _lockAnchor.Y;
            if (dx == 0 && dy == 0)
                return;

            if (_rightPressActive)
                AccumulateRightTravel(dx, dy);

            Submit(InputEvent.PointerDelta(new Vector2(dx, dy)));
            Win32Interop.SetCursorPos(_lockAnchor.X, _lockAnchor.Y);
            return;
        }

        var position = new Win32Interop.POINT
        {
            X = Win32Interop.LowInt16(lParam),
            Y = Win32Interop.HighInt16(lParam),
        };

        if (_rightPressActive)
            AccumulateRightTravel(position.X - _lastClientPosition.X, position.Y - _lastClientPosition.Y);

        _lastClientPosition = position;

        Submit(InputEvent.PointerMove(new Vector2(_lastClientPosition.X, _lastClientPosition.Y)));
    }

    private void OnButtonDown(PointerButtons button)
    {
        // Focus follows the click, because keyboard messages go to the focused
        // window: without this the viewport would render and respond to the
        // mouse while every shortcut went to the panel next to it.
        Win32Interop.SetFocus(_hwnd);

        if (_buttonsDown is PointerButtons.None)
            Win32Interop.SetCapture(_hwnd);

        _buttonsDown |= button;

        if (button == PointerButtons.Right)
        {
            _rightPressActive = true;
            _rightBecameDrag = false;
            _rightTravelX = 0;
            _rightTravelY = 0;
            _rightPressPosition = _lastClientPosition;
            _rightClickSlack = ResolveRightClickSlack();
        }

        Submit(InputEvent.PointerDown(button));
    }

    private void OnButtonUp(PointerButtons button)
    {
        _buttonsDown &= ~button;

        // Capture is what makes a drag that leaves the viewport still end when
        // the user lets go, so it is held until the last button comes up. Not
        // while the cursor is locked, which needs it for its own reasons.
        if (_buttonsDown is PointerButtons.None && !_cursorLocked)
            Win32Interop.ReleaseCapture();

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

    // Leaving the rectangle is what makes a press a drag, and it is one-way:
    // coming back inside afterwards does not turn a freelook into a click.
    private void AccumulateRightTravel(int dx, int dy)
    {
        if (_rightBecameDrag)
            return;

        _rightTravelX += dx;
        _rightTravelY += dy;

        if (Math.Abs(_rightTravelX) > _rightClickSlack || Math.Abs(_rightTravelY) > _rightClickSlack)
            _rightBecameDrag = true;
    }

    private int ResolveRightClickSlack()
    {
        // Half of SM_CXDRAG, which is the full WIDTH of the rectangle while
        // the travel here is measured from its centre. Per-window DPI, so the
        // slack is the same physical distance on a 200% display as on a 100%
        // one; the fallback is the metric's own default.
        const int fallback = 4;

        if (_hwnd == 0)
            return fallback;

        try
        {
            uint dpi = Win32Interop.GetDpiForWindow(_hwnd);
            if (dpi == 0)
                return fallback;

            int width = Win32Interop.GetSystemMetricsForDpi(Win32Interop.SM_CXDRAG, dpi);
            return width > 1 ? width / 2 : fallback;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1607 Windows has neither entry point; the constant is what
            // the metric returns at 100% anyway.
            return fallback;
        }
    }

    // --- Cursor lock ---------------------------------------------------------

    private void BeginCursorLock()
    {
        if (_cursorLocked || _hwnd == 0)
            return;

        // The anchor is the middle of the viewport rather than wherever the
        // cursor happens to be: pinning at an edge means half the mouse's
        // travel leaves the window before the teleport catches it.
        Win32Interop.GetClientRect(_hwnd, out Win32Interop.RECT client);
        var centre = new Win32Interop.POINT
        {
            X = (client.Left + client.Right) / 2,
            Y = (client.Top + client.Bottom) / 2,
        };

        _lockRestore = _lastClientPosition;
        Win32Interop.ClientToScreen(_hwnd, ref centre);
        _lockAnchor = centre;

        _cursorLocked = true;
        Win32Interop.SetCapture(_hwnd);
        Win32Interop.SetCursor(0);
        Win32Interop.SetCursorPos(_lockAnchor.X, _lockAnchor.Y);

        // The hidden pointer is fenced to the VIEWPORT for the lock's duration:
        // it cannot drift onto another monitor or another application while
        // invisible, and however close the window sits to a screen edge the
        // anchor keeps at least half the viewport of travel in every direction
        // before the OS would saturate the position. The whole client rect
        // rather than a small band around the anchor, deliberately — a tight
        // fence SHRINKS the headroom the differencing survives a UI stall
        // with, which is the opposite of hardening. Every release path funnels
        // through EndCursorLock (mode change, focus loss, destruction), so the
        // fence cannot outlive the lock.
        var topLeft = new Win32Interop.POINT { X = client.Left, Y = client.Top };
        var bottomRight = new Win32Interop.POINT { X = client.Right, Y = client.Bottom };
        Win32Interop.ClientToScreen(_hwnd, ref topLeft);
        Win32Interop.ClientToScreen(_hwnd, ref bottomRight);
        Win32Interop.ClipCursor(new Win32Interop.RECT
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = bottomRight.X,
            Bottom = bottomRight.Y,
        });
    }

    private void EndCursorLock()
    {
        if (!_cursorLocked)
            return;

        _cursorLocked = false;

        // Before the restore teleport, or the fence would clamp it: a cursor
        // released outside the viewport (the press was near the pane's edge)
        // must land where the press happened, not on the fence line.
        Win32Interop.ClipCursorRelease(0);

        Win32Interop.POINT restore = _lockRestore;
        Win32Interop.ClientToScreen(_hwnd, ref restore);
        Win32Interop.SetCursorPos(restore.X, restore.Y);
        Win32Interop.SetCursor(_arrowCursor);

        if (_buttonsDown is PointerButtons.None)
            Win32Interop.ReleaseCapture();
    }

    // --- Plumbing ------------------------------------------------------------

    private void Submit(in InputEvent input) => Host?.SubmitInput(in input);

    private Vector2D<int> ReadClientSize()
    {
        if (_hwnd == 0 || !Win32Interop.GetClientRect(_hwnd, out Win32Interop.RECT client))
            return new Vector2D<int>(1, 1);

        // Never zero: a minimised or collapsed pane would otherwise hand the
        // swap chain a degenerate size, which every backend refuses in its own
        // way and none of them refuses quietly.
        return new Vector2D<int>(
            Math.Max(1, client.Right - client.Left),
            Math.Max(1, client.Bottom - client.Top));
    }
}
