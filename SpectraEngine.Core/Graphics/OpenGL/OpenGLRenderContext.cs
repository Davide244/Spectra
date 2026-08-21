using Silk.NET.OpenGL;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// Per-frame inputs handed to an <see cref="IOpenGLRenderPipeline"/>.
/// Aggregates the things the pipeline routinely needs without exposing the
/// renderer's full surface. Deliberately excludes the window: GLFW window
/// queries are main-thread-only, so pipelines read sizes from the renderer's
/// <see cref="Renderer.FramebufferSize"/> latch instead.
/// </summary>
public readonly struct OpenGLRenderContext
{
    public required OpenGLRenderer Renderer { get; init; }
    public required GL Gl { get; init; }
    public required Scene.Scene? Scene { get; init; }

    /// <summary>
    /// The engine-built, frustum-culled draw list for this frame; pipelines
    /// iterate it instead of walking the scene graph themselves.
    /// </summary>
    public required RenderView View { get; init; }

    public required double DeltaTime { get; init; }
}
