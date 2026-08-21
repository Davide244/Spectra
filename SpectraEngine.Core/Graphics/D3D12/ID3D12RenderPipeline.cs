using Silk.NET.Direct3D12;
using System;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// A swappable D3D12 rendering strategy (forward, wireframe, ...). Executes
/// once per frame against an open command list owned by the renderer; the
/// renderer handles begin/end barriers, submission, and presentation.
/// </summary>
public interface ID3D12RenderPipeline : IDisposable
{
    string Name { get; }
    void Initialize(D3D12Renderer renderer);
    void Execute(in D3D12RenderContext context);
}

/// <summary>
/// Everything a D3D12 pipeline needs for one frame. Deliberately excludes the
/// window: GLFW window queries are main-thread-only, so pipelines read sizes
/// from the renderer's <see cref="Graphics.Renderer.FramebufferSize"/> latch
/// instead.
/// </summary>
public readonly struct D3D12RenderContext
{
    public required D3D12Renderer Renderer { get; init; }
    public required CpuDescriptorHandle BackBufferRtv { get; init; }
    public required CpuDescriptorHandle DepthView { get; init; }
    public required Scene.Scene? Scene { get; init; }

    /// <summary>
    /// The engine-built, frustum-culled draw list for this frame; pipelines
    /// iterate it instead of walking the scene graph themselves.
    /// </summary>
    public required RenderView View { get; init; }

    public required double DeltaTime { get; init; }
}
