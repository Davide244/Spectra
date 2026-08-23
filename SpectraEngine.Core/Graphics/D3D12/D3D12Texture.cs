using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System;
// Aliased: inside a Texture subclass the bare name resolves to the inherited
// Texture.ColorSpace property instead of the helper class.
using ColorMath = SpectraEngine.Core.Graphics.ColorSpace;

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

    /// <summary>The underlying resource, for an RTV over it or a barrier on it.</summary>
    internal ID3D12Resource* Resource => (ID3D12Resource*)_texture.Handle;

    /// <summary>The DXGI format actually in use: what an RTV and a PSO must match.</summary>
    internal Silk.NET.DXGI.Format DxgiFormat { get; private set; }

    // Only set for render-target textures, whose storage can be replaced.
    private TextureFilter _filter;
    private TextureWrap _wrap;

    private D3D12Texture(D3D12Renderer renderer, int width, int height,
        TextureFormat format, TextureColorSpace colorSpace, Silk.NET.DXGI.Format dxgiFormat,
        TextureFilter filter, TextureWrap wrap)
    {
        Width = width;
        Height = height;
        Format = format;
        ColorSpace = colorSpace;
        DxgiFormat = dxgiFormat;
        _filter = filter;
        _wrap = wrap;

        _srvHeap = renderer.CreateDescriptorHeap(DescriptorHeapType.CbvSrvUav, 1, shaderVisible: false);
        _samplerHeap = renderer.CreateDescriptorHeap(DescriptorHeapType.Sampler, 1, shaderVisible: false);
        SrvCpu = ((ID3D12DescriptorHeap*)_srvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();
        SamplerCpu = ((ID3D12DescriptorHeap*)_samplerHeap.Handle)->GetCPUDescriptorHandleForHeapStart();

        CreateSampler(renderer, filter, wrap);
    }

    /// <summary>
    /// Creates an empty texture usable as both a render target and a shader
    /// resource: the colour attachment of a <see cref="D3D12RenderTarget"/>.
    /// </summary>
    /// <remarks>
    /// It starts in <see cref="ResourceStates.PixelShaderResource"/>, which is
    /// what the target's state tracking assumes and what makes a texture that is
    /// created and then sampled without ever being drawn into legal rather than
    /// undefined.
    /// </remarks>
    internal static D3D12Texture CreateRenderTargetTexture(
        D3D12Renderer renderer, int width, int height, TextureFormat format,
        TextureColorSpace colorSpace, TextureFilter filter, TextureWrap wrap)
    {
        TextureColorSpace resolved = TextureFormatInfo.Resolve(format, colorSpace);
        Silk.NET.DXGI.Format dxgiFormat = RenderTargetDxgiFormat(format, resolved);

        var texture = new D3D12Texture(renderer, width, height, format, resolved, dxgiFormat, filter, wrap);
        texture.AllocateRenderTargetStorage(renderer, width, height);
        return texture;
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

    /// <summary>
    /// Creates a depth texture written through a depth-stencil view and read
    /// through a sampler.
    /// </summary>
    /// <remarks>
    /// <b>The resource is TYPELESS and the depth-ness lives on the views.</b>
    /// D3D refuses a shader-resource view over a resource declared
    /// <c>D32_FLOAT</c>, so the resource is <c>R32_TYPELESS</c>, the DSV says
    /// <c>D32_FLOAT</c> and the SRV says <c>R32_FLOAT</c>.
    /// </remarks>
    internal static D3D12Texture CreateDepthTexture(D3D12Renderer renderer, int width, int height)
    {
        var texture = new D3D12Texture(
            renderer, width, height, TextureFormat.Depth32Float, TextureColorSpace.Linear,
            Silk.NET.DXGI.Format.FormatR32Typeless, TextureFilter.Nearest, TextureWrap.Clamp);
        texture.AllocateDepthStorage(renderer, width, height);
        return texture;
    }

    /// <summary>Reallocates a depth texture in place, keeping the wrapper and its SRV slot.</summary>
    internal void ReplaceDepthStorage(D3D12Renderer renderer, int width, int height)
    {
        ComOwnership.Release(ref _texture);
        AllocateDepthStorage(renderer, width, height);
        Width = width;
        Height = height;
    }

    private void AllocateDepthStorage(D3D12Renderer renderer, int width, int height)
    {
        _texture = renderer.CreateDepthResource((uint)width, (uint)height);

        var srvDesc = new ShaderResourceViewDesc
        {
            // R32_FLOAT, not the typeless resource format: a typeless SRV is
            // rejected, and this is the read half of the two views.
            Format = Silk.NET.DXGI.Format.FormatR32Float,
            ViewDimension = SrvDimension.Texture2D,
            Shader4ComponentMapping = D3D12Renderer.DefaultComponentMapping,
        };
        srvDesc.Anonymous.Texture2D = new Tex2DSrv
        {
            MostDetailedMip = 0,
            MipLevels = 1,
            PlaneSlice = 0,
            ResourceMinLODClamp = 0f,
        };
        renderer.DevicePtr->CreateShaderResourceView(Resource, &srvDesc, SrvCpu);
    }

    /// <summary>
    /// Replaces the resource at a new size, <b>keeping the same wrapper and the
    /// same SRV heap slot</b>, so materials and copied descriptor tables survive
    /// a render-target resize.
    /// </summary>
    internal void ReplaceStorage(D3D12Renderer renderer, int width, int height)
    {
        ComOwnership.Release(ref _texture);
        AllocateRenderTargetStorage(renderer, width, height);
        Width = width;
        Height = height;
    }

    private void AllocateRenderTargetStorage(D3D12Renderer renderer, int width, int height)
    {
        _texture = renderer.CreateRenderTargetResource((uint)width, (uint)height, DxgiFormat);

        var srvDesc = new ShaderResourceViewDesc
        {
            Format = DxgiFormat,
            ViewDimension = SrvDimension.Texture2D,
            Shader4ComponentMapping = D3D12Renderer.DefaultComponentMapping,
        };
        srvDesc.Anonymous.Texture2D = new Tex2DSrv
        {
            MostDetailedMip = 0,
            MipLevels = 1,
            PlaneSlice = 0,
            ResourceMinLODClamp = 0f,
        };
        renderer.DevicePtr->CreateShaderResourceView(Resource, &srvDesc, SrvCpu);
    }

    private void CreateSampler(D3D12Renderer renderer, TextureFilter filter, TextureWrap wrap)
    {
        Filter samplerFilter = filter == TextureFilter.Nearest ? Filter.MinMagMipPoint : Filter.MinMagMipLinear;
        var addr = wrap == TextureWrap.Repeat ? TextureAddressMode.Wrap : TextureAddressMode.Clamp;
        var samplerDesc = new SamplerDesc
        {
            Filter = samplerFilter,
            AddressU = addr,
            AddressV = addr,
            AddressW = addr,
            MipLODBias = 0f,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunc.None,
            MinLOD = 0f,
            MaxLOD = float.MaxValue,
        };
        renderer.DevicePtr->CreateSampler(&samplerDesc, SamplerCpu);
    }

    internal D3D12Texture(
        D3D12Renderer renderer,
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

        Width = width;
        Height = height;
        Format = format;
        ColorSpace = resolved;

        // Expand RGB→RGBA (no 24-bit DXGI format) and normalize to a canonical
        // (bytesPerPixel, dxgiFormat) pair.
        Silk.NET.DXGI.Format dxgiFormat;
        int bpp;
        byte[] level0;
        switch (format)
        {
            case TextureFormat.Rgba8:
                dxgiFormat = srgb
                    ? Silk.NET.DXGI.Format.FormatR8G8B8A8UnormSrgb
                    : Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm;
                bpp = 4;
                level0 = pixels.ToArray();
                break;
            case TextureFormat.Rgb8:
                dxgiFormat = srgb
                    ? Silk.NET.DXGI.Format.FormatR8G8B8A8UnormSrgb
                    : Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm;
                bpp = 4;
                level0 = ExpandRgbToRgba(pixels, width, height);
                break;
            case TextureFormat.R8:
                // No R8_UNORM_SRGB exists; Resolve already forced linear.
                dxgiFormat = Silk.NET.DXGI.Format.FormatR8Unorm;
                bpp = 1;
                level0 = pixels.ToArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        bool wantsMips = filter == TextureFilter.LinearMipmap;
        var mips = BuildMipChain(level0, width, height, bpp, wantsMips, srgb);

        DxgiFormat = dxgiFormat;
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
    /// <remarks>
    /// <para>
    /// <b><paramref name="srgb"/> is not cosmetic.</b> D3D12 has no
    /// GenerateMips, so unlike the other two backends this chain is built in
    /// software, and averaging four sRGB bytes averages display codes rather
    /// than light. The result is a mip chain that darkens as it goes down, most
    /// visibly on the tiled brush surfaces that dominate this engine's screen,
    /// and only on this backend. So an sRGB chain decodes each tap to linear,
    /// averages there, and re-encodes, which is what the hardware does on D3D11
    /// and GL.
    /// </para>
    /// <para>
    /// <b>Alpha is excluded from the conversion.</b> It is coverage, stored
    /// linearly even inside an sRGB format. It is always the fourth channel, so
    /// the loop simply leaves index 3 alone.
    /// </para>
    /// </remarks>
    private static List<(byte[] Pixels, uint Width, uint Height)> BuildMipChain(
        byte[] level0, int width, int height, int bpp, bool wantsMips, bool srgb)
    {
        var mips = new List<(byte[], uint, uint)> { (level0, (uint)width, (uint)height) };
        if (!wantsMips) return mips;

        // 256-entry decode table: the alternative is four MathF.Pow calls per
        // channel per texel, which is seconds rather than milliseconds on a
        // large texture.
        float[]? toLinear = srgb ? SrgbDecodeTable : null;

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
                        byte t0 = prev[(sy0 * w + sx0) * bpp + c];
                        byte t1 = prev[(sy0 * w + sx1) * bpp + c];
                        byte t2 = prev[(sy1 * w + sx0) * bpp + c];
                        byte t3 = prev[(sy1 * w + sx1) * bpp + c];

                        // c == 3 is alpha: already linear, so average it directly.
                        next[(y * nw + x) * bpp + c] = toLinear is null || c == 3
                            ? (byte)((t0 + t1 + t2 + t3) / 4)
                            : EncodeSrgb(
                                (toLinear[t0] + toLinear[t1] + toLinear[t2] + toLinear[t3]) * 0.25f);
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

    private static readonly float[] SrgbDecodeTable = BuildSrgbDecodeTable();

    private static float[] BuildSrgbDecodeTable()
    {
        var table = new float[256];
        for (int i = 0; i < table.Length; i++)
            table[i] = ColorMath.SrgbToLinear(i / 255f);
        return table;
    }

    private static byte EncodeSrgb(float linear)
    {
        float encoded = ColorMath.LinearToSrgb(linear);
        // The +0.5 rounds to nearest instead of truncating; over a full chain,
        // truncation is a systematic darkening of about half a code per level.
        return (byte)Math.Clamp((int)(encoded * 255f + 0.5f), 0, 255);
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
        ComOwnership.Release(ref _samplerHeap);
        ComOwnership.Release(ref _srvHeap);
        ComOwnership.Release(ref _texture);
    }
}
