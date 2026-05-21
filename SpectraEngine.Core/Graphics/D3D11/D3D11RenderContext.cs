using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Windowing;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// Per-frame inputs handed to an <see cref="ID3D11RenderPipeline"/>. The
/// renderer owns the device and context; the pipeline borrows them for the
/// duration of <see cref="ID3D11RenderPipeline.Execute"/>.
/// </summary>
public readonly unsafe struct D3D11RenderContext
{
    public required D3D11Renderer Renderer { get; init; }
    public required ComPtr<ID3D11Device> Device { get; init; }
    public required ComPtr<ID3D11DeviceContext> Context { get; init; }
    public required ComPtr<ID3D11RenderTargetView> BackBufferRtv { get; init; }
    public required ComPtr<ID3D11DepthStencilView> DepthView { get; init; }
    public required IWindow Window { get; init; }
    public required Scene.Scene? Scene { get; init; }
    public required double DeltaTime { get; init; }
}
