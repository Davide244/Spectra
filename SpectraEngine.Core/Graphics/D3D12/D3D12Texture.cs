using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// A default-heap 2D texture with its SRV and sampler in small CPU-only
/// descriptor heaps, ready to be copied into the renderer's shader-visible
/// rings at draw time. D3D12 has no GenerateMips, so mip chains are built with
/// a CPU box filter and every level is uploaded through a staging buffer.
/// </summary>
internal sealed unsafe class D3D12Texture : Texture
{
    private ComPtr<ID3D12Resource> _texture;
    private ComPtr<ID3D12DescriptorHeap> _srvHeap;      // 1 slot, non-shader-visible
    private ComPtr<ID3D12DescriptorHeap> _samplerHeap;  // 1 slot, non-shader-visible
    private bool _disposed;

    internal CpuDescriptorHandle SrvCpu { get; private set; }
    internal CpuDescriptorHandle SamplerCpu { get; private set; }

    internal D3D12Texture(
        D3D12Renderer renderer,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureFilter filter,
        TextureWrap wrap)
    {
        Width = width;
        Height = height;
        Format = format;

        // Expand RGB→RGBA (no 24-bit DXGI format) and normalize to a canonical
        // (bytesPerPixel, dxgiFormat) pair.
        Silk.NET.DXGI.Format dxgiFormat;
        int bpp;
        byte[] level0;
        switch (format)
        {
            case TextureFormat.Rgba8:
                dxgiFormat = Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm;
                bpp = 4;
                level0 = pixels.ToArray();
                break;
            case TextureFormat.Rgb8:
                dxgiFormat = Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm;
                bpp = 4;
                level0 = ExpandRgbToRgba(pixels, width, height);
                break;
            case TextureFormat.R8:
                dxgiFormat = Silk.NET.DXGI.Format.FormatR8Unorm;
                bpp = 1;
                level0 = pixels.ToArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        bool wantsMips = filter == TextureFilter.LinearMipmap;
        var mips = BuildMipChain(level0, width, height, bpp, wantsMips);

        _texture = renderer.CreateTexture2D((uint)width, (uint)height, (ushort)mips.Count, dxgiFormat);
        renderer.UploadTexture(_texture, mips, (uint)width, (uint)height, bpp, dxgiFormat);

        // SRV + sampler descriptors in tiny staging heaps owned by the texture.
        _srvHeap = renderer.CreateDescriptorHeap(DescriptorHeapType.CbvSrvUav, 1, shaderVisible: false);
        _samplerHeap = renderer.CreateDescriptorHeap(DescriptorHeapType.Sampler, 1, shaderVisible: false);
        SrvCpu = ((ID3D12DescriptorHeap*)_srvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();
        SamplerCpu = ((ID3D12DescriptorHeap*)_samplerHeap.Handle)->GetCPUDescriptorHandleForHeapStart();

        var srvDesc = new ShaderResourceViewDesc
        {
            Format = dxgiFormat,
            ViewDimension = SrvDimension.Texture2D,
            Shader4ComponentMapping = D3D12Renderer.DefaultComponentMapping,
        };
        srvDesc.Anonymous.Texture2D = new Tex2DSrv
        {
            MostDetailedMip = 0,
            MipLevels = (uint)mips.Count,
            PlaneSlice = 0,
            ResourceMinLODClamp = 0f,
        };
        renderer.DevicePtr->CreateShaderResourceView((ID3D12Resource*)_texture.Handle, &srvDesc, SrvCpu);

        Filter samplerFilter = filter switch
        {
            TextureFilter.Nearest => Filter.MinMagMipPoint,
            TextureFilter.Linear => Filter.MinMagMipLinear,
            TextureFilter.LinearMipmap => Filter.MinMagMipLinear,
            _ => Filter.MinMagMipLinear,
        };
        var addr = wrap == TextureWrap.Repeat ? TextureAddressMode.Wrap : TextureAddressMode.Clamp;
        var samplerDesc = new SamplerDesc
        {
            Filter = samplerFilter,
            AddressU = addr,
            AddressV = addr,
            AddressW = addr,
            MipLODBias = 0f,
            MaxAnisotropy = 1,
            // None, not Always: non-comparison filters ignore the func, and the
            // debug layer warns when one is set anyway.
            ComparisonFunc = ComparisonFunc.None,
            MinLOD = 0f,
            MaxLOD = float.MaxValue,
        };
        renderer.DevicePtr->CreateSampler(&samplerDesc, SamplerCpu);
    }

    /// <summary>Level-0 plus successively box-filtered halvings down to 1×1 (when enabled).</summary>
    private static List<(byte[] Pixels, uint Width, uint Height)> BuildMipChain(
        byte[] level0, int width, int height, int bpp, bool wantsMips)
    {
        var mips = new List<(byte[], uint, uint)> { (level0, (uint)width, (uint)height) };
        if (!wantsMips) return mips;

        byte[] prev = level0;
        uint w = (uint)width, h = (uint)height;
        while (w > 1 || h > 1)
        {
            uint nw = Math.Max(1, w / 2);
            uint nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * bpp];

            for (uint y = 0; y < nh; y++)
            {
                // Clamp the source window so odd sizes stay in bounds.
                uint sy0 = Math.Min(y * 2, h - 1);
                uint sy1 = Math.Min(y * 2 + 1, h - 1);
                for (uint x = 0; x < nw; x++)
                {
                    uint sx0 = Math.Min(x * 2, w - 1);
                    uint sx1 = Math.Min(x * 2 + 1, w - 1);
                    for (int c = 0; c < bpp; c++)
                    {
                        int sum = prev[(sy0 * w + sx0) * bpp + c]
                                + prev[(sy0 * w + sx1) * bpp + c]
                                + prev[(sy1 * w + sx0) * bpp + c]
                                + prev[(sy1 * w + sx1) * bpp + c];
                        next[(y * nw + x) * bpp + c] = (byte)(sum / 4);
                    }
                }
            }

            mips.Add((next, nw, nh));
            prev = next;
            w = nw;
            h = nh;
        }
        return mips;
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
        _samplerHeap.Dispose();
        _srvHeap.Dispose();
        _texture.Dispose();
    }
}
