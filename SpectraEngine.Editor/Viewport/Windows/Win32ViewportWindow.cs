using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Input;
using System;
using System.Runtime.InteropServices;

namespace SpectraEngine.Editor.Viewport.Windows;

/// <summary>
/// A real Win32 child window the engine renders into, and the platform half of
/// the shell's input path.
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
/// <b>What is left here is the window, and only the window.</b> Every decision
/// about what a press MEANS lives in <see cref="ViewportInputRouter"/>, which
/// names no platform type and is therefore testable; this class registers the
/// class, pumps the messages, answers <c>WM_SETCURSOR</c>, translates messages
/// into router calls, and implements <see cref="IViewportCursor"/> for it. The
/// split exists because the input path is the one part of the shell a host swap
/// would otherwise force somebody to rewrite blind.
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
internal sealed class Win32ViewportWindow : IRenderSurface, IViewportCursor, IDisposable
{
    private const string ClassName = "SpectraViewportWindow";

    private static ushort _classAtom;
    private static nint _arrowCursor;

    // Held in a field for the window's whole life: the delegate is marshalled
    // to a raw function pointer inside the class registration, and a collected
    // one is a call into freed memory the next time the mouse moves.
    private static Win32Interop.WndProc? _sharedProc;

    private readonly ViewportInputRouter _router;

    private nint _hwnd;
    private Vector2D<int> _size;
    private EngineHost? _host;

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

        _router = new ViewportInputRouter(this);
        _router.ShellChord += chord => ShellChord?.Invoke(chord);
        _router.ContextMenuRequested += (x, y) => ContextMenuRequested?.Invoke(x, y);

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
    /// engine. Forwarded from the router, which owns the table.
    /// </summary>
    public event Action<ShellChord>? ShellChord;

    /// <summary>
    /// Raised on the UI thread when a right press ends without having become a
    /// drag: a right-CLICK, in client pixels. Forwarded from the router, which
    /// owns the arbitration.
    /// </summary>
    public event Action<int, int>? ContextMenuRequested;

    /// <summary>
    /// Where submitted input goes, once the engine exists. Null before then, and
    /// input that arrives in that window is dropped rather than queued: a
    /// keystroke into a viewport that has no scene yet has nothing to mean.
    /// </summary>
    internal EngineHost? Host
    {
        get => _host;
        set
        {
            _host = value;

            // EngineHost is not itself an IInputSink (it owns one), so the
            // router is handed an adapter rather than the host: the router must
            // be constructible in a test with no engine at all.
            _router.Sink = value is null ? null : new EngineHostSink(value);
        }
    }

