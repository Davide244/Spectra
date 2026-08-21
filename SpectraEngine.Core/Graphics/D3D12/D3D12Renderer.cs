using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

// Disambiguate: our namespace and Silk.NET's API class are both named "D3D12".
using D3D12Api = Silk.NET.Direct3D12.D3D12;
using DxgiApi = Silk.NET.DXGI.DXGI;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// Direct3D 12 implementation of <see cref="Renderer"/>. Owns the device, the
/// direct queue, a flip-model swap chain, descriptor heaps, and the per-frame
/// command list; pipelines record into that list between the renderer's
/// begin/end barriers. Runs a single frame in flight (full fence sync after
/// each present) — simple and correct first, deeper pipelining later.
/// </summary>
public sealed unsafe class D3D12Renderer : Renderer
{
    internal const Format BackBufferFormat = Format.FormatR8G8B8A8Unorm;
    internal const Format DepthFormat = Format.FormatD24UnormS8Uint;
    private const uint BufferCount = 2;

    // Hard API ceiling: shader-visible sampler heaps cannot exceed 2048 slots.
    private const uint MaxSamplerRingCapacity = 2048;

    // Resource-binding tier 1's ceiling for a shader-visible CBV/SRV/UAV heap.
    private const uint MaxSrvRingCapacity = 1_000_000;

    // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING — identity RGBA swizzle.
    internal const uint DefaultComponentMapping = 5768;

    internal readonly D3D12Api D3D12Api = D3D12Api.GetApi();
    private readonly DxgiApi _dxgi = DxgiApi.GetApi();
    private readonly D3DCompiler _d3dCompiler = D3DCompiler.GetApi();

    private IWindow? _window;
    private ComPtr<ID3D12Device> _device;
    private ComPtr<ID3D12CommandQueue> _queue;
    private ComPtr<IDXGISwapChain3> _swapChain;
    private ComPtr<ID3D12CommandAllocator> _commandAllocator;
    private ComPtr<ID3D12GraphicsCommandList> _commandList;
    private ComPtr<ID3D12InfoQueue> _infoQueue;

    private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
    private ComPtr<ID3D12DescriptorHeap> _dsvHeap;
    private readonly ComPtr<ID3D12Resource>[] _backBuffers = new ComPtr<ID3D12Resource>[BufferCount];
    private ComPtr<ID3D12Resource> _depthBuffer;
    private uint _rtvStride;
    private uint _frameIndex;

    // Fence-based frame sync (single frame in flight).
    private ComPtr<ID3D12Fence> _fence;
    private ulong _fenceValue;
    private nint _fenceEvent;

    // Per-frame linear upload allocator (cbuffer slices, debug line vertices).
    private ComPtr<ID3D12Resource> _uploadRing;
    private byte* _uploadRingCpu;
    private ulong _uploadRingGpuVa;
    private uint _uploadRingCapacity = 1024 * 1024;
    private uint _uploadRingOffset;

    // Upload rings outgrown mid-frame: the command list being recorded still
    // holds GPU VAs into them (root CBVs, dynamic line VBs), so they must stay
    // alive until the frame's fence completes. Disposed in Present.
    private readonly List<ComPtr<ID3D12Resource>> _retiredUploadRings = [];

    // Shader-visible descriptor rings, reset each frame; draws copy their
    // texture SRVs/samplers in and bind tables at the copied position.
    // Capacities grow between frames when a frame's demand nears the cap
    // (see GrowDescriptorRingsIfNeeded); peaks record each frame's demand.
    private ComPtr<ID3D12DescriptorHeap> _srvRing;
    private ComPtr<ID3D12DescriptorHeap> _samplerRing;
    private uint _srvStride;
    private uint _samplerStride;
    private uint _srvRingOffset;
    private uint _samplerRingOffset;
    private uint _srvRingCapacity = 512;
    private uint _samplerRingCapacity = 256;
    private uint _srvRingPeak;
    private uint _samplerRingPeak;

    // Last table staged this frame, reused by a draw that finds the rings full
    // (see StageDescriptors) — 0 slots means nothing has been staged yet.
    private GpuDescriptorHandle _lastSrvTable;
    private GpuDescriptorHandle _lastSamplerTable;
    private uint _stagedSlotCount;
    private bool _ringOverflowReported;

    // Draws a frame may issue beyond its RenderView (the debug-draw overlay,
    // and headroom so a one-item growth does not force a reallocation).
    private const int DescriptorReserveSlackDraws = 64;

    // Every resource built by the Create* factories is tracked here so
    // Shutdown can free stragglers. Meshes/textures leave early through
    // Renderer.DestroyMesh/DestroyTexture via the Unregister callback handed
    // out at creation. Unsynchronized: creation and destruction both happen
    // on the render thread.
    private readonly List<Mesh> _meshes = [];
    private readonly List<Texture> _textures = [];
    private readonly List<ShaderProgram> _shaders = [];
    private readonly List<ID3D12RenderPipeline> _pipelines = [];
    private int _pipelineIndex;

    // Size the swap chain currently has. Render() compares it against the
    // engine-fed base-class framebuffer latch each frame and reruns the resize
    // path when the window has changed; the resize must run on this (render)
    // thread between frames, never in a window event.
    private Vector2D<int> _swapChainSize;

    private D3D12LineBatch? _lineBatch;
    private ShaderProgram? _debugShader;
    private D3D12Texture? _fallbackTexture;
    private bool _isRecording;

