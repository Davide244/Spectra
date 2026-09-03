using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using D12 = Silk.NET.Direct3D12;
using D3D11Api = Silk.NET.Direct3D11.D3D11;
using DxgiApi = Silk.NET.DXGI.DXGI;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// The COM ownership rule the engine documents, restated for the shell.
/// </summary>
/// <remarks>
/// <b>This is a copy because <c>SpectraEngine.Core.Graphics.ComOwnership</c> is
/// internal to the engine assembly</b>, and the probe lives here deliberately:
/// Core's graphics folder is under a convention test that bans naming a window,
/// and this is host-side measurement rather than engine code. The rule is
/// unchanged and is not optional. Silk's <c>ComPtr&lt;T&gt;</c> constructor has
/// WRL semantics - it AddRefs rather than adopting - so wrapping a pointer a
/// <c>Create*</c> or <c>QueryInterface</c> call already returned at one leaves
/// the object at two, and disposing the wrapper drops it to one rather than
/// zero. On a texture that is a leak; on anything a swap chain holds it is a
/// crash.
/// </remarks>
internal static unsafe partial class ProbeCom
{
    /// <summary>Wraps <paramref name="raw"/> and releases the caller's reference.</summary>
    internal static ComPtr<T> Own<T>(T* raw) where T : unmanaged, IComVtbl<T>
    {
        if (raw is null)
            return default;

        var owned = new ComPtr<T>(raw);
        ((IUnknown*)raw)->Release();
        return owned;
    }

    /// <summary>
    /// Releases <paramref name="field"/> and clears it, so a second release is
    /// a no-op rather than an over-release.
    /// </summary>
    internal static void Release<T>(ref ComPtr<T> field) where T : unmanaged, IComVtbl<T>
    {
        if (field.Handle is null)
            return;

        field.Dispose();
        field = default;
    }

    // Returned as the raw BOOL: Silk.NET.Core.Native also defines an
    // UnmanagedType, so naming the marshalling attribute here would be
    // ambiguous, and nothing reads the result anyway.
    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    internal static partial int CloseHandle(nint handle);
}

/// <summary>
/// One route's texture: the resource, the handle the compositor is asked to
/// import, and which kind of handle that is.
/// </summary>
/// <remarks>
/// <b>The solid colour is written before the handle leaves this class</b>, under
/// the texture's own keyed mutex where it has one, so the import and the
/// hand-over are the only things the probe is still measuring by the time it
/// asks the compositor anything.
/// </remarks>
internal sealed unsafe class SharedProbeTexture : IDisposable
{
    private ComPtr<ID3D11Texture2D> _texture11;
    private ComPtr<D12.ID3D12Resource> _texture12;
    private nint _handle;
    private readonly bool _ntHandle;

    internal SharedProbeTexture(
        string handleKind,
        int width,
        int height,
        nint handle,
        bool ntHandle,
        bool keyedMutex,
        ComPtr<ID3D11Texture2D> texture11,
        ComPtr<D12.ID3D12Resource> texture12)
    {
        HandleKind = handleKind;
        Width = width;
        Height = height;
        KeyedMutex = keyedMutex;
        _handle = handle;
        _ntHandle = ntHandle;
        _texture11 = texture11;
        _texture12 = texture12;
    }

    /// <summary>The <c>KnownPlatformGraphicsExternalImageHandleTypes</c> value to import under.</summary>
    internal string HandleKind { get; }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>Whether the resource actually carries an <c>IDXGIKeyedMutex</c>.</summary>
    /// <remarks>
    /// A D3D12 resource never does, which is the whole reason route 3 exists as
    /// a separate measurement from route 4.
    /// </remarks>
    internal bool KeyedMutex { get; }

    internal nint Handle => _handle;

    /// <summary>The key the importer must acquire; this side released it.</summary>
    internal uint AcquireKey => 1;

    /// <summary>The key the importer hands back, which is where this side started.</summary>
    internal uint ReleaseKey => 0;