    /// <summary>
    /// Applies the cursor mode the engine is asking for. <b>UI thread only</b>,
    /// once per pass of the shell's own pump.
    /// </summary>
    /// <remarks>
    /// This is the shell's half of the embedded cursor split. The engine has no
    /// device to hide behind a host-supplied surface, so it publishes a request
    /// and the router performs the capture, the pinning and the hide through
    /// this window. The engine's state machine is closed afterwards with
    /// <see cref="EngineHost.ApplyPendingCursorMode"/>, exactly as the
    /// standalone event pump does.
    /// </remarks>
    internal void PumpCursorMode()
    {
        if (_host is not { } host)
            return;

        _router.ApplyCursorMode(host.RequestedCursorMode);
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

        // Before the handle goes: every platform call the lock's release makes
        // needs a window to make it against.
        _router.ApplyCursorMode(CursorMode.Normal);

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
    /// nearest available shape and never learns that it did, which is the
    /// whole reason the vocabulary is the engine's rather than the platform's.
    /// </para>
    /// </remarks>
    private nint ResolveCursor()
    {
        CursorShape shape = _host?.RequestedCursorShape ?? CursorShape.Arrow;

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
                // anywhere but this message is reverted within a frame, which
                // is exactly what produces the "the cursor flickers" report
                // that has no other explanation.
                Win32Interop.SetCursor(_router.IsCursorLocked ? 0 : ResolveCursor());
                result = 1;
                return true;

            case Win32Interop.WM_SIZE:
                OnResized();
                return false;

            case Win32Interop.WM_KILLFOCUS:
                _router.OnFocusLost();
                return false;

            case Win32Interop.WM_MOUSEMOVE:
                _router.OnPointerMove(Win32Interop.LowInt16(lParam), Win32Interop.HighInt16(lParam));
                return false;

            case Win32Interop.WM_LBUTTONDOWN: OnButtonDown(PointerButtons.Left); return false;
            case Win32Interop.WM_RBUTTONDOWN: OnButtonDown(PointerButtons.Right); return false;
            case Win32Interop.WM_MBUTTONDOWN: OnButtonDown(PointerButtons.Middle); return false;

            case Win32Interop.WM_LBUTTONUP: _router.OnPointerUp(PointerButtons.Left); return false;
            case Win32Interop.WM_RBUTTONUP: _router.OnPointerUp(PointerButtons.Right); return false;
            case Win32Interop.WM_MBUTTONUP: _router.OnPointerUp(PointerButtons.Middle); return false;

            case Win32Interop.WM_MOUSEWHEEL:
                _router.OnScroll(0f, Win32Interop.HighInt16(wParam) / (float)Win32Interop.WHEEL_DELTA);
                return false;

            case Win32Interop.WM_MOUSEHWHEEL:
                _router.OnScroll(Win32Interop.HighInt16(wParam) / (float)Win32Interop.WHEEL_DELTA, 0f);
                return false;

            case Win32Interop.WM_KEYDOWN:
            case Win32Interop.WM_SYSKEYDOWN:
                // The router decides whether it claimed the key: a chord the
                // shell owns, or anything Alt is involved in. ORed with the
                // system-key kind because Windows classifies a few more messages
                // that way (F10 alone activates the menu bar with no Alt held),
                // and every one of them must be claimed here or the OS treats it
                // as a menu accelerator and eats the next keystroke.
                if (_router.OnKeyDown(Win32Keys.ToInputKey((int)wParam, lParam), ReadModifiers()))
                {
                    result = 0;
                    return true;
                }

                result = 0;
                return message == Win32Interop.WM_SYSKEYDOWN;

            case Win32Interop.WM_KEYUP:
            case Win32Interop.WM_SYSKEYUP:
                _router.OnKeyUp(Win32Keys.ToInputKey((int)wParam, lParam));
                result = 0;
                return message == Win32Interop.WM_SYSKEYUP;

            case Win32Interop.WM_DESTROY:
                _windows.TryRemove(_hwnd, out _);
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// The modifiers as of the message being processed, in the engine's own
    /// vocabulary.
    /// </summary>
    /// <remarks>
    /// Super is deliberately not read: nothing in the chord table or the Alt
    /// claim consults it, and a modifier nobody tests for is a P/Invoke per
    /// keystroke for nothing.
    /// </remarks>
    private static KeyModifiers ReadModifiers()
    {
        KeyModifiers modifiers = KeyModifiers.None;

        if (Win32Interop.IsKeyDown(Win32Interop.VK_CONTROL))
            modifiers |= KeyModifiers.Control;
        if (Win32Interop.IsKeyDown(Win32Interop.VK_SHIFT))
            modifiers |= KeyModifiers.Shift;
        if (Win32Interop.IsKeyDown(Win32Interop.VK_MENU))
            modifiers |= KeyModifiers.Alt;

        return modifiers;
    }

    private void OnButtonDown(PointerButtons button)
    {
        // Focus follows the click, because keyboard messages go to the focused
        // window: without this the viewport would render and respond to the
        // mouse while every shortcut went to the panel next to it. Before the
        // router runs, so the capture it takes is taken by a focused window.
        Win32Interop.SetFocus(_hwnd);
        _router.OnPointerDown(button);
    }

    private void OnResized()
    {
        Vector2D<int> size = ReadClientSize();
        if (size == _size)
            return;

        _size = size;
        Resized?.Invoke(size);
    }

    // --- IViewportCursor -----------------------------------------------------

    /// <inheritdoc/>
    ViewportSize IViewportCursor.ClientSize
    {
        get
        {
            Vector2D<int> size = ReadClientSize();
            return new ViewportSize(size.X, size.Y);
        }
    }

    /// <inheritdoc/>
    int IViewportCursor.DragSlack
    {
        get
        {
            // Half of SM_CXDRAG, which is the full WIDTH of the rectangle while
            // the travel is measured from its centre. Per-window DPI, so the
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
    }

    /// <inheritdoc/>
    ViewportPoint IViewportCursor.ClientToScreen(ViewportPoint client)
    {
        if (_hwnd == 0)
            return client;

        var point = new Win32Interop.POINT { X = client.X, Y = client.Y };
        Win32Interop.ClientToScreen(_hwnd, ref point);
        return new ViewportPoint(point.X, point.Y);
    }

    /// <inheritdoc/>
    void IViewportCursor.MoveCursor(int screenX, int screenY) =>
        Win32Interop.SetCursorPos(screenX, screenY);

    /// <inheritdoc/>
    void IViewportCursor.ClipToClient(bool clip)
    {
        if (!clip)
        {
            Win32Interop.ClipCursorRelease(0);
            return;
        }

        if (_hwnd == 0 || !Win32Interop.GetClientRect(_hwnd, out Win32Interop.RECT client))
            return;

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

    /// <inheritdoc/>
    void IViewportCursor.SetCursorHidden(bool hidden) =>
        Win32Interop.SetCursor(hidden ? 0 : _arrowCursor);

    /// <inheritdoc/>
    void IViewportCursor.SetPointerCapture(bool captured)
    {
        if (captured)
        {
            if (_hwnd != 0)
                Win32Interop.SetCapture(_hwnd);
        }
        else
        {
            Win32Interop.ReleaseCapture();
        }
    }

    // --- Plumbing ------------------------------------------------------------

    // The host owns an input sink rather than being one, and the router must not
    // name EngineHost at all: it is constructed in tests against a recorder.
    private sealed class EngineHostSink(EngineHost host) : IInputSink
    {
        public void Submit(in InputEvent input) => host.SubmitInput(in input);
    }

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