    // Same GL→D3D clip-space Z remap as the D3D11 backend; the two APIs share
    // clip-space conventions. Row-vector: z_d3d = 0.5*z_gl + 0.5*w_gl.
    public static readonly Matrix4x4 GlToD3dClipZ = new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 0.5f, 0f,
        0f, 0f, 0.5f, 1f);

    public override GraphicsBackend Backend => GraphicsBackend.D3D12;

    /// <summary>D3D12 creates its own device, so the window must not bring up an OpenGL context.</summary>
    public override GraphicsAPI WindowApi => GraphicsAPI.None;

    public override void AcquireContext(IWindow window) { /* not thread-affine */ }
    public override void ReleaseContext(IWindow window) { }

    public override string CurrentPipelineName =>
        _pipelines.Count == 0 ? "None" : _pipelines[_pipelineIndex].Name;

    internal ID3D12Device* DevicePtr => (ID3D12Device*)_device.Handle;

    /// <summary>The command list pipelines and resources record into; null outside a frame.</summary>
    internal ID3D12GraphicsCommandList* CurrentList => _isRecording ? (ID3D12GraphicsCommandList*)_commandList.Handle : null;

    /// <summary>The program most recently activated with <see cref="ShaderProgram.Use"/>; meshes resolve PSOs against it.</summary>
    internal D3D12ShaderProgram? CurrentProgram { get; set; }

    /// <summary>Fill mode baked into PSOs for subsequent draws; set by the active pipeline.</summary>
    internal FillMode CurrentFillMode { get; set; } = FillMode.Solid;

    /// <summary>
    /// Monotonic frame counter (first frame renders as 1). Shader programs use
    /// it to detect their first Use() per frame: the upload ring restarts every
    /// frame, so slices cached from an earlier frame must not be rebound.
    /// </summary>
    internal ulong FrameNumber { get; private set; }

    public D3D12Renderer(ILogger<Renderer> logger, IShaderCompiler shaderCompiler)
        : base(logger, shaderCompiler)
    {
    }

    // ─── Initialization ──────────────────────────────────────

    public override void Initialize(IWindow window)
    {
        _window = window;

        // Read the engine-fed latch, not window.FramebufferSize: this runs on
        // the render thread while the main thread is already pumping
        // glfwPollEvents, and GLFW guarantees no thread safety for that pair.
        // The engine seeded the latch before this thread started.
        Vector2D<int> size = FramebufferSize;
        _swapChainSize = size;

        CreateDevice();
        CreateQueueAndSwapChain(window, (uint)size.X, (uint)size.Y);
        CreateFrameResources((uint)size.X, (uint)size.Y);

        DefaultShader = BaseShaders.LitPath is { } litPath
            ? CreateShaderFromFile(litPath)
            : CreateShaderFromSource(BaseShaders.Lit);
        _debugShader = BaseShaders.DebugLinePath is { } debugPath
            ? CreateShaderFromFile(debugPath)
            : CreateShaderFromSource(BaseShaders.DebugLine);
        // Debug overlays draw always-on-top (depth off), matching the OpenGL
        // backend's depth-disabled flush; must be set before the first draw
        // builds a PSO.
        ((D3D12ShaderProgram)_debugShader).DepthTestEnabled = false;
        _lineBatch = new D3D12LineBatch(this, (D3D12ShaderProgram)_debugShader);

        // 1×1 white fallback so unset texture slots in a descriptor table are
        // always valid (sampling it is a no-op multiply).
        ReadOnlySpan<byte> white = [255, 255, 255, 255];
        _fallbackTexture = new D3D12Texture(this, white, 1, 1, TextureFormat.Rgba8, TextureFilter.Nearest, TextureWrap.Repeat);

        RegisterPipeline(new D3D12ForwardPipeline());
        RegisterPipeline(new D3D12WireframePipeline());

        DrainDebugMessages();
        _logger.LogInformation("Renderer initialized (D3D12, pipeline={Pipeline})", CurrentPipelineName);
    }

    private void CreateDevice()
    {
        // Enable the debug layer when the SDK layers are installed; fall back
        // silently so dev machines without Graphics Tools still run.
        ID3D12Debug* debug = null;
        Guid debugGuid = ID3D12Debug.Guid;
        if (D3D12Api.GetDebugInterface(&debugGuid, (void**)&debug) >= 0)
        {
            debug->EnableDebugLayer();
            debug->Release();
            _logger.LogInformation("D3D12 debug layer active.");
        }
        else
        {
            _logger.LogInformation("D3D12 debug layer unavailable; creating without it.");
        }

        ID3D12Device* device = null;
        Guid deviceGuid = ID3D12Device.Guid;
        SilkMarshal.ThrowHResult(D3D12Api.CreateDevice(
            default(ComPtr<IUnknown>), D3DFeatureLevel.Level110, &deviceGuid, (void**)&device));
        _device = new ComPtr<ID3D12Device>(device);

        ID3D12InfoQueue* infoQueue = null;
        Guid infoQueueGuid = ID3D12InfoQueue.Guid;
        if (device->QueryInterface(&infoQueueGuid, (void**)&infoQueue) >= 0)
            _infoQueue = new ComPtr<ID3D12InfoQueue>(infoQueue);
    }

    private void CreateQueueAndSwapChain(IWindow window, uint width, uint height)
    {
        var native = window.Native
            ?? throw new InvalidOperationException("D3D12 requires a native window handle; window has none.");
        nint hwnd = native.Win32?.Hwnd
            ?? throw new InvalidOperationException("D3D12 backend only runs on Win32 (need HWND).");

        var queueDesc = new CommandQueueDesc
        {
            Type = CommandListType.Direct,
            Priority = 0,
            Flags = CommandQueueFlags.None,
            NodeMask = 0,
        };
        ID3D12CommandQueue* queue = null;
        Guid queueGuid = ID3D12CommandQueue.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateCommandQueue(&queueDesc, &queueGuid, (void**)&queue));
        _queue = new ComPtr<ID3D12CommandQueue>(queue);

        IDXGIFactory2* factory = null;
        Guid factoryGuid = IDXGIFactory2.Guid;
        SilkMarshal.ThrowHResult(_dxgi.CreateDXGIFactory2(0u, &factoryGuid, (void**)&factory));

        // Flip model is mandatory on D3D12. The per-frame full fence sync means
        // the rotating back buffer is never in flight when we touch it.
        var desc = new SwapChainDesc1
        {
            Width = width,
            Height = height,
            Format = BackBufferFormat,
            Stereo = 0,
            SampleDesc = new SampleDesc(1, 0),
            BufferUsage = DxgiApi.UsageRenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Unspecified,
            Flags = 0,
        };

        IDXGISwapChain1* swapChain1 = null;
        SilkMarshal.ThrowHResult(factory->CreateSwapChainForHwnd(
            (IUnknown*)queue, hwnd, &desc, null, null, &swapChain1));

        IDXGISwapChain3* swapChain3 = null;
        Guid sc3Guid = IDXGISwapChain3.Guid;
        SilkMarshal.ThrowHResult(swapChain1->QueryInterface(&sc3Guid, (void**)&swapChain3));
        swapChain1->Release();
        _swapChain = new ComPtr<IDXGISwapChain3>(swapChain3);
        factory->Release();

        _frameIndex = _swapChain.GetCurrentBackBufferIndex();
    }

    private void CreateFrameResources(uint width, uint height)
    {
        _rtvHeap = CreateDescriptorHeap(DescriptorHeapType.Rtv, BufferCount, shaderVisible: false);
        _dsvHeap = CreateDescriptorHeap(DescriptorHeapType.Dsv, 1, shaderVisible: false);
        _rtvStride = DevicePtr->GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
        _srvStride = DevicePtr->GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);
        _samplerStride = DevicePtr->GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);

        CreateBackBufferViews(width, height);

        ID3D12CommandAllocator* allocator = null;
        Guid allocGuid = ID3D12CommandAllocator.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateCommandAllocator(CommandListType.Direct, &allocGuid, (void**)&allocator));
        _commandAllocator = new ComPtr<ID3D12CommandAllocator>(allocator);

        ID3D12GraphicsCommandList* list = null;
        Guid listGuid = ID3D12GraphicsCommandList.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateCommandList(
            0, CommandListType.Direct, allocator, (ID3D12PipelineState*)null, &listGuid, (void**)&list));
        _commandList = new ComPtr<ID3D12GraphicsCommandList>(list);
        SilkMarshal.ThrowHResult(list->Close()); // lists are created open

        ID3D12Fence* fence = null;
        Guid fenceGuid = ID3D12Fence.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateFence(0, FenceFlags.None, &fenceGuid, (void**)&fence));
        _fence = new ComPtr<ID3D12Fence>(fence);
        _fenceEvent = Kernel32.CreateEvent(0, 0, 0, null);
        if (_fenceEvent == 0)
            throw new InvalidOperationException("Failed to create fence event.");

        _uploadRing = CreateUploadBuffer(_uploadRingCapacity, "FrameUploadRing");
        MapUploadRing();

        _srvRing = CreateDescriptorHeap(DescriptorHeapType.CbvSrvUav, _srvRingCapacity, shaderVisible: true);
        _samplerRing = CreateDescriptorHeap(DescriptorHeapType.Sampler, _samplerRingCapacity, shaderVisible: true);
    }

    private void MapUploadRing()
    {
        var res = (ID3D12Resource*)_uploadRing.Handle;
        void* cpu = null;
        var readRange = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
        SilkMarshal.ThrowHResult(res->Map(0, &readRange, &cpu));
        _uploadRingCpu = (byte*)cpu;
        _uploadRingGpuVa = res->GetGPUVirtualAddress();
    }

    private void CreateBackBufferViews(uint width, uint height)
    {
        var rtvStart = ((ID3D12DescriptorHeap*)_rtvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();
        for (uint i = 0; i < BufferCount; i++)
        {
            ID3D12Resource* backBuffer = null;
            Guid resGuid = ID3D12Resource.Guid;
            SilkMarshal.ThrowHResult(((IDXGISwapChain3*)_swapChain.Handle)->GetBuffer(i, &resGuid, (void**)&backBuffer));
            _backBuffers[i] = new ComPtr<ID3D12Resource>(backBuffer);

            var handle = new CpuDescriptorHandle { Ptr = rtvStart.Ptr + i * _rtvStride };
            DevicePtr->CreateRenderTargetView(backBuffer, null, handle);
        }

        var depthDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = DepthFormat,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowDepthStencil,
        };
        var heapProps = new HeapProperties { Type = HeapType.Default };
        var clearValue = new ClearValue { Format = DepthFormat };
        clearValue.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 };

        ID3D12Resource* depth = null;
        Guid depthGuid = ID3D12Resource.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateCommittedResource(
            &heapProps, HeapFlags.None, &depthDesc, ResourceStates.DepthWrite, &clearValue, &depthGuid, (void**)&depth));
        _depthBuffer = new ComPtr<ID3D12Resource>(depth);

        var dsvHandle = ((ID3D12DescriptorHeap*)_dsvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();
        DevicePtr->CreateDepthStencilView(depth, null, dsvHandle);
    }

    private void ReleaseBackBufferViews()
    {
        for (int i = 0; i < _backBuffers.Length; i++)
        {
            _backBuffers[i].Dispose();
            _backBuffers[i] = default;
        }
        _depthBuffer.Dispose();
        _depthBuffer = default;
    }

    // ─── Frame loop ──────────────────────────────────────────

    public void RegisterPipeline(ID3D12RenderPipeline pipeline)
    {
        pipeline.Initialize(this);
        _pipelines.Add(pipeline);
    }

    public override string NextPipeline()
    {
        if (_pipelines.Count == 0)
            return "None";
        _pipelineIndex = (_pipelineIndex + 1) % _pipelines.Count;
        _logger.LogInformation("Pipeline switched to {Pipeline}", CurrentPipelineName);
        return CurrentPipelineName;
    }

    public override void Render(Scene.Scene? scene, RenderView view, double deltaTime)
    {
        DrainPendingResize();
        HotReloader.PumpPendingReloads();

        if (_pipelines.Count == 0 || _window is null) return;

        var allocator = (ID3D12CommandAllocator*)_commandAllocator.Handle;
        var list = (ID3D12GraphicsCommandList*)_commandList.Handle;
        SilkMarshal.ThrowHResult(allocator->Reset());
        SilkMarshal.ThrowHResult(list->Reset(allocator, (ID3D12PipelineState*)null));
        _isRecording = true;
        CurrentProgram = null;
        CurrentFillMode = FillMode.Solid;

        // Fresh per-frame arenas — safe because the previous frame fully
        // synced on the fence before this one starts.
        FrameNumber++;
        _uploadRingOffset = 0;
        _srvRingOffset = 0;
        _samplerRingOffset = 0;
        _stagedSlotCount = 0;
        _ringOverflowReported = false;

        // Size the rings for the draw list about to be recorded, BEFORE the
        // heaps are bound. Growing between frames from the previous frame's
        // peak (GrowDescriptorRingsIfNeeded) only absorbs demand that rises
        // gradually: a camera cut or a fast yaw that pulls a dense region into
        // the frustum multiplies the visible draw count in a single frame, and
        // per-material world batching multiplies it again. The view is already
        // built and culled at this point, so the exact demand is known here —
        // and here is the one place a heap can be swapped safely (GPU idle on
        // the Present fence, command list reset, nothing bound yet).
        ReserveDescriptorRings(view);

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2]
        {
            (ID3D12DescriptorHeap*)_srvRing.Handle,
            (ID3D12DescriptorHeap*)_samplerRing.Handle,
        };
        list->SetDescriptorHeaps(2, heaps);

        Transition(list, (ID3D12Resource*)_backBuffers[_frameIndex].Handle,
            ResourceStates.Present, ResourceStates.RenderTarget);

        var rtvStart = ((ID3D12DescriptorHeap*)_rtvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();
        var context = new D3D12RenderContext
        {
            Renderer = this,
            BackBufferRtv = new CpuDescriptorHandle { Ptr = rtvStart.Ptr + _frameIndex * _rtvStride },
            DepthView = ((ID3D12DescriptorHeap*)_dsvHeap.Handle)->GetCPUDescriptorHandleForHeapStart(),
            Scene = scene,
            View = view,
            DeltaTime = deltaTime,
        };
        _pipelines[_pipelineIndex].Execute(context);

        Transition(list, (ID3D12Resource*)_backBuffers[_frameIndex].Handle,
            ResourceStates.RenderTarget, ResourceStates.Present);

        SilkMarshal.ThrowHResult(list->Close());
        _isRecording = false;

        ID3D12CommandList* executeList = (ID3D12CommandList*)list;
        ((ID3D12CommandQueue*)_queue.Handle)->ExecuteCommandLists(1, &executeList);
    }

    public override void Present(IWindow window)
    {
        if (_swapChain.Handle is null) return;
        SilkMarshal.ThrowHResult(((IDXGISwapChain3*)_swapChain.Handle)->Present(0, 0));
        WaitForGpu();

        // GPU idle and no list recording: the only safe point to free upload
        // rings the frame outgrew and to swap descriptor rings for bigger ones.
        DisposeRetiredUploadRings();
        GrowDescriptorRingsIfNeeded();

        _frameIndex = _swapChain.GetCurrentBackBufferIndex();
        DrainDebugMessages();
    }

    /// <summary>Frees upload rings retired by mid-frame growth. Call only after the frame fence completed.</summary>
    private void DisposeRetiredUploadRings()
    {
        if (_retiredUploadRings.Count == 0) return;
        foreach (var ring in _retiredUploadRings)
            ring.Dispose();
        _retiredUploadRings.Clear();
    }

    /// <summary>
    /// Grows the shader-visible descriptor rings, if needed, to hold every draw
    /// in <paramref name="view"/> plus a fixed slack for the debug overlay and
    /// anything else drawn outside the view. Call at the top of a frame, before
    /// the heaps are bound and before anything is recorded.
    /// </summary>
    /// <remarks>
    /// The per-draw cost is the widest SRV table any loaded program declares —
    /// the renderer cannot know which program each item will pick, so it sizes
    /// for the worst case. That over-reserves a few descriptor slots, which cost
    /// 32 bytes each; the alternative (guessing low) is the mid-frame
    /// exhaustion this exists to prevent.
    /// </remarks>
    private void ReserveDescriptorRings(RenderView view)
    {
        uint perDraw = 0;
        for (int i = 0; i < _shaders.Count; i++)
        {
            if (_shaders[i] is D3D12ShaderProgram program)
                perDraw = Math.Max(perDraw, program.SrvCount);
        }

        if (perDraw == 0) return;

        // Both lists are drawn one item at a time (see the pipelines' DrawView).
        long draws = (long)view.Items.Count + view.WorldItems.Count + DescriptorReserveSlackDraws;
        ulong required = (ulong)draws * perDraw;

        _srvRingCapacity = EnsureRingCapacity(
            ref _srvRing, DescriptorHeapType.CbvSrvUav, _srvRingCapacity, required, MaxSrvRingCapacity, "SRV");
        _samplerRingCapacity = EnsureRingCapacity(
            ref _samplerRing, DescriptorHeapType.Sampler, _samplerRingCapacity, required,
            MaxSamplerRingCapacity, "sampler");
    }

    // Returns the capacity to record for a ring, recreating the heap when the
    // frame needs more than it holds. Capped rings (samplers) stop at their
    // ceiling; StageDescriptors degrades rather than crashing past it.
    private uint EnsureRingCapacity(
        ref ComPtr<ID3D12DescriptorHeap> ring,
        DescriptorHeapType type,
        uint capacity,
        ulong required,
        uint maximum,
        string label)
    {
        if (required <= capacity || capacity >= maximum) return capacity;

        uint target = capacity;
        while (target < required && target < maximum)
        {
            // Doubling keeps the number of reallocations logarithmic in scene
            // size; the clamp stops the shift from overflowing at the ceiling.
            target = target > maximum / 2 ? maximum : target * 2;
        }

        ring.Dispose();
        ring = CreateDescriptorHeap(type, target, shaderVisible: true);
        _logger.LogInformation(
            "D3D12 {Label} descriptor ring sized to {Capacity} slots for a {Required}-descriptor frame",
            label, target, required);
        return target;
    }

    /// <summary>
    /// Recreates a shader-visible descriptor ring one size up when the last
    /// frame's demand crossed ~75% of its capacity. A backstop under
    /// <see cref="ReserveDescriptorRings"/> for demand the draw list does not
    /// account for. Must run between frames (after the Present-side fence wait):
    /// with a single frame in flight, nothing on the GPU or CPU references the
    /// old heap there.
    /// </summary>
    private void GrowDescriptorRingsIfNeeded()
    {
        if (_srvRingPeak * 4 > _srvRingCapacity * 3)
        {
            uint newCapacity = _srvRingCapacity;
            while (_srvRingPeak * 4 > newCapacity * 3)
                newCapacity *= 2;
            _srvRing.Dispose();
            _srvRing = CreateDescriptorHeap(DescriptorHeapType.CbvSrvUav, newCapacity, shaderVisible: true);
            _srvRingCapacity = newCapacity;
            _logger.LogInformation("D3D12 SRV descriptor ring grown to {Capacity} slots", newCapacity);
        }

        if (_samplerRingPeak * 4 > _samplerRingCapacity * 3 && _samplerRingCapacity < MaxSamplerRingCapacity)
        {
            uint newCapacity = _samplerRingCapacity;
            while (_samplerRingPeak * 4 > newCapacity * 3 && newCapacity < MaxSamplerRingCapacity)
                newCapacity *= 2;
            _samplerRing.Dispose();
            _samplerRing = CreateDescriptorHeap(DescriptorHeapType.Sampler, newCapacity, shaderVisible: true);
            _samplerRingCapacity = newCapacity;
            _logger.LogInformation("D3D12 sampler descriptor ring grown to {Capacity} slots", newCapacity);
        }

        _srvRingPeak = 0;
        _samplerRingPeak = 0;
    }

    /// <summary>Blocks until the queue has finished all submitted work.</summary>
    internal void WaitForGpu()
    {
        if (_fence.Handle is null) return;
        ulong value = ++_fenceValue;
        SilkMarshal.ThrowHResult(((ID3D12CommandQueue*)_queue.Handle)->Signal((ID3D12Fence*)_fence.Handle, value));
        if (((ID3D12Fence*)_fence.Handle)->GetCompletedValue() < value)
        {
            SilkMarshal.ThrowHResult(((ID3D12Fence*)_fence.Handle)->SetEventOnCompletion(value, (void*)_fenceEvent));
            Kernel32.WaitForSingleObject(_fenceEvent, Kernel32.Infinite);
        }
    }

    private static void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* resource,
        ResourceStates before, ResourceStates after)
    {
        var barrier = new ResourceBarrier
        {
            Type = ResourceBarrierType.Transition,
            Flags = ResourceBarrierFlags.None,
        };
        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource = resource,
            Subresource = uint.MaxValue, // all subresources
            StateBefore = before,
            StateAfter = after,
        };
        list->ResourceBarrier(1, &barrier);
    }

    internal void SetViewportAndScissor(int width, int height)
    {
        var list = CurrentList;
        if (list is null) return;
        var viewport = new Viewport
        {
            TopLeftX = 0,
            TopLeftY = 0,
            Width = width,
            Height = height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        list->RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, width, height);
        list->RSSetScissorRects(1, &scissor);
    }

    /// <summary>
    /// Uploads and draws the accumulated <see cref="Renderer.DebugDraw"/> lines.
    /// Called by pipelines after their main scene pass.
    /// </summary>
    internal void FlushDebugDraw(Scene.Camera camera)
    {
        if (DebugDraw.VertexCount == 0 || _debugShader is null || _lineBatch is null) return;

        var debug = (D3D12ShaderProgram)_debugShader;
        debug.SetUniform("uView", camera.View);
        debug.SetUniform("uProjection", camera.Projection * GlToD3dClipZ);
        debug.Use();
        _lineBatch.Draw(DebugDraw.Vertices, (uint)DebugDraw.VertexCount);
    }

    // ─── Per-frame arenas ────────────────────────────────────

    internal readonly struct UploadSlice
    {
        public required byte* Cpu { get; init; }
        public required ulong GpuVa { get; init; }
    }

    /// <summary>Bump-allocates a slice of the frame upload ring (grows the ring when exhausted).</summary>
    internal UploadSlice AllocUpload(uint size, uint alignment)
    {
        uint aligned = (_uploadRingOffset + alignment - 1) / alignment * alignment;
        if (aligned + size > _uploadRingCapacity)
        {
            // The command list being recorded already holds GPU VAs into this
            // ring (root CBVs, dynamic line VBs), and WaitForGpu can only fence
            // SUBMITTED work — so the old ring must be retired, not destroyed,
            // and freed only after this frame's fence (see Present). The
            // replacement is a distinct resource, so restarting its offset at
            // 0 cannot alias slices handed out earlier this frame.
            ((ID3D12Resource*)_uploadRing.Handle)->Unmap(0, null);
            _uploadRingCpu = null;
            _retiredUploadRings.Add(_uploadRing);

            // The field must stop aliasing the retired-list entry BEFORE the
            // replacement is created: if CreateUploadBuffer or the Map below
            // throws, Shutdown would otherwise dispose the same resource twice
            // (field + list) and underflow its COM refcount.
            _uploadRing = default;

            // Size for the whole frame's demand so far (old offset + this
            // request), so the next frame fits in a single ring.
            while (aligned + size > _uploadRingCapacity)
                _uploadRingCapacity *= 2;
            _uploadRing = CreateUploadBuffer(_uploadRingCapacity, "FrameUploadRing");
            MapUploadRing();
            _logger.LogInformation("D3D12 upload ring grown to {Size} KiB", _uploadRingCapacity / 1024);
            aligned = 0;
        }

        _uploadRingOffset = aligned + size;
        return new UploadSlice
        {
            Cpu = _uploadRingCpu + aligned,
            GpuVa = _uploadRingGpuVa + aligned,
        };
    }

    /// <summary>
    /// Copies the pending texture SRVs/samplers (fallback white for unset
    /// slots) into the shader-visible rings and returns the tables' GPU handles.
    /// </summary>
    internal (GpuDescriptorHandle SrvTable, GpuDescriptorHandle SamplerTable) StageDescriptors(
        Dictionary<uint, D3D12Texture> pending, uint slotCount)
    {
        // Record demand even when it does not fit, so the between-frames
        // growth (GrowDescriptorRingsIfNeeded) sizes the next heap correctly.
        uint srvDemand = _srvRingOffset + slotCount;
        uint samplerDemand = _samplerRingOffset + slotCount;
        _srvRingPeak = Math.Max(_srvRingPeak, srvDemand);
        _samplerRingPeak = Math.Max(_samplerRingPeak, samplerDemand);

        if (srvDemand > _srvRingCapacity || samplerDemand > _samplerRingCapacity)
        {
            // A single draw wider than a whole ring is a program/root-signature
            // problem, not a scene-size one, and there is no descriptor range to
            // hand back — that one still has to be fatal.
            if (slotCount > _srvRingCapacity || slotCount > _samplerRingCapacity)
                throw new InvalidOperationException(
                    $"A single draw needs {slotCount} descriptors, more than the whole shader-visible ring " +
                    $"(SRV {_srvRingCapacity}, sampler {_samplerRingCapacity}). Raise the initial ring " +
                    "capacities in D3D12Renderer (sampler heaps are capped at 2048 slots by the API).");

            // Exhaustion for the ordinary reason — too many draws — must NOT
            // throw: this runs deep inside the pipeline's draw loop, so the
            // exception would escape Render, leave the command list open, and
            // take the render thread (and the process) down. The frame is
            // already sized from the draw list at its start
            // (ReserveDescriptorRings), so getting here means demand the view
            // did not account for, or the API's hard sampler ceiling. Reuse the
            // last staged table: this draw samples the previous draw's textures
            // — visibly wrong for the draws past the cap, but every draw before
            // them keeps its own descriptors and the frame still completes.
            if (!_ringOverflowReported)
            {
                _ringOverflowReported = true;
                _logger.LogWarning(
                    "D3D12 shader-visible descriptor rings exhausted mid-frame (SRV {SrvDemand}/{SrvCapacity}, " +
                    "sampler {SamplerDemand}/{SamplerCapacity}); further draws reuse the last staged " +
                    "descriptor table and will sample the wrong textures",
                    srvDemand, _srvRingCapacity, samplerDemand, _samplerRingCapacity);
            }

            if (_stagedSlotCount == slotCount)
                return (_lastSrvTable, _lastSamplerTable);

            // Nothing compatible to borrow (first draw of the frame, or a
            // different table width): restart the ring. Descriptors staged
            // earlier are overwritten, so those draws sample this one's
            // textures — still only a visual defect, and unreachable in
            // practice because the rings hold hundreds of entries.
            _srvRingOffset = 0;
            _samplerRingOffset = 0;
        }

        var srvHeap = (ID3D12DescriptorHeap*)_srvRing.Handle;
        var samplerHeap = (ID3D12DescriptorHeap*)_samplerRing.Handle;
        var srvCpuStart = srvHeap->GetCPUDescriptorHandleForHeapStart();
        var srvGpuStart = srvHeap->GetGPUDescriptorHandleForHeapStart();
        var samplerCpuStart = samplerHeap->GetCPUDescriptorHandleForHeapStart();
        var samplerGpuStart = samplerHeap->GetGPUDescriptorHandleForHeapStart();

        uint srvBase = _srvRingOffset;
        uint samplerBase = _samplerRingOffset;

        for (uint slot = 0; slot < slotCount; slot++)
        {
            var texture = pending.TryGetValue(slot, out var t) ? t : _fallbackTexture!;
            var srvDst = new CpuDescriptorHandle { Ptr = srvCpuStart.Ptr + (srvBase + slot) * _srvStride };
            var samplerDst = new CpuDescriptorHandle { Ptr = samplerCpuStart.Ptr + (samplerBase + slot) * _samplerStride };
            DevicePtr->CopyDescriptorsSimple(1, srvDst, texture.SrvCpu, DescriptorHeapType.CbvSrvUav);
            DevicePtr->CopyDescriptorsSimple(1, samplerDst, texture.SamplerCpu, DescriptorHeapType.Sampler);
        }

        _srvRingOffset += slotCount;
        _samplerRingOffset += slotCount;

        _lastSrvTable = new GpuDescriptorHandle { Ptr = srvGpuStart.Ptr + srvBase * _srvStride };
        _lastSamplerTable = new GpuDescriptorHandle { Ptr = samplerGpuStart.Ptr + samplerBase * _samplerStride };
        _stagedSlotCount = slotCount;
        return (_lastSrvTable, _lastSamplerTable);
    }

    // ─── Resource creation helpers ───────────────────────────

    internal ComPtr<ID3D12DescriptorHeap> CreateDescriptorHeap(DescriptorHeapType type, uint count, bool shaderVisible)
    {
        var desc = new DescriptorHeapDesc
        {
            Type = type,
            NumDescriptors = count,
            Flags = shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None,
            NodeMask = 0,
        };
        ID3D12DescriptorHeap* heap = null;
        Guid heapGuid = ID3D12DescriptorHeap.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateDescriptorHeap(&desc, &heapGuid, (void**)&heap));
        return new ComPtr<ID3D12DescriptorHeap>(heap);
    }

    internal ComPtr<ID3D12Resource> CreateUploadBuffer(uint sizeBytes, string debugName)
    {
        var heapProps = new HeapProperties { Type = HeapType.Upload };
        var desc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,
            Width = Math.Max(sizeBytes, 1u),
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutRowMajor,
            Flags = ResourceFlags.None,
        };
        ID3D12Resource* res = null;
        Guid resGuid = ID3D12Resource.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateCommittedResource(
            &heapProps, HeapFlags.None, &desc, ResourceStates.GenericRead, null, &resGuid, (void**)&res));
        return new ComPtr<ID3D12Resource>(res);
    }

    internal ComPtr<ID3D12Resource> CreateTexture2D(uint width, uint height, ushort mipLevels, Format format)
    {
        var heapProps = new HeapProperties { Type = HeapType.Default };
        var desc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = mipLevels,
            Format = format,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.None,
        };
        ID3D12Resource* res = null;
        Guid resGuid = ID3D12Resource.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateCommittedResource(
            &heapProps, HeapFlags.None, &desc, ResourceStates.CopyDest, null, &resGuid, (void**)&res));
        return new ComPtr<ID3D12Resource>(res);
    }

    /// <summary>
    /// Stages every mip level through an upload buffer, records the copies plus
    /// the final transition to pixel-shader-resource on the frame command list,
    /// and executes immediately (blocking). Only used at load time.
    /// </summary>
    internal void UploadTexture(
        ComPtr<ID3D12Resource> texture,
        List<(byte[] Pixels, uint Width, uint Height)> mips,
        uint width, uint height, int bytesPerPixel, Format format)
    {
        if (_isRecording)
            throw new InvalidOperationException("Texture upload mid-frame is not supported; create textures at load time.");

        uint mipCount = (uint)mips.Count;
        var texDesc = ((ID3D12Resource*)texture.Handle)->GetDesc();

        var footprints = stackalloc PlacedSubresourceFootprint[(int)mipCount];
        var numRows = stackalloc uint[(int)mipCount];
        var rowSizes = stackalloc ulong[(int)mipCount];
        ulong totalBytes = 0;
        DevicePtr->GetCopyableFootprints(&texDesc, 0, mipCount, 0, footprints, numRows, rowSizes, &totalBytes);

        var staging = CreateUploadBuffer((uint)totalBytes, "TextureStaging");
        var res = (ID3D12Resource*)staging.Handle;
        void* mapped = null;
        var readRange = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
        SilkMarshal.ThrowHResult(res->Map(0, &readRange, &mapped));

        for (int mip = 0; mip < mipCount; mip++)
        {
            var (pixels, w, _) = mips[mip];
            uint srcRowBytes = w * (uint)bytesPerPixel;
            uint dstRowPitch = footprints[mip].Footprint.RowPitch;
            byte* dst = (byte*)mapped + footprints[mip].Offset;
            fixed (byte* src = pixels)
            {
                for (uint row = 0; row < numRows[mip]; row++)
                {
                    System.Buffer.MemoryCopy(
                        src + row * srcRowBytes,
                        dst + row * dstRowPitch,
                        srcRowBytes, srcRowBytes);
                }
            }
        }
        res->Unmap(0, null);

        var allocator = (ID3D12CommandAllocator*)_commandAllocator.Handle;
        var list = (ID3D12GraphicsCommandList*)_commandList.Handle;
        SilkMarshal.ThrowHResult(allocator->Reset());
        SilkMarshal.ThrowHResult(list->Reset(allocator, (ID3D12PipelineState*)null));

        for (uint mip = 0; mip < mipCount; mip++)
        {
            var dst = new TextureCopyLocation
            {
                PResource = (ID3D12Resource*)texture.Handle,
                Type = TextureCopyType.SubresourceIndex,
            };
            dst.Anonymous.SubresourceIndex = mip;
            var src = new TextureCopyLocation
            {
                PResource = res,
                Type = TextureCopyType.PlacedFootprint,
            };
            src.Anonymous.PlacedFootprint = footprints[mip];
            list->CopyTextureRegion(&dst, 0, 0, 0, &src, null);
        }

        Transition(list, (ID3D12Resource*)texture.Handle,
            ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

        SilkMarshal.ThrowHResult(list->Close());
        ID3D12CommandList* executeList = (ID3D12CommandList*)list;
        ((ID3D12CommandQueue*)_queue.Handle)->ExecuteCommandLists(1, &executeList);
        WaitForGpu();
        staging.Dispose();
    }

    // ─── Renderer factory overrides ──────────────────────────

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        var mesh = new D3D12Mesh(this, vertices, indices, attributes);
        mesh.Unregister = () => _meshes.Remove(mesh);
        _meshes.Add(mesh);
        return mesh;
    }

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels, int width, int height,
        TextureFormat format, TextureFilter filter, TextureWrap wrap)
    {
        var texture = new D3D12Texture(this, pixels, width, height, format, filter, wrap);
        texture.Unregister = () => _textures.Remove(texture);
        _textures.Add(texture);
        return texture;
    }

    public override ShaderProgram CreateShader(string vertexSource, string fragmentSource)
    {
        var shader = new D3D12ShaderProgram(this, _d3dCompiler, vertexSource, fragmentSource);
        _shaders.Add(shader);
        return shader;
    }

    public override ShaderProgram CreateShader(PipelineBlob blob)
    {
        if (blob.Backend != GraphicsBackend.D3D12)
            throw new ArgumentException($"Expected D3D12 blob, got {blob.Backend}");
        if (blob.Format != ShaderDataFormat.SourceText)
            throw new ArgumentException($"D3D12 expects HLSL SourceText, got {blob.Format}");

        string vs = Encoding.UTF8.GetString(blob.VertexData
            ?? throw new InvalidOperationException("Compiled shader has no vertex stage"));
        string ps = Encoding.UTF8.GetString(blob.FragmentData
            ?? throw new InvalidOperationException("Compiled shader has no fragment stage"));

        return CreateShader(vs, ps);
    }

    // ─── Resize / shutdown ───────────────────────────────────

    private void DrainPendingResize()
    {
        // The engine feeds the base-class latch from the main thread; when it
        // disagrees with what the swap chain was built for, resize here on the
        // render thread before the frame starts recording.
        Vector2D<int> newSize = FramebufferSize;
        if (newSize == _swapChainSize) return;
        if (newSize.X <= 0 || newSize.Y <= 0 || _swapChain.Handle is null) return;

        WaitForGpu();
        ReleaseBackBufferViews();
        SilkMarshal.ThrowHResult(((IDXGISwapChain3*)_swapChain.Handle)->ResizeBuffers(
            BufferCount, (uint)newSize.X, (uint)newSize.Y, Format.FormatUnknown, 0u));
        CreateBackBufferViews((uint)newSize.X, (uint)newSize.Y);
        _frameIndex = _swapChain.GetCurrentBackBufferIndex();
        _swapChainSize = newSize;
    }

    public override void Shutdown()
    {
        WaitForGpu();

        foreach (var pipeline in _pipelines)
            pipeline.Dispose();
        _pipelines.Clear();

        _lineBatch?.Dispose();
        _lineBatch = null;
        _debugShader = null;
        _fallbackTexture?.Dispose();
        _fallbackTexture = null;

        foreach (var mesh in _meshes) mesh.Dispose();
        _meshes.Clear();
        foreach (var tex in _textures) tex.Dispose();
        _textures.Clear();
        foreach (var sh in _shaders) sh.Dispose();
        _shaders.Clear();
        DefaultShader = null;

        ReleaseBackBufferViews();
        _samplerRing.Dispose();
        _srvRing.Dispose();
        if (_uploadRingCpu is not null)
        {
            ((ID3D12Resource*)_uploadRing.Handle)->Unmap(0, null);
            _uploadRingCpu = null;
        }
        _uploadRing.Dispose();
        DisposeRetiredUploadRings(); // safe: WaitForGpu ran above
        _rtvHeap.Dispose();
        _dsvHeap.Dispose();
        _commandList.Dispose();
        _commandAllocator.Dispose();
        _fence.Dispose();
        if (_fenceEvent != 0)
        {
            Kernel32.CloseHandle(_fenceEvent);
            _fenceEvent = 0;
        }
        _swapChain.Dispose();
        _queue.Dispose();
        _infoQueue.Dispose();
        _device.Dispose();

        base.Shutdown();
        _logger.LogInformation("Renderer shut down (D3D12)");
    }

    // ─── Debug layer ─────────────────────────────────────────

    private void DrainDebugMessages()
    {
        if (_infoQueue.Handle is null) return;

        var queue = (ID3D12InfoQueue*)_infoQueue.Handle;
        ulong count = queue->GetNumStoredMessages();
        for (ulong i = 0; i < count; i++)
        {
            nuint byteLength = 0;
            if (queue->GetMessageA(i, null, &byteLength) < 0 || byteLength == 0)
                continue;

            byte[] storage = new byte[(int)byteLength];
            fixed (byte* p = storage)
            {
                var msg = (Message*)p;
                if (queue->GetMessageA(i, msg, &byteLength) < 0)
                    continue;

                string text = Encoding.ASCII.GetString(msg->PDescription, (int)msg->DescriptionByteLength).TrimEnd('\0');
                switch (msg->Severity)
                {
                    case MessageSeverity.Corruption:
                    case MessageSeverity.Error:
                        _logger.LogError("D3D12 debug layer: {Message}", text);
                        break;
                    case MessageSeverity.Warning:
                        _logger.LogWarning("D3D12 debug layer: {Message}", text);
                        break;
                    default:
                        _logger.LogDebug("D3D12 debug layer: {Message}", text);
                        break;
                }
            }
        }
        if (count > 0)
            queue->ClearStoredMessages();
    }

    private static class Kernel32
    {
        public const uint Infinite = 0xFFFFFFFF;

        [DllImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
        public static extern nint CreateEvent(nint securityAttributes, int manualReset, int initialState, char* name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(nint handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CloseHandle(nint handle);
    }
}
