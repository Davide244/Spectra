using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;

using D3D11Api = Silk.NET.Direct3D11.D3D11;
using DxgiApi = Silk.NET.DXGI.DXGI;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// A second D3D11 device that opens the producer's shared texture by handle and
/// takes the consumer's turn on it. See <see cref="ISharedTargetConsumer"/> for
/// why a second device rather than a second thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>It serves BOTH backends</b>, because the shared texture is a D3D11 one on
/// both: D3D11 creates it directly and D3D12 creates it through the D3D11On12
/// bridge, precisely so the two cannot disagree about its colour space. So this
/// opens a D3D11 handle either way and there is one implementation rather than
/// two.
/// </para>
/// <para>
/// <b>It asks NO renderer for anything, which is what keeps both backends'
/// files out of this.</b> Everything it needs is on the handle: a shared
/// resource opens on the device that created it or on another device on the
/// same ADAPTER and nowhere else, so the adapter is found by trying each one in
/// turn until the open succeeds. On an ordinary machine that is one attempt; on
/// a hybrid one, or under <c>--adapter=</c>, it is the attempt that finds the
/// GPU the producer actually opened, which a consumer built on the system
/// default would have missed and reported as a broken handle.
/// </para>
/// <para>
/// <b>It really copies.</b> A compositor's turn is an acquire, a snapshot of the
/// whole texture and a release, and the snapshot is most of what the turn
/// costs; a consumer that acquired and released with nothing in between would
/// report a hand-over rate no real consumer can reach. The copy is inside the
/// bracket, and the flush is before the release for the reason the producer
/// flushes before its own: a texture handed over with the copy still queued is
/// a texture the GPU has not finished reading.
/// </para>
/// <para>
/// <b>No debug layer, ever.</b> This is an instrument, it is created inside a
/// measurement, and a validated second device would put its own cost into the
/// number being measured.
/// </para>
/// </remarks>
internal sealed unsafe class D3D11SharedTargetConsumer : ISharedTargetConsumer
{
    /// <summary>WAIT_TIMEOUT, which AcquireSync returns as a success-coded HRESULT.</summary>
    private const int WaitTimeout = 0x00000102;

    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _context;
    private ComPtr<ID3D11Texture2D> _shared;
    private ComPtr<ID3D11Texture2D> _snapshot;
    private ComPtr<IDXGIKeyedMutex> _mutex;

    private D3D11SharedTargetConsumer(
        ComPtr<ID3D11Device> device,
        ComPtr<ID3D11DeviceContext> context,
        ComPtr<ID3D11Texture2D> shared,
        ComPtr<ID3D11Texture2D> snapshot,
        ComPtr<IDXGIKeyedMutex> mutex)
    {
        _device = device;
        _context = context;
        _shared = shared;
        _snapshot = snapshot;
        _mutex = mutex;
    }

    /// <summary>
    /// Opens <paramref name="sharedHandle"/> on a fresh device, searching the
    /// machine's adapters for the one that will take it. Null when none will.
    /// </summary>
    internal static D3D11SharedTargetConsumer? TryOpen(
        nint sharedHandle, int width, int height, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (sharedHandle == 0)
        {
            logger.LogError("The producer published no shared handle to open.");
            return null;
        }

        var dxgi = DxgiApi.GetApi();
        IDXGIFactory1* factoryPtr = null;
        Guid factoryGuid = IDXGIFactory1.Guid;
        if (dxgi.CreateDXGIFactory1(&factoryGuid, (void**)&factoryPtr) < 0)
        {
            logger.LogError("Could not enumerate adapters to open the shared target on.");
            return null;
        }

        ComPtr<IDXGIFactory1> factory = ComOwnership.Own(factoryPtr);
        try
        {
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1* adapterPtr = null;
                if (((IDXGIFactory1*)factory.Handle)->EnumAdapters1(index, &adapterPtr) < 0)
                    break;

                ComPtr<IDXGIAdapter1> adapter = ComOwnership.Own(adapterPtr);
                try
                {
                    // Software adapters are skipped for the reason DxgiAdapters
                    // already states: WARP takes almost anything and then runs at
                    // a hundredth of the speed, which here would turn a pacing
                    // measurement into a measurement of WARP.
                    AdapterDesc1 desc = default;
                    ((IDXGIAdapter1*)adapter.Handle)->GetDesc1(&desc);
                    if ((desc.Flags & (uint)AdapterFlag.Software) != 0)
                        continue;

                    if (TryOpenOn((IDXGIAdapter1*)adapter.Handle, sharedHandle, width, height)
                        is { } opened)
                    {
                        return opened;
                    }
                }
                finally
                {
                    ComOwnership.Release(ref adapter);
                }
            }
        }
        finally
        {
            ComOwnership.Release(ref factory);
        }

