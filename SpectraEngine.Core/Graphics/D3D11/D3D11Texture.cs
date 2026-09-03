using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Graphics.D3D11;

internal sealed unsafe partial class D3D11Texture : Texture
{
    private ComPtr<ID3D11Texture2D> _texture;
    private ComPtr<ID3D11ShaderResourceView> _srv;
    private ComPtr<ID3D11SamplerState> _sampler;
    private ComPtr<IDXGIKeyedMutex> _keyedMutex;
    private nint _sharedHandle;
    private bool _disposed;

    public ComPtr<ID3D11ShaderResourceView> Srv => _srv;
    public ComPtr<ID3D11SamplerState> Sampler => _sampler;

    /// <summary>The underlying resource, for a render-target view over it.</summary>
    internal ID3D11Resource* Resource => (ID3D11Resource*)_texture.Handle;

    /// <summary>The DXGI format the RESOURCE was created with, which the SRV must match.</summary>
    internal Silk.NET.DXGI.Format DxgiFormat { get; private set; }

    /// <summary>
    /// The DXGI format a render-target view over this texture must be created
    /// with, which is the resource's own format everywhere except on a shared
    /// target. See <see cref="CreateRenderTargetTexture"/> for why those differ.
    /// </summary>
    internal Silk.NET.DXGI.Format RtvFormat { get; private set; }

    /// <summary>
    /// The NT handle something outside this renderer imports this texture by, or
    /// zero when it is not shared.
    /// </summary>
    internal nint SharedHandle => _sharedHandle;

    /// <summary>Whether this texture carries a shared handle and a keyed mutex.</summary>
    internal bool IsShared => _sharedHandle != 0;

    /// <summary>
    /// The keyed mutex that takes turns on this texture, or null when it is not
    /// shared. See <see cref="Renderer.SharedProducerKey"/> for the protocol.
    /// </summary>
    internal IDXGIKeyedMutex* KeyedMutex => (IDXGIKeyedMutex*)_keyedMutex.Handle;

