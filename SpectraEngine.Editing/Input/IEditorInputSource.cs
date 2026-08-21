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
    EditorInputFrame CaptureFrame(float deltaTime);
}
