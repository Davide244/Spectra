using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The standalone path's <see cref="IRenderSurface"/>: a Silk.NET window,
/// presented as nothing more than a handle, a context and a size.
/// </summary>
/// <remarks>
/// <b>Deliberately the thinnest possible adapter.</b> Every property forwards,
/// nothing is cached, and the resize event is passed straight through, so the
/// standalone path behaves exactly as it did when the renderer took an
/// <c>IWindow</c> directly. That equivalence is the point of introducing the
/// seam this way: the refactor can be verified by "nothing changed" rather than
/// by reasoning about what might have.
/// <para>
/// The window's title, cursor, fullscreen state and event pump stay with
/// <c>Engine</c>, which is what actually owns them. None of them appears here,
/// because none of them is something an embedded host would supply.
/// </para>
/// </remarks>
public sealed class WindowRenderSurface : IRenderSurface
{
    private readonly IWindow _window;

    /// <summary>Wraps a window the engine created.</summary>
    public WindowRenderSurface(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _window.FramebufferResize += OnFramebufferResize;
    }

    /// <inheritdoc/>
    public event Action<Vector2D<int>>? Resized;

    /// <inheritdoc/>
    /// <remarks>
    /// Reports <see cref="RenderSurfaceKind.None"/> rather than guessing when
    /// the window exposes no native handle the engine knows: an OpenGL window on
    /// any platform legitimately has none to offer, and the GL backend does not
    /// ask.
    /// </remarks>
    public RenderSurfaceKind Kind
    {
        get
        {
            if (_window.Native is not { } native)
                return RenderSurfaceKind.None;

            if (native.Win32 is not null) return RenderSurfaceKind.Win32;
            if (native.X11 is not null) return RenderSurfaceKind.X11;
            if (native.Wayland is not null) return RenderSurfaceKind.Wayland;
            return RenderSurfaceKind.None;
        }
    }

    /// <inheritdoc/>
    public nint NativeHandle
    {
        get
        {
            if (_window.Native is not { } native)
                return 0;

            if (native.Win32 is { } win32) return win32.Hwnd;
            if (native.X11 is { } x11) return (nint)x11.Window;
            if (native.Wayland is { } wayland) return wayland.Surface;
            return 0;
        }
    }

    /// <inheritdoc/>
    public IGLContext? GLContext => _window.GLContext;

    /// <inheritdoc/>
    public Vector2D<int> PixelSize => _window.FramebufferSize;

    /// <summary>
    /// The window behind this surface, for the engine code that legitimately
    /// owns it: the title, the cursor, the window-mode latch and the event pump.
    /// Nothing in <c>Graphics/</c> reads it.
    /// </summary>
    public IWindow Window => _window;

    private void OnFramebufferResize(Vector2D<int> size) => Resized?.Invoke(size);
}
