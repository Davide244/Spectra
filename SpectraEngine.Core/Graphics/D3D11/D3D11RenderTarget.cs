using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// A texture bound as both a render target and a shader resource, with an
/// optional depth-stencil buffer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard here is that the colour attachment is bound twice.</b> A
/// material that sampled this target last frame left its SRV in a pixel-shader
/// slot; binding the same resource as a render target while that SRV is live is
/// a read-write conflict. D3D11 resolves it by silently unbinding the SRV and
/// logging a debug-layer warning, so the picture is usually right and the
/// warning is usually ignored. <see cref="D3D11Renderer"/> nulls the slots
/// explicitly at <c>BeginPass</c> instead, because "usually" is not a contract
/// and the same mistake on D3D12 is a corrupt read rather than a warning.
/// </para>
/// </remarks>
internal sealed unsafe class D3D11RenderTarget : RenderTarget
{
    private readonly ComPtr<ID3D11Device> _device;
    private readonly D3D11Texture? _color;
    private readonly D3D11Texture? _depth;
    private ComPtr<ID3D11RenderTargetView> _rtv;
    private ComPtr<ID3D11DepthStencilView> _dsv;
    private bool _disposed;

    internal ID3D11RenderTargetView* Rtv => (ID3D11RenderTargetView*)_rtv.Handle;
    internal ID3D11DepthStencilView* Dsv => (ID3D11DepthStencilView*)_dsv.Handle;

    internal D3D11RenderTarget(ComPtr<ID3D11Device> device, in RenderTargetDesc desc)
    {
        desc.Validate();

        _device = device;
        Desc = desc;

        if (desc.Color)
        {
            _color = D3D11Texture.CreateRenderTargetTexture(
                device, desc.Width, desc.Height, desc.ColorFormat, desc.ColorSpace, desc.Filter, desc.Wrap,
                desc.Sharing);
        }

        if (desc.Depth)
            _depth = D3D11Texture.CreateDepthTexture(device, desc.Width, desc.Height);

        Allocate(desc.Width, desc.Height);
    }

    public override Texture? ColorTexture => _color;

    public override Texture? DepthTexture => _depth;

    /// <summary>The colour attachment as this backend sees it, for the shared handle and its keyed mutex.</summary>
    internal D3D11Texture? Color => _color;

    public override void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == Width && height == Height) return;
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"Render target size must be positive; got {width}x{height}.");

        // Refused HERE as well as inside ReplaceStorage, and not because one
        // guard is unreliable: a no-op resize returns above without touching the
        // texture at all, so this is the only place that can say why a caller
        // asking for a genuinely different size is wrong before any of the views
        // have been released. See RenderTarget's remarks on where the
        // identity-survives-a-resize guarantee stops.
        if (Desc.Sharing != RenderTargetSharing.None)
        {
            throw new InvalidOperationException(
                $"A shared render target cannot be resized in place ({Width}x{Height} to {width}x{height}): " +
                "the consumer imported its NT handle. Recreate it under a new generation and retire the old one.");
        }

        ReleaseViews();
        // Swaps the resource and the SRV inside the existing wrapper, so every
        // material sampling this target survives the resize.
        _color?.ReplaceStorage(_device, width, height);
        _depth?.ReplaceDepthStorage(_device, width, height);
        Allocate(width, height);
    }

    private void Allocate(int width, int height)
    {
        var dev = (ID3D11Device*)_device.Handle;

        if (_color is not null)
        {
            ID3D11RenderTargetView* rtv = null;
            var rtvDesc = new RenderTargetViewDesc
            {
                // The VIEW's format, which is the resource's own everywhere but
                // on a shared attachment: there the resource is UNORM so an
                // outside importer does not decode a second time, and the sRGB
                // encode lives on this view. Measured legal on this machine;
                // see D3D11Texture.CreateRenderTargetTexture.
                Format = _color.RtvFormat,
                ViewDimension = RtvDimension.Texture2D,
            };
            rtvDesc.Anonymous.Texture2D = new Tex2DRtv { MipSlice = 0 };
            SilkMarshal.ThrowHResult(dev->CreateRenderTargetView(_color.Resource, &rtvDesc, &rtv));
            _rtv = ComOwnership.Own(rtv);
        }

        if (_depth is not null)
        {
            // An explicit desc, because the resource is typeless: a null desc
            // means "the resource's own format", and R32_TYPELESS is not a
            // depth format the runtime will accept for a DSV.
            var dsvDesc = new DepthStencilViewDesc
            {
                Format = Silk.NET.DXGI.Format.FormatD32Float,
                ViewDimension = DsvDimension.Texture2D,
            };
            dsvDesc.Anonymous.Texture2D = new Tex2DDsv { MipSlice = 0 };

            ID3D11DepthStencilView* dsv = null;
            SilkMarshal.ThrowHResult(dev->CreateDepthStencilView(_depth.Resource, &dsvDesc, &dsv));
            _dsv = ComOwnership.Own(dsv);
        }

        Width = width;
        Height = height;
    }

    private void ReleaseViews()
    {
        // ComOwnership, not Dispose: with one reference instead of two, a second
        // release is an over-release rather than something a leak absorbs. See
        // ComOwnership for the whole rule.
        ComOwnership.Release(ref _dsv);
        ComOwnership.Release(ref _rtv);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseViews();
        _color?.Dispose();
        _depth?.Dispose();
    }
}
