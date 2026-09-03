using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using System;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// A surface with no window behind it: the engine renders into a shared target
/// and somebody else puts it on screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole of what a composited viewport hands the engine is a size.</b>
/// There is no HWND, no swap chain and no present - the renderer resolves the
/// frame into a keyed-mutex texture instead, and what appears on screen is the
/// compositor's business. That is why <see cref="IRenderSurface"/> gained no
/// member for any of it: the shared handle is asked for through the renderer
/// and travels out on the frame snapshot, so the surface stays exactly the five
/// things it always was and the convention test's stub still compiles.
/// </para>
/// <para>
/// <b>Never zero, ever.</b> A collapsed pane or a minimised window would
/// otherwise hand the renderer a degenerate size, which every backend refuses
/// in its own way and none of them refuses quietly. The same floor the native
/// child's client size carries, for the same reason.
/// </para>
/// <para>
/// <b>Threading:</b> written from the UI thread, read by the engine at attach
/// and then through the renderer's own size latch, which is what
/// <see cref="Resized"/> feeds.
/// </para>
/// </remarks>
internal sealed class CompositedRenderSurface : IRenderSurface
{
    private Vector2D<int> _size = new(1, 1);

    /// <inheritdoc/>
    public RenderSurfaceKind Kind => RenderSurfaceKind.Composited;

    /// <inheritdoc/>
    public nint NativeHandle => 0;

    /// <inheritdoc/>
    /// <remarks>
    /// Always null, and composited OpenGL is refused by name rather than
    /// attempted: an embedded GL surface needs its own context and a
    /// proc-address loader, and there is nothing here for one to be current
    /// against.
    /// </remarks>
    public IGLContext? GLContext => null;

    /// <inheritdoc/>
    public Vector2D<int> PixelSize => _size;

    /// <inheritdoc/>
    public event Action<Vector2D<int>>? Resized;

    /// <summary>
    /// Reports the viewport's size in real pixels. Raises
    /// <see cref="Resized"/> only when it actually moved, because the renderer
    /// rebuilds its shared target under a new generation for every change it is
    /// told about.
    /// </summary>
    internal void SetPixelSize(int width, int height)
    {
        var size = new Vector2D<int>(Math.Max(1, width), Math.Max(1, height));
        if (size == _size)
            return;

        _size = size;
        Resized?.Invoke(size);
    }
}