    public void Dispose()
    {
        ProbeCom.Release(ref _texture11);
        ProbeCom.Release(ref _texture12);

        // Only an NT handle is a kernel object. A legacy global shared handle
        // is not one and must not be passed to CloseHandle.
        if (_handle != 0 && _ntHandle)
            _ = ProbeCom.CloseHandle(_handle);
        _handle = 0;
    }
}

/// <summary>
/// Creates the real textures the interop probe hands to the compositor: one per
/// route, on the adapter the compositor reported.
/// </summary>
/// <remarks>
/// <para>
/// <b>A capability flag is not proof.</b> The compositor advertises which handle
/// kinds it imports and how each can be synchronised, and a machine can
/// advertise a kind it cannot actually take a keyed mutex on. The question that
/// gates replacing the viewport's native child is narrower still and is not
/// advertised at all: whether a handle created by a <b>D3D12</b> device is
/// accepted, given that Avalonia's Windows interop is ANGLE - GL ES over D3D11.
/// So this class creates devices and textures and lets the import answer.
/// </para>
/// <para>
/// <b>The adapter is chosen by the compositor's own LUID</b>, not by the system
/// default. A shared handle only opens on the device that created it or on
/// another device on the same adapter, so on a hybrid laptop a probe that took
/// the default adapter would measure a cross-adapter refusal and report it as a
/// driver limitation.
/// </para>
/// <para>
/// <b>Devices are created per route, on demand.</b> A machine with no D3D12
/// support must still return real answers for routes 1 and 2.
/// </para>
/// </remarks>
internal sealed unsafe partial class InteropProbeTextures : IDisposable
{
    /// <summary>Big enough to be a real texture, small enough to cost nothing.</summary>
    internal const int TextureSize = 64;

    private const uint SharedResourceRead = 0x80000000u;
    private const uint SharedResourceWrite = 0x00000001u;

    // D3D12 shared handles take GENERIC_ALL and nothing else.
    private const uint GenericAll = 0x10000000u;

    private const uint Infinite = 0xFFFFFFFFu;

    // Written into every route's texture so a hand-over that succeeds moved
    // something rather than nothing.
    private static readonly float[] SolidColour = [0.10f, 0.55f, 0.90f, 1.0f];

    private readonly ILogger _logger;
    private readonly DxgiApi _dxgi = DxgiApi.GetApi();
    private readonly D3D11Api _d3d11 = D3D11Api.GetApi();
    private readonly D12.D3D12 _d3d12 = D12.D3D12.GetApi();

    private ComPtr<IDXGIAdapter> _adapter;
    private ComPtr<ID3D11Device> _device11;
    private ComPtr<ID3D11DeviceContext> _context11;
    private ComPtr<D12.ID3D12Device> _device12;
    private ComPtr<D12.ID3D12CommandQueue> _queue12;
    private ComPtr<ID3D11Device> _device11On12;
    private ComPtr<ID3D11DeviceContext> _context11On12;

    internal InteropProbeTextures(byte[]? compositorLuid, ILogger logger)
    {
        _logger = logger;
        _adapter = FindAdapter(compositorLuid, out string name);
        AdapterName = name;
        DriverVersion = ReadDriverVersion(_adapter);
    }

