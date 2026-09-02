using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// What kind of native surface a renderer is being pointed at, and therefore
/// which platform's handle <see cref="IRenderSurface.NativeHandle"/> carries.
/// </summary>
/// <remarks>
/// <b>A bare <c>nint</c> is not enough to hand to a graphics API.</b> D3D needs
/// to know it has an HWND and not an X11 window id, and today it learns that
/// from <c>window.Native.Win32?.Hwnd</c> returning null. Losing that check
/// behind an untyped handle would turn "wrong platform" from a clear refusal
/// into whatever a driver does with a nonsense pointer.
/// </remarks>
public enum RenderSurfaceKind
{
    /// <summary>No native handle at all: an OpenGL surface, or a headless one.</summary>
    None,

    /// <summary>A Win32 <c>HWND</c>. The only kind the D3D backends accept.</summary>
    Win32,

    /// <summary>An X11 window id.</summary>
    X11,

    /// <summary>A Wayland surface pointer.</summary>
    Wayland,

    /// <summary>
    /// No native surface at all: the renderer draws into a shared target that
    /// somebody else presents. There is no HWND and there is no swap chain, so
    /// <see cref="IRenderSurface.NativeHandle"/> is zero and nothing about
    /// presentation belongs to the engine.
    /// </summary>
    /// <remarks>
    /// <b>Appended, and appended for a reason.</b> These values are compared and
    /// stored, so renumbering them silently re-labels every surface a backend
    /// has already refused or accepted. New kinds go on the end.
    /// </remarks>
    Composited,
}

/// <summary>
/// The thing a renderer draws into and presents to, with no assumption that the
/// engine created it or owns it.
/// </summary>
/// <remarks>
/// <b>This exists so the engine can be embedded.</b> The renderer used to take a
/// Silk.NET <c>IWindow</c>, which carries not just a surface but a title, a
/// cursor, an event pump and a lifetime. An editor shell owns all of those; what
/// it can hand the engine is a native child window and, for OpenGL, a context.
/// Narrowing the renderer's dependency to exactly those two things is what makes
/// hosting a matter of writing one small adapter rather than of touching a
/// backend.
/// <para>
/// <b>It does not remove Silk.NET from Core, and is not trying to.</b> The
/// standalone path is a Silk window and stays one; <c>IGLContext</c> and
/// <c>Vector2D</c> are the vocabulary the GL backend already speaks. The
/// dependency being broken is on window OWNERSHIP, not on a library.
/// </para>
/// <para>
/// <b>A surface offers a native handle, a GL context, or both.</b> The D3D
/// backends need <see cref="RenderSurfaceKind.Win32"/>; the OpenGL backend needs
/// <see cref="GLContext"/>. Each refuses a surface that cannot serve it, and
/// says which one it got.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="PixelSize"/> and <see cref="Resized"/> belong to
/// whichever thread owns the surface (the OS-event thread for a window), and the
/// render thread must not read them. It reads
/// <c>Renderer.GetFramebufferSize</c>'s latch instead, which is fed from
/// <see cref="Resized"/> and is the only size the render side is allowed to
/// trust. <see cref="GLContext"/> is the exception: making a context current is
/// exactly the render thread's job.
/// </para>
/// </remarks>
public interface IRenderSurface
{
    /// <summary>Which platform's handle <see cref="NativeHandle"/> is, if any.</summary>
    RenderSurfaceKind Kind { get; }

    /// <summary>
    /// The native surface handle, or zero when <see cref="Kind"/> is
    /// <see cref="RenderSurfaceKind.None"/>.
    /// </summary>
    nint NativeHandle { get; }

    /// <summary>
    /// The OpenGL context bound to this surface, or null when there is none.
    /// The GL backend creates its API against this and makes it current on the
    /// render thread; every other backend ignores it.
    /// </summary>
    IGLContext? GLContext { get; }

    /// <summary>
    /// The surface's current size in pixels. Read on the surface's own thread;
    /// see the threading note on the interface.
    /// </summary>
    Vector2D<int> PixelSize { get; }

    /// <summary>
    /// Raised when the surface's pixel size changes, on the surface's own
    /// thread. The engine wires this straight into the renderer's size latch,
    /// which is how a resize reaches the render thread safely.
    /// </summary>
    event Action<Vector2D<int>>? Resized;
}
