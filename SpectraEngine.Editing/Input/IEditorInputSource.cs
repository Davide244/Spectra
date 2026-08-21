namespace SpectraEngine.Editing.Input;

/// <summary>
/// Produces one <see cref="EditorInputFrame"/> per frame for the editing layer.
/// This is the swappable half of the input seam: the Silk-backed
/// <see cref="EngineEditorInputSource"/> is today's implementation, an Uno/WinUI
/// host would supply its own, and no editor tool above the seam changes either
/// way.
/// </summary>
/// <remarks>
/// <b>Threading:</b> called on the render thread, once per frame, before any
/// tool consumes the frame. Implementations must be safe to call from that
/// thread and must not allocate in steady state.
/// </remarks>
public interface IEditorInputSource
{
    /// <summary>
    /// Snapshots the current input state for a frame of
    /// <paramref name="deltaTime"/> seconds.
    /// </summary>
    /// <param name="deltaTime">The frame's duration in seconds.</param>
    /// <param name="navigation">
    /// The fly-camera axis the host resolved from its own keymap this frame.
    /// It arrives as a parameter rather than being read here because the
    /// <em>keyboard</em> is the host's business — the same reason
    /// <c>GizmoTool.Update</c> takes its cancel flag as a parameter. A host with
    /// no movement bindings omits it.
    /// </param>
    EditorInputFrame CaptureFrame(float deltaTime, EditorNavigationInput navigation = default);
}
