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

    private static ShellChord? ShellChordFor(int virtualKey) => virtualKey switch
    {
        0x4E => Viewport.ShellChord.NewMap,   // N
        0x4F => Viewport.ShellChord.OpenMap,  // O
        0x53 => Win32Interop.IsKeyDown(Win32Interop.VK_SHIFT)
            ? Viewport.ShellChord.SaveMapAs
            : Viewport.ShellChord.SaveMap,    // S
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

                Win32Interop.SetCursor(_cursorLocked ? 0 : _arrowCursor);
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
                if (Win32Interop.IsKeyDown(Win32Interop.VK_CONTROL)
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

            Submit(InputEvent.PointerDelta(new Vector2(dx, dy)));
            Win32Interop.SetCursorPos(_lockAnchor.X, _lockAnchor.Y);
            return;
        }

        _lastClientPosition = new Win32Interop.POINT
        {
            X = Win32Interop.LowInt16(lParam),
            Y = Win32Interop.HighInt16(lParam),
        };

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
    }

    private void EndCursorLock()
    {
        if (!_cursorLocked)
            return;

        _cursorLocked = false;

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
