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

    private readonly List<Mesh> _meshes = [];
    private readonly List<Texture> _textures = [];
    private readonly List<ShaderProgram> _shaders = [];
    private readonly List<ID3D11RenderPipeline> _pipelines = [];
    private int _pipelineIndex;

    // Resize is reported on the window thread but the immediate context belongs
    // to the render thread, so we cache the latest pending size and drain it
    // from Render() before the pipeline runs.
    private readonly object _resizeLock = new();
    private Vector2D<int>? _pendingResize;

    private D3D11LineBatch? _lineBatch;
    private ShaderProgram? _debugShader;

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
        if (_swapChain.Handle is not null)
            SilkMarshal.ThrowHResult(((IDXGISwapChain1*)_swapChain.Handle)->Present(0, 0));
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
        Vector2D<int> size = window.FramebufferSize;

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

        window.FramebufferResize += OnFramebufferResize;

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

    public override void Render(Scene.Scene? scene, double deltaTime)
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
            Window = _window,
            Scene = scene,
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
        _lineBatch.Draw(DebugDraw.Vertices, (uint)DebugDraw.VertexCount);
    }

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        var litShader = (D3D11ShaderProgram?)DefaultShader
            ?? throw new InvalidOperationException("Default shader must be created before meshes.");
        var mesh = D3D11Mesh.Create(_device, vertices, indices, attributes, litShader.VertexBytecode);
        _meshes.Add(mesh);
        return mesh;
    }

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels, int width, int height,
        TextureFormat format, TextureFilter filter, TextureWrap wrap)
    {
        var texture = D3D11Texture.Create(_device, pixels, width, height, format, filter, wrap);
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
        if (_window is not null)
            _window.FramebufferResize -= OnFramebufferResize;

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

        ReleaseBackBufferViews();
        _solidRasterizer.Dispose();
        _defaultDepth.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();

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
        // typical engine framerates.
        var desc = new SwapChainDesc1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm,
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
        _swapChain = new ComPtr<IDXGISwapChain1>(swapChainPtr);

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
        _backBufferRtv = new ComPtr<ID3D11RenderTargetView>(rtv);
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
        _depthBuffer = new ComPtr<ID3D11Texture2D>(depthTex);

        ID3D11DepthStencilView* dsv = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateDepthStencilView((ID3D11Resource*)depthTex, null, &dsv));
        _depthView = new ComPtr<ID3D11DepthStencilView>(dsv);
    }

    private void ReleaseBackBufferViews()
    {
        _depthView.Dispose();
        _depthBuffer.Dispose();
        _backBufferRtv.Dispose();
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
        _solidRasterizer = new ComPtr<ID3D11RasterizerState>(rast);
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
        _defaultDepth = new ComPtr<ID3D11DepthStencilState>(depth);
        ((ID3D11DeviceContext*)_context.Handle)->OMSetDepthStencilState(depth, 0);
    }

    private void OnFramebufferResize(Vector2D<int> newSize)
    {
        // Window-thread callback — do NOT touch the immediate context here.
        // Latch the size; the render thread drains it from DrainPendingResize.
        lock (_resizeLock) _pendingResize = newSize;
    }

    private void DrainPendingResize()
    {
        Vector2D<int>? pending;
        lock (_resizeLock)
        {
            pending = _pendingResize;
            _pendingResize = null;
        }
        if (pending is not { } newSize) return;
        if (newSize.X <= 0 || newSize.Y <= 0 || _swapChain.Handle is null) return;

        // ResizeBuffers fails with DXGI_ERROR_INVALID_CALL if anything still
        // holds the back buffer. ClearState drops every device-context
        // binding (RTVs, SRVs, samplers, shaders, IA buffers, raster/depth
        // state); Flush submits any queued work that might still reference
        // the back buffer. Together they make the resize legal.
        var ctx = (ID3D11DeviceContext*)_context.Handle;
        ctx->ClearState();
        ctx->Flush();

        ReleaseBackBufferViews();
        SilkMarshal.ThrowHResult(((IDXGISwapChain1*)_swapChain.Handle)->ResizeBuffers(
            0u,
            (uint)newSize.X,
            (uint)newSize.Y,
            Silk.NET.DXGI.Format.FormatUnknown,
            0u));
        CreateBackBufferViews((uint)newSize.X, (uint)newSize.Y);

        // ClearState reset our default rasterizer/depth state; restore them so
        // the next frame starts from the same baseline as a fresh Initialize().
        ctx->RSSetState((ID3D11RasterizerState*)_solidRasterizer.Handle);
        ctx->OMSetDepthStencilState((ID3D11DepthStencilState*)_defaultDepth.Handle, 0);
    }
}
