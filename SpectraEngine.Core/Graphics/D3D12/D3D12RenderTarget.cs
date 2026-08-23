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
    private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
    private ComPtr<ID3D12DescriptorHeap> _dsvHeap;
    private ComPtr<ID3D12Resource> _depth;
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

    /// <summary>The DXGI format of the colour attachment: what a PSO drawing here must be built against.</summary>
    internal Format ColorFormat => _color.DxgiFormat;

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

        ComOwnership.Release(ref _depth);
        _color.ReplaceStorage(_renderer, width, height);
        ColorState = ResourceStates.PixelShaderResource;
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

        if (Desc.Depth)
        {
            var depthDesc = new ResourceDesc
            {
                Dimension = ResourceDimension.Texture2D,
                Alignment = 0,
                Width = (ulong)width,
                Height = (uint)height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = D3D12Renderer.DepthFormat,
                SampleDesc = new SampleDesc(1, 0),
                Layout = TextureLayout.LayoutUnknown,
                Flags = ResourceFlags.AllowDepthStencil,
            };
            var heapProps = new HeapProperties { Type = HeapType.Default };
            var clearValue = new ClearValue { Format = D3D12Renderer.DepthFormat };
            clearValue.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 };

            ID3D12Resource* depth = null;
            Guid guid = ID3D12Resource.Guid;
            SilkMarshal.ThrowHResult(_renderer.DevicePtr->CreateCommittedResource(
                &heapProps, HeapFlags.None, &depthDesc, ResourceStates.DepthWrite, &clearValue,
                &guid, (void**)&depth));
            _depth = ComOwnership.Own(depth);

            _renderer.DevicePtr->CreateDepthStencilView(depth, null, Dsv);
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

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ComOwnership.Release(ref _depth);
        ComOwnership.Release(ref _dsvHeap);
        ComOwnership.Release(ref _rtvHeap);
        _color.Dispose();
    }
}
