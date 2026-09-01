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

    /// <summary>
    /// The format the back buffer is <i>viewed</i> through, and therefore what a
    /// shader write and a clear are encoded into. R2's display-encode step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resource stays <see cref="BackBufferFormat"/> and only the view is
    /// sRGB, which is not a stylistic choice: a flip-model swap chain (mandatory
    /// on D3D12) may not be created with an _SRGB format at all, and gets its
    /// display encoding from an _SRGB render-target view over the _UNORM buffer.
    /// D3D11 next door does the opposite for the same reason in reverse, because
    /// its bitblt chain allows the format directly.
    /// </para>
    /// <para>
    /// <b>A pipeline state is compiled against the VIEW format, not the resource
    /// format</b>, so this is what <see cref="D3D12TargetState.BackBuffer"/>
    /// carries. Naming the _UNORM format there instead would mismatch every PSO
    /// against the RTV bound to it, which the debug layer reports and a
    /// release build renders wrong.
    /// </para>
    /// </remarks>
    internal const Format BackBufferRtvFormat = Format.FormatR8G8B8A8UnormSrgb;
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

    private IRenderSurface? _surface;
    private ComPtr<ID3D12Device> _device;
    private ComPtr<ID3D12CommandQueue> _queue;
    private ComPtr<IDXGISwapChain3> _swapChain;
    private ComPtr<ID3D12CommandAllocator> _commandAllocator;
    private ComPtr<ID3D12GraphicsCommandList> _commandList;
    private ComPtr<ID3D12InfoQueue> _infoQueue;
    private DxgiDebugMessages? _dxgiMessages;

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
    private readonly List<RenderTarget> _renderTargets = [];
    private int _pipelineIndex;

    // Size the swap chain currently has. Render() compares it against the
    // engine-fed base-class framebuffer latch each frame and reruns the resize
    // path when the window has changed; the resize must run on this (render)
    // thread between frames, never in a window event.
    private Vector2D<int> _swapChainSize;

    // The last size ResizeBuffers refused, if any. A recoverable resize failure
    // leaves the swap chain on its old buffers, so the latch keeps disagreeing
    // and the path would be re-entered — and re-fail — every single frame,
    // burning a WaitForGpu and a view rebuild each time and drowning the log.
    // Cleared by the next resize that succeeds, so going away and coming back
    // to the same size does get retried.
    private Vector2D<int>? _failedResizeSize;

    // Set once the device is gone. Everything that would otherwise call into a
    // dead device — the fence wait, Present, the shutdown teardown — checks it,
    // so the run ends on the one clear diagnosis instead of a cascade of
    // secondary COM failures.
    private bool _deviceLost;

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


    /// <inheritdoc/>
    /// <remarks>The 0..1 clip-Z remap above, exposed to backend-neutral code.</remarks>
    public override Matrix4x4 ClipZCorrection => GlToD3dClipZ;

    /// <inheritdoc/>
    /// <remarks>
    /// Identity, because <see cref="GlToD3dClipZ"/> already put clip z in the
    /// 0..1 range a depth buffer stores. OpenGL needs the other answer.
    /// </remarks>
    public override Vector2 DepthToNdcZ => new(1f, 0f);

    public override GraphicsBackend Backend => GraphicsBackend.D3D12;

    /// <summary>D3D12 creates its own device, so the window must not bring up an OpenGL context.</summary>
    public override GraphicsAPI WindowApi => GraphicsAPI.None;

    public override void AcquireContext(IRenderSurface surface) { /* not thread-affine */ }
    public override void ReleaseContext(IRenderSurface surface) { }

    public override string CurrentPipelineName =>
        _pipelines.Count == 0 ? "None" : _pipelines[_pipelineIndex].Name;

    // Cached because it rides every host snapshot; rebuilt only when the
    // pipeline count moves, which is registration at Initialize and the clear
    // at Shutdown.
    private string[] _pipelineNames = [];

    public override IReadOnlyList<string> PipelineNames
    {
        get
        {
            if (_pipelineNames.Length != _pipelines.Count)
            {
                var names = new string[_pipelines.Count];
                for (int i = 0; i < names.Length; i++)
                    names[i] = _pipelines[i].Name;
                _pipelineNames = names;
            }
            return _pipelineNames;
        }
    }

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

    // ─── last-bound state, per command list recording ────────────────────────
    //
    // The deferred geometry pass draws every item with one program and one PSO,
    // yet each draw used to re-issue the full block: root signature, PSO,
    // topology, and a root CBV per cbuffer. All of it is command-list state
    // that persists across draws, so a draw that repeats the previous one can
    // skip the calls entirely. Tracked here rather than per program because the
    // state belongs to the LIST: it survives program switches and dies at the
    // frame's list reset, where ResetLastBoundState is owed. Every set of these
    // states must go through the Bind* methods below, or the cache answers for
    // a bind that never happened, which is the D3D11 bind-cache bug all over.
    private nint _lastRootSignature;
    private nint _lastPso;
    private int _lastTopology = -1;

    // Root CBV GPU addresses by root parameter index, meaningful only under
    // _lastRootSignature (a root signature CHANGE invalidates root bindings,
    // so BindRootSignature clears this; a redundant re-set of the same one
    // leaves bindings intact per the D3D12 spec, and is skipped anyway).
    private readonly ulong[] _lastRootCbv = new ulong[16];

    internal void BindRootSignature(ID3D12GraphicsCommandList* list, nint rootSignature)
    {
        if (_lastRootSignature == rootSignature) return;
        _lastRootSignature = rootSignature;
        Array.Clear(_lastRootCbv);
        list->SetGraphicsRootSignature((ID3D12RootSignature*)rootSignature);
    }

    internal void BindRootCbv(ID3D12GraphicsCommandList* list, int rootParam, ulong gpuVa)
    {
        if ((uint)rootParam < (uint)_lastRootCbv.Length)
        {
            if (_lastRootCbv[rootParam] == gpuVa) return;
            _lastRootCbv[rootParam] = gpuVa;
        }
        list->SetGraphicsRootConstantBufferView((uint)rootParam, gpuVa);
    }

    internal void BindPipelineState(ID3D12GraphicsCommandList* list, ID3D12PipelineState* pso)
    {
        if (_lastPso == (nint)pso) return;
        _lastPso = (nint)pso;
        list->SetPipelineState(pso);
    }

    internal void BindTopology(ID3D12GraphicsCommandList* list, D3DPrimitiveTopology topology)
    {
        if (_lastTopology == (int)topology) return;
        _lastTopology = (int)topology;
        list->IASetPrimitiveTopology(topology);
    }

    // The list reset drops every binding, and a shader hot-reload earlier in
    // the frame may have recreated objects at recycled addresses; both are why
    // this runs at the top of every frame, before anything records.
    private void ResetLastBoundState()
    {
        _lastRootSignature = 0;
        _lastPso = 0;
        _lastTopology = -1;
        Array.Clear(_lastRootCbv);
    }

    public D3D12Renderer(ILogger<Renderer> logger, IShaderCompiler shaderCompiler)
        : base(logger, shaderCompiler)
    {
    }

    // ─── Initialization ──────────────────────────────────────

    public override void Initialize(IRenderSurface surface)
    {
        _surface = surface;

        // Read the engine-fed latch, not window.FramebufferSize: this runs on
        // the render thread while the main thread is already pumping
        // glfwPollEvents, and GLFW guarantees no thread safety for that pair.
        // The engine seeded the latch before this thread started.
        Vector2D<int> size = FramebufferSize;
        _swapChainSize = size;

        CreateDevice();
        CreateQueueAndSwapChain(surface, (uint)size.X, (uint)size.Y);
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
        _lineBatch = new D3D12LineBatch(this, (D3D12ShaderProgram)_debugShader);

        // 1×1 white fallback so unset texture slots in a descriptor table are
        // always valid (sampling it is a no-op multiply).
        ReadOnlySpan<byte> white = [255, 255, 255, 255];
        // sRGB, like every other colour texture: white is 255 in both spaces, so
        // the choice does not change this one texel, but a slot whose colour
        // space differed from the texture it stands in for would be a trap the
        // first time the fallback is anything but white.
        _fallbackTexture = new D3D12Texture(
            this, white, 1, 1, TextureFormat.Rgba8, TextureColorSpace.Srgb,
            TextureFilter.Nearest, TextureWrap.Repeat);

        // Deferred first: see OpenGLRenderer for why it is the default.
        RegisterPipeline(new D3D12DeferredPipeline());
        RegisterPipeline(new D3D12ForwardPipeline());
        RegisterPipeline(new D3D12WireframePipeline());

        DrainDebugMessages();
        _logger.LogInformation("Renderer initialized (D3D12, pipeline={Pipeline})", CurrentPipelineName);
    }

    private void CreateDevice()
    {
        // Only when asked for. This layer validates EVERY command-list call, so
        // leaving it on unconditionally taxed every frame anyone ever measured
        // and would have shipped with the engine. See Renderer.EnableDebugLayer.
        if (!EnableDebugLayer)
        {
            _logger.LogInformation("D3D12 debug layer off (not requested).");
        }
        else
        {
            ID3D12Debug* debug = null;
            Guid debugGuid = ID3D12Debug.Guid;
            if (D3D12Api.GetDebugInterface(&debugGuid, (void**)&debug) >= 0)
            {
                debug->EnableDebugLayer();
                debug->Release();
                DebugLayerActive = true;
                _logger.LogInformation("D3D12 debug layer active.");
            }
            else
            {
                _logger.LogInformation("D3D12 debug layer unavailable; creating without it.");
            }
        }

        // Null means the system default, which is what every previous build
        // did unconditionally.
        ComPtr<IDXGIAdapter> adapter = DxgiAdapters.Find(_dxgi, PreferredAdapter, _logger, out string adapterName);
        AdapterName = adapterName;

        ID3D12Device* device = null;
        Guid deviceGuid = ID3D12Device.Guid;
        try
        {
            SilkMarshal.ThrowHResult(D3D12Api.CreateDevice(
                (IUnknown*)adapter.Handle, D3DFeatureLevel.Level110, &deviceGuid, (void**)&device));
        }
        finally
        {
            ComOwnership.Release(ref adapter);
        }
        _device = ComOwnership.Own(device);

        ID3D12InfoQueue* infoQueue = null;
        Guid infoQueueGuid = ID3D12InfoQueue.Guid;
        if (device->QueryInterface(&infoQueueGuid, (void**)&infoQueue) >= 0)
            _infoQueue = ComOwnership.Own(infoQueue);
    }

    private void CreateQueueAndSwapChain(IRenderSurface surface, uint width, uint height)
    {
        if (surface.Kind != RenderSurfaceKind.Win32 || surface.NativeHandle == 0)
        {
            throw new InvalidOperationException(
                $"The D3D12 backend needs a Win32 surface with an HWND; this one is {surface.Kind}. " +
                "On another platform, or for a surface that offers only a GL context, use the OpenGL backend.");
        }

        nint hwnd = surface.NativeHandle;

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
        _queue = ComOwnership.Own(queue);

        // The DXGI debug layer is what turns a bare DXGI_ERROR_INVALID_CALL out
        // of ResizeBuffers into a sentence saying which reference is still
        // outstanding, but it is validation, and it obeys the same gate as the
        // device layer above. This request used to be unconditional, which kept
        // DXGI validating the Present path in every build on any machine with
        // Graphics Tools, --debug-layer=false included; D3D11 had the gate
        // right, so "validation off" measured different things per backend.
        IDXGIFactory2* factory = null;
        Guid factoryGuid = IDXGIFactory2.Guid;
        bool debugFactory = false;
        if (EnableDebugLayer)
        {
            int factoryHr = _dxgi.CreateDXGIFactory2(
                DxgiDebugMessages.CreateFactoryDebug, &factoryGuid, (void**)&factory);
            debugFactory = factoryHr >= 0;
            if (debugFactory)
            {
                _dxgiMessages = DxgiDebugMessages.Acquire(_dxgi);
                _logger.LogInformation(
                    "DXGI debug layer {State}.", _dxgiMessages.IsAvailable ? "active" : "requested but no info queue");
            }
            else
            {
                _logger.LogInformation("DXGI debug layer unavailable (hr=0x{Hr:X}); creating the factory without it.", factoryHr);
            }
        }
        if (!debugFactory)
            SilkMarshal.ThrowHResult(_dxgi.CreateDXGIFactory2(0u, &factoryGuid, (void**)&factory));

        // Flip model is mandatory on D3D12. The per-frame full fence sync means
        // the rotating back buffer is never in flight when we touch it.
        //
        // Flags stays 0 on purpose — in particular WITHOUT
        // DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH. This engine never switches
        // display modes: fullscreen is borderless windowed driven by
        // WindowModeLatch, and MakeWindowAssociation below stops DXGI from
        // driving a mode switch behind our back. A swap chain created without
        // that flag being put into a fullscreen transition anyway is precisely
        // the state ResizeBuffers rejects with DXGI_ERROR_INVALID_CALL.
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
        _swapChain = ComOwnership.Own(swapChain3);

        // Before the factory goes: the window association is per-factory, so it
        // has to be made on THIS one — the one that created the chain — and
        // therefore before the Release below. See DxgiInterop.SuppressAltEnter
        // for what DXGI does to the render thread if we skip it.
        DxgiInterop.SuppressAltEnter(factory, hwnd, _logger, "D3D12");
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
        _commandAllocator = ComOwnership.Own(allocator);

        ID3D12GraphicsCommandList* list = null;
        Guid listGuid = ID3D12GraphicsCommandList.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateCommandList(
            0, CommandListType.Direct, allocator, (ID3D12PipelineState*)null, &listGuid, (void**)&list));
        _commandList = ComOwnership.Own(list);
        SilkMarshal.ThrowHResult(list->Close()); // lists are created open

        ID3D12Fence* fence = null;
        Guid fenceGuid = ID3D12Fence.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateFence(0, FenceFlags.None, &fenceGuid, (void**)&fence));
        _fence = ComOwnership.Own(fence);
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
            // Own, not `new ComPtr<>(...)`: the ComPtr constructor AddRefs, so
            // wrapping GetBuffer's already-owned pointer would leave TWO
            // references on the back buffer and ReleaseBackBufferViews would
            // only ever drop it to one. DXGI then refuses every ResizeBuffers
            // with DXGI_ERROR_INVALID_CALL — which is exactly why resizing this
            // backend's window used to kill the render thread. See ComOwnership.
            _backBuffers[i] = ComOwnership.Own(backBuffer);

            // An explicit desc, not null: null means "the resource's own format",
            // which is _UNORM and would skip the sRGB encode entirely.
            var rtvDesc = new RenderTargetViewDesc
            {
                Format = BackBufferRtvFormat,
                ViewDimension = RtvDimension.Texture2D,
            };
            rtvDesc.Anonymous.Texture2D = new Tex2DRtv { MipSlice = 0, PlaneSlice = 0 };

            var handle = new CpuDescriptorHandle { Ptr = rtvStart.Ptr + i * _rtvStride };
            DevicePtr->CreateRenderTargetView(backBuffer, &rtvDesc, handle);
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
        // Same ownership handover: without it the depth texture survives every
        // ReleaseBackBufferViews and each resize leaks a full-screen surface.
        _depthBuffer = ComOwnership.Own(depth);

        var dsvHandle = ((ID3D12DescriptorHeap*)_dsvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();
        DevicePtr->CreateDepthStencilView(depth, null, dsvHandle);
    }

    // Idempotent on purpose: the resize path releases the buffers and a device
    // loss can throw before they are rebuilt, after which Shutdown releases
    // them again. Clearing each field is what keeps that second pass a no-op
    // rather than an over-release. See ComOwnership.Release.
    private void ReleaseBackBufferViews()
    {
        for (int i = 0; i < _backBuffers.Length; i++)
            ComOwnership.Release(ref _backBuffers[i]);
        ComOwnership.Release(ref _depthBuffer);
    }

    // ─── Frame loop ──────────────────────────────────────────

    public void RegisterPipeline(ID3D12RenderPipeline pipeline)
    {
        pipeline.Initialize(this);
        _pipelines.Add(pipeline);
    }

    public override bool TrySelectPipeline(string name)
    {
        for (int i = 0; i < _pipelines.Count; i++)
        {
            if (!string.Equals(_pipelines[i].Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            _pipelineIndex = i;
            _logger.LogInformation("Pipeline selected: {Pipeline}", CurrentPipelineName);
            return true;
        }

        return false;
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

        // Once per FRAME, and deliberately not once per pipeline execution:
        // a frame with ProbeTarget set runs the pipeline twice into one
        // command list. See Renderer.BeginFrameInstanceBuffers.
        BeginFrameInstanceBuffers();

        if (_pipelines.Count == 0 || _surface is null) return;

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
        ResetLastBoundState();
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

        var context = new D3D12RenderContext
        {
            Renderer = this,
            Scene = scene,
            View = view,
            DeltaTime = deltaTime,
        };
        if (ProbeTarget is { } probe)
        {
            FrameTarget = probe;
            _pipelines[_pipelineIndex].Execute(context);
        }

        RenderTarget? sceneTarget = HdrEnabled ? EnsureSceneTarget() : null;
        FrameTarget = sceneTarget;
        _pipelines[_pipelineIndex].Execute(context);

        if (sceneTarget is null)
            DrawOverlay(scene);
        else
            ResolveTo(sceneTarget.ColorTexture!, null, scene);

        Transition(list, (ID3D12Resource*)_backBuffers[_frameIndex].Handle,
            ResourceStates.RenderTarget, ResourceStates.Present);

        SilkMarshal.ThrowHResult(list->Close());
        _isRecording = false;

        ID3D12CommandList* executeList = (ID3D12CommandList*)list;
        ((ID3D12CommandQueue*)_queue.Handle)->ExecuteCommandLists(1, &executeList);
    }

    public override void Present(IRenderSurface surface)
    {
        if (_swapChain.Handle is null || _deviceLost) return;

        // Present is the other call that reports a lost device, and it reports
        // it far more often than ResizeBuffers does (a TDR lands here). Same
        // treatment: a named diagnosis with the removed reason, not an opaque
        // COMException from deep inside SilkMarshal.
        int hr = ((IDXGISwapChain3*)_swapChain.Handle)->Present(0, 0);
        if (hr < 0)
        {
            if (DxgiInterop.IsDeviceLost(hr))
                throw DeviceLost(hr, "presenting a frame");
            SilkMarshal.ThrowHResult(hr);
        }

        WaitForGpu();

        // GPU idle and no list recording: the only safe point to free upload
        // rings the frame outgrew and to swap descriptor rings for bigger ones.
        DisposeRetiredUploadRings();
        RecycleRetiredMeshBuffers();
        GrowDescriptorRingsIfNeeded();

        _frameIndex = _swapChain.GetCurrentBackBufferIndex();
        DrainDebugMessages();
    }

    /// <summary>Frees upload rings retired by mid-frame growth. Call only after the frame fence completed.</summary>
    private void DisposeRetiredUploadRings()
    {
        if (_retiredUploadRings.Count == 0) return;
        // Dispose is enough here (rather than ComOwnership.Release) only
        // because the Clear below drops the entries: each retired ring holds
        // exactly one reference and is released exactly once.
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

        // Release before the new heap is created: if creation throws, the
        // field must be empty rather than holding a freed handle Shutdown
        // would release a second time.
        ComOwnership.Release(ref ring);
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
            ComOwnership.Release(ref _srvRing);
            _srvRing = CreateDescriptorHeap(DescriptorHeapType.CbvSrvUav, newCapacity, shaderVisible: true);
            _srvRingCapacity = newCapacity;
            _logger.LogInformation("D3D12 SRV descriptor ring grown to {Capacity} slots", newCapacity);
        }

        if (_samplerRingPeak * 4 > _samplerRingCapacity * 3 && _samplerRingCapacity < MaxSamplerRingCapacity)
        {
            uint newCapacity = _samplerRingCapacity;
            while (_samplerRingPeak * 4 > newCapacity * 3 && newCapacity < MaxSamplerRingCapacity)
                newCapacity *= 2;
            ComOwnership.Release(ref _samplerRing);
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
        // A dead device never signals, so the wait below would either fail or
        // block forever. Returning is the only thing that lets the teardown
        // path finish and the run end on its real diagnosis.
        if (_fence.Handle is null || _deviceLost) return;
        ulong value = ++_fenceValue;
        SilkMarshal.ThrowHResult(((ID3D12CommandQueue*)_queue.Handle)->Signal((ID3D12Fence*)_fence.Handle, value));
        if (((ID3D12Fence*)_fence.Handle)->GetCompletedValue() < value)
        {
            SilkMarshal.ThrowHResult(((ID3D12Fence*)_fence.Handle)->SetEventOnCompletion(value, (void*)_fenceEvent));
            Kernel32.WaitForSingleObject(_fenceEvent, Kernel32.Infinite);
        }
    }

    internal static void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* resource,
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

    // The back buffer's views, resolved per frame because the flip-model chain
    // rotates which buffer is current.
    private CpuDescriptorHandle CurrentBackBufferRtv
    {
        get
        {
            var start = ((ID3D12DescriptorHeap*)_rtvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();
            return new CpuDescriptorHandle { Ptr = start.Ptr + _frameIndex * _rtvStride };
        }
    }

    private CpuDescriptorHandle DepthStencilView =>
        ((ID3D12DescriptorHeap*)_dsvHeap.Handle)->GetCPUDescriptorHandleForHeapStart();

    protected override void BeginPassCore(
        RenderTarget? target, ReadOnlySpan<RenderTarget> targets, in PassClear clear)
    {
        var list = CurrentList;
        if (list is null) return;

        Vector2D<int> size = PassSize;
        SetViewportAndScissor(size.X, size.Y);

        CpuDescriptorHandle rtv;
        CpuDescriptorHandle dsv;
        bool hasDepth;
        bool hasColor = true;

        if (target is D3D12RenderTarget offscreen)
        {
            // The barrier that makes this legal. Without it the attachment is
            // still PixelShaderResource from whoever sampled it, and writing to
            // it is undefined: the debug layer reports it, a shipping build does
            // not, and the picture is wrong on some hardware and fine on others.
            offscreen.TransitionColor(list, ResourceStates.RenderTarget);
            // And the same for depth, which the deferred light pass samples
            // between geometry passes. Symmetric with EndPassCore, so a target
            // is always left readable and always made writable again; the
            // alternative is a barrier emitted from inside whatever binds the
            // texture, which is one path out of several and easy to miss.
            offscreen.TransitionDepth(list, ResourceStates.DepthWrite);

            // A depth-only target contributes NO render-target formats, and the
            // pipeline state has to agree: a PSO built for one RTV and bound
            // with none is a validation failure, not a wrong pixel.
            _currentTargetState = offscreen.HasColor
                ? new D3D12TargetState(offscreen.ColorFormat, 1, offscreen.DepthViewFormat, 1)
                : new D3D12TargetState(Format.FormatUnknown, 0, offscreen.DepthViewFormat, 1);

            rtv = offscreen.Rtv;
            dsv = offscreen.Dsv;
            hasDepth = offscreen.HasDepth;
            hasColor = offscreen.HasColor;
        }
        else
        {
            _currentTargetState = D3D12TargetState.BackBuffer;
            rtv = CurrentBackBufferRtv;
            dsv = DepthStencilView;
            hasDepth = true;
        }

        if (clear.Color is { } color && hasColor)
        {
            float* value = stackalloc float[4] { color.X, color.Y, color.Z, color.W };
            list->ClearRenderTargetView(rtv, value, 0, null);
        }
        if (clear.Depth is { } depth && hasDepth)
            list->ClearDepthStencilView(dsv, ClearFlags.Depth | ClearFlags.Stencil, depth, 0, 0, null);

        if (targets.Length > 1)
        {
            // Every attachment needs its own barrier into RenderTarget and its
            // own clear, and the pipeline state must be compiled against ALL of
            // their formats: a PSO built for one RTV bound to three is a
            // validation failure, not a wrong pixel.
            CpuDescriptorHandle* views = stackalloc CpuDescriptorHandle[targets.Length];
            var formats = new Format[targets.Length];
            // Hoisted out of the loop: a stackalloc inside one cannot reuse its
            // space, so an eight-attachment pass would allocate eight times.
            float* clearValue = stackalloc float[4];
            if (clear.Color is { } extraColor)
            {
                clearValue[0] = extraColor.X;
                clearValue[1] = extraColor.Y;
                clearValue[2] = extraColor.Z;
                clearValue[3] = extraColor.W;
            }

            for (int i = 0; i < targets.Length; i++)
            {
                var extra = (D3D12RenderTarget)targets[i];
                if (i > 0)
                {
                    extra.TransitionColor(list, ResourceStates.RenderTarget);
                    if (clear.Color is not null)
                        list->ClearRenderTargetView(extra.Rtv, clearValue, 0, null);
                }
                views[i] = extra.Rtv;
                formats[i] = extra.ColorFormat;
            }

            // The FIRST target owns depth, so its view format is the one the
            // pipeline must be built against.
            Format depthFormat = targets[0] is D3D12RenderTarget first
                ? first.DepthViewFormat
                : Format.FormatUnknown;
            _currentTargetState = D3D12TargetState.ForTargets(formats, depthFormat);
            list->OMSetRenderTargets((uint)targets.Length, views, 0, hasDepth ? &dsv : null);
            return;
        }

        if (!hasColor)
        {
            // Zero render targets: the depth-only bind that makes a shadow pass
            // cheap, and that the pipeline state above was built to match.
            list->OMSetRenderTargets(0, null, 0, &dsv);
        }
        else if (hasDepth)
        {
            list->OMSetRenderTargets(1, &rtv, 0, &dsv);
        }
        else
        {
            list->OMSetRenderTargets(1, &rtv, 0, null);
        }
    }

    protected override void EndPassCore(RenderTarget? target, ReadOnlySpan<RenderTarget> targets)
    {
        for (int i = 1; i < targets.Length; i++)
        {
            if (CurrentList is not null && targets[i] is D3D12RenderTarget extra)
                extra.TransitionColor(CurrentList, ResourceStates.PixelShaderResource);
        }

        _currentTargetState = D3D12TargetState.BackBuffer;

        var list = CurrentList;
        if (list is null || target is not D3D12RenderTarget offscreen) return;

        // Back to readable, here rather than lazily at the first sample: the
        // command list is open now, and the alternative is a barrier emitted
        // from inside a draw call, which is both harder to reason about and
        // easy to forget on one of the paths that binds a texture.
        offscreen.TransitionColor(list, ResourceStates.PixelShaderResource);
        offscreen.TransitionDepth(list, ResourceStates.PixelShaderResource);
    }

    /// <summary>
    /// The target configuration the open pass is drawing into, which every PSO
    /// built during it must be compiled against. See <see cref="D3D12PsoKey"/>.
    /// </summary>
    internal D3D12TargetState CurrentTargetState => _currentTargetState;

    private D3D12TargetState _currentTargetState = D3D12TargetState.BackBuffer;

    public override RenderTarget CreateRenderTarget(in RenderTargetDesc desc)
    {
        var target = new D3D12RenderTarget(this, desc);
        target.Unregister = () => _renderTargets.Remove(target);
        _renderTargets.Add(target);
        return target;
    }

    /// <summary>A typeless depth resource that can be both written and sampled.</summary>
    internal ComPtr<ID3D12Resource> CreateDepthResource(uint width, uint height)
    {
        var heapProps = new HeapProperties { Type = HeapType.Default };
        var desc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatR32Typeless,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            // AllowDepthStencil WITHOUT DenyShaderResource, which is the flag
            // that would make this unreadable and is easy to add by reflex.
            Flags = ResourceFlags.AllowDepthStencil,
        };

        // The clear value must name a real depth format, not the typeless one.
        var clearValue = new ClearValue { Format = Format.FormatD32Float };
        clearValue.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 };

        ID3D12Resource* res = null;
        Guid guid = ID3D12Resource.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateCommittedResource(
            &heapProps, HeapFlags.None, &desc, ResourceStates.DepthWrite, &clearValue,
            &guid, (void**)&res));
        return ComOwnership.Own(res);
    }

    /// <summary>A default-heap texture that can be both drawn into and sampled.</summary>
    internal ComPtr<ID3D12Resource> CreateRenderTargetResource(uint width, uint height, Format format)
    {
        var heapProps = new HeapProperties { Type = HeapType.Default };
        var desc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowRenderTarget,
        };

        // An optimised clear value matching what the pass will actually clear to
        // is what keeps the driver's fast clear path available; a mismatch is a
        // debug-layer warning and a slower clear.
        var clearValue = new ClearValue { Format = format };
        clearValue.Anonymous.Color[0] = ClearColors.Sky.X;
        clearValue.Anonymous.Color[1] = ClearColors.Sky.Y;
        clearValue.Anonymous.Color[2] = ClearColors.Sky.Z;
        clearValue.Anonymous.Color[3] = ClearColors.Sky.W;

        ID3D12Resource* res = null;
        Guid guid = ID3D12Resource.Guid;
        SilkMarshal.ThrowHResult(DevicePtr->CreateCommittedResource(
            &heapProps, HeapFlags.None, &desc, ResourceStates.PixelShaderResource, &clearValue,
            &guid, (void**)&res));
        return ComOwnership.Own(res);
    }

    protected override void DrawFullscreen(PostPass pass)
    {
        Mesh triangle = EnsureFullscreenTriangle();

        // Fill and depth are ambient here too, but on this backend they are
        // baked into the pipeline state rather than set on the context, so a
        // stale value cannot return a wrongly-cached PSO -- only a correctly
        // compiled one for the wrong state. Both are keys of D3D12PsoKey.
        FillMode previousFill = CurrentFillMode;
        DepthMode previousDepth = CurrentDepthMode;
        CurrentFillMode = FillMode.Solid;
        CurrentDepthMode = DepthMode.None;

        // Use LAST, as on D3D11: uniforms go into constant shadows that Use
        // uploads. Calling Use first would also clear the pending texture table,
        // and the pass would then sample the white fallback.
        pass.ApplyTo(pass.Shader);
        pass.Shader.Use();
        triangle.Draw();

        CurrentFillMode = previousFill;
        CurrentDepthMode = previousDepth;
    }

    /// <summary>
    /// Depth state for the next mesh draw. Ambient, like <see cref="CurrentFillMode"/>,
    /// and safe for the same reason: it is part of the pipeline-state key, so a
    /// stale value cannot hand back a pipeline compiled for different state.
    /// </summary>
    internal DepthMode CurrentDepthMode { get; set; } = DepthMode.TestWrite;

    internal void SetViewportAndScissor(int width, int height) =>
        SetViewportCore(0, 0, width, height);

    protected override void SetViewportCore(int x, int y, int width, int height)
    {
        var list = CurrentList;
        if (list is null) return;
        var viewport = new Viewport
        {
            TopLeftX = x,
            TopLeftY = y,
            Width = width,
            Height = height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        list->RSSetViewports(1, &viewport);

        // The scissor moves with the viewport, or a cascade would rasterise
        // into its own quadrant and still be allowed to clear or blend outside
        // it. They are separate state on this backend and easy to desync.
        var scissor = new Box2D<int>(x, y, x + width, y + height);
        list->RSSetScissorRects(1, &scissor);
    }

    /// <summary>
    /// Uploads and draws the accumulated <see cref="Renderer.DebugDraw"/> lines.
    /// Called by pipelines after their main scene pass.
    /// </summary>
    protected override void FlushDebugDrawCore(Scene.Camera camera)
    {
        if (DebugDraw.VertexCount == 0 || _debugShader is null || _lineBatch is null) return;

        var debug = (D3D12ShaderProgram)_debugShader;
        debug.SetUniform("uView", camera.View);
        debug.SetUniform("uProjection", camera.Projection * GlToD3dClipZ);
        debug.Use();
        _lineBatch.Draw(DebugDraw.Vertices, (uint)DebugDraw.VertexCount, DepthMode.None);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The depth mode rides the DRAW rather than the batch, because it goes
    /// into the PSO key: a pipeline compiled for the always-on-top overlay
    /// handed to a depth-tested draw is a wrong picture, not an error.
    /// </remarks>
    protected override void FlushWorldLinesCore(
        Scene.Camera camera, ShaderProgram program, float nudge)
    {
        if (_lineBatch is null)
            return;

        var typed = (D3D12ShaderProgram)program;
        typed.SetUniform("uView", camera.View);
        typed.SetUniform("uProjection", camera.Projection * GlToD3dClipZ);
        typed.SetUniform("uCameraPosition", camera.Position);
        typed.SetUniform("uDepthNudge", nudge);
        typed.Use();
        _lineBatch.Draw(WorldLines.Vertices, (uint)WorldLines.VertexCount, DepthMode.TestWriteEqual, typed);
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
        return ComOwnership.Own(heap);
    }

    // ---- mesh buffer pool --------------------------------------------------
    //
    // CreateCommittedResource allocates a whole heap per resource and measured
    // about 480 microseconds per mesh here, against 24 on D3D11's CreateBuffer.
    // With a world brush animating, the static-world compiler lands new chunk
    // meshes every frame, so that was 7 ms of a 10 ms frame spent allocating
    // buffers the previous frame had just freed.
    //
    // Freed buffers are therefore kept and handed back out. Sizes are rounded up
    // to a power of two so a chunk whose vertex count wobbles by a few triangles
    // still hits the same bucket; the VIEW carries the real byte count, so a
    // buffer larger than its contents is not a correctness question.
    private readonly Dictionary<uint, Stack<ComPtr<ID3D12Resource>>> _freeMeshBuffers = [];
    private readonly List<(uint Capacity, ComPtr<ID3D12Resource> Buffer)> _retiredMeshBuffers = [];

    /// <summary>Buffers currently parked in the pool. Diagnostics.</summary>
    internal int PooledMeshBufferCount { get; private set; }

    /// <summary>Rounds a request up to its pool bucket. At least 256, D3D12's buffer alignment.</summary>
    internal static uint MeshBufferBucket(uint sizeBytes)
    {
        uint bucket = 256;
        while (bucket < sizeBytes) bucket <<= 1;
        return bucket;
    }

    /// <summary>Takes a buffer of at least <paramref name="capacity"/> bytes, from the pool if one is parked.</summary>
    internal ComPtr<ID3D12Resource> RentMeshBuffer(uint capacity)
    {
        if (_freeMeshBuffers.TryGetValue(capacity, out Stack<ComPtr<ID3D12Resource>>? bucket) && bucket.Count > 0)
        {
            PooledMeshBufferCount--;
            return bucket.Pop();
        }

        return CreateUploadBuffer(capacity, "MeshBuffer");
    }

    /// <summary>
    /// Gives a mesh buffer back. It is NOT reusable until the GPU has finished
    /// with the frames that referenced it, so it waits on the retired list until
    /// the next fence wait rather than going straight back into the pool.
    /// </summary>
    internal void ReturnMeshBuffer(uint capacity, ComPtr<ID3D12Resource> buffer)
    {
        if (buffer.Handle is null) return;
        _retiredMeshBuffers.Add((capacity, buffer));
    }

    // Called where the GPU is known idle. Everything freed since the last one is
    // now safe to hand out again.
    private void RecycleRetiredMeshBuffers()
    {
        if (_retiredMeshBuffers.Count == 0) return;

        foreach ((uint capacity, ComPtr<ID3D12Resource> buffer) in _retiredMeshBuffers)
        {
            if (!_freeMeshBuffers.TryGetValue(capacity, out Stack<ComPtr<ID3D12Resource>>? bucket))
                _freeMeshBuffers[capacity] = bucket = new Stack<ComPtr<ID3D12Resource>>();

            bucket.Push(buffer);
            PooledMeshBufferCount++;
        }

        _retiredMeshBuffers.Clear();
    }

    // Releases the pool for good. Shutdown only.
    private void ReleaseMeshBufferPool()
    {
        RecycleRetiredMeshBuffers();
        foreach (Stack<ComPtr<ID3D12Resource>> bucket in _freeMeshBuffers.Values)
        {
            while (bucket.Count > 0)
            {
                ComPtr<ID3D12Resource> buffer = bucket.Pop();
                ComOwnership.Release(ref buffer);
            }
        }
        _freeMeshBuffers.Clear();
        PooledMeshBufferCount = 0;
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
        return ComOwnership.Own(res);
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
        return ComOwnership.Own(res);
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

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes, MeshCpuAccess cpuAccess = MeshCpuAccess.Retained)
    {
        MeshesCreated++;
        var mesh = new D3D12Mesh(this, vertices, indices, attributes, cpuAccess);
        mesh.Unregister = () => _meshes.Remove(mesh);
        _meshes.Add(mesh);
        return mesh;
    }

    /// <inheritdoc/>
    public override InstanceBuffer CreateInstanceBuffer(
        int capacityInstances, ReadOnlySpan<VertexAttribute> attributes, ShaderProgram program)
    {
        // program is unused: on D3D12 the input layout is part of the PSO, and
        // the PSO is selected per draw from the program actually bound.
        int floats = ValidateInstanceLayout(capacityInstances, attributes);
        return new D3D12InstanceBuffer(
            _device, capacityInstances, VertexAttribute.StandardLayout, attributes, floats);
    }

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels, int width, int height,
        TextureFormat format, TextureColorSpace colorSpace, TextureFilter filter, TextureWrap wrap)
    {
        var texture = new D3D12Texture(this, pixels, width, height, format, colorSpace, filter, wrap);
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

    /// <summary>
    /// Reconciles the swap chain with the engine's framebuffer-size latch.
    /// Render thread only, between frames — never from a window event.
    /// </summary>
    /// <remarks>
    /// <b>A resize failure must not take the render thread down.</b> The three
    /// outcomes are kept apart deliberately: a degenerate size is not a resize
    /// at all and is skipped; a device loss ends the run with a diagnosis; and
    /// anything else is logged with its HRESULT and the attempted size, the
    /// previous swap-chain state is rebuilt so the next frame has valid views,
    /// and the engine keeps running at the old size.
    /// </remarks>
    private void DrainPendingResize()
    {
        // The engine feeds the base-class latch from the main thread; when it
        // disagrees with what the swap chain was built for, resize here on the
        // render thread before the frame starts recording.
        //
        // Read the latch exactly ONCE. The main thread may publish another size
        // while ResizeBuffers is running (a live window drag, or the borderless
        // fullscreen toggle landing), and _swapChainSize below records the size
        // the buffers were actually built at — so the next frame sees the fresh
        // mismatch and resizes again, instead of this one claiming a size that
        // never happened.
        Vector2D<int> newSize = FramebufferSize;

        // Every "should we touch the swap chain at all" rule — unchanged size,
        // no chain, dead device, degenerate (minimised) size, a size that
        // already failed — lives in the shared policy, so this backend and
        // D3D11 cannot drift apart on it. See SwapChainResizePolicy.
        if (!SwapChainResizePolicy.ShouldResize(
                newSize, _swapChainSize, _failedResizeSize, _swapChain.Handle is not null, _deviceLost))
            return;

        WaitForGpu();
        ReleaseBackBufferViews();

        int hr = ((IDXGISwapChain3*)_swapChain.Handle)->ResizeBuffers(
            BufferCount, (uint)newSize.X, (uint)newSize.Y, Format.FormatUnknown, 0u);

        if (hr < 0)
        {
            if (DxgiInterop.IsDeviceLost(hr))
                throw DeviceLost(hr, $"resizing the swap chain to {newSize.X}×{newSize.Y}");

            _logger.LogError(
                "D3D12 ResizeBuffers to {Width}×{Height} failed: {Code} (0x{Hr:X8}). Staying at {OldWidth}×{OldHeight}.",
                newSize.X, newSize.Y, DxgiInterop.Describe(hr), hr, _swapChainSize.X, _swapChainSize.Y);
            _failedResizeSize = newSize;

            // A failed ResizeBuffers leaves the chain on its PREVIOUS buffers,
            // so the views released above have to come back at the old size.
            // Skipping this would turn one bad resize into a guaranteed crash
            // on the very next frame, which renders through a null RTV.
            CreateBackBufferViews((uint)_swapChainSize.X, (uint)_swapChainSize.Y);
            _frameIndex = _swapChain.GetCurrentBackBufferIndex();
            DrainDebugMessages();
            return;
        }

        CreateBackBufferViews((uint)newSize.X, (uint)newSize.Y);
        _frameIndex = _swapChain.GetCurrentBackBufferIndex();
        _swapChainSize = newSize;
        _failedResizeSize = null;
    }

    /// <summary>
    /// Belt-and-braces before the swap chain is released: DXGI documents that
    /// releasing a swap chain still in exclusive fullscreen is undefined
    /// behaviour, and the classic symptom is a hang or a crash on exit.
    /// </summary>
    /// <remarks>
    /// The engine never asks for exclusive fullscreen and, since
    /// <see cref="DxgiInterop.SuppressAltEnter"/>, DXGI cannot enter it behind
    /// our back either — so this should always find the chain windowed. It
    /// stays because the cost is one virtual call at shutdown and the failure
    /// it guards against is unrecoverable and machine-dependent (the
    /// association call itself can fail on an odd driver, and that failure is
    /// only a warning).
    /// </remarks>
    private void EnsureSwapChainWindowed()
    {
        if (_swapChain.Handle is null || _deviceLost) return;

        int fullscreen = 0;
        IDXGIOutput* output = null;
        if (((IDXGISwapChain3*)_swapChain.Handle)->GetFullscreenState(&fullscreen, &output) >= 0)
        {
            if (fullscreen != 0)
            {
                _logger.LogWarning("D3D12 swap chain was in exclusive fullscreen at shutdown; returning it to windowed first.");
                ((IDXGISwapChain3*)_swapChain.Handle)->SetFullscreenState(false, (IDXGIOutput*)null);
            }
            if (output is not null)
                output->Release();
        }
    }

    /// <summary>
    /// Builds the one exception a lost device gets, having first asked the
    /// device why it went — the HRESULT alone only says "gone", the removed
    /// reason names the actual fault.
    /// </summary>
    private GraphicsDeviceLostException DeviceLost(int hr, string action)
    {
        int reason = _device.Handle is not null ? DevicePtr->GetDeviceRemovedReason() : 0;

        // Flip the flag before anything else: the throw unwinds through
        // Engine's crash handler straight into Shutdown, which must not try to
        // fence-wait on a dead queue and mask this diagnosis with its own.
        _deviceLost = true;

        // Last chance to get the debug layer's account of it into the log.
        DrainDebugMessages();

        return new GraphicsDeviceLostException(
            $"D3D12 device lost while {action}: {DxgiInterop.Describe(hr)} (0x{hr:X8}); " +
            $"ID3D12Device::GetDeviceRemovedReason = {DxgiInterop.Describe(reason)} (0x{reason:X8}). " +
            "The engine cannot recreate a device mid-run, so this ends the session.");
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

        ReleaseFrameResources();
        ReleaseMeshBufferPool();

        foreach (var target in _renderTargets)
            target.Dispose();
        _renderTargets.Clear();
        foreach (var tex in _textures) tex.Dispose();
        _textures.Clear();
        foreach (var sh in _shaders) sh.Dispose();
        _shaders.Clear();
        DefaultShader = null;

        // Release, not Dispose: Engine.RenderLoop calls Shutdown a second time
        // from its crash handler when the first one threw, and a ComPtr keeps
        // its handle after Dispose — so plain disposal here would over-release
        // everything the first pass already freed. See ComOwnership.
        ReleaseBackBufferViews();
        ComOwnership.Release(ref _samplerRing);
        ComOwnership.Release(ref _srvRing);
        if (_uploadRingCpu is not null)
        {
            ((ID3D12Resource*)_uploadRing.Handle)->Unmap(0, null);
            _uploadRingCpu = null;
        }
        ComOwnership.Release(ref _uploadRing);
        DisposeRetiredUploadRings(); // safe: WaitForGpu ran above
        ComOwnership.Release(ref _rtvHeap);
        ComOwnership.Release(ref _dsvHeap);
        ComOwnership.Release(ref _commandList);
        ComOwnership.Release(ref _commandAllocator);
        ComOwnership.Release(ref _fence);
        if (_fenceEvent != 0)
        {
            Kernel32.CloseHandle(_fenceEvent);
            _fenceEvent = 0;
        }
        EnsureSwapChainWindowed();
        ComOwnership.Release(ref _swapChain);
        ComOwnership.Release(ref _queue);
        ComOwnership.Release(ref _infoQueue);
        _dxgiMessages?.Dispose();
        _dxgiMessages = null;
        ComOwnership.Release(ref _device);

        base.Shutdown();
        _logger.LogInformation("Renderer shut down (D3D12)");
    }

    // ─── Debug layer ─────────────────────────────────────────

    private void DrainDebugMessages()
    {
        // DXGI validates on its own queue, separate from the device's: every
        // swap-chain rejection (ResizeBuffers, Present) is explained there and
        // nowhere else, so it is drained in the same slot.
        int errors = _dxgiMessages?.Drain(_logger, "D3D12") ?? 0;

        if (_infoQueue.Handle is null)
        {
            NoteDebugLayerErrors(errors);
            return;
        }

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
                        errors++;
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

        NoteDebugLayerErrors(errors);
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
