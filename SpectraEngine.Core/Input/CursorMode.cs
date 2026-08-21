namespace SpectraEngine.Core.Input;

/// <summary>
/// How the mouse cursor is presented over the window, in the engine's own
/// backend-neutral vocabulary — the same reason
/// <see cref="PointerButtons"/> and <see cref="KeyModifiers"/> exist rather than
/// the windowing backend's enums being passed around.
/// </summary>
/// <remarks>
/// Layers above the backend seam (the editing assembly, a future Uno host)
/// request a mode through <see cref="ICursorLock"/> and never name the backend's
/// own cursor enum. The mapping onto GLFW is
/// <see cref="Normal"/> → <c>Normal</c>, <see cref="Hidden"/> → <c>Hidden</c>,
/// <see cref="Locked"/> → <c>Disabled</c>.
/// </remarks>
public enum CursorMode
{
    /// <summary>Visible, free to leave the window. The resting state.</summary>
    Normal,

    /// <summary>
    /// Invisible but still an ordinary cursor: absolute position stays
    /// meaningful and it can still leave the window.
    /// </summary>
    Hidden,

    /// <summary>
    /// Invisible and captured: the OS stops reporting a real position and only
    /// relative motion remains meaningful. What a first-person freelook needs,
    /// and the reason
    /// <see cref="SpectraEngine.Core.Input.InputManager.MousePosition"/> freezes
    /// while it is in effect — see <see cref="ICursorLock"/>.
    /// </summary>
    Locked,
}
