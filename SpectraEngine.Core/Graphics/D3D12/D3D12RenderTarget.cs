using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// A committed texture with its own RTV, an optional depth buffer with its own
/// DSV, and an SRV so anything can sample the result.
/// </summary>
/// <remarks>
/// <para>
/// <b>The resource state is tracked here, on the target, not inferred.</b> D3D12
/// has no runtime that notices a resource being read while it is a render
/// target: a missed barrier is undefined data that the debug layer reports and a
/// shipping build silently renders. So a target knows which state it is in and
/// only emits a barrier when the state actually changes, which also makes a
/// redundant transition (the debug layer's other complaint) impossible.
/// </para>
/// <para>
/// <b>The SRV lives in a heap slot this target owns for its whole life.</b> A
/// resize replaces the underlying resource and writes a fresh SRV into the same
/// slot, so <c>SrvCpu</c> stays valid and every descriptor table already copied
/// from it keeps working. Handing out a new handle instead would strand every
/// material that sampled the target.
/// </para>
/// </remarks>
internal sealed unsafe class D3D12RenderTarget : RenderTarget
{
    private readonly D3D12Renderer _renderer;
    private readonly D3D12Texture _color;
    private readonly D3D12Texture? _depth;
    private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
    private ComPtr<ID3D12DescriptorHeap> _dsvHeap;
    private bool _disposed;

    internal CpuDescriptorHandle Rtv { get; private set; }
    internal CpuDescriptorHandle Dsv { get; private set; }
    internal bool HasDepth => Desc.Depth;

    /// <summary>
    /// What the colour attachment's state is right now, so a barrier is emitted
    /// only when it genuinely changes.
    /// </summary>
    internal ResourceStates ColorState { get; private set; } = ResourceStates.PixelShaderResource;

    internal D3D12RenderTarget(D3D12Renderer renderer, in RenderTargetDesc desc)
    {
        desc.Validate();

        _renderer = renderer;
        Desc = desc;

        _color = D3D12Texture.CreateRenderTargetTexture(
            renderer, desc.Width, desc.Height, desc.ColorFormat, desc.ColorSpace, desc.Filter, desc.Wrap);

        if (desc.Depth)
            _depth = D3D12Texture.CreateDepthTexture(renderer, desc.Width, desc.Height);

        _rtvHeap = renderer.CreateDescriptorHeap(DescriptorHeapType.Rtv, 1, shaderVisible: false);
        Rtv = ((ID3D12DescriptorHeap*)_rtvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();

        if (desc.Depth)
        {
            _dsvHeap = renderer.CreateDescriptorHeap(DescriptorHeapType.Dsv, 1, shaderVisible: false);
            Dsv = ((ID3D12DescriptorHeap*)_dsvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();
        }

        Allocate(desc.Width, desc.Height);
    }

    public override Texture ColorTexture => _color;

    public override Texture? DepthTexture => _depth;

    /// <summary>
    /// What state the depth attachment is in, tracked exactly like the colour
    /// one so a barrier is only emitted on a real change.
    /// </summary>
    internal ResourceStates DepthState { get; private set; } = ResourceStates.DepthWrite;

    /// <summary>The DXGI format of the colour attachment: what a PSO drawing here must be built against.</summary>
    internal Format ColorFormat => _color.DxgiFormat;

    /// <summary>
    /// The format of the depth-stencil VIEW, which is what a pipeline state must
    /// name.
    /// </summary>
    /// <remarks>
    /// Not the resource format, which is typeless, and not the back buffer's,
    /// which is D24_UNORM_S8_UINT. An offscreen target's depth is D32_FLOAT so
    /// it can also be sampled, and a pipeline compiled against the wrong one is
    /// rejected at every draw.
    /// </remarks>
    internal Format DepthViewFormat =>
        HasDepth ? Format.FormatD32Float : Format.FormatUnknown;

    public override void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == Width && height == Height) return;
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"Render target size must be positive; got {width}x{height}.");

        // The GPU must be done with the old resource before it is released. The
        // renderer fully syncs on the frame fence at Present, so any call
        // reaching here between frames is already safe; this makes that a
        // requirement rather than a coincidence.
        _renderer.WaitForGpu();

        _color.ReplaceStorage(_renderer, width, height);
        _depth?.ReplaceDepthStorage(_renderer, width, height);
        ColorState = ResourceStates.PixelShaderResource;
        DepthState = ResourceStates.DepthWrite;
        Allocate(width, height);
    }

    private void Allocate(int width, int height)
    {
        var rtvDesc = new RenderTargetViewDesc
        {
            Format = _color.DxgiFormat,
            ViewDimension = RtvDimension.Texture2D,
        };
        rtvDesc.Anonymous.Texture2D = new Tex2DRtv { MipSlice = 0, PlaneSlice = 0 };
        _renderer.DevicePtr->CreateRenderTargetView(_color.Resource, &rtvDesc, Rtv);

        if (_depth is not null)
        {
            // An explicit desc, because the resource is typeless and a null one
            // would ask the runtime to use R32_TYPELESS as a depth format.
            var dsvDesc = new DepthStencilViewDesc
            {
                Format = Format.FormatD32Float,
                ViewDimension = DsvDimension.Texture2D,
            };
            dsvDesc.Anonymous.Texture2D = new Tex2DDsv { MipSlice = 0 };

            _renderer.DevicePtr->CreateDepthStencilView(_depth.Resource, &dsvDesc, Dsv);
        }

        Width = width;
        Height = height;
    }

    /// <summary>
    /// Moves the colour attachment to <paramref name="state"/>, emitting a
    /// barrier only if it is not already there.
    /// </summary>
    internal void TransitionColor(ID3D12GraphicsCommandList* list, ResourceStates state)
    {
        if (ColorState == state) return;

        D3D12Renderer.Transition(list, _color.Resource, ColorState, state);
        ColorState = state;
    }

    /// <summary>
    /// Moves the depth attachment between being written and being sampled.
    /// </summary>
    /// <remarks>
    /// Unlike colour, depth's resting state is <c>DepthWrite</c>: it is written
    /// by every geometry pass and read only by whatever reconstructs position
    /// from it, so leaving it readable would mean a barrier on every frame
    /// rather than only on the frames something samples it.
    /// </remarks>
    internal void TransitionDepth(ID3D12GraphicsCommandList* list, ResourceStates state)
    {
        if (_depth is null || DepthState == state) return;

        D3D12Renderer.Transition(list, _depth.Resource, DepthState, state);
        DepthState = state;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ComOwnership.Release(ref _dsvHeap);
        ComOwnership.Release(ref _rtvHeap);
        _color.Dispose();
        _depth?.Dispose();
    }
}
