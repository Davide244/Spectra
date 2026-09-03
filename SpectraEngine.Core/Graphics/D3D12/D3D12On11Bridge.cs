using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Runtime.InteropServices;
using D12 = Silk.NET.Direct3D12;
using EngineD3D11 = SpectraEngine.Core.Graphics.D3D11;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// A D3D11 front end over this renderer's own D3D12 device and queue, whose one
/// job is to copy the frame's resolve target into a keyed-mutex shared texture
/// something outside the engine can import.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bridge exists because the import refuses the direct route, and that
/// was measured rather than reasoned about.</b> <c>--interop-probe</c> imports
/// real textures on this machine: a D3D11 keyed-mutex NT handle imports and
/// updates, a <b>D3D12-created handle is refused with <c>E_NOINTERFACE</c>
/// inside the compositor's own import</b>, and a D3D11On12 device over the same
/// D3D12 device works. So D3D12's shared-target members are not an
/// implementation of <see cref="RenderTargetSharing"/> on a D3D12 resource at
/// all: the frame lands in an ordinary private D3D12 target, and one copy per
/// frame hands it to a texture D3D11 created. The cost is that copy, and what it
/// buys is exactly one synchronisation implementation in the engine rather than
/// two.
/// </para>
/// <para>
/// <b>The shared texture is built by the D3D11 backend's own code path</b>
/// (<see cref="EngineD3D11.D3D11Texture.CreateRenderTargetTexture"/> with
/// <see cref="RenderTargetSharing.KeyedMutex"/>), on this bridge's D3D11 device.
/// A second implementation here would be a second place for the UNORM resource
/// plus <c>_SRGB</c> render-target view split to be got wrong, and that split
/// produces a washed-out picture with no diagnostic anywhere.
/// </para>
/// <para>
/// <b>Nothing on the D3D12 side ever touches the shared texture</b>, which is
/// what makes the key bracket narrow here and wide on D3D11: over there the
/// pipeline draws straight into the shared resource, so the mutex has to cover
/// the whole frame; here it covers only <see cref="Publish"/>. A keyed-mutex
/// resource written without its key returns <c>S_OK</c>, logs nothing and
/// writes zeros, so the bracket has to cover every touch and no more.
/// </para>
/// <para>
/// Render thread only, like every other owner of a GPU resource's lifetime.
/// </para>
/// </remarks>
internal sealed unsafe partial class D3D12On11Bridge : IDisposable
{
    // Long-lived: these wrap the renderer's device and queue, neither of which
    // is ever recreated during a session, so a resize replaces the surface
    // below and leaves these alone.
    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _context;

    // ID3D11On12Device. Held as an IUnknown because Silk.NET 2.23 binds neither
    // the interface nor its entry point: the three methods are reached through
    // the vtable below. IUnknown is still a Silk COM type, so ComOwnership's
    // rules apply to it unchanged, which is the point of not using a raw
    // pointer here.
    private ComPtr<IUnknown> _on12;

    private BridgeSurface? _surface;

    private D3D12On11Bridge(
        ComPtr<ID3D11Device> device, ComPtr<ID3D11DeviceContext> context, ComPtr<IUnknown> on12)
    {
        _device = device;
        _context = context;
        _on12 = on12;
    }

    /// <summary>
    /// Brings up the D3D11On12 device over <paramref name="device12"/> and
    /// <paramref name="queue12"/>.
    /// </summary>
    /// <remarks>
    /// <b>The queue is the whole reason this is a bridge and not a second
    /// device.</b> An 11On12 device is a D3D11 front end that records into
    /// somebody else's D3D12 queue, so its copy is ordered against the frame's
    /// own command list by the queue rather than by a fence the engine would
    /// have to invent - and <see cref="D3D12Renderer.WaitForGpu"/> at present
    /// time already covers both.
    /// </remarks>
    internal static D3D12On11Bridge Create(D12.ID3D12Device* device12, D12.ID3D12CommandQueue* queue12)
    {
        D3DFeatureLevel* levels = stackalloc D3DFeatureLevel[1] { D3DFeatureLevel.Level110 };
        D3DFeatureLevel chosen = default;
        void* queue = queue12;
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;

        // Flags zero, which is what --interop-probe measured this machine's
        // 11On12 path with. BGRA support would cost nothing and is not asked
        // for, because the only texture this device ever creates is
        // R8G8B8A8 and a flag nothing needs is a flag nobody measured.
        SilkMarshal.ThrowHResult(D3D11On12CreateDevice(
            device12, 0u, levels, 1u, &queue, 1u, 0u, &device, &context, &chosen));

        ComPtr<ID3D11Device> owned = ComOwnership.Own(device);
        ComPtr<ID3D11DeviceContext> ownedContext = ComOwnership.Own(context);
        ComPtr<IUnknown> on12 = default;
        try
        {
            IUnknown* raw = null;
            Guid guid = On12DeviceGuid;
            SilkMarshal.ThrowHResult(((ID3D11Device*)owned.Handle)->QueryInterface(&guid, (void**)&raw));
            on12 = ComOwnership.Own(raw);
        }
        catch
        {
            // Half a bridge is worse than none: the caller would get a device
            // that cannot wrap anything, and the failure would surface later as
            // a black consumer rather than as the HRESULT that happened.
            ComOwnership.Release(ref ownedContext);
            ComOwnership.Release(ref owned);
            throw;
        }

        return new D3D12On11Bridge(owned, ownedContext, on12);
    }

