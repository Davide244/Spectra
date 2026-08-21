using Silk.NET.Maths;
using Silk.NET.Windowing;
using System;

namespace SpectraEngine.Core.Windowing;

/// <summary>
/// Adapts a Silk.NET <see cref="IWindow"/> to <see cref="IWindowModeTarget"/>.
/// The one place in the window-mode path that names a windowing backend, and
/// therefore the one piece a future non-Silk host (the Uno editor) rewrites.
/// </summary>
/// <remarks>
/// <b>Window thread only.</b> Every property here forwards straight to GLFW,
/// which answers on the thread that created the window and nowhere else — the
/// latch is what keeps other threads out.
/// </remarks>
internal sealed class SilkWindowModeTarget : IWindowModeTarget
{
    private readonly IWindow _window;

    internal SilkWindowModeTarget(IWindow window)
    {
        _window = window;
    }

    /// <inheritdoc/>
    public WindowRect Bounds
    {
        get
        {
            Vector2D<int> position = _window.Position;
            Vector2D<int> size = _window.Size;
            return new WindowRect(position.X, position.Y, size.X, size.Y);
        }
        set
        {
            _window.Position = new Vector2D<int>(value.X, value.Y);
            _window.Size = new Vector2D<int>(value.Width, value.Height);
        }
    }

    /// <inheritdoc/>
    public bool Decorated
    {
        // Silk's WindowBorder folds "has a frame" and "can be dragged to
        // resize" into one enum. Resizable is the engine's windowed default,
        // so that is what re-decorating restores to.
        get => _window.WindowBorder != WindowBorder.Hidden;
        set => _window.WindowBorder = value ? WindowBorder.Resizable : WindowBorder.Hidden;
    }

    /// <inheritdoc/>
    public bool IsMaximized
    {
        get => _window.WindowState == WindowState.Maximized;
        set => _window.WindowState = value ? WindowState.Maximized : WindowState.Normal;
    }

    /// <inheritdoc/>
    public bool TryGetDisplayBounds(out WindowRect bounds)
    {
        if (_window.Monitor is { } monitor)
        {
            // Silk's GLFW monitor reports Bounds from glfwGetMonitorWorkarea —
            // the desktop MINUS the taskbar — so filling it would leave the
            // taskbar sitting on top of a "fullscreen" game. The video mode
            // carries the display's real resolution, so the larger of the two
            // per axis is what actually covers the screen; the max also means a
            // backend that reports no video mode degrades to the work area
            // rather than to nothing.
            Rectangle<int> area = monitor.Bounds;
            Vector2D<int> resolution = monitor.VideoMode.Resolution ?? area.Size;
            bounds = new WindowRect(
                area.Origin.X,
                area.Origin.Y,
                Math.Max(area.Size.X, resolution.X),
                Math.Max(area.Size.Y, resolution.Y));
            return true;
        }

        bounds = default;
        return false;
    }
}
