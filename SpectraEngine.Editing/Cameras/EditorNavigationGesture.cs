namespace SpectraEngine.Editing.Cameras;

/// <summary>
/// Which navigation gesture currently owns the pointer, if any. Exactly one is
/// live at a time — see
/// <see cref="EditorCameraController.Update(in Input.EditorInputFrame)"/> for the
/// precedence.
/// </summary>
public enum EditorNavigationGesture
{
    /// <summary>Nothing is dragging; the camera is only settling, if that.</summary>
    None,

    /// <summary>
    /// Freelook: the camera turns in place about its own position and the
    /// movement keys fly it through the world. The primary mode, and the one
    /// that captures the cursor.
    /// </summary>
    FreeLook,

    /// <summary>
    /// Orbit: the camera swings around its focus point at a fixed distance. The
    /// modifier gesture, reached for after framing something.
    /// </summary>
    Orbit,

    /// <summary>Pan: the focus slides in the camera's own view plane.</summary>
    Pan,
}
