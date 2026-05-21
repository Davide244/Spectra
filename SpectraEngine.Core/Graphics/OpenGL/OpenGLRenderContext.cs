using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// Per-frame inputs handed to an <see cref="IOpenGLRenderPipeline"/>.
/// Aggregates the things the pipeline routinely needs without exposing the
/// renderer's full surface.
/// </summary>
public readonly struct OpenGLRenderContext
{
    public required OpenGLRenderer Renderer { get; init; }
    public required GL Gl { get; init; }
    public required IWindow Window { get; init; }
    public required Scene.Scene? Scene { get; init; }
    public required double DeltaTime { get; init; }
}
