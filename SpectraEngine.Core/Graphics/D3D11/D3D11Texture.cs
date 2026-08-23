using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Buffers;

namespace SpectraEngine.Core.Graphics.D3D11;

internal sealed unsafe class D3D11Texture : Texture
{
    private ComPtr<ID3D11Texture2D> _texture;
    private ComPtr<ID3D11ShaderResourceView> _srv;
    private ComPtr<ID3D11SamplerState> _sampler;
    private bool _disposed;

    public ComPtr<ID3D11ShaderResourceView> Srv => _srv;
    public ComPtr<ID3D11SamplerState> Sampler => _sampler;

    /// <summary>The underlying resource, for a render-target view over it.</summary>
    internal ID3D11Resource* Resource => (ID3D11Resource*)_texture.Handle;

    /// <summary>The DXGI format actually in use, which an RTV over this texture must match.</summary>
    internal Silk.NET.DXGI.Format DxgiFormat { get; private set; }

    private D3D11Texture(
        ComPtr<ID3D11Texture2D> texture,
        ComPtr<ID3D11ShaderResourceView> srv,
        ComPtr<ID3D11SamplerState> sampler,
        int width, int height, TextureFormat format, TextureColorSpace colorSpace,
        Silk.NET.DXGI.Format dxgiFormat)
    {
        _texture = texture;
        _srv = srv;
        _sampler = sampler;
        Width = width;
        Height = height;
        Format = format;
        ColorSpace = colorSpace;
        DxgiFormat = dxgiFormat;
    }

    internal static D3D11Texture Create(
        ComPtr<ID3D11Device> device,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter,
        TextureWrap wrap)
    {
        TextureColorSpace resolved = TextureFormatInfo.Resolve(format, colorSpace);
        bool srgb = resolved == TextureColorSpace.Srgb;

        if (TextureFormatInfo.IsFloat(format))
            throw new ArgumentOutOfRangeException(
                nameof(format), $"{format} cannot be uploaded from byte pixels; it is a render-target format.");

        // D3D11 has no native 24-bit RGB texture format, so for Rgb8 input we
        // expand to RGBA8 on the CPU side. R8 and Rgba8 map directly.
        Silk.NET.DXGI.Format dxgiFormat;
        byte[]? expanded = null;
        ReadOnlySpan<byte> uploadPixels = pixels;
        uint rowPitch;
        switch (format)
        {
            case TextureFormat.Rgba8:
                dxgiFormat = srgb
                    ? Silk.NET.DXGI.Format.FormatR8G8B8A8UnormSrgb
                    : Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm;
                rowPitch = (uint)(width * 4);
                break;
            case TextureFormat.Rgb8:
                dxgiFormat = srgb
                    ? Silk.NET.DXGI.Format.FormatR8G8B8A8UnormSrgb
                    : Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm;
                expanded = ExpandRgbToRgba(pixels, width, height);
                uploadPixels = expanded;
                rowPitch = (uint)(width * 4);
                break;
            case TextureFormat.R8:
                // No R8_UNORM_SRGB exists; TextureFormatInfo.Resolve already
                // forced `resolved` to linear for this case.
                dxgiFormat = Silk.NET.DXGI.Format.FormatR8Unorm;
                rowPitch = (uint)width;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        bool wantsMipmaps = filter == TextureFilter.LinearMipmap;
        uint mipLevels = wantsMipmaps ? 0u : 1u; // 0 = full chain
        uint bindFlags = (uint)BindFlag.ShaderResource;
        uint miscFlags = 0u;
        if (wantsMipmaps)
        {
            // For runtime GenerateMips we need RenderTarget bind + GenerateMips misc flag.
            // An sRGB format is fine here and is in fact the point: GenerateMips
            // filters through the view's format, so an sRGB chain is built by
            // decoding, averaging light, and re-encoding.
            bindFlags |= (uint)BindFlag.RenderTarget;
            miscFlags |= (uint)ResourceMiscFlag.GenerateMips;
        }

        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = mipLevels,
            ArraySize = 1,
            Format = dxgiFormat,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = bindFlags,
            CPUAccessFlags = 0,
            MiscFlags = miscFlags,
        };

        var dev = (ID3D11Device*)device.Handle;
        ID3D11Texture2D* texPtr = null;
        if (wantsMipmaps)
        {
            // Have to create without initial data (mipmap chain isn't ready)
            // then UpdateSubresource into mip 0 + GenerateMips.
            SilkMarshal.ThrowHResult(dev->CreateTexture2D(&desc, null, &texPtr));
        }
        else
        {
            fixed (byte* p = uploadPixels)
            {
                var init = new SubresourceData
                {
                    PSysMem = p,
                    SysMemPitch = rowPitch,
                    SysMemSlicePitch = 0,
                };
                SilkMarshal.ThrowHResult(dev->CreateTexture2D(&desc, &init, &texPtr));
            }
        }

        // Shader resource view (full mip chain).
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = dxgiFormat,
            ViewDimension = Silk.NET.Core.Native.D3DSrvDimension.D3DSrvDimensionTexture2D,
        };
        srvDesc.Anonymous.Texture2D = new Tex2DSrv
        {
            MostDetailedMip = 0,
            MipLevels = unchecked((uint)-1),
        };

