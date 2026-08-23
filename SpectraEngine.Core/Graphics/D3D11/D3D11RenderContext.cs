using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// Per-frame inputs handed to an <see cref="ID3D11RenderPipeline"/>. The
/// renderer owns the device and context; the pipeline borrows them for the
/// duration of <see cref="ID3D11RenderPipeline.Execute"/>. Deliberately
/// excludes the window: GLFW window queries are main-thread-only, so pipelines
/// read sizes from the renderer's <see cref="Graphics.Renderer.PassSize"/>
/// instead. It also no longer carries the back-buffer views: where a pipeline's
/// output goes is <see cref="Graphics.Renderer.BeginPass"/>'s business, not a
/// per-frame input.
/// </summary>
public readonly unsafe struct D3D11RenderContext
{
    public required D3D11Renderer Renderer { get; init; }
    public required ComPtr<ID3D11Device> Device { get; init; }
    public required ComPtr<ID3D11DeviceContext> Context { get; init; }
    public required Scene.Scene? Scene { get; init; }

    /// <summary>
    /// The engine-built, frustum-culled draw list for this frame; pipelines
    /// iterate it instead of walking the scene graph themselves.
    /// </summary>
    public required RenderView View { get; init; }

    public required double DeltaTime { get; init; }
}
