using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using System;
using System.Numerics;

namespace SpectraEngine.Editing.Input;

/// <summary>
/// The engine-hosted <see cref="IEditorInputSource"/>: builds each frame's
/// <see cref="EditorInputFrame"/> from the running engine's
/// <see cref="InputManager"/> (cursor, buttons, edges, modifiers, wheel) and the
/// renderer's latched framebuffer size (viewport).
/// </summary>
/// <remarks>
/// This adapter is the ONLY place in the editing assembly that knows where
/// input comes from, and even here it reads the backend-neutral half of the
/// engine's API — <see cref="InputManager.PointerButtonsDown"/> and friends,
/// <see cref="Renderer.GetFramebufferSize"/> — never a Silk.NET type. Re-hosting
/// the viewport means writing a sibling of this class; nothing else moves.
/// <para>
/// The framebuffer latch is used rather than <c>IWindow</c> because GLFW only
/// answers window-size queries on the thread that created the window, and this
/// runs on the render thread. For a docked editor whose 3D viewport is a
/// sub-rect of the window, use
/// <see cref="CaptureFrame(float, Vector2, Vector2)"/> instead.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, and it expects
/// <see cref="InputManager.Update"/> to have already latched this frame's edges
/// and deltas (the engine loop does that first thing each tick). Capturing a
/// frame allocates nothing.
/// </para>
/// </remarks>
public sealed class EngineEditorInputSource : IEditorInputSource
{
    private readonly InputManager _input;
    private readonly Renderer _renderer;

    /// <summary>
    /// Creates an input source over a live engine's input manager and renderer.
    /// </summary>
    public EngineEditorInputSource(InputManager input, Renderer renderer)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(renderer);
        _input = input;
        _renderer = renderer;
    }

    /// <summary>
    /// Snapshots input with the whole framebuffer as the viewport — the
    /// full-window editor case.
    /// </summary>
    public EditorInputFrame CaptureFrame(float deltaTime)
    {
        _renderer.GetFramebufferSize(out int width, out int height);
        return CaptureFrame(deltaTime, Vector2.Zero, new Vector2(width, height));
    }

    /// <summary>
    /// Snapshots input for a viewport that occupies only part of the window:
    /// <paramref name="viewportOrigin"/> is the viewport's top-left corner in
    /// window client pixels, and the reported cursor position is rebased to be
    /// relative to it (so it stays the coordinate
    /// <c>Camera.ScreenPointToRay</c> wants). The cursor may land outside the
    /// rect — see <see cref="EditorInputFrame.IsCursorInsideViewport"/>.
    /// </summary>
    public EditorInputFrame CaptureFrame(float deltaTime, Vector2 viewportOrigin, Vector2 viewportSize) =>
        new(
            _input.MousePosition - viewportOrigin,
            viewportSize,
            _input.PointerButtonsDown,
            _input.PointerButtonsPressed,
            _input.PointerButtonsReleased,
            _input.Modifiers,
            _input.ScrollDelta,
            deltaTime);
}