    /// <summary>The NT handle of the live shared texture, or zero before one exists.</summary>
    internal nint SharedHandle => _surface?.Shared.SharedHandle ?? 0;

    /// <summary>
    /// The keyed mutex of the live shared texture, or null before one exists.
    /// See <see cref="Renderer.SharedProducerKey"/> for the protocol.
    /// </summary>
    internal IDXGIKeyedMutex* KeyedMutex => _surface is { } surface ? surface.Shared.KeyedMutex : null;

    /// <summary>Whether a shared texture exists to publish into.</summary>
    internal bool HasSurface => _surface is not null;

    /// <summary>
    /// Builds the shared texture for a fresh present target and the D3D11 alias
    /// of that target's colour resource.
    /// </summary>
    /// <remarks>
    /// <b>The wrapped resource is created once per surface, never per frame.</b>
    /// <c>CreateWrappedResource</c> allocates an alias object and a state-tracking
    /// entry for it; doing it per frame would make an allocation and a
    /// destruction part of the present path, and the resource being aliased does
    /// not change until the target is recreated.
    /// </remarks>
    /// <param name="width">Pixel width of the present target.</param>
    /// <param name="height">Pixel height of the present target.</param>
    /// <param name="resolveResource">The present target's colour resource.</param>
    internal void Attach(int width, int height, D12.ID3D12Resource* resolveResource)
    {
        if (_surface is not null)
            throw new InvalidOperationException("The bridge already has a surface; detach the old one first.");

        // Srgb plus KeyedMutex, which is what makes the RESOURCE unorm and only
        // the render-target view sRGB - the D3D12 side has already encoded on
        // its own sRGB view, so a consumer that decodes on sample must not find
        // an sRGB-typed resource and decode a second time. Reusing the D3D11
        // path is what keeps that one decision in one place.
        var shared = EngineD3D11.D3D11Texture.CreateRenderTargetTexture(
            _device, width, height, TextureFormat.Rgba8, TextureColorSpace.Srgb,
            TextureFilter.Linear, TextureWrap.Clamp, RenderTargetSharing.KeyedMutex);

        ComPtr<ID3D11Resource> wrapped = default;
        try
        {
            wrapped = WrapResource(resolveResource);
        }
        catch
        {
            shared.Dispose();
            throw;
        }

        _surface = new BridgeSurface(shared, wrapped);
    }

    /// <summary>
    /// Hands the live surface over as one disposable and leaves the bridge with
    /// none.
    /// </summary>
    /// <remarks>
    /// One object, because a retired generation's shared texture and its wrapped
    /// alias have to be freed together and at the moment the consumer says so:
    /// the alias holds a reference on the D3D12 resource the retired target
    /// owns, so freeing the target while the alias lives leaves an aliased
    /// resource whose owner is gone.
    /// </remarks>
    internal IDisposable? Detach()
    {
        BridgeSurface? surface = _surface;
        _surface = null;
        return surface;
    }

    /// <summary>
    /// Copies the present target into the shared texture. Called with the shared
    /// key held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Acquire, copy, release, flush, in that order, and the release is not
    /// optional.</b> <c>AcquireWrappedResources</c> transitions the aliased
    /// resource out of the state D3D12 left it in and
    /// <c>ReleaseWrappedResources</c> puts it back; skipping the release leaves
    /// the resource in whatever state the 11On12 layer chose while
    /// <see cref="D3D12RenderTarget"/> still believes it is
    /// <c>PixelShaderResource</c>, and the next frame's barrier is then computed
    /// from a state the resource is not in. That is a D3D12 debug-layer error
    /// and, without the layer, undefined data.
    /// </para>
    /// <para>
    /// <b>The flush is before the key changes hands, not after.</b> A composited
    /// surface never calls <c>Present</c>, so nothing else submits this work at
    /// all, and the consumer runs on its own device: handing the key over with
    /// the copy still queued gives it a texture the GPU has not written yet.
    /// </para>
    /// </remarks>
    internal void Publish()
    {
        if (_surface is not { } surface) return;

        ID3D11Resource* wrapped = surface.Wrapped;
        AcquireWrappedResources(&wrapped, 1);
        try
        {
            ((ID3D11DeviceContext*)_context.Handle)->CopyResource(surface.Shared.Resource, wrapped);
        }
        finally
        {
            ReleaseWrappedResources(&wrapped, 1);
        }

        ((ID3D11DeviceContext*)_context.Handle)->Flush();
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
        ComOwnership.Release(ref _on12);
        ComOwnership.Release(ref _context);
        ComOwnership.Release(ref _device);
    }

