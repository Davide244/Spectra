using Avalonia.Controls;
using Avalonia.Platform;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Editor.Viewport.Windows;
using System;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// The pane the engine renders into: a native child window embedded in the
/// visual tree, with the engine's own swap chain behind it.
/// </summary>
/// <remarks>
/// <b>This is the whole of the embedding.</b> Avalonia creates and positions a
/// native child; the engine is handed it as an <see cref="IRenderSurface"/> and
/// draws into it with its own render thread, its own device and its own present.
/// Nothing about the engine's frame goes through the UI framework, which is
/// exactly why a XAML layout pass cannot stall the viewport and why the async
/// CSG pipeline keeps its single-threaded proofs.
/// <para>
/// <b>The surface arrives before the engine does, and that ordering is
/// forced.</b> A renderer cannot initialise without a handle, and Avalonia only
/// produces one when the control is attached to a visual tree, so
/// <see cref="SurfaceCreated"/> is what a host waits for rather than starting
/// the engine at construction and hoping.
/// </para>
/// <para>
/// <b>Windows only for v1</b>, and it says so rather than degrading: on another
/// platform the base class's default child still appears (so the layout is
/// honest) but no surface is published and no engine starts. The Linux half is
/// the embedded OpenGL context, which is the arc's largest single piece of
/// remaining work and not something to fake.
/// </para>
/// </remarks>
public sealed class Win32EngineViewport : NativeControlHost, IEngineViewport
{
    private Win32ViewportWindow? _window;
    private EngineHost? _host;

    /// <summary>
    /// Raised on the UI thread once the native surface exists. A host starts
    /// the engine here.
    /// </summary>
    public event Action<IRenderSurface>? SurfaceCreated;

    /// <summary>
    /// Raised on the UI thread before the native surface is destroyed. A host
    /// must have stopped the engine by the time this returns, or the driver is
    /// handed a window that no longer exists.
    /// </summary>
    public event Action? SurfaceDestroying;

    /// <summary>
    /// Raised for a Ctrl chord the shell owns rather than the engine.
    /// </summary>
    /// <remarks>
    /// Forwarded from the native child window, which is where the OS delivers
    /// the keyboard while the viewport has focus. Without this the File menu's
    /// accelerators are inert exactly while somebody is working in the scene.
    /// </remarks>
    public event Action<ShellChord>? ShellChord;

    /// <summary>
    /// Raised on the UI thread for a right-click that never became a freelook
    /// drag, in the viewport's own pixels (which are framebuffer pixels: the
    /// child window is the framebuffer). The shell opens its context menu; the
    /// engine has already seen the balanced button events.
    /// </summary>
    public event Action<int, int>? ContextMenuRequested;

    /// <summary>Whether this platform can host the engine at all.</summary>
    /// <remarks>
    /// The answer now lives with the choice between the two viewports rather
    /// than on one of them, because a shell asking "can this machine host a
    /// viewport" is not asking about a particular kind.
    /// </remarks>
    public static bool IsSupported => EngineViewports.IsSupported;

    /// <inheritdoc/>
    public Control Control => this;

    /// <summary>
    /// The running engine's host, once there is one. Setting it is what turns
    /// the viewport's input on.
    /// </summary>
    public EngineHost? Host
    {
        get => _host;
        set
        {
            _host = value;
            if (_window is not null)
                _window.Host = value;
        }
    }

    /// <summary>
    /// Applies whatever cursor mode the engine has asked for. <b>UI thread
    /// only</b>, once per pass of the shell's pump.
    /// </summary>
    /// <remarks>
    /// The shell's equivalent of the slot <c>Engine.Run</c> applies its own
    /// cursor latch in, and it exists for the identical reason: capturing and
    /// hiding a pointer is window-thread work, and the engine asks for it from
    /// the render thread.
    /// </remarks>
    public void PumpCursorMode() => _window?.PumpCursorMode();

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Nothing to do, and that is the whole of the native child's answer.</b>
    /// The surface here IS the HWND: destroying the control destroys it, and
    /// <see cref="DestroyNativeControlCore"/> already raises
    /// <see cref="SurfaceDestroying"/> at the one moment the render thread must
    /// be off it. Raising anything here would put the engine's stop a step
    /// earlier for no gain and make the two paths disagree about which event
    /// ends a session. There is no re-parent to tell apart either: a native
    /// viewport is pinned in its own cell, because moving it is exactly what
    /// takes the session with it.
    /// </remarks>
    public void Shutdown()
    {
    }

    /// <summary>
    /// Hands the keyboard to the engine's native child window.
    /// </summary>
    /// <remarks>
    /// Not Avalonia's <c>Focus()</c>, which is a silent no-op here: a
    /// <see cref="NativeControlHost"/> is not focusable, and even a focusable
    /// one would hold AVALONIA focus while the OS keeps delivering keys
    /// wherever they were going. The engine hears the keyboard only while its
    /// own HWND has Win32 focus — the same <c>SetFocus</c> a click performs.
    /// </remarks>
    public void FocusEngine() => _window?.FocusKeyboard();

    /// <inheritdoc/>
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!IsSupported)
            return base.CreateNativeControlCore(parent);

        _window = new Win32ViewportWindow(parent.Handle) { Host = _host };
        _window.ShellChord += chord => ShellChord?.Invoke(chord);
        _window.ContextMenuRequested += (x, y) => ContextMenuRequested?.Invoke(x, y);

        // After the handle exists and before anything can render: a host that
        // started the engine any earlier would be initialising a renderer
        // against a window that is not there yet.
        SurfaceCreated?.Invoke(_window);

        return new PlatformHandle(_window.NativeHandle, "HWND");
    }

    /// <inheritdoc/>
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_window is null)
        {
            base.DestroyNativeControlCore(control);
            return;
        }

        // The engine has to be off this surface before the window goes, not
        // after: the render thread owns the swap chain that presents to it, and
        // presenting into a destroyed HWND is a driver-level failure rather
        // than an exception anything here could catch.
        SurfaceDestroying?.Invoke();

        _window.Dispose();
        _window = null;
    }
}