    private D3D11Texture(
        ComPtr<ID3D11Texture2D> texture,
        ComPtr<ID3D11ShaderResourceView> srv,
        ComPtr<ID3D11SamplerState> sampler,
        int width, int height, TextureFormat format, TextureColorSpace colorSpace,
        Silk.NET.DXGI.Format dxgiFormat,
        Silk.NET.DXGI.Format? rtvFormat = null)
    {
        _texture = texture;
        _srv = srv;
        _sampler = sampler;
        Width = width;
        Height = height;
        Format = format;
        ColorSpace = colorSpace;
        DxgiFormat = dxgiFormat;
        RtvFormat = rtvFormat ?? dxgiFormat;
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
    /// <remarks>
    /// <para>
    /// <b>A shared attachment is a UNORM resource with an <c>_SRGB</c> render
    /// target view, and that split is the whole defence against a double
    /// encode.</b> The engine's tone-map resolve writes linear light and relies
    /// on the target's format to encode it, exactly as it does into the window;
    /// the consumer on the other side of the handle then decodes once when it
    /// samples. If the RESOURCE were sRGB-typed too, the consumer's own import
    /// would decode a second time and the picture would come out washed out,
    /// with no error anywhere. It is the same trick <c>D3D12TargetState</c>
    /// already uses on the back buffer, for the same reason: a view carries the
    /// colour space, and what an outside importer sees is the resource.
    /// </para>
    /// <para>
    /// <b>The SRV stays UNORM, and that is not a choice.</b> Measured on this
    /// machine: an <c>_SRGB</c> shader-resource view over an <c>_UNORM</c>
    /// resource is refused with <c>E_INVALIDARG</c> while the <c>_SRGB</c>
    /// render-target view over the same resource is accepted. So sampling a
    /// shared target inside the engine reads encoded values undecoded - which
    /// costs nothing today, because a shared target is what gets presented and
    /// nothing samples it, and is written down because the first thing to sample
    /// one will be surprised.
    /// </para>
    /// <para>
    /// <b><c>SHARED_NTHANDLE</c> is only legal alongside <c>SHARED</c> or
    /// <c>SHARED_KEYEDMUTEX</c></b>, and the keyed mutex is what the hand-over
    /// wants, so both flags always travel together here.
    /// </para>
    /// </remarks>
    internal static D3D11Texture CreateRenderTargetTexture(
        ComPtr<ID3D11Device> device,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter,
        TextureWrap wrap,
        RenderTargetSharing sharing = RenderTargetSharing.None)
    {
        TextureColorSpace resolved = TextureFormatInfo.Resolve(format, colorSpace);
        Silk.NET.DXGI.Format viewFormat = RenderTargetDxgiFormat(format, resolved);

        // Unshared is the path every existing target takes and is unchanged: one
        // format for the resource and both views.
        bool shared = sharing != RenderTargetSharing.None;
        Silk.NET.DXGI.Format resourceFormat = shared
            ? RenderTargetDxgiFormat(format, TextureColorSpace.Linear)
            : viewFormat;

        uint misc = shared
            ? (uint)(ResourceMiscFlag.SharedKeyedmutex | ResourceMiscFlag.SharedNthandle)
            : 0u;

        var dev = (ID3D11Device*)device.Handle;
        ID3D11Texture2D* texPtr = CreateRenderTargetResource(dev, width, height, resourceFormat, misc);
        ID3D11ShaderResourceView* srvPtr = CreateSrv(dev, texPtr, resourceFormat, mipLevels: 1);
        ID3D11SamplerState* samplerPtr = CreateSampler(dev, filter, wrap);

        var texture = new D3D11Texture(
            ComOwnership.Own(texPtr),
            ComOwnership.Own(srvPtr),
            ComOwnership.Own(samplerPtr),
            width, height, format, resolved, resourceFormat, viewFormat);

        if (shared)
        {
            try
            {
                texture.AcquireSharing();
            }
            catch
            {
                // Half a shared texture is worse than none: the caller would get
                // a target that renders and can never be handed over, and the
                // failure would surface as a silently black consumer instead of
                // as the HRESULT that actually happened.
                texture.Dispose();
                throw;
            }
        }

        return texture;
    }

    // Queries out the two interfaces a shared attachment is reached through and
    // mints the NT handle. Split out so the failure path above can dispose one
    // object rather than unwinding four raw pointers by hand.
    private void AcquireSharing()
    {
        IDXGIResource1* resourcePtr = null;
        Guid resourceGuid = IDXGIResource1.Guid;
        SilkMarshal.ThrowHResult(((ID3D11Texture2D*)_texture.Handle)->QueryInterface(
            &resourceGuid, (void**)&resourcePtr));
        ComPtr<IDXGIResource1> resource = ComOwnership.Own(resourcePtr);
        try
        {
            void* handle = null;
            SilkMarshal.ThrowHResult(((IDXGIResource1*)resource.Handle)->CreateSharedHandle(
                (SecurityAttributes*)null, SharedResourceRead | SharedResourceWrite, (char*)null, &handle));
            _sharedHandle = (nint)handle;
        }
        finally
        {
            ComOwnership.Release(ref resource);
        }

        IDXGIKeyedMutex* mutexPtr = null;
        Guid mutexGuid = IDXGIKeyedMutex.Guid;
        SilkMarshal.ThrowHResult(((ID3D11Texture2D*)_texture.Handle)->QueryInterface(
            &mutexGuid, (void**)&mutexPtr));
        _keyedMutex = ComOwnership.Own(mutexPtr);
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
        // A shared attachment is the one target whose storage may NOT be swapped
        // inside its wrapper. The wrapper's identity surviving a resize is what
        // keeps every material sampling it correct - and it is exactly what
        // would let a caller assume the HANDLE survived too, when the consumer
        // has already imported the old one and would go on reading a resource
        // that no longer exists. A shared target is recreated under a new
        // generation instead; see SharedTargetRetirement.
        if (IsShared)
        {
            throw new InvalidOperationException(
                "A shared render target cannot be resized in place: the consumer imported its NT handle, and a " +
                "handle cannot be swapped inside the wrapper the way a plain GPU resource can. Recreate the " +
                "target under a new generation and retire the old one.");
        }

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
        ID3D11Device* dev, int width, int height, Silk.NET.DXGI.Format dxgiFormat, uint miscFlags = 0)
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
            MiscFlags = miscFlags,
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
        ComOwnership.Release(ref _keyedMutex);
        ComOwnership.Release(ref _sampler);
        ComOwnership.Release(ref _srv);
        ComOwnership.Release(ref _texture);

        // An NT handle is a kernel object with its own reference on the
        // resource, so releasing the COM pointers is not enough: leaving it open
        // pins the whole surface for the process's life, which on a target
        // recreated per resize is a full-screen leak that no COM audit finds.
        // Cleared first so a second Dispose cannot close it twice - a closed
        // handle value is reusable, and closing somebody else's is far worse
        // than leaking this one.
        if (_sharedHandle != 0)
        {
            nint handle = _sharedHandle;
            _sharedHandle = 0;
            _ = Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>DXGI_SHARED_RESOURCE_READ.</summary>
    private const uint SharedResourceRead = 0x80000000u;

    /// <summary>DXGI_SHARED_RESOURCE_WRITE.</summary>
    private const uint SharedResourceWrite = 0x00000001u;

    // The one Win32 call the graphics layer needs that Silk.NET does not bind:
    // an NT shared handle is a kernel object and CloseHandle is the only way to
    // let go of one.
    private static partial class Kernel32
    {
        // Returned as the raw BOOL rather than marshalled: Silk.NET.Core.Native
        // also defines an UnmanagedType, so naming the marshalling attribute
        // here would be ambiguous, and nothing reads the result anyway.
        [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        internal static partial int CloseHandle(nint handle);
    }
}