        logger.LogError(
            "No adapter on this machine would open the producer's shared handle 0x{Handle:X}.",
            sharedHandle);
        return null;
    }

    private static D3D11SharedTargetConsumer? TryOpenOn(
        IDXGIAdapter1* adapter, nint sharedHandle, int width, int height)
    {
        ComPtr<ID3D11Device> device = default;
        ComPtr<ID3D11DeviceContext> context = default;
        ComPtr<ID3D11Texture2D> shared = default;
        ComPtr<ID3D11Texture2D> snapshot = default;
        ComPtr<IDXGIKeyedMutex> mutex = default;

        try
        {
            var api = D3D11Api.GetApi();
            ID3D11Device* devicePtr = null;
            ID3D11DeviceContext* contextPtr = null;
            D3DFeatureLevel chosen = default;

            // Unknown driver type, which is what D3D11CreateDevice demands when
            // an adapter is named: naming Hardware AND an adapter is refused with
            // E_INVALIDARG, and the message says nothing about which of the two
            // arguments it means.
            SilkMarshal.ThrowHResult(api.CreateDevice(
                (IDXGIAdapter*)adapter, D3DDriverType.Unknown, 0, 0u,
                (D3DFeatureLevel*)null, 0u, D3D11Api.SdkVersion,
                &devicePtr, &chosen, &contextPtr));

            device = ComOwnership.Own(devicePtr);
            context = ComOwnership.Own(contextPtr);

            shared = OpenShared(devicePtr, sharedHandle);
            snapshot = CreateSnapshot(devicePtr, width, height);

            IDXGIKeyedMutex* mutexPtr = null;
            Guid mutexGuid = IDXGIKeyedMutex.Guid;
            SilkMarshal.ThrowHResult(((ID3D11Texture2D*)shared.Handle)->QueryInterface(
                &mutexGuid, (void**)&mutexPtr));
            mutex = ComOwnership.Own(mutexPtr);

            return new D3D11SharedTargetConsumer(device, context, shared, snapshot, mutex);
        }
        catch (Exception)
        {
            // A refusal here is the ordinary answer for every adapter but one, so
            // it is not reported: what a caller needs to know is that NO adapter
            // took it, which TryOpen says once. Half a consumer is worse than
            // none either way - the caller would get an object that can never
            // take a turn, and the producer would then report a timeout per frame
            // instead of the HRESULT that happened.
            ComOwnership.Release(ref mutex);
            ComOwnership.Release(ref snapshot);
            ComOwnership.Release(ref shared);
            ComOwnership.Release(ref context);
            ComOwnership.Release(ref device);
            return null;
        }
    }

    /// <inheritdoc/>
    public bool TakeTurn(int timeoutMs)
    {
        if (_mutex.Handle is null) return false;

        var mutex = (IDXGIKeyedMutex*)_mutex.Handle;
        int hr = mutex->AcquireSync(Renderer.SharedConsumerKey, (uint)Math.Max(0, timeoutMs));

        // WAIT_TIMEOUT is a SUCCESS-coded HRESULT, so the ordinary hr < 0 test
        // reads a producer that never released as an acquisition and the
        // release below then fails on a key this side never held.
        if (hr == WaitTimeout) return false;
        if (hr < 0) return false;

        try
        {
            var context = (ID3D11DeviceContext*)_context.Handle;
            context->CopyResource((ID3D11Resource*)_snapshot.Handle, (ID3D11Resource*)_shared.Handle);
            context->Flush();
        }
        finally
        {
            // In a finally, because dropping the release deadlocks the producer
            // on its next frame with nothing anywhere reporting a disagreement.
            mutex->ReleaseSync(Renderer.SharedProducerKey);
        }

        return true;
    }

    public void Dispose()
    {
        ComOwnership.Release(ref _mutex);
        ComOwnership.Release(ref _snapshot);
        ComOwnership.Release(ref _shared);
        ComOwnership.Release(ref _context);
        ComOwnership.Release(ref _device);
    }

    private static ComPtr<ID3D11Texture2D> OpenShared(ID3D11Device* device, nint sharedHandle)
    {
        // OpenSharedResource1, never OpenSharedResource: the producer minted an
        // NT handle, and the older entry point takes the legacy global one and
        // refuses this with E_INVALIDARG.
        ID3D11Device1* device1Ptr = null;
        Guid device1Guid = ID3D11Device1.Guid;
        SilkMarshal.ThrowHResult(device->QueryInterface(&device1Guid, (void**)&device1Ptr));
        ComPtr<ID3D11Device1> device1 = ComOwnership.Own(device1Ptr);
        try
        {
            ID3D11Texture2D* texturePtr = null;
            Guid textureGuid = ID3D11Texture2D.Guid;
            SilkMarshal.ThrowHResult(device1Ptr->OpenSharedResource1(
                (void*)sharedHandle, &textureGuid, (void**)&texturePtr));
            return ComOwnership.Own(texturePtr);
        }
        finally
        {
            ComOwnership.Release(ref device1);
        }
    }

    private static ComPtr<ID3D11Texture2D> CreateSnapshot(ID3D11Device* device, int width, int height)
    {
        // R8G8B8A8_UNORM, matching the shared RESOURCE rather than its sRGB
        // render-target view: the producer's encode already happened on the way
        // in, and CopyResource requires the two resources to agree.
        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.ShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        ID3D11Texture2D* texturePtr = null;
        SilkMarshal.ThrowHResult(device->CreateTexture2D(&desc, null, &texturePtr));
        return ComOwnership.Own(texturePtr);
    }
}
