using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Buffers;

namespace SpectraEngine.Core.Graphics.D3D11;

internal sealed unsafe class D3D11Texture : Texture
{
    private readonly ComPtr<ID3D11Texture2D> _texture;
    private ComPtr<ID3D11ShaderResourceView> _srv;
    private ComPtr<ID3D11SamplerState> _sampler;
    private bool _disposed;

    public ComPtr<ID3D11ShaderResourceView> Srv => _srv;
    public ComPtr<ID3D11SamplerState> Sampler => _sampler;

    private D3D11Texture(
        ComPtr<ID3D11Texture2D> texture,
        ComPtr<ID3D11ShaderResourceView> srv,
        ComPtr<ID3D11SamplerState> sampler,
        int width, int height, TextureFormat format)
    {
        _texture = texture;
        _srv = srv;
        _sampler = sampler;
        Width = width;
        Height = height;
        Format = format;
    }

    internal static D3D11Texture Create(
        ComPtr<ID3D11Device> device,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureFilter filter,
        TextureWrap wrap)
    {
        // D3D11 has no native 24-bit RGB texture format, so for Rgb8 input we
        // expand to RGBA8 on the CPU side. R8 and Rgba8 map directly.
        Silk.NET.DXGI.Format dxgiFormat;
        byte[]? expanded = null;
        ReadOnlySpan<byte> uploadPixels = pixels;
        uint rowPitch;
        switch (format)
        {
            case TextureFormat.Rgba8:
                dxgiFormat = Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm;
                rowPitch = (uint)(width * 4);
                break;
            case TextureFormat.Rgb8:
                dxgiFormat = Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm;
                expanded = ExpandRgbToRgba(pixels, width, height);
                uploadPixels = expanded;
                rowPitch = (uint)(width * 4);
                break;
            case TextureFormat.R8:
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

        // Sampler state.
        Silk.NET.Direct3D11.Filter samplerFilter = filter switch
        {
            TextureFilter.Nearest => Silk.NET.Direct3D11.Filter.MinMagMipPoint,
            TextureFilter.Linear => Silk.NET.Direct3D11.Filter.MinMagMipLinear,
            TextureFilter.LinearMipmap => Silk.NET.Direct3D11.Filter.MinMagMipLinear,
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

        return new D3D11Texture(
            new ComPtr<ID3D11Texture2D>(texPtr),
            new ComPtr<ID3D11ShaderResourceView>(srvPtr),
            new ComPtr<ID3D11SamplerState>(samplerPtr),
            width, height, format);
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
        _sampler.Dispose();
        _srv.Dispose();
        _texture.Dispose();
    }
}
