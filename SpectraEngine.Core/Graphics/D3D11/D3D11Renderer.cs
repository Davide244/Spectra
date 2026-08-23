using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

// Disambiguate: our namespace and Silk.NET's API class are both named "D3D11".
using D3D11Api = Silk.NET.Direct3D11.D3D11;
using DxgiApi = Silk.NET.DXGI.DXGI;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// Direct3D 11 implementation of <see cref="Renderer"/>. Owns the D3D11 device,
/// the immediate context, the swap chain, and the back-buffer/depth views; the
/// pipelines drive per-frame rendering against this state.
/// </summary>
public sealed unsafe class D3D11Renderer : Renderer
{
    private readonly D3D11Api _d3d11 = D3D11Api.GetApi();
    private readonly DxgiApi _dxgi = DxgiApi.GetApi();
    internal readonly D3DCompiler _d3dCompiler = D3DCompiler.GetApi();

    private IWindow? _window;
    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _context;
    private ComPtr<IDXGISwapChain1> _swapChain;
    private ComPtr<ID3D11RenderTargetView> _backBufferRtv;
    private ComPtr<ID3D11Texture2D> _depthBuffer;
    private ComPtr<ID3D11DepthStencilView> _depthView;
    private ComPtr<ID3D11RasterizerState> _solidRasterizer;
    private ComPtr<ID3D11DepthStencilState> _defaultDepth;
    private ComPtr<ID3D11DepthStencilState> _overlayDepth;

    // Every resource built by the Create* factories is tracked here so
    // Shutdown can free stragglers. Meshes/textures leave early through
    // Renderer.DestroyMesh/DestroyTexture via the Unregister callback handed
    // out at creation. Unsynchronized: creation and destruction both happen
    // on the render thread.
    private readonly List<Mesh> _meshes = [];
    private readonly List<Texture> _textures = [];
    private readonly List<ShaderProgram> _shaders = [];
    private readonly List<ID3D11RenderPipeline> _pipelines = [];
    private int _pipelineIndex;

    // Size the swap chain currently has. Render() compares it against the
    // engine-fed base-class framebuffer latch each frame and reruns the resize
    // path when the window has changed; the immediate context belongs to this
    // (render) thread, so the resize must happen here, never in a window event.
    private Vector2D<int> _swapChainSize;

    // The last size ResizeBuffers refused, if any. A recoverable resize failure
    // leaves the swap chain on its old buffers, so the latch keeps disagreeing
    // and the path would be re-entered — and re-fail — every single frame,
    // burning a ClearState/Flush and a view rebuild each time and drowning the
    // log. Cleared by the next resize that succeeds, so going away and coming
    // back to the same size does get retried.
    private Vector2D<int>? _failedResizeSize;

    // Set once the device is gone. Present and the resize path check it so the
    // run ends on one clear diagnosis instead of a cascade of secondary COM
    // failures against a dead device.
    private bool _deviceLost;

    private D3D11LineBatch? _lineBatch;
    private ShaderProgram? _debugShader;

    // Debug-layer message queue, present only when the device was created with
    // the Debug flag. Drained into the logger once per frame (and after init)
    // so validation errors are visible without a native debugger attached.
    private ComPtr<ID3D11InfoQueue> _infoQueue;

    // DXGI's own message queue, which is where every swap-chain rejection is
    // explained — the device info queue above never sees them. Available when
    // the device was created with the Debug flag and Graphics Tools is
    // installed; a cheap no-op otherwise.
    private DxgiDebugMessages? _dxgiMessages;

    // GL clip-space Z is [-1, 1]; D3D clip-space Z is [0, 1]. SpectraShade
    // shaders are authored once and used with both; we post-mul the projection
    // matrix by this remap so the same shader source produces correct D3D NDC.
    // Row-vector convention: z_d3d = 0.5*z_gl + 0.5*w_gl.
    public static readonly Matrix4x4 GlToD3dClipZ = new(
        1f, 0f,    0f,   0f,
        0f, 1f,    0f,   0f,
        0f, 0f,   0.5f, 0f,
        0f, 0f,   0.5f, 1f);

    public override GraphicsBackend Backend => GraphicsBackend.D3D11;

    /// <summary>D3D11 creates its own device, so the window must not bring up an OpenGL context.</summary>
    public override GraphicsAPI WindowApi => GraphicsAPI.None;

