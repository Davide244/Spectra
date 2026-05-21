using System;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// A swappable rendering strategy for the D3D11 backend. Mirrors
/// <c>IOpenGLRenderPipeline</c>: pipelines own per-frame work (clear, scene
/// walk, post-passes); the renderer owns GPU resources and pipeline lifecycle.
/// </summary>
public interface ID3D11RenderPipeline : IDisposable
{
    string Name { get; }
    void Initialize(D3D11Renderer renderer);
    void Execute(in D3D11RenderContext context);
}