    /// <summary>
    /// Reads the adapter's user-mode driver version, or an empty string.
    /// </summary>
    /// <remarks>
    /// Never throws: this is an identifier for a cache key, and a machine whose
    /// driver version cannot be read is a machine whose composited history
    /// simply never matches - which costs a fallback to the native child and
    /// nothing else.
    /// </remarks>
    private static string ReadDriverVersion(ComPtr<IDXGIAdapter> adapter)
    {
        if (adapter.Handle is null)
            return string.Empty;

        try
        {
            Guid device = IDXGIDevice.Guid;
            long umd = 0;
            if (((IDXGIAdapter*)adapter.Handle)->CheckInterfaceSupport(&device, &umd) < 0)
                return string.Empty;

            // The four 16-bit parts a driver version is written as everywhere
            // else, so a value in a settings file can be compared by eye with
            // what Device Manager reports.
            ulong bits = (ulong)umd;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(bits >> 48) & 0xFFFF}.{(bits >> 32) & 0xFFFF}.{(bits >> 16) & 0xFFFF}.{bits & 0xFFFF}");
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>What the textures are created on, and how that was decided.</summary>
    internal string AdapterName { get; }

    /// <summary>
    /// The user-mode driver build behind that adapter, or an empty string when
    /// it could not be read.
    /// </summary>
    /// <remarks>
    /// <b>The other half of a machine's identity, and the half that moves.</b>
    /// A composited viewport that works today and not after a driver update is
    /// exactly the failure the flip policy's green run is guarding against, so
    /// the recorded history has to be anchored to the driver as well as to the
    /// GPU. <c>CheckInterfaceSupport</c> is what reports it; it is documented as
    /// answering for D3D10-era interfaces only, which is why a failure here is
    /// an empty string rather than an error - an unknown driver simply never
    /// matches a recorded one, so the count restarts, which is the safe answer.
    /// </remarks>
    internal string DriverVersion { get; private set; } = string.Empty;

    /// <summary>Route 1: a keyed-mutex D3D11 texture shared through an NT handle.</summary>
    /// <param name="size">
    /// The square's edge. <see cref="TextureSize"/> for a measurement of the
    /// route; one texel for a launch-time rehearsal, where the question is only
    /// whether the compositor will accept the import at all and the cheapest
    /// texture that can be offered is the right one.
    /// </param>
    internal SharedProbeTexture CreateD3D11NtHandleTexture(int size = TextureSize)
    {
        EnsureDevice11();
        return CreateSharedD3D11Texture(_device11, _context11, ntHandle: true, size);
    }

    /// <summary>Route 2: the same texture shared through the legacy global handle.</summary>
    internal SharedProbeTexture CreateD3D11GlobalHandleTexture()
    {
        EnsureDevice11();
        return CreateSharedD3D11Texture(_device11, _context11, ntHandle: false, TextureSize);
    }

    /// <summary>
    /// Route 3, the unanswered question: a committed D3D12 resource on a shared
    /// heap, offered under the same NT-handle kind.
    /// </summary>
    /// <remarks>
    /// <b>It carries no keyed mutex and cannot be made to.</b> D3D12 synchronises
    /// with fences, so if this handle imports at all, the hand-over still has to
    /// find a second synchronisation path - which is exactly the cost the
    /// D3D11On12 bridge buys out.
    /// </remarks>
    internal SharedProbeTexture CreateD3D12Texture()
    {
        EnsureDevice12();
        var device = (D12.ID3D12Device*)_device12.Handle;

        var heapProps = new D12.HeapProperties { Type = D12.HeapType.Default };
        var desc = new D12.ResourceDesc
        {
            Dimension = D12.ResourceDimension.Texture2D,
            Alignment = 0,
            Width = TextureSize,
            Height = TextureSize,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Layout = D12.TextureLayout.LayoutUnknown,
            Flags = D12.ResourceFlags.AllowRenderTarget,
        };

        var clearValue = new D12.ClearValue { Format = Format.FormatR8G8B8A8Unorm };
        clearValue.Anonymous.Color[0] = SolidColour[0];
        clearValue.Anonymous.Color[1] = SolidColour[1];
        clearValue.Anonymous.Color[2] = SolidColour[2];
        clearValue.Anonymous.Color[3] = SolidColour[3];

        D12.ID3D12Resource* raw = null;
        Guid resourceGuid = D12.ID3D12Resource.Guid;

        // COMMON is the state a shared resource is created in; anything else is
        // refused, and the clear below transitions in and back out again.
        SilkMarshal.ThrowHResult(device->CreateCommittedResource(
            &heapProps, D12.HeapFlags.Shared, &desc, D12.ResourceStates.Common, &clearValue,
            &resourceGuid, (void**)&raw));
        ComPtr<D12.ID3D12Resource> texture = ProbeCom.Own(raw);

        try
        {
            ClearD3D12Texture(texture);

            void* handle = null;
            SilkMarshal.ThrowHResult(device->CreateSharedHandle(
                (D12.ID3D12DeviceChild*)texture.Handle, (SecurityAttributes*)null, GenericAll,
                (char*)null, &handle));

            return new SharedProbeTexture(
                KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle,
                TextureSize, TextureSize, (nint)handle, ntHandle: true, keyedMutex: false,
                default, texture);
        }
        catch
        {
            ProbeCom.Release(ref texture);
            throw;
        }
    }

    /// <summary>
    /// Route 4: a D3D11On12 device over the same D3D12 device, and route 1's
    /// texture created on it.
    /// </summary>
    internal SharedProbeTexture CreateD3D11On12Texture()
    {
        EnsureDevice11On12();
        return CreateSharedD3D11Texture(_device11On12, _context11On12, ntHandle: true, TextureSize);
    }

    public void Dispose()
    {
        ProbeCom.Release(ref _context11On12);
        ProbeCom.Release(ref _device11On12);
        ProbeCom.Release(ref _queue12);
        ProbeCom.Release(ref _device12);
        ProbeCom.Release(ref _context11);
        ProbeCom.Release(ref _device11);
        ProbeCom.Release(ref _adapter);
    }

    // --- devices -------------------------------------------------------------

    private void EnsureDevice11()
    {
        if (_device11.Handle is not null)
            return;

        D3DFeatureLevel* levels = stackalloc D3DFeatureLevel[1] { D3DFeatureLevel.Level110 };
        D3DFeatureLevel chosen = default;
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;

        // With an explicit adapter the driver type must be Unknown: D3D11
        // refuses Hardware plus an adapter.
        D3DDriverType driverType = _adapter.Handle is null ? D3DDriverType.Hardware : D3DDriverType.Unknown;

        SilkMarshal.ThrowHResult(_d3d11.CreateDevice(
            (IDXGIAdapter*)_adapter.Handle,
            driverType,
            0,
            (uint)CreateDeviceFlag.BgraSupport,
            levels,
            1,
            D3D11Api.SdkVersion,
            &device,
            &chosen,
            &context));

        _device11 = ProbeCom.Own(device);
        _context11 = ProbeCom.Own(context);
    }

    private void EnsureDevice12()
    {
        if (_device12.Handle is not null)
            return;

        D12.ID3D12Device* device = null;
        Guid deviceGuid = D12.ID3D12Device.Guid;
        SilkMarshal.ThrowHResult(_d3d12.CreateDevice(
            (IUnknown*)_adapter.Handle, D3DFeatureLevel.Level110, &deviceGuid, (void**)&device));
        _device12 = ProbeCom.Own(device);

        // The queue exists for the clear, and because D3D11On12CreateDevice
        // takes one: an 11On12 device is a D3D11 front end over somebody else's
        // command queue.
        var queueDesc = new D12.CommandQueueDesc
        {
            Type = D12.CommandListType.Direct,
            Priority = 0,
            Flags = D12.CommandQueueFlags.None,
            NodeMask = 0,
        };
        D12.ID3D12CommandQueue* queue = null;
        Guid queueGuid = D12.ID3D12CommandQueue.Guid;
        SilkMarshal.ThrowHResult(((D12.ID3D12Device*)_device12.Handle)->CreateCommandQueue(
            &queueDesc, &queueGuid, (void**)&queue));
        _queue12 = ProbeCom.Own(queue);
    }

    private void EnsureDevice11On12()
    {
        if (_device11On12.Handle is not null)
            return;

        EnsureDevice12();

        D3DFeatureLevel* levels = stackalloc D3DFeatureLevel[1] { D3DFeatureLevel.Level110 };
        D3DFeatureLevel chosen = default;
        void* queue = _queue12.Handle;
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;

        SilkMarshal.ThrowHResult(D3D11On12CreateDevice(
            _device12.Handle, 0, levels, 1, &queue, 1, 0, &device, &context, &chosen));

        _device11On12 = ProbeCom.Own(device);
        _context11On12 = ProbeCom.Own(context);
    }

    // Silk.NET 2.23 binds no D3D11On12 entry point and no ID3D11On12Device, so
    // the one function this needs is declared here rather than pulling in a
    // second binding library for it.
    [LibraryImport("d3d11.dll", EntryPoint = "D3D11On12CreateDevice")]
    private static partial int D3D11On12CreateDevice(
        void* device,
        uint flags,
        D3DFeatureLevel* featureLevels,
        uint featureLevelCount,
        void** commandQueues,
        uint queueCount,
        uint nodeMask,
        ID3D11Device** outDevice,
        ID3D11DeviceContext** outContext,
        D3DFeatureLevel* chosenLevel);

    // --- textures ------------------------------------------------------------

    private static SharedProbeTexture CreateSharedD3D11Texture(
        ComPtr<ID3D11Device> device, ComPtr<ID3D11DeviceContext> context, bool ntHandle, int size)
    {
        // SHARED_NTHANDLE is only legal alongside SHARED or SHARED_KEYEDMUTEX,
        // and the keyed mutex is what the compositor's hand-over wants, so both
        // forms of this texture carry one.
        uint misc = (uint)ResourceMiscFlag.SharedKeyedmutex;
        if (ntHandle)
            misc |= (uint)ResourceMiscFlag.SharedNthandle;

        var desc = new Texture2DDesc
        {
            Width = (uint)size,
            Height = (uint)size,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
            CPUAccessFlags = 0,
            MiscFlags = misc,
        };

        ID3D11Texture2D* raw = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)device.Handle)->CreateTexture2D(
            &desc, (SubresourceData*)null, &raw));
        ComPtr<ID3D11Texture2D> texture = ProbeCom.Own(raw);

        ComPtr<IDXGIKeyedMutex> mutex = default;
        ComPtr<ID3D11RenderTargetView> rtv = default;
        try
        {
            IDXGIKeyedMutex* rawMutex = null;
            Guid mutexGuid = IDXGIKeyedMutex.Guid;
            SilkMarshal.ThrowHResult(((ID3D11Texture2D*)texture.Handle)->QueryInterface(
                &mutexGuid, (void**)&rawMutex));
            mutex = ProbeCom.Own(rawMutex);

            // Key 0 is the one a freshly created keyed mutex starts released on.
            SilkMarshal.ThrowHResult(((IDXGIKeyedMutex*)mutex.Handle)->AcquireSync(0, Infinite));
            try
            {
                ID3D11RenderTargetView* view = null;
                SilkMarshal.ThrowHResult(((ID3D11Device*)device.Handle)->CreateRenderTargetView(
                    (ID3D11Resource*)texture.Handle, (RenderTargetViewDesc*)null, &view));
                rtv = ProbeCom.Own(view);

                fixed (float* colour = SolidColour)
                {
                    ((ID3D11DeviceContext*)context.Handle)->ClearRenderTargetView(
                        (ID3D11RenderTargetView*)rtv.Handle, colour);
                }

                // The importer runs on another device, so the write has to have
                // been submitted before the key changes hands.
                ((ID3D11DeviceContext*)context.Handle)->Flush();
            }
            finally
            {
                SilkMarshal.ThrowHResult(((IDXGIKeyedMutex*)mutex.Handle)->ReleaseSync(1));
            }

            (nint handle, string kind) = ntHandle
                ? (CreateNtHandle(texture), KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle)
                : (GetGlobalSharedHandle(texture), KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle);

            return new SharedProbeTexture(
                kind, size, size, handle, ntHandle, keyedMutex: true, texture, default);
        }
        catch
        {
            ProbeCom.Release(ref texture);
            throw;
        }
        finally
        {
            ProbeCom.Release(ref rtv);
            ProbeCom.Release(ref mutex);
        }
    }

    private static nint CreateNtHandle(ComPtr<ID3D11Texture2D> texture)
    {
        IDXGIResource1* raw = null;
        Guid guid = IDXGIResource1.Guid;
        SilkMarshal.ThrowHResult(((ID3D11Texture2D*)texture.Handle)->QueryInterface(&guid, (void**)&raw));
        ComPtr<IDXGIResource1> resource = ProbeCom.Own(raw);
        try
        {
            void* handle = null;
            SilkMarshal.ThrowHResult(((IDXGIResource1*)resource.Handle)->CreateSharedHandle(
                (SecurityAttributes*)null, SharedResourceRead | SharedResourceWrite, (char*)null, &handle));
            return (nint)handle;
        }
        finally
        {
            ProbeCom.Release(ref resource);
        }
    }

    private static nint GetGlobalSharedHandle(ComPtr<ID3D11Texture2D> texture)
    {
        IDXGIResource* raw = null;
        Guid guid = IDXGIResource.Guid;
        SilkMarshal.ThrowHResult(((ID3D11Texture2D*)texture.Handle)->QueryInterface(&guid, (void**)&raw));
        ComPtr<IDXGIResource> resource = ProbeCom.Own(raw);
        try
        {
            void* handle = null;
            SilkMarshal.ThrowHResult(((IDXGIResource*)resource.Handle)->GetSharedHandle(&handle));
            return (nint)handle;
        }
        finally
        {
            ProbeCom.Release(ref resource);
        }
    }

    private void ClearD3D12Texture(ComPtr<D12.ID3D12Resource> texture)
    {
        var device = (D12.ID3D12Device*)_device12.Handle;

        ComPtr<D12.ID3D12DescriptorHeap> heap = default;
        ComPtr<D12.ID3D12CommandAllocator> allocator = default;
        ComPtr<D12.ID3D12GraphicsCommandList> list = default;
        ComPtr<D12.ID3D12Fence> fence = default;
        try
        {
            var heapDesc = new D12.DescriptorHeapDesc
            {
                Type = D12.DescriptorHeapType.Rtv,
                NumDescriptors = 1,
                Flags = D12.DescriptorHeapFlags.None,
                NodeMask = 0,
            };
            D12.ID3D12DescriptorHeap* rawHeap = null;
            Guid heapGuid = D12.ID3D12DescriptorHeap.Guid;
            SilkMarshal.ThrowHResult(device->CreateDescriptorHeap(&heapDesc, &heapGuid, (void**)&rawHeap));
            heap = ProbeCom.Own(rawHeap);

            D12.CpuDescriptorHandle rtv =
                ((D12.ID3D12DescriptorHeap*)heap.Handle)->GetCPUDescriptorHandleForHeapStart();
            device->CreateRenderTargetView(
                (D12.ID3D12Resource*)texture.Handle, (D12.RenderTargetViewDesc*)null, rtv);

            D12.ID3D12CommandAllocator* rawAllocator = null;
            Guid allocatorGuid = D12.ID3D12CommandAllocator.Guid;
            SilkMarshal.ThrowHResult(device->CreateCommandAllocator(
                D12.CommandListType.Direct, &allocatorGuid, (void**)&rawAllocator));
            allocator = ProbeCom.Own(rawAllocator);

            D12.ID3D12GraphicsCommandList* rawList = null;
            Guid listGuid = D12.ID3D12GraphicsCommandList.Guid;
            SilkMarshal.ThrowHResult(device->CreateCommandList(
                0, D12.CommandListType.Direct, (D12.ID3D12CommandAllocator*)allocator.Handle,
                (D12.ID3D12PipelineState*)null, &listGuid, (void**)&rawList));
            list = ProbeCom.Own(rawList);

            var listPtr = (D12.ID3D12GraphicsCommandList*)list.Handle;
            var resourcePtr = (D12.ID3D12Resource*)texture.Handle;

            Transition(listPtr, resourcePtr, D12.ResourceStates.Common, D12.ResourceStates.RenderTarget);
            fixed (float* colour = SolidColour)
                listPtr->ClearRenderTargetView(rtv, colour, 0, (Silk.NET.Maths.Box2D<int>*)null);
            Transition(listPtr, resourcePtr, D12.ResourceStates.RenderTarget, D12.ResourceStates.Common);

            SilkMarshal.ThrowHResult(listPtr->Close());

            var executeList = (D12.ID3D12CommandList*)listPtr;
            ((D12.ID3D12CommandQueue*)_queue12.Handle)->ExecuteCommandLists(1, &executeList);

            D12.ID3D12Fence* rawFence = null;
            Guid fenceGuid = D12.ID3D12Fence.Guid;
            SilkMarshal.ThrowHResult(device->CreateFence(0, D12.FenceFlags.None, &fenceGuid, (void**)&rawFence));
            fence = ProbeCom.Own(rawFence);
            SilkMarshal.ThrowHResult(((D12.ID3D12CommandQueue*)_queue12.Handle)->Signal(
                (D12.ID3D12Fence*)fence.Handle, 1));

            // A spin rather than an event: clearing 64x64 finishes in
            // microseconds, and a probe that could hang is worse than one that
            // reports a timeout.
            var waited = Stopwatch.StartNew();
            while (((D12.ID3D12Fence*)fence.Handle)->GetCompletedValue() < 1)
            {
                if (waited.Elapsed > TimeSpan.FromSeconds(2))
                    throw new TimeoutException("the D3D12 clear did not complete within 2 s");
                Thread.Sleep(0);
            }
        }
        finally
        {
            ProbeCom.Release(ref fence);
            ProbeCom.Release(ref list);
            ProbeCom.Release(ref allocator);
            ProbeCom.Release(ref heap);
        }
    }

    private static void Transition(
        D12.ID3D12GraphicsCommandList* list,
        D12.ID3D12Resource* resource,
        D12.ResourceStates before,
        D12.ResourceStates after)
    {
        var barrier = new D12.ResourceBarrier
        {
            Type = D12.ResourceBarrierType.Transition,
            Flags = D12.ResourceBarrierFlags.None,
        };
        barrier.Anonymous.Transition = new D12.ResourceTransitionBarrier
        {
            PResource = resource,
            Subresource = uint.MaxValue,
            StateBefore = before,
            StateAfter = after,
        };
        list->ResourceBarrier(1, &barrier);
    }

    // --- adapter -------------------------------------------------------------

    private ComPtr<IDXGIAdapter> FindAdapter(byte[]? compositorLuid, out string name)
    {
        if (compositorLuid is not { Length: 8 })
        {
            name = "system default (the compositor reported no LUID)";
            return default;
        }

        uint low = BitConverter.ToUInt32(compositorLuid, 0);
        int high = BitConverter.ToInt32(compositorLuid, 4);

        IDXGIFactory1* rawFactory = null;
        Guid factoryGuid = IDXGIFactory1.Guid;
        if (_dxgi.CreateDXGIFactory1(&factoryGuid, (void**)&rawFactory) < 0)
        {
            name = "system default (adapters could not be enumerated)";
            return default;
        }

        ComPtr<IDXGIFactory1> factory = ProbeCom.Own(rawFactory);
        try
        {
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1* rawAdapter = null;
                if (((IDXGIFactory1*)factory.Handle)->EnumAdapters1(index, &rawAdapter) < 0)
                    break;

                ComPtr<IDXGIAdapter1> adapter = ProbeCom.Own(rawAdapter);
                AdapterDesc1 desc = default;
                ((IDXGIAdapter1*)adapter.Handle)->GetDesc1(&desc);

                if (desc.AdapterLuid.Low != low || desc.AdapterLuid.High != high)
                {
                    ProbeCom.Release(ref adapter);
                    continue;
                }

                string description = DescriptionOf(ref desc);
                IDXGIAdapter* asBase = null;
                Guid baseGuid = IDXGIAdapter.Guid;
                int hr = ((IDXGIAdapter1*)adapter.Handle)->QueryInterface(&baseGuid, (void**)&asBase);
                ProbeCom.Release(ref adapter);

                if (hr < 0)
                {
                    name = $"system default ({description} matched the LUID but could not be queried)";
                    return default;
                }

                _logger.LogDebug("Interop probe: creating textures on {Adapter}", description);
                name = description;
                return ProbeCom.Own(asBase);
            }

            name = "system default (no adapter matched the compositor's LUID)";
            return default;
        }
        finally
        {
            ProbeCom.Release(ref factory);
        }
    }

    // The description is a fixed 128-char UTF-16 buffer inside the struct.
    private static string DescriptionOf(ref AdapterDesc1 desc)
    {
        fixed (char* p = desc.Description)
        {
            var span = new ReadOnlySpan<char>(p, 128);
            int end = span.IndexOf('\0');
            return new string(end < 0 ? span : span[..end]);
        }
    }
}