    public override string CurrentPipelineName =>
        _pipelines.Count == 0 ? "None" : _pipelines[_pipelineIndex].Name;

    public override void AcquireContext(IWindow window) { /* D3D11 immediate context isn't thread-affine */ }
    public override void ReleaseContext(IWindow window) { }

    public override void Present(IWindow window)
    {
        if (_swapChain.Handle is not null && !_deviceLost)
        {
            // Present is the call a lost device most often surfaces on (a TDR
            // lands here). It gets the same treatment as the resize path: a
            // named diagnosis carrying the removed reason, not an opaque
            // COMException from deep inside SilkMarshal.
            int hr = ((IDXGISwapChain1*)_swapChain.Handle)->Present(0, 0);
            if (hr < 0)
            {
                if (DxgiInterop.IsDeviceLost(hr))
                    throw DeviceLost(hr, "presenting a frame");
                SilkMarshal.ThrowHResult(hr);
            }
        }
        DrainDebugMessages();
    }

    // Pops every message the debug layer accumulated and logs it. No-ops on
    // release-mode devices (no info queue). Runs on the render thread.
    private void DrainDebugMessages()
    {
        // DXGI validates on its own queue, separate from the device's: every
        // swap-chain rejection (ResizeBuffers, Present) is explained there and
        // nowhere else, so it is drained in the same slot.
        _dxgiMessages?.Drain(_logger, "D3D11");

        if (_infoQueue.Handle is null) return;

        var queue = (ID3D11InfoQueue*)_infoQueue.Handle;
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
                        _logger.LogError("D3D11 debug layer: {Message}", text);
                        break;
                    case MessageSeverity.Warning:
                        _logger.LogWarning("D3D11 debug layer: {Message}", text);
                        break;
                    default:
                        _logger.LogDebug("D3D11 debug layer: {Message}", text);
                        break;
                }
            }
        }
        if (count > 0)
            queue->ClearStoredMessages();
    }

    internal ComPtr<ID3D11Device> Device => _device;
    internal ComPtr<ID3D11DeviceContext> Context => _context;

    public D3D11Renderer(ILogger<Renderer> logger, IShaderCompiler shaderCompiler)
        : base(logger, shaderCompiler)
    {
    }

    public override void Initialize(IWindow window)
    {
        _window = window;

        // Read the engine-fed latch, not window.FramebufferSize: this runs on
        // the render thread while the main thread is already pumping
        // glfwPollEvents, and GLFW guarantees no thread safety for that pair.
        // The engine seeded the latch before this thread started.
        Vector2D<int> size = FramebufferSize;
        _swapChainSize = size;

        CreateDeviceAndSwapChain(window, size.X, size.Y);
        CreateBackBufferViews((uint)size.X, (uint)size.Y);
        CreateDefaultStates();

        // Build the base shaders. Same source-first / embedded-fallback pattern
        // as the OpenGL backend so hot-reload works in the dev tree.
        DefaultShader = BaseShaders.LitPath is { } litPath
            ? CreateShaderFromFile(litPath)
            : CreateShaderFromSource(BaseShaders.Lit);
        _debugShader = BaseShaders.DebugLinePath is { } debugPath
            ? CreateShaderFromFile(debugPath)
            : CreateShaderFromSource(BaseShaders.DebugLine);
        _lineBatch = new D3D11LineBatch(_device, _context, (D3D11ShaderProgram)_debugShader!);

        RegisterPipeline(new D3D11ForwardPipeline());
        RegisterPipeline(new D3D11WireframePipeline());

        DrainDebugMessages();
        _logger.LogInformation("Renderer initialized (D3D11, pipeline={Pipeline})", CurrentPipelineName);
    }

    public void RegisterPipeline(ID3D11RenderPipeline pipeline)
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

        var ctx = new D3D11RenderContext
        {
            Renderer = this,
            Device = _device,
            Context = _context,
            BackBufferRtv = _backBufferRtv,
            DepthView = _depthView,
            Scene = scene,
            View = view,
            DeltaTime = deltaTime,
        };
        _pipelines[_pipelineIndex].Execute(ctx);
    }

    /// <summary>
    /// Uploads and draws the accumulated <see cref="Renderer.DebugDraw"/> lines
    /// with depth-test off. Called by pipelines after their main scene pass.
    /// </summary>
    internal void FlushDebugDraw(Scene.Camera camera)
    {
        if (DebugDraw.VertexCount == 0 || _debugShader is null || _lineBatch is null) return;

        var debug = (D3D11ShaderProgram)_debugShader;
        debug.SetUniform("uView", camera.View);
        debug.SetUniform("uProjection", camera.Projection * GlToD3dClipZ);
        debug.Use();
        // Always-on-top: swap to the depth-off overlay state for the lines and
        // restore the default so the next frame's main pass is depth-correct.
        var ctx = (ID3D11DeviceContext*)_context.Handle;
        ctx->OMSetDepthStencilState((ID3D11DepthStencilState*)_overlayDepth.Handle, 0);
        _lineBatch.Draw(DebugDraw.Vertices, (uint)DebugDraw.VertexCount);
        ctx->OMSetDepthStencilState((ID3D11DepthStencilState*)_defaultDepth.Handle, 0);
    }

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        var litShader = (D3D11ShaderProgram?)DefaultShader
            ?? throw new InvalidOperationException("Default shader must be created before meshes.");
        var mesh = D3D11Mesh.Create(_device, vertices, indices, attributes, litShader.VertexBytecode);
        mesh.Unregister = () => _meshes.Remove(mesh);
        _meshes.Add(mesh);
        return mesh;
    }

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels, int width, int height,
        TextureFormat format, TextureColorSpace colorSpace, TextureFilter filter, TextureWrap wrap)
    {
        var texture = D3D11Texture.Create(_device, pixels, width, height, format, colorSpace, filter, wrap);
        texture.Unregister = () => _textures.Remove(texture);
        _textures.Add(texture);
        return texture;
    }

    public override ShaderProgram CreateShader(string vertexSource, string fragmentSource)
    {
        var shader = D3D11ShaderProgram.Create(_d3dCompiler, _device, _context, vertexSource, fragmentSource);
        _shaders.Add(shader);
        return shader;
    }

    public override ShaderProgram CreateShader(PipelineBlob blob)
    {
        if (blob.Backend != GraphicsBackend.D3D11)
            throw new ArgumentException($"Expected D3D11 blob, got {blob.Backend}");
        if (blob.Format != ShaderDataFormat.SourceText)
            throw new ArgumentException($"D3D11 expects HLSL SourceText, got {blob.Format}");

        string vs = Encoding.UTF8.GetString(blob.VertexData
            ?? throw new InvalidOperationException("Compiled shader has no vertex stage"));
        string ps = Encoding.UTF8.GetString(blob.FragmentData
            ?? throw new InvalidOperationException("Compiled shader has no fragment stage"));

        return CreateShader(vs, ps);
    }

    public override void Shutdown()
    {
        foreach (var pipeline in _pipelines)
            pipeline.Dispose();
        _pipelines.Clear();

        _lineBatch?.Dispose();
        _lineBatch = null;
        _debugShader = null;

        foreach (var mesh in _meshes) mesh.Dispose();
        _meshes.Clear();
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
        ComOwnership.Release(ref _solidRasterizer);
        ComOwnership.Release(ref _defaultDepth);
        ComOwnership.Release(ref _overlayDepth);
        EnsureSwapChainWindowed();
        ComOwnership.Release(ref _swapChain);
        ComOwnership.Release(ref _infoQueue);
        _dxgiMessages?.Dispose();
        _dxgiMessages = null;
        ComOwnership.Release(ref _context);
        ComOwnership.Release(ref _device);

        base.Shutdown();
        _logger.LogInformation("Renderer shut down (D3D11)");
    }

    // ─── Device + swap chain setup ───────────────────────────

    private void CreateDeviceAndSwapChain(IWindow window, int width, int height)
    {
        var native = window.Native
            ?? throw new InvalidOperationException("D3D11 requires a native window handle; window has none.");
        nint hwnd = native.Win32?.Hwnd
            ?? throw new InvalidOperationException("D3D11 backend only runs on Win32 (need HWND).");

        D3DFeatureLevel featureLevel = default;
        D3DFeatureLevel[] requested = [D3DFeatureLevel.Level110];

        // Try with the debug layer first (requires the Windows Graphics Tools
        // optional feature). If it fails (E_FAIL when SDK debug layer missing),
        // fall back to release-mode creation so we don't lock out dev machines.
        const uint baseFlags = (uint)CreateDeviceFlag.BgraSupport;
        const uint debugFlags = baseFlags | (uint)CreateDeviceFlag.Debug;

        fixed (D3DFeatureLevel* featureLevels = requested)
        {
            int hr = _d3d11.CreateDevice(
                default(ComPtr<IDXGIAdapter>),
                D3DDriverType.Hardware,
                Software: 0,
                Flags: debugFlags,
                pFeatureLevels: featureLevels,
                FeatureLevels: (uint)requested.Length,
                SDKVersion: D3D11Api.SdkVersion,
                ppDevice: ref _device,
                pFeatureLevel: ref featureLevel,
                ppImmediateContext: ref _context);

            if (hr < 0)
            {
                _logger.LogInformation("D3D11 debug layer unavailable (hr=0x{Hr:X}); creating without it.", hr);
                SilkMarshal.ThrowHResult(_d3d11.CreateDevice(
                    default(ComPtr<IDXGIAdapter>),
                    D3DDriverType.Hardware,
                    Software: 0,
                    Flags: baseFlags,
                    pFeatureLevels: featureLevels,
                    FeatureLevels: (uint)requested.Length,
                    SDKVersion: D3D11Api.SdkVersion,
                    ppDevice: ref _device,
                    pFeatureLevel: ref featureLevel,
                    ppImmediateContext: ref _context));
            }
            else
            {
                _logger.LogInformation("D3D11 debug layer active.");

                ID3D11InfoQueue* infoQueue = null;
                Guid infoQueueGuid = ID3D11InfoQueue.Guid;
                if (((ID3D11Device*)_device.Handle)->QueryInterface(&infoQueueGuid, (void**)&infoQueue) >= 0)
                    _infoQueue = ComOwnership.Own(infoQueue);

                // The device debug flag also brings up the DXGI debug layer, so
                // this is where its queue becomes available.
                _dxgiMessages = DxgiDebugMessages.Acquire(_dxgi);
            }
        }

        // Walk device -> DXGI device -> adapter -> factory to be able to make
        // the swap chain. Using IDXGIFactory2 so we get the flip-discard model.
        ComPtr<IDXGIDevice> dxgiDevice = default;
        Guid dxgiDeviceGuid = IDXGIDevice.Guid;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->QueryInterface(
            &dxgiDeviceGuid, (void**)dxgiDevice.GetAddressOf()));

        IDXGIAdapter* adapter = null;
        SilkMarshal.ThrowHResult(dxgiDevice.GetAdapter(&adapter));
        Guid factoryGuid = IDXGIFactory2.Guid;
        IDXGIFactory2* factory = null;
        SilkMarshal.ThrowHResult(adapter->GetParent(&factoryGuid, (void**)&factory));

        // Legacy bitblt model (DXGI_SWAP_EFFECT_DISCARD). The flip models
        // (FlipDiscard/FlipSequential) rotate the back buffer internally on
        // every Present, which means a cached RTV from GetBuffer(0) gets stale
        // and ResizeBuffers can fail with DXGI_ERROR_INVALID_CALL because of
        // the implicit reference. Discard model keeps GetBuffer(0) stable and
        // is fine for desktop use; the perf delta vs Flip is negligible at
        // typical engine framerates. It is NOT implicated in the Alt+Enter
        // fullscreen crash — that one is about who drives the transition, not
        // about the swap effect — so it stays as documented.
        //
        // Flags stays 0 on purpose, in particular WITHOUT
        // DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH: this engine never switches
        // display modes. Fullscreen is borderless windowed driven by
        // WindowModeLatch, and MakeWindowAssociation below stops DXGI from
        // driving a mode switch behind our back.
        var desc = new SwapChainDesc1
        {
            Width = (uint)width,
            Height = (uint)height,
            // R2: the back buffer encodes sRGB on write, so the shader's linear
            // output becomes correct display values with no shader involvement.
            //
            // Named directly on the CHAIN rather than on the RTV, which is the
            // opposite of what D3D12 does two files over, and the difference is
            // the swap effect. A bitblt chain (Discard, above) may be created
            // with an _SRGB format outright; a flip-model chain may NOT, and
            // gets its sRGB-ness from an _SRGB view over a _UNORM buffer
            // instead. Each backend uses the form its own swap effect allows.
            Format = Silk.NET.DXGI.Format.FormatR8G8B8A8UnormSrgb,
            Stereo = 0,
            SampleDesc = new SampleDesc(1, 0),
            BufferUsage = DxgiApi.UsageRenderTargetOutput,
            BufferCount = 1,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.Discard,
            AlphaMode = AlphaMode.Unspecified,
            Flags = 0,
        };

        IDXGISwapChain1* swapChainPtr = null;
        SilkMarshal.ThrowHResult(factory->CreateSwapChainForHwnd(
            (IUnknown*)_device.Handle,
            hwnd,
            &desc,
            null,
            null,
            &swapChainPtr));
        _swapChain = ComOwnership.Own(swapChainPtr);

        // Before the factory goes: the window association is per-factory, so it
        // has to be made on THIS one — the one that created the chain, reached
        // through device → DXGI device → adapter → parent — and therefore
        // before the Release below. See DxgiInterop.SuppressAltEnter for what
        // DXGI does to the render thread if we skip it.
        DxgiInterop.SuppressAltEnter(factory, hwnd, _logger, "D3D11");

        adapter->Release();
        factory->Release();
        dxgiDevice.Dispose();
    }

    private void CreateBackBufferViews(uint width, uint height)
    {
        // Render target view of the back buffer.
        Guid texGuid = ID3D11Texture2D.Guid;
        ID3D11Texture2D* backBuffer = null;
        SilkMarshal.ThrowHResult(((IDXGISwapChain1*)_swapChain.Handle)->GetBuffer(0, &texGuid, (void**)&backBuffer));
        ID3D11RenderTargetView* rtv = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, &rtv));
        // Own, not `new ComPtr<>(...)`: the ComPtr constructor AddRefs, so
        // wrapping CreateRenderTargetView's already-owned pointer would leave
        // TWO references on the view and ReleaseBackBufferViews would only ever
        // drop it to one. A surviving RTV holds its own reference to the back
        // buffer, and DXGI then refuses ResizeBuffers with
        // DXGI_ERROR_INVALID_CALL — the identical trap D3D12 hit one level up.
        // See ComOwnership.
        _backBufferRtv = ComOwnership.Own(rtv);
        backBuffer->Release();

        // Depth-stencil texture + view.
        var depthDesc = new Texture2DDesc
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Silk.NET.DXGI.Format.FormatD24UnormS8Uint,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.DepthStencil,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };
        ID3D11Texture2D* depthTex = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateTexture2D(&depthDesc, null, &depthTex));
        _depthBuffer = ComOwnership.Own(depthTex);

        ID3D11DepthStencilView* dsv = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateDepthStencilView((ID3D11Resource*)depthTex, null, &dsv));
        // Same handover for the depth pair: without it each resize leaks a
        // full-screen depth surface and its view.
        _depthView = ComOwnership.Own(dsv);
    }

    // Idempotent on purpose: the resize path releases the views and a device
    // loss can throw before they are rebuilt, after which Shutdown releases
    // them again. Clearing the fields is what keeps that second pass a no-op
    // rather than an over-release. See ComOwnership.Release.
    private void ReleaseBackBufferViews()
    {
        ComOwnership.Release(ref _depthView);
        ComOwnership.Release(ref _depthBuffer);
        ComOwnership.Release(ref _backBufferRtv);
    }

    private void CreateDefaultStates()
    {
        // Match the OpenGL defaults: back-face culling, CCW front-facing,
        // depth test enabled with Less compare and depth write on.
        var rastDesc = new RasterizerDesc
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.Back,
            FrontCounterClockwise = 1,
            DepthBias = 0,
            DepthBiasClamp = 0f,
            SlopeScaledDepthBias = 0f,
            DepthClipEnable = 1,
            ScissorEnable = 0,
            MultisampleEnable = 0,
            AntialiasedLineEnable = 0,
        };
        ID3D11RasterizerState* rast = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateRasterizerState(&rastDesc, &rast));
        _solidRasterizer = ComOwnership.Own(rast);
        ((ID3D11DeviceContext*)_context.Handle)->RSSetState(rast);

        var depthDesc = new DepthStencilDesc
        {
            DepthEnable = 1,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunc.Less,
            StencilEnable = 0,
        };
        ID3D11DepthStencilState* depth = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateDepthStencilState(&depthDesc, &depth));
        _defaultDepth = ComOwnership.Own(depth);
        ((ID3D11DeviceContext*)_context.Handle)->OMSetDepthStencilState(depth, 0);

        // Debug-overlay state: depth test and writes off, so debug lines draw
        // always-on-top exactly like the OpenGL backend's flush (which brackets
        // its lines with glDisable(GL_DEPTH_TEST)).
        var overlayDesc = new DepthStencilDesc
        {
            DepthEnable = 0,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunc.Always,
            StencilEnable = 0,
        };
        ID3D11DepthStencilState* overlay = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateDepthStencilState(&overlayDesc, &overlay));
        _overlayDepth = ComOwnership.Own(overlay);
    }

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
        // render thread, which owns the immediate context.
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
        // D3D12 cannot drift apart on it. See SwapChainResizePolicy.
        if (!SwapChainResizePolicy.ShouldResize(
                newSize, _swapChainSize, _failedResizeSize, _swapChain.Handle is not null, _deviceLost))
            return;

        // ResizeBuffers fails with DXGI_ERROR_INVALID_CALL if anything still
        // holds the back buffer. ClearState drops every device-context
        // binding (RTVs, SRVs, samplers, shaders, IA buffers, raster/depth
        // state); Flush submits any queued work that might still reference
        // the back buffer. Together they make the resize legal.
        var ctx = (ID3D11DeviceContext*)_context.Handle;
        ctx->ClearState();
        ctx->Flush();

        ReleaseBackBufferViews();

        int hr = ((IDXGISwapChain1*)_swapChain.Handle)->ResizeBuffers(
            0u,
            (uint)newSize.X,
            (uint)newSize.Y,
            Silk.NET.DXGI.Format.FormatUnknown,
            0u);

        if (hr < 0)
        {
            if (DxgiInterop.IsDeviceLost(hr))
                throw DeviceLost(hr, $"resizing the swap chain to {newSize.X}×{newSize.Y}");

            _logger.LogError(
                "D3D11 ResizeBuffers to {Width}×{Height} failed: {Code} (0x{Hr:X8}). Staying at {OldWidth}×{OldHeight}.",
                newSize.X, newSize.Y, DxgiInterop.Describe(hr), hr, _swapChainSize.X, _swapChainSize.Y);
            _failedResizeSize = newSize;

            // A failed ResizeBuffers leaves the chain on its PREVIOUS buffers,
            // so the views released above have to come back at the old size.
            // Skipping this would turn one bad resize into a guaranteed crash
            // on the very next frame, which renders through a null RTV.
            CreateBackBufferViews((uint)_swapChainSize.X, (uint)_swapChainSize.Y);
            RestoreDefaultContextState(ctx);
            DrainDebugMessages();
            return;
        }

        CreateBackBufferViews((uint)newSize.X, (uint)newSize.Y);
        _swapChainSize = newSize;
        _failedResizeSize = null;
        RestoreDefaultContextState(ctx);
    }

    // ClearState above reset our default rasterizer/depth state; restore them
    // so the next frame starts from the same baseline as a fresh Initialize().
    // Both exits from the resize path owe this — the failure path cleared the
    // state just as thoroughly as the success path did.
    private void RestoreDefaultContextState(ID3D11DeviceContext* ctx)
    {
        ctx->RSSetState((ID3D11RasterizerState*)_solidRasterizer.Handle);
        ctx->OMSetDepthStencilState((ID3D11DepthStencilState*)_defaultDepth.Handle, 0);
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
        if (((IDXGISwapChain1*)_swapChain.Handle)->GetFullscreenState(&fullscreen, &output) >= 0)
        {
            if (fullscreen != 0)
            {
                _logger.LogWarning("D3D11 swap chain was in exclusive fullscreen at shutdown; returning it to windowed first.");
                ((IDXGISwapChain1*)_swapChain.Handle)->SetFullscreenState(false, (IDXGIOutput*)null);
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
        int reason = _device.Handle is not null
            ? ((ID3D11Device*)_device.Handle)->GetDeviceRemovedReason()
            : 0;

        // Flip the flag before anything else: the throw unwinds through
        // Engine's crash handler straight into Shutdown, which must not go on
        // talking to a dead device and mask this diagnosis with its own.
        _deviceLost = true;

        // Last chance to get the debug layer's account of it into the log.
        DrainDebugMessages();

        return new GraphicsDeviceLostException(
            $"D3D11 device lost while {action}: {DxgiInterop.Describe(hr)} (0x{hr:X8}); " +
            $"ID3D11Device::GetDeviceRemovedReason = {DxgiInterop.Describe(reason)} (0x{reason:X8}). " +
            "The engine cannot recreate a device mid-run, so this ends the session.");
    }
}
