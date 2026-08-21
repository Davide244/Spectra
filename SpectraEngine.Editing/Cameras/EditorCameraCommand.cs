namespace SpectraEngine.Editing.Cameras;

/// <summary>
/// One navigation verb the viewport camera understands, resolved from the
/// host's keymap and handed to <see cref="EditorCameraController.Apply"/>.
/// </summary>
/// <remarks>
/// Pointer gestures (orbit, pan, wheel) are not verbs: they arrive
/// continuously through <see cref="Input.EditorInputFrame"/>. Only the discrete,
/// keyboard-triggered actions live here, for the same reason
/// <c>GizmoCommand</c> exists — so this assembly can carry a default keymap
/// without naming a windowing backend's key enum.
/// </remarks>
public enum EditorCameraCommand
{
    /// <summary>Fit the current selection in view. The F key, everywhere.</summary>
    FrameSelection,

    /// <summary>Fit every spatial node in the scene in view.</summary>
    FrameAll,
}