    // ─── ID3D11On12Device ────────────────────────────────────
    //
    // Silk.NET 2.23 binds neither D3D11On12CreateDevice nor ID3D11On12Device, so
    // the entry point is declared below and the interface is called through its
    // vtable. Three methods, in the header's own order, which is the trap: the
    // interface declares CreateWrappedResource, then RELEASE, then ACQUIRE, so
    // reading the slots in the order the calls are made puts the two halves of
    // the bracket the wrong way round - and both are void, so a swap compiles,
    // runs, and reports nothing but a wrong picture.

    private ComPtr<ID3D11Resource> WrapResource(D12.ID3D12Resource* resource12)
    {
        // Bind flags that mirror what the D3D12 resource was created with
        // (AllowRenderTarget). The 11On12 layer validates the D3D11 flags
        // against the D3D12 description, so claiming a capability the resource
        // does not have is refused here rather than at the copy.
        var flags = new D3D11ResourceFlags
        {
            BindFlags = (uint)BindFlag.RenderTarget,
            MiscFlags = 0,
            CPUAccessFlags = 0,
            StructureByteStride = 0,
        };

        // In and out are both PixelShaderResource because that is the state
        // D3D12RenderTarget.EndPass leaves its colour attachment in and the
        // state the next frame's barrier is computed from. A pair that does not
        // match the target's own tracking is exactly the silent state error the
        // debug layer is the only witness to.
        ID3D11Resource* wrapped = null;
        Guid guid = ID3D11Resource.Guid;

        // this, pResource12, pFlags11, InState, OutState, riid, ppResource11.
        // Written out because a vtable call has no arity check of any kind: the
        // first version of this line dropped pResource12 and read the flags
        // struct as the resource, which is an access violation with no managed
        // stack under it.
        var create = (delegate* unmanaged[Stdcall]<
            IUnknown*, IUnknown*, D3D11ResourceFlags*, uint, uint, Guid*, void**, int>)Vtbl[3];

        SilkMarshal.ThrowHResult(create(
            (IUnknown*)_on12.Handle, (IUnknown*)resource12, &flags,
            (uint)D12.ResourceStates.PixelShaderResource,
            (uint)D12.ResourceStates.PixelShaderResource,
            &guid, (void**)&wrapped));

        // The one place a resource12 pointer is aliased: CreateWrappedResource
        // takes its own reference on it, so this wrapper owns the D3D11 side
        // alone and the D3D12 target goes on owning the resource.
        return ComOwnership.Own(wrapped);
    }

    private void AcquireWrappedResources(ID3D11Resource** resources, uint count)
    {
        var acquire = (delegate* unmanaged[Stdcall]<IUnknown*, ID3D11Resource**, uint, void>)Vtbl[5];
        acquire((IUnknown*)_on12.Handle, resources, count);
    }

    private void ReleaseWrappedResources(ID3D11Resource** resources, uint count)
    {
        var release = (delegate* unmanaged[Stdcall]<IUnknown*, ID3D11Resource**, uint, void>)Vtbl[4];
        release((IUnknown*)_on12.Handle, resources, count);
    }

    private void** Vtbl => *(void***)_on12.Handle;

    /// <summary>IID_ID3D11On12Device, {85611e73-70a9-490e-9614-a9e302777904}.</summary>
    private static readonly Guid On12DeviceGuid =
        new(0x85611e73, 0x70a9, 0x490e, 0x96, 0x14, 0xa9, 0xe3, 0x02, 0x77, 0x79, 0x04);

    /// <summary>D3D11_RESOURCE_FLAGS: what a wrapped resource looks like to D3D11.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11ResourceFlags
    {
        public uint BindFlags;
        public uint MiscFlags;
        public uint CPUAccessFlags;
        public uint StructureByteStride;
    }

    [LibraryImport("d3d11.dll", EntryPoint = "D3D11On12CreateDevice")]
    private static partial int D3D11On12CreateDevice(
        D12.ID3D12Device* device,
        uint flags,
        D3DFeatureLevel* featureLevels,
        uint featureLevelCount,
        void** commandQueues,
        uint queueCount,
        uint nodeMask,
        ID3D11Device** outDevice,
        ID3D11DeviceContext** outContext,
        D3DFeatureLevel* chosenLevel);

    /// <summary>
    /// One generation's shared texture and the D3D11 alias of the present target
    /// it is copied from.
    /// </summary>
    /// <remarks>
    /// Paired in one object because they are retired together: see
    /// <see cref="Detach"/>.
    /// </remarks>
    private sealed class BridgeSurface(EngineD3D11.D3D11Texture shared, ComPtr<ID3D11Resource> wrapped)
        : IDisposable
    {
        private ComPtr<ID3D11Resource> _wrapped = wrapped;

        internal EngineD3D11.D3D11Texture Shared { get; } = shared;

        internal ID3D11Resource* Wrapped => (ID3D11Resource*)_wrapped.Handle;

        public void Dispose()
        {
            // The alias first: it holds a reference on the D3D12 resource, and
            // the shared texture holds nothing of it, so the order only matters
            // for keeping the two halves of one generation obviously paired.
            ComOwnership.Release(ref _wrapped);
            Shared.Dispose();
        }
    }
}
