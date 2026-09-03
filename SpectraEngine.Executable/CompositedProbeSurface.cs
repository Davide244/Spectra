using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using System;

namespace SpectraEngine.Executable;

/// <summary>
/// A surface with no window, no handle and no GL context: exactly what an
/// embedded host that composites the engine's output offers, and the only kind
/// that has a shared target on it.
/// </summary>
/// <remarks>
/// <para>
/// Shared by every probe that measures the composited route, because a second
/// spelling of "no window, this size" is a second place for the surface KIND to
/// be got wrong - and the kind is what decides whether the renderer builds a
/// swap chain or a shared target at all, so getting it wrong measures the
/// window path while claiming to measure the other one.
/// </para>
/// <para>
/// The size never changes, so <see cref="Resized"/> is a real event that simply
/// never fires. Removing the member is not an option and neither is throwing
/// from it: <c>Engine.AttachSurface</c> subscribes and unsubscribes on every
/// run.
/// </para>
/// </remarks>
internal sealed class CompositedProbeSurface(int width, int height) : IRenderSurface
{
    public RenderSurfaceKind Kind => RenderSurfaceKind.Composited;

    public nint NativeHandle => 0;

    public IGLContext? GLContext => null;

    public Vector2D<int> PixelSize => new(width, height);

    public event Action<Vector2D<int>>? Resized
    {
        add { }
        remove { }
    }
}
