namespace SpectraEngine.Core.Windowing;

/// <summary>
/// How the engine's window covers the display, in the engine's own
/// backend-neutral vocabulary — the same reason
/// <see cref="Input.CursorMode"/> exists rather than the windowing backend's
/// enum being passed around.
/// </summary>
/// <remarks>
/// There is deliberately no exclusive-fullscreen member. With a flip-model swap
/// chain, borderless windowed is visually and performance-wise equivalent on
/// modern Windows, and it avoids the entire family of problems exclusive mode
/// brings: display mode switches, device-lost transitions on alt-tab, the
/// <c>SetFullscreenState(FALSE)</c>-before-release shutdown requirement, and —
/// the bug this type was written for — DXGI performing its own fullscreen
/// transition on the window thread while the render thread is presenting.
/// See <see cref="IWindowModeLatch"/>.
/// </remarks>
public enum WindowMode
{
    /// <summary>An ordinary decorated, movable, resizable window. The resting state.</summary>
    Windowed,

    /// <summary>
    /// Undecorated and sized to fill the display the window is on. The engine
    /// drives this itself as plain window-state work, which is why every
    /// backend — OpenGL included — gets it without a line of backend code.
    /// </summary>
    BorderlessFullscreen,
}
