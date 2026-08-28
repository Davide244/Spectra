using System.Numerics;

namespace SpectraEngine.Editing.Hosting;

/// <summary>
/// Something a host hangs off the editor's frame: it runs once per
/// <see cref="SceneEditorHost.Update"/>, after the real input frame has been
/// consumed, and may drive the scene itself.
/// </summary>
/// <remarks>
/// <b>This exists so instrumentation does not have to live inside the
/// editor.</b> The demo's editing self-test synthesises a whole
/// pick/grab/drag/commit/undo/redo gesture every five seconds, which is a
/// property of that HOST rather than of editing, and inlining it made the
/// editor unusable by any other shell without dragging the test along.
/// <para>
/// <b>It is handed the frame's idle flag, and that flag is what makes it
/// safe.</b> A probe that drove the scene while a real gesture was open would
/// be a second hand on the same mouse; passing "the viewport is doing nothing
/// this frame" lets it stand down instead of fighting.
/// </para>
/// </remarks>
public interface IEditorFrameProbe
{
    /// <summary>
    /// Runs one frame of whatever the host attached. Called on the render
    /// thread, inside the editor's own update.
    /// </summary>
    /// <param name="deltaTime">Seconds since the previous frame.</param>
    /// <param name="viewportSize">The viewport in pixels, for synthesised picks.</param>
    /// <param name="viewportIdle">
    /// Whether the real viewport is quiet this frame: no gesture open, nothing
    /// grabbed, no marquee. A probe that touches the scene must do nothing when
    /// this is false.
    /// </param>
    void Update(double deltaTime, Vector2 viewportSize, bool viewportIdle);
}