        ID3D11ShaderResourceView* srvPtr = null;
        SilkMarshal.ThrowHResult(dev->CreateShaderResourceView((ID3D11Resource*)texPtr, &srvDesc, &srvPtr));

        if (wantsMipmaps)
        {
            ID3D11DeviceContext* ctxPtr = null;
            dev->GetImmediateContext(&ctxPtr);
            fixed (byte* p = uploadPixels)
            {
                ctxPtr->UpdateSubresource((ID3D11Resource*)texPtr, 0, null, p, rowPitch, 0u);
            }
            ctxPtr->GenerateMips(srvPtr);
            ctxPtr->Release();
        }

        ID3D11SamplerState* samplerPtr = CreateSampler(dev, filter, wrap);

        return new D3D11Texture(
            ComOwnership.Own(texPtr),
            ComOwnership.Own(srvPtr),
            ComOwnership.Own(samplerPtr),
            width, height, format, resolved, dxgiFormat);
    }

    /// <summary>
    /// Creates an empty texture usable as both a render target and a shader
    /// resource: the colour attachment of a <see cref="D3D11RenderTarget"/>.
    /// </summary>
    internal static D3D11Texture CreateRenderTargetTexture(
        ComPtr<ID3D11Device> device,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter,
        TextureWrap wrap)
    {
        TextureColorSpace resolved = TextureFormatInfo.Resolve(format, colorSpace);
        Silk.NET.DXGI.Format dxgiFormat = RenderTargetDxgiFormat(format, resolved);

        var dev = (ID3D11Device*)device.Handle;
        ID3D11Texture2D* texPtr = CreateRenderTargetResource(dev, width, height, dxgiFormat);
        ID3D11ShaderResourceView* srvPtr = CreateSrv(dev, texPtr, dxgiFormat, mipLevels: 1);
        ID3D11SamplerState* samplerPtr = CreateSampler(dev, filter, wrap);

        return new D3D11Texture(
            ComOwnership.Own(texPtr),
            ComOwnership.Own(srvPtr),
            ComOwnership.Own(samplerPtr),
            width, height, format, resolved, dxgiFormat);
    }

    /// <summary>
    /// Creates a depth texture that can be both written through a depth-stencil
    /// view and read through a sampler.
    /// </summary>
    /// <remarks>
    /// <b>The resource is TYPELESS and the depth-ness lives on the views.</b>
    /// D3D refuses a shader-resource view over a resource declared
    /// <c>D32_FLOAT</c>, so writing needs <c>D32_FLOAT</c> on the DSV and
    /// reading needs <c>R32_FLOAT</c> on the SRV, over one <c>R32_TYPELESS</c>
    /// resource. That is the whole of what "typeless depth" means and the whole
    /// of why a deferred pass cannot just reuse the ordinary depth path.
    /// </remarks>
    internal static D3D11Texture CreateDepthTexture(
        ComPtr<ID3D11Device> device, int width, int height)
    {
        var dev = (ID3D11Device*)device.Handle;

        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Silk.NET.DXGI.Format.FormatR32Typeless,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.DepthStencil | BindFlag.ShaderResource),
        };

        ID3D11Texture2D* texPtr = null;
        SilkMarshal.ThrowHResult(dev->CreateTexture2D(&desc, null, &texPtr));

        ID3D11ShaderResourceView* srvPtr = CreateSrv(
            dev, texPtr, Silk.NET.DXGI.Format.FormatR32Float, mipLevels: 1);
        // Point sampling: a depth is data, and interpolating two of them yields
        // a value that lies on neither surface.
        ID3D11SamplerState* samplerPtr = CreateSampler(dev, TextureFilter.Nearest, TextureWrap.Clamp);

        return new D3D11Texture(
            ComOwnership.Own(texPtr),
            ComOwnership.Own(srvPtr),
            ComOwnership.Own(samplerPtr),
            width, height, TextureFormat.Depth32Float, TextureColorSpace.Linear,
            Silk.NET.DXGI.Format.FormatR32Typeless);
    }

    /// <summary>Reallocates a depth texture in place, keeping the wrapper.</summary>
    internal void ReplaceDepthStorage(ComPtr<ID3D11Device> device, int width, int height)
    {
        D3D11Texture replacement = CreateDepthTexture(device, width, height);

        ComOwnership.Release(ref _srv);
        ComOwnership.Release(ref _texture);

        _texture = replacement._texture;
        _srv = replacement._srv;
        replacement._texture = default;
        replacement._srv = default;
        replacement.Dispose();

        Width = width;
        Height = height;
    }

    /// <summary>
    /// Replaces this texture's resource and view at a new size, <b>keeping the
    /// same wrapper object</b>. What a render-target resize needs; the sampler
    /// is unaffected and is deliberately kept.
    /// </summary>
    internal void ReplaceStorage(ComPtr<ID3D11Device> device, int width, int height)
    {
        var dev = (ID3D11Device*)device.Handle;
        ID3D11Texture2D* texPtr = CreateRenderTargetResource(dev, width, height, DxgiFormat);
        ID3D11ShaderResourceView* srvPtr = CreateSrv(dev, texPtr, DxgiFormat, mipLevels: 1);

        // Old first: nothing can be sampling this between the two lines, because
        // resource creation and rendering share the render thread.
        ComOwnership.Release(ref _srv);
        ComOwnership.Release(ref _texture);

        _texture = ComOwnership.Own(texPtr);
        _srv = ComOwnership.Own(srvPtr);
        Width = width;
        Height = height;
    }

    // The format argument used to be accepted and then ignored here, which was
    // invisible only because RenderTargetDesc.Validate allowed nothing but
    // Rgba8. The moment a float format became legal it would have produced a
    // silently 8-bit target on this backend while OpenGL was correct.
    private static Silk.NET.DXGI.Format RenderTargetDxgiFormat(
        TextureFormat format, TextureColorSpace resolved) => format switch
    {
        TextureFormat.Rgba8 => resolved == TextureColorSpace.Srgb
            ? Silk.NET.DXGI.Format.FormatR8G8B8A8UnormSrgb
            : Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm,
        TextureFormat.Rgba16Float => Silk.NET.DXGI.Format.FormatR16G16B16A16Float,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), $"{format} is not a render-target format."),
    };

    private static ID3D11Texture2D* CreateRenderTargetResource(
        ID3D11Device* dev, int width, int height, Silk.NET.DXGI.Format dxgiFormat)
    {
        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            // One level: there is nothing to build a chain from, and the
            // contents change every frame, so a chain would be stale anyway.
            MipLevels = 1,
            ArraySize = 1,
            Format = dxgiFormat,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.ShaderResource | BindFlag.RenderTarget),
        };

        ID3D11Texture2D* texPtr = null;
        SilkMarshal.ThrowHResult(dev->CreateTexture2D(&desc, null, &texPtr));
        return texPtr;
    }

    private static ID3D11ShaderResourceView* CreateSrv(
        ID3D11Device* dev, ID3D11Texture2D* texture, Silk.NET.DXGI.Format dxgiFormat, uint mipLevels)
    {
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = dxgiFormat,
            ViewDimension = Silk.NET.Core.Native.D3DSrvDimension.D3DSrvDimensionTexture2D,
        };
        srvDesc.Anonymous.Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = mipLevels };

        ID3D11ShaderResourceView* srvPtr = null;
        SilkMarshal.ThrowHResult(dev->CreateShaderResourceView((ID3D11Resource*)texture, &srvDesc, &srvPtr));
        return srvPtr;
    }

    private static ID3D11SamplerState* CreateSampler(
        ID3D11Device* dev, TextureFilter filter, TextureWrap wrap)
    {
        Silk.NET.Direct3D11.Filter samplerFilter = filter switch
        {
            TextureFilter.Nearest => Silk.NET.Direct3D11.Filter.MinMagMipPoint,
            _ => Silk.NET.Direct3D11.Filter.MinMagMipLinear,
        };
        var addrMode = wrap == TextureWrap.Repeat ? TextureAddressMode.Wrap : TextureAddressMode.Clamp;

        var samplerDesc = new SamplerDesc
        {
            Filter = samplerFilter,
            AddressU = addrMode,
            AddressV = addrMode,
            AddressW = addrMode,
            MipLODBias = 0f,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunc.Always,
            MinLOD = 0f,
            MaxLOD = float.MaxValue,
        };

        ID3D11SamplerState* samplerPtr = null;
        SilkMarshal.ThrowHResult(dev->CreateSamplerState(&samplerDesc, &samplerPtr));
        return samplerPtr;
    }

    private static byte[] ExpandRgbToRgba(ReadOnlySpan<byte> rgb, int width, int height)
    {
        int pixelCount = width * height;
        var rgba = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            rgba[i * 4 + 0] = rgb[i * 3 + 0];
            rgba[i * 4 + 1] = rgb[i * 3 + 1];
            rgba[i * 4 + 2] = rgb[i * 3 + 2];
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ComOwnership.Release(ref _sampler);
        ComOwnership.Release(ref _srv);
        ComOwnership.Release(ref _texture);
    }
}
