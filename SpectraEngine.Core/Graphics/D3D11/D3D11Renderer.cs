using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Diagnostics;
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

    private IRenderSurface? _surface;
    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _context;
    private ComPtr<IDXGISwapChain1> _swapChain;
    private ComPtr<ID3D11RenderTargetView> _backBufferRtv;
    private ComPtr<ID3D11Texture2D> _depthBuffer;
    private ComPtr<ID3D11DepthStencilView> _depthView;
    private ComPtr<ID3D11RasterizerState> _solidRasterizer;

    // The depth-biased twin of _solidRasterizer, rebuilt only when the bias
    // values themselves change (which in practice is once, if ever). A
    // dictionary would be the general answer; there is exactly one biased pass
    // in the engine, and one cached object says so.
    private ComPtr<ID3D11RasterizerState> _biasedRasterizer;
    private DepthBias _biasedRasterizerFor;
    private ComPtr<ID3D11DepthStencilState> _defaultDepth;
    private ComPtr<ID3D11DepthStencilState> _overlayDepth;
    private ComPtr<ID3D11DepthStencilState> _worldLineDepth;
    private ComPtr<ID3D11BlendState> _alphaBlend;

    // Every resource built by the Create* factories is tracked here so
    // Shutdown can free stragglers. Meshes/textures leave early through
    // Renderer.DestroyMesh/DestroyTexture via the Unregister callback handed
    // out at creation. Unsynchronized: creation and destruction both happen
    // on the render thread.
    private readonly List<Mesh> _meshes = [];
    private readonly List<Texture> _textures = [];
    private readonly List<ShaderProgram> _shaders = [];
    private readonly List<ID3D11RenderPipeline> _pipelines = [];
    private readonly List<RenderTarget> _renderTargets = [];
    private int _pipelineIndex;

    // One skip cache for the one immediate context, shared by every program and
    // reset by every site that clears the context's SRV slots. Context-level on
    // purpose; see D3D11BindCache for the failure a per-program cache was.
    private readonly D3D11BindCache _bindCache = new();

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

    // True for a surface somebody else presents: no swap chain, no back buffer,
    // and the frame resolves into _presentTarget instead of into the window.
    private bool _composited;

    // Where the frame is presented on a composited surface: a shared target
    // whose NT handle the consumer imports. Null on a window surface, which is
    // exactly what makes every "target null means the back buffer" call site
    // below keep meaning what it always meant.
    private D3D11RenderTarget? _presentTarget;

    // The generation _presentTarget was built under, and the retired ones still
    // held for a consumer that has not let go. See SharedTargetRetirement.
    private SharedTargetRetirement? _retirement;
    private int _presentGeneration;

    // Whether the shared key is currently held, so EndSharedWrite is a no-op
    // after a Begin that timed out rather than a release of a key this side
    // never took - which the runtime reports and which hands the texture to a
    // consumer mid-write.
    private bool _sharedWriteHeld;

    // A consumer that is not being drawn never takes its turn, so the timeout
    // is a steady state rather than an event: logged the first time and then
    // once more when it clears, or the log is the frame rate.
    private bool _sharedTimeoutLogged;

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


    /// <inheritdoc/>
    /// <remarks>The 0..1 clip-Z remap above, exposed to backend-neutral code.</remarks>
    public override Matrix4x4 ClipZCorrection => GlToD3dClipZ;

    /// <inheritdoc/>
    /// <remarks>
    /// Identity, because <see cref="GlToD3dClipZ"/> already put clip z in the
    /// 0..1 range a depth buffer stores. OpenGL needs the other answer.
    /// </remarks>
    public override Vector2 DepthToNdcZ => new(1f, 0f);

    public override GraphicsBackend Backend => GraphicsBackend.D3D11;

    /// <summary>D3D11 creates its own device, so the window must not bring up an OpenGL context.</summary>
    public override GraphicsAPI WindowApi => GraphicsAPI.None;

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

    public override void AcquireContext(IRenderSurface surface) { /* D3D11 immediate context isn't thread-affine */ }
    public override void ReleaseContext(IRenderSurface surface) { }

    public override void Present(IRenderSurface surface)
    {
        if (_swapChain.Handle is not null && !_deviceLost)
        {
            // Present is the call a lost device most often surfaces on (a TDR
            // lands here). It gets the same treatment as the resize path: a
            // named diagnosis carrying the removed reason, not an opaque
            // COMException from deep inside SilkMarshal.
            int hr = ((IDXGISwapChain1*)_swapChain.Handle)->Present(VSync ? 1u : 0u, 0);
            if (hr < 0)
            {
                if (DxgiInterop.IsDeviceLost(hr))
                    throw DeviceLost(hr, "presenting a frame");
                SilkMarshal.ThrowHResult(hr);
            }
        }

        // OUTSIDE the swap-chain guard, and that placement is the composited
        // path's whole error gate. A composited surface has no chain, so the
        // Present above is skipped every frame; it also has no offscreen probe
        // and no back buffer to read a pixel out of, which leaves the debug
        // layer as the only continuous detector of a missing bind or a pipeline
        // state compiled for another target. Draining only when there is
        // something to present would turn that off exactly where it is the only
        // thing left.
        DrainDebugMessages();
    }

    // Pops every message the debug layer accumulated and logs it. No-ops on
    // release-mode devices (no info queue). Runs on the render thread.
    private void DrainDebugMessages()
    {
        // DXGI validates on its own queue, separate from the device's: every
        // swap-chain rejection (ResizeBuffers, Present) is explained there and
        // nowhere else, so it is drained in the same slot.
        int errors = _dxgiMessages?.Drain(_logger, "D3D11") ?? 0;

        if (_infoQueue.Handle is null)
        {
            NoteDebugLayerErrors(errors);
            return;
        }

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
                        errors++;
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

        NoteDebugLayerErrors(errors);
    }

    internal ComPtr<ID3D11Device> Device => _device;
    internal ComPtr<ID3D11DeviceContext> Context => _context;

    public D3D11Renderer(ILogger<Renderer> logger, IShaderCompiler shaderCompiler)
        : base(logger, shaderCompiler)
    {
    }

    public override void Initialize(IRenderSurface surface)
    {
        _surface = surface;

        // Read the engine-fed latch, not window.FramebufferSize: this runs on
        // the render thread while the main thread is already pumping
        // glfwPollEvents, and GLFW guarantees no thread safety for that pair.
        // The engine seeded the latch before this thread started.
        Vector2D<int> size = FramebufferSize;
        _swapChainSize = size;
        _composited = surface.Kind == RenderSurfaceKind.Composited;

        CreateDevice(surface);

        // A composited surface is presented by somebody else, so there is no
        // chain and no back buffer: everything the frame would have written into
        // the window goes into the shared target instead, built on demand from
        // the same size latch a swap chain would have followed.
        if (!_composited)
        {
            CreateSwapChain(surface.NativeHandle, size.X, size.Y);
            CreateBackBufferViews((uint)size.X, (uint)size.Y);
        }

        CreateDefaultStates();

        // Build the base shaders. Same source-first / embedded-fallback pattern
        // as the OpenGL backend so hot-reload works in the dev tree.
        DefaultShader = CreateBaseShader(BaseShaders.LitFileName);
        _debugShader = CreateBaseShader(BaseShaders.DebugLineFileName);
        _lineBatch = new D3D11LineBatch(_device, _context, (D3D11ShaderProgram)_debugShader!);

        // Deferred first: see OpenGLRenderer for why it is the default.
        RegisterPipeline(new D3D11DeferredPipeline());
        RegisterPipeline(new D3D11ForwardPipeline());
        RegisterPipeline(new D3D11WireframePipeline());

        // Built here rather than on the first frame, because the handle is what
        // a host wires its consumer up with and it must exist by the time
        // Initialize returns: a host that has to render a frame before it can be
        // told where to look has to special-case its own startup, and would
        // publish a zero handle if it did not.
        EnsurePresentTarget();

        DrainDebugMessages();
        _logger.LogInformation(
            "Renderer initialized (D3D11, pipeline={Pipeline}, surface={Surface})",
            CurrentPipelineName, _composited ? "composited (no swap chain)" : "window");
    }

    public void RegisterPipeline(ID3D11RenderPipeline pipeline)
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

        // Null on a window surface, which is what keeps every "output null means
        // the back buffer" decision below byte-for-byte the path it always took.
        RenderTarget? present = EnsurePresentTarget();

        // A composited surface with no target has nowhere to draw: the pane is
        // collapsed or mid-layout, which is not an error and not a frame.
        if (_composited && present is null) return;

        var ctx = new D3D11RenderContext
        {
            Renderer = this,
            Device = _device,
            Context = _context,
            Scene = scene,
            View = view,
            DeltaTime = deltaTime,
        };

        // Outside the shared bracket on purpose: the probe writes its own target
        // and never touches the shared one.
        if (ProbeTarget is { } probe)
        {
            FrameTarget = probe;
            _pipelines[_pipelineIndex].Execute(ctx);
        }

        // With HDR off there is no intermediate to resolve from, so the pipeline
        // draws straight into whatever is being presented - which on a
        // composited surface is the shared texture itself, and is why the
        // bracket has to cover the pipeline as well as the resolve.
        // Before the live target's own bracket, so a turn queued against a
        // generation this frame's resize just retired is answered rather than
        // left waiting for a release that is never coming. See
        // SharedTargetRetirement.OfferTurns.
        _retirement?.OfferTurns();

        RenderTarget? sceneTarget = HdrEnabled ? EnsureSceneTarget() : present;

        if (present is not null && !BeginSharedWrite())
        {
            // The consumer never took its turn, so the key never came back. That
            // is a hidden pane rather than a fault: skip this frame's write and
            // leave it holding the last one it was given, which is the right
            // picture for something nobody is looking at. Blocking here would
            // stall the whole engine on a turn that may never arrive.
            return;
        }

        try
        {
            FrameTarget = sceneTarget;
            _pipelines[_pipelineIndex].Execute(ctx);

            // The overlay follows the resolve's output, always. Left pointed at
            // the window it would draw into a null render-target view on a
            // composited surface: no error, no debug-layer message, and a
            // viewport with no gizmo handles in it.
            if (sceneTarget is null || ReferenceEquals(sceneTarget, present))
            {
                DrawOverlay(scene, present);
                return;
            }

            ResolveTo(sceneTarget.ColorTexture!, present, scene);

            // The same source, the same pass, the same frame, into an ordinary
            // sRGB target: whatever the shared write does differently is then
            // the only thing a byte comparison can find. Inside the bracket
            // because it costs nothing to be and because leaving it outside
            // would put a second resolve between the frame's write and the
            // hand-over for no reason. See Renderer.CompareTarget.
            if (CompareTarget is { } reference)
                ResolveTo(sceneTarget.ColorTexture!, reference, scene);
        }
        finally
        {
            if (present is not null) EndSharedWrite();
        }
    }

    protected override void DrawFullscreen(PostPass pass, Mesh geometry)
    {
        var context = (ID3D11DeviceContext*)_context.Handle;

        // Depth off, and solid fill: D3D11WireframePipeline restores its OWN
        // solid rasterizer object rather than the renderer's, so the state here
        // is whatever the last pipeline left.
        context->OMSetDepthStencilState((ID3D11DepthStencilState*)_overlayDepth.Handle, 0);
        context->RSSetState((ID3D11RasterizerState*)_solidRasterizer.Handle);

        // Use LAST on this backend: SetUniform writes CPU-side constant shadows
        // and Use is what uploads them. The opposite of OpenGL, which is why
        // this method is per-backend at all.
        pass.ApplyTo(pass.Shader);
        pass.Shader.Use();
        geometry.Draw();

        context->OMSetDepthStencilState((ID3D11DepthStencilState*)_defaultDepth.Handle, 0);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One texel through the region path, so this backend has exactly one
    /// staging copy and one row-flip expression rather than two that can drift.
    /// </remarks>
    internal override (byte R, byte G, byte B, byte A) ReadTargetPixel(
        RenderTarget target, int x, int y)
    {
        Span<byte> one = stackalloc byte[4];
        ReadTargetPixels(target, x, y, 1, 1, one);
        return (one[0], one[1], one[2], one[3]);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The row flip is here, and it is the only place a D3D readback may do
    /// it.</b> A D3D render target's origin is top-left, so the row a clip
    /// y = -1 vertex rasterises to is the LAST one; the contract's y counts from
    /// the bottom of the picture, so the region's top edge in resource rows is
    /// <c>target.Height - y - height</c> and the staging surface arrives
    /// top-first, which is why the copy below walks the destination backwards.
    /// <para>
    /// <b>The staging surface's row pitch is the driver's, never
    /// <c>width * 4</c>.</b> D3D aligns it however it likes, so a whole-surface
    /// read that assumed a tight pitch would shear the picture by a few texels
    /// per row on one machine and be perfectly correct on another - which is
    /// the worst possible way to find out. <c>Map</c> on the immediate context
    /// is what waits for the copy; there is no fence to take.
    /// </para>
    /// </remarks>
    internal override void ReadTargetPixels(
        RenderTarget target, int x, int y, int width, int height, Span<byte> destination)
    {
        PixelReadback.ValidateRegion(target, x, y, width, height, destination);
        if (target.ColorTexture is not D3D11Texture color)
            throw new ArgumentException("The target has no colour attachment to read.", nameof(target));

        var dev = (ID3D11Device*)_device.Handle;
        var context = (ID3D11DeviceContext*)_context.Handle;

        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = color.DxgiFormat,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Staging,
            BindFlags = 0,
            CPUAccessFlags = (uint)CpuAccessFlag.Read,
            MiscFlags = 0,
        };

        ID3D11Texture2D* stagingPtr = null;
        SilkMarshal.ThrowHResult(dev->CreateTexture2D(&desc, null, &stagingPtr));
        ComPtr<ID3D11Texture2D> staging = ComOwnership.Own(stagingPtr);

        try
        {
            uint top = (uint)(target.Height - y - height);
            var box = new Silk.NET.Direct3D11.Box
            {
                Left = (uint)x,
                Top = top,
                Front = 0,
                Right = (uint)(x + width),
                Bottom = top + (uint)height,
                Back = 1,
            };
            context->CopySubresourceRegion(
                (ID3D11Resource*)stagingPtr, 0, 0, 0, 0, color.Resource, 0, &box);

            MappedSubresource mapped = default;
            SilkMarshal.ThrowHResult(context->Map((ID3D11Resource*)stagingPtr, 0, Map.Read, 0, &mapped));
            try
            {
                PixelReadback.CopyRowsBottomFirst((byte*)mapped.PData, mapped.RowPitch, width, height, destination);
            }
            finally
            {
                context->Unmap((ID3D11Resource*)stagingPtr, 0);
            }
        }
        finally
        {
            ComOwnership.Release(ref staging);
        }
    }

    protected override void SetViewportCore(int x, int y, int width, int height)
    {
        var viewport = new Viewport
        {
            TopLeftX = x, TopLeftY = y,
            Width = width, Height = height,
            MinDepth = 0f, MaxDepth = 1f,
        };
        ((ID3D11DeviceContext*)_context.Handle)->RSSetViewports(1, &viewport);
    }

    protected override void BeginPassCore(
        RenderTarget? target, ReadOnlySpan<RenderTarget> targets, in PassClear clear)
    {
        var ctx = (ID3D11DeviceContext*)_context.Handle;

        ID3D11RenderTargetView* rtv;
        ID3D11DepthStencilView* dsv;
        if (target is D3D11RenderTarget offscreen)
        {
            // The attachment may still be bound as a shader resource from a
            // previous frame that sampled it. D3D11 would unbind it silently and
            // log a warning; doing it here makes the intent explicit and keeps
            // the debug layer quiet enough that a real message is visible.
            UnbindPixelShaderResources();
            rtv = offscreen.Rtv;
            dsv = offscreen.Dsv;
        }
        else
        {
            rtv = (ID3D11RenderTargetView*)_backBufferRtv.Handle;
            dsv = (ID3D11DepthStencilView*)_depthView.Handle;
        }

        // A depth-only target has no RTV BY DESIGN, which is the one case a null
        // RTV is not a failure. Everything else with a null one is: a resize that
        // failed rebuilds the views at the old size, and a device loss between
        // release and rebuild can leave them null for a frame. Drawing through
        // that is the guaranteed crash next frame DrainPendingResize exists to
        // avoid, so those passes are a no-op instead.
        bool depthOnly = target is D3D11RenderTarget { Desc.Color: false };
        if (rtv is null && !depthOnly) return;

        Vector2D<int> size = PassSize;
        SetViewportCore(0, 0, size.X, size.Y);

        if (clear.Color is { } color && rtv is not null)
        {
            Span<float> value = stackalloc float[4] { color.X, color.Y, color.Z, color.W };
            fixed (float* pColor = value)
                ctx->ClearRenderTargetView(rtv, pColor);
        }
        // A depth-less target is legal; clearing a null DSV is not.
        if (clear.Depth is { } depth && dsv is not null)
            ctx->ClearDepthStencilView(dsv, (uint)(ClearFlag.Depth | ClearFlag.Stencil), depth, 0);

        if (targets.Length > 1)
        {
            // All N views at once. Clearing was done on attachment 0 above; the
            // extras are cleared here so a geometry pass never reads a stale
            // G-buffer channel from the previous frame.
            ID3D11RenderTargetView** views = stackalloc ID3D11RenderTargetView*[targets.Length];

            // Hoisted: a stackalloc inside the loop cannot reuse its space, so
            // an eight-attachment pass would allocate eight times per pass and
            // grow the frame's stack use with the G-buffer's width.
            Span<float> clearValue = stackalloc float[4];
            if (clear.Color is { } extraColor)
            {
                clearValue[0] = extraColor.X;
                clearValue[1] = extraColor.Y;
                clearValue[2] = extraColor.Z;
                clearValue[3] = extraColor.W;
            }

            for (int i = 0; i < targets.Length; i++)
            {
                var extra = (D3D11RenderTarget)targets[i];
                views[i] = extra.Rtv;
                if (i > 0 && clear.Color is not null)
                {
                    fixed (float* pExtra = clearValue)
                        ctx->ClearRenderTargetView(extra.Rtv, pExtra);
                }
            }
            ctx->OMSetRenderTargets((uint)targets.Length, views, dsv);
            return;
        }

        // Zero views for a depth-only pass, which is what makes it depth-only:
        // binding a target the pixel shader then writes into is the whole cost
        // this avoids.
        if (rtv is null)
            ctx->OMSetRenderTargets(0, null, dsv);
        else
            ctx->OMSetRenderTargets(1, &rtv, dsv);
    }

    protected override void EndPassCore(RenderTarget? target, ReadOnlySpan<RenderTarget> targets)
    {
        if (target is null) return;

        // Unbind, so the next pass cannot draw into a texture it did not ask
        // for, and so the attachment is free to be sampled.
        var ctx = (ID3D11DeviceContext*)_context.Handle;
        ID3D11RenderTargetView* none = null;
        ctx->OMSetRenderTargets(1, &none, (ID3D11DepthStencilView*)null);
    }

    // Clears every pixel-shader texture slot the engine can bind. Cheap, and it
    // removes the read-write hazard by construction rather than by relying on
    // the runtime to notice it.
    private void UnbindPixelShaderResources()
    {
        const int Slots = D3D11BindCache.TrackedSlots;
        var ctx = (ID3D11DeviceContext*)_context.Handle;
        ID3D11ShaderResourceView** none = stackalloc ID3D11ShaderResourceView*[Slots];
        for (int i = 0; i < Slots; i++) none[i] = null;
        ctx->PSSetShaderResources(0, Slots, none);

        // The slots just changed under every program's feet, so the skip cache
        // must forget them, or SetTexture skips a rebind the context needs and
        // the next pass silently samples null. See D3D11BindCache.
        _bindCache.Reset();
    }

    public override RenderTarget CreateRenderTarget(in RenderTargetDesc desc)
    {
        var target = new D3D11RenderTarget(_device, desc);
        target.Unregister = () => _renderTargets.Remove(target);
        _renderTargets.Add(target);
        return target;
    }

    // ─── The shared present target ───────────────────────────

    /// <summary>
    /// The shared target the frame is presented into on a composited surface,
    /// built or rebuilt to the current size. Always null on a window surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rebuilt, never resized.</b> Every other target in the engine swaps its
    /// GPU resource inside the wrapper so materials sampling it survive; a
    /// shared one cannot, because the consumer imported the NT handle and a
    /// handle is not swappable. So a size change mints a fresh generation and
    /// retires the old target rather than freeing it, since the consumer may be
    /// reading it this instant and freeing it underneath raises nothing on
    /// either side.
    /// </para>
    /// <para>
    /// <b>The size comes from the same latch a swap chain would follow</b>, so a
    /// composited host resizes the engine exactly as a windowed one does and
    /// there is no second size to keep in step.
    /// </para>
    /// </remarks>
    private RenderTarget? EnsurePresentTarget()
    {
        if (!_composited) return null;

        Vector2D<int> size = FramebufferSize;

        // Collapsed or mid-layout: null, so the frame is skipped whole. Same
        // answer EnsureSceneTarget gives at zero, and they have to agree - a
        // frame that kept the previous target while the HDR one came back null
        // would resolve nothing and draw the overlay onto last frame's picture.
        // The existing target is kept rather than torn down, so the consumer
        // holds the last good frame and the handle it already imported stays
        // valid for when the size comes back.
        if (size.X <= 0 || size.Y <= 0) return null;

        if (_presentTarget is { } existing && existing.Width == size.X && existing.Height == size.Y)
            return existing;

        _retirement ??= new SharedTargetRetirement(_logger);

        if (_presentTarget is { } outgoing)
        {
            int retiring = _presentGeneration;
            _presentTarget = null;

            // The flag names the LIVE target's key, and the live target is
            // changing. Unreachable while this runs at the top of Render, above
            // every BeginSharedWrite, and stated anyway because it is the
            // invariant rather than the call order that makes it true.
            _sharedWriteHeld = false;

            // The mutex belongs to the target this closure frees, so it is
            // valid for exactly as long as the offer can be called: the
            // retirement holds both and drops them together.
            IDXGIKeyedMutex* retiredMutex = outgoing.Color is { IsShared: true } retiredColor
                ? retiredColor.KeyedMutex
                : null;

            _retirement.Retire(
                retiring,
                () => DestroyRenderTarget(outgoing),
                () => SharedTargetTurn.Offer(retiredMutex, _logger, retiring));
        }

        // Srgb, because this is where linear light stops: the resolve writes
        // linear values and the target's own view encodes them, exactly as the
        // window's back buffer does. The RESOURCE stays UNORM so the consumer
        // does not decode a second time; see D3D11Texture.
        //
        // Depth, because this stands in for the back buffer and the back buffer
        // has one. The HDR path never uses it - the scene has already been drawn
        // and depth-tested into its own target by the time the resolve runs - so
        // it is a full-screen surface spent on the HdrEnabled = false path,
        // which renders the scene straight in here and cannot work without it.
        var fresh = (D3D11RenderTarget)CreateRenderTarget(new RenderTargetDesc(
            size.X, size.Y, TextureFormat.Rgba8, TextureColorSpace.Srgb,
            Depth: true, TextureFilter.Linear, TextureWrap.Clamp, Color: true,
            RenderTargetSharing.KeyedMutex));

        _presentTarget = fresh;
        _presentGeneration = _retirement.Next();
        _sharedTimeoutLogged = false;

        _logger.LogInformation(
            "Shared present target {Width}x{Height}, generation {Generation}, handle 0x{Handle:X}.",
            size.X, size.Y, _presentGeneration, fresh.Color?.SharedHandle ?? 0);

        return fresh;
    }

    /// <summary>The present target, for tests that drive a pass into it directly.</summary>
    /// <remarks>
    /// Internal because nothing in a game reaches for this: the frame resolves
    /// into it and the host reads the handle. A test needs it because the thing
    /// being proved - that a write on this side is visible through the handle on
    /// another device - is not observable from anywhere else.
    /// </remarks>
    internal RenderTarget? PresentTargetForTest => _presentTarget;

    /// <summary>Runs the present target's size maintenance, for tests that resize without a scene.</summary>
    /// <remarks>
    /// A resize reaches the target through <see cref="Render"/>, which needs a
    /// scene and a view. Driving exactly this step is what keeps a test of the
    /// target from also being a test of the pipelines.
    /// </remarks>
    internal RenderTarget? EnsurePresentTargetForTest() => EnsurePresentTarget();

    private D3D11Texture? SharedColor =>
        _presentTarget?.Color is { IsShared: true } color ? color : null;

    /// <inheritdoc/>
    public override bool TryGetSharedHandle(out SharedTargetHandle handle)
    {
        if (SharedColor is { } color && _presentTarget is { } target)
        {
            handle = new SharedTargetHandle(
                color.SharedHandle, target.Width, target.Height, _presentGeneration);
            return true;
        }

        handle = default;
        return false;
    }

    /// <inheritdoc/>
    public override bool BeginSharedWrite(int timeoutMs = 100)
    {
        if (SharedColor is not { } color) return false;

        // Re-entering would take the key twice and release it once, which reads
        // as a working frame and then deadlocks the consumer forever.
        if (_sharedWriteHeld)
            throw new InvalidOperationException("BeginSharedWrite was called while the shared key was already held.");

        // Timed, because a frame that WAITED here and a frame that WORKED
        // report the same frame time and there is no other way to tell them
        // apart. See Renderer.RecordSharedAcquireWait.
        long acquireStartedAt = Stopwatch.GetTimestamp();
        int hr = color.KeyedMutex->AcquireSync(SharedProducerKey, (uint)Math.Max(0, timeoutMs));
        RecordSharedAcquireWait(Stopwatch.GetTimestamp() - acquireStartedAt);

        // WAIT_TIMEOUT is 0x00000102: a SUCCESS-coded HRESULT, so `hr < 0` reads
        // it as an acquisition, and SilkMarshal.ThrowHResult would let it
        // through. The frame then writes a texture the consumer owns, and the
        // ReleaseSync that follows fails because this side never held the key.
        // Measured, because the value alone does not look like a failure.
        if (hr == WaitTimeout)
        {
            if (!_sharedTimeoutLogged)
            {
                _sharedTimeoutLogged = true;
                _logger.LogInformation(
                    "Shared target key not available within {Timeout} ms; skipping the shared write while the " +
                    "consumer is not taking its turn. It keeps the last frame it was given.", timeoutMs);
            }
            return false;
        }

        if (hr < 0)
        {
            _logger.LogError(
                "Acquiring the shared target key failed: {Code} (0x{Hr:X8}). Skipping this frame's shared write.",
                DxgiInterop.Describe(hr), hr);
            return false;
        }

        // WAIT_ABANDONED (0x00000080): the key IS acquired, but whoever held it
        // last went away without releasing. Worth saying so once - the consumer
        // has died - and worth carrying on, because the texture is ours.
        if (hr == WaitAbandoned)
            _logger.LogWarning("The shared target key was abandoned by its previous holder; taking it anyway.");

        if (_sharedTimeoutLogged)
        {
            _sharedTimeoutLogged = false;
            _logger.LogInformation("Shared target key available again; resuming shared writes.");
        }

        _sharedWriteHeld = true;
        return true;
    }

    /// <inheritdoc/>
    public override void EndSharedWrite()
    {
        if (!_sharedWriteHeld) return;
        _sharedWriteHeld = false;

        if (SharedColor is not { } color) return;

        // Flushed BEFORE the key changes hands. A composited surface never
        // calls Present, so nothing else submits the frame at all, and the
        // consumer runs on its own device: handing over the key with the work
        // still queued gives it a texture the GPU has not written yet.
        ((ID3D11DeviceContext*)_context.Handle)->Flush();

        int hr = color.KeyedMutex->ReleaseSync(SharedConsumerKey);
        if (hr < 0)
        {
            _logger.LogError(
                "Releasing the shared target key failed: {Code} (0x{Hr:X8}). The consumer will not get this frame.",
                DxgiInterop.Describe(hr), hr);
        }
    }

    /// <inheritdoc/>
    public override void NotifySharedTargetReleased(int generation)
    {
        int released = _retirement?.ConsumerReleased(generation) ?? 0;
        if (released > 0)
            _logger.LogDebug("Released {Count} retired shared target generation(s) up to {Generation}.", released, generation);
    }

    /// <inheritdoc/>
    internal override bool TakeSharedConsumerTurn(int timeoutMs = 100) =>
        WithConsumerKey(timeoutMs, static _ => { });

    /// <inheritdoc/>
    /// <remarks>
    /// The present target IS the shared texture on this backend, so this is an
    /// ordinary readback of it. The bracket is what makes it a measurement of
    /// the protocol rather than of the resource.
    /// </remarks>
    internal override bool TryReadSharedPixels(Span<byte> destination, int timeoutMs = 100)
    {
        if (_presentTarget is not { } target) return false;

        // Copied out of the span before the lambda, because a Span cannot be
        // captured; the array is the probe's own and lives exactly as long as
        // the call.
        byte[] scratch = new byte[PixelReadback.ByteCount(target.Width, target.Height)];
        bool read = WithConsumerKey(timeoutMs, self => self.ReadTargetPixels(target, scratch));
        if (read) scratch.CopyTo(destination);
        return read;
    }

    /// <summary>
    /// Runs <paramref name="work"/> holding <see cref="Renderer.SharedConsumerKey"/>
    /// and hands <see cref="Renderer.SharedProducerKey"/> back afterwards.
    /// </summary>
    /// <remarks>
    /// <b>The release is in a finally and the timeout is not a failure</b>, for
    /// the two reasons <see cref="BeginSharedWrite"/> already states: dropping
    /// the release deadlocks the producer on its next frame with nothing
    /// reporting a disagreement, and <c>WAIT_TIMEOUT</c> is a SUCCESS-coded
    /// HRESULT that an <c>hr &lt; 0</c> test reads as an acquisition.
    /// </remarks>
    private bool WithConsumerKey(int timeoutMs, Action<D3D11Renderer> work)
    {
        if (SharedColor is not { } color) return false;

        int hr = color.KeyedMutex->AcquireSync(SharedConsumerKey, (uint)Math.Max(0, timeoutMs));
        if (hr == WaitTimeout) return false;
        if (hr < 0)
        {
            _logger.LogError(
                "Taking the shared target's consumer turn failed: {Code} (0x{Hr:X8}).",
                DxgiInterop.Describe(hr), hr);
            return false;
        }

        try
        {
            work(this);
        }
        finally
        {
            int released = color.KeyedMutex->ReleaseSync(SharedProducerKey);
            if (released < 0)
            {
                _logger.LogError(
                    "Handing the shared target's key back failed: {Code} (0x{Hr:X8}). The next frame will time out.",
                    DxgiInterop.Describe(released), released);
            }
        }

        return true;
    }

    /// <summary>WAIT_TIMEOUT, which AcquireSync returns as a success-coded HRESULT.</summary>
    private const int WaitTimeout = 0x00000102;

    /// <summary>WAIT_ABANDONED: acquired, but the previous holder never released.</summary>
    private const int WaitAbandoned = 0x00000080;

    /// <summary>
    /// Uploads and draws the accumulated <see cref="Renderer.DebugDraw"/> lines
    /// with depth-test off. Called by pipelines after their main scene pass.
    /// </summary>
    protected override void FlushDebugDrawCore(Scene.Camera camera)
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

    /// <inheritdoc/>
    /// <remarks>
    /// <b>A second line batch, because a D3D11 input layout is bound to the
    /// shader signature it was validated against.</b> The batch is created from
    /// a program's vertex bytecode, so reusing the debug batch with the
    /// G-buffer world-line program creates successfully and then fails every
    /// draw. The layouts are identical in content and that changes nothing:
    /// this is the same trap the instance buffer already documents.
    /// </remarks>
    protected override void FlushWorldLinesCore(
        Scene.Camera camera, ShaderProgram program, float nudge, GBuffer? gbuffer)
    {
        var typed = (D3D11ShaderProgram)program;

        // One batch PER PROGRAM. A D3D11 input layout is bound to the shader
        // signature it was validated against, so a batch built for one program
        // creates successfully against another and then fails every draw with a
        // missing-semantic error - the same trap the instance buffer already
        // documents, and the reason this is a dictionary rather than a field.
        if (!_worldLineBatches.TryGetValue(program, out D3D11LineBatch? batch))
        {
            batch = new D3D11LineBatch(_device, _context, typed);
            _worldLineBatches[program] = batch;
        }

        // Use() LAST on this backend: the writes are staged into a constant
        // shadow that Use() flushes, so writing after it would leave this
        // draw with the previous frame's values. The texture bind rides the
        // same order.
        typed.SetUniform("uView", camera.View);
        typed.SetUniform("uProjection", camera.Projection * GlToD3dClipZ);
        typed.SetUniform("uCameraPosition", camera.Position);
        typed.SetUniform("uDepthNudge", nudge);
        typed.SetUniform("uFadeCenter", WorldLines.FadeCenter);
        typed.SetUniform("uFadeStart", WorldLines.FadeStart);
        typed.SetUniform("uFadeEnd", WorldLines.FadeEnd);
        typed.SetUniform("uOpacity", WorldLines.Opacity);

        if (gbuffer is not null)
        {
            typed.SetUniform("uNdcToUv", NdcToUv);
            typed.SetUniform("uDepthToNdc", DepthToNdcZ);
            typed.SetUniform("uGBufferSize", new Vector2(gbuffer.Width, gbuffer.Height));
            // No SRV/DSV hazard: the open pass's depth is the frame target's
            // own, never the G-buffer's, so the sampled depth is bound nowhere
            // else.
            typed.SetTexture("uDepth", 0, gbuffer.Depth);
        }

        typed.Use();

        var ctx = (ID3D11DeviceContext*)_context.Handle;

        // Forward: hardware LessEqual with write off against the pass's live
        // depth. Deferred: depth fully off (the shader compares against the
        // sampled G-buffer depth), which is exactly the overlay state.
        ctx->OMSetDepthStencilState(
            (ID3D11DepthStencilState*)(gbuffer is null ? _worldLineDepth.Handle : _overlayDepth.Handle), 0);
        ctx->OMSetBlendState((ID3D11BlendState*)_alphaBlend.Handle, null, 0xFFFFFFFF);
        batch.Draw(WorldLines.Vertices, (uint)WorldLines.VertexCount);
        ctx->OMSetBlendState(null, null, 0xFFFFFFFF);
        ctx->OMSetDepthStencilState((ID3D11DepthStencilState*)_defaultDepth.Handle, 0);
    }

    private readonly Dictionary<ShaderProgram, D3D11LineBatch> _worldLineBatches = [];

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes, MeshCpuAccess cpuAccess = MeshCpuAccess.Retained)
    {
        MeshesCreated++;
        var litShader = (D3D11ShaderProgram?)DefaultShader
            ?? throw new InvalidOperationException("Default shader must be created before meshes.");
        var mesh = D3D11Mesh.Create(_device, vertices, indices, attributes, litShader.VertexBytecode, cpuAccess);
        mesh.Unregister = () => _meshes.Remove(mesh);
        _meshes.Add(mesh);
        return mesh;
    }

    /// <inheritdoc/>
    public override InstanceBuffer CreateInstanceBuffer(
        int capacityInstances, ReadOnlySpan<VertexAttribute> attributes, ShaderProgram program)
    {
        int floats = ValidateInstanceLayout(capacityInstances, attributes);

        // The layout is validated against THIS program's signature and is only
        // usable under it. Building it against the default shader instead
        // compiles, creates and then fails every draw; see
        // Renderer.CreateInstanceBuffer for the message it produces.
        if (program is not D3D11ShaderProgram d3dProgram)
            throw new ArgumentException("Shader program belongs to another backend.", nameof(program));

        return new D3D11InstanceBuffer(
            _device, capacityInstances,
            VertexAttribute.StandardLayout, attributes, floats, d3dProgram.VertexBytecode);
    }

    protected override Texture CreateTextureCore(in TextureUploadDesc desc)
    {
        var texture = D3D11Texture.Create(_device, in desc);
        texture.Unregister = () => _textures.Remove(texture);
        _textures.Add(texture);
        return texture;
    }

    public override ShaderProgram CreateShader(string vertexSource, string fragmentSource)
    {
        var shader = D3D11ShaderProgram.Create(_d3dCompiler, _device, _context, _bindCache, vertexSource, fragmentSource);
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

        foreach (D3D11LineBatch batch in _worldLineBatches.Values)
            batch.Dispose();

        _worldLineBatches.Clear();
        _lineBatch = null;
        _debugShader = null;

        foreach (var mesh in _meshes) mesh.Dispose();
        _meshes.Clear();

        // Before the target loop below, because releasing a retired generation
        // calls DestroyRenderTarget, which mutates the very list that loop walks.
        // Regardless of acknowledgement: the device is going with them, so there
        // is nothing left for a consumer to hold on to. Nulled so the second
        // Shutdown Engine's crash handler makes is a no-op.
        _retirement?.ReleaseAll();
        _retirement = null;
        _presentTarget = null;
        _presentGeneration = 0;
        _sharedWriteHeld = false;

        ReleaseFrameResources();

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
        ComOwnership.Release(ref _solidRasterizer);
        ComOwnership.Release(ref _biasedRasterizer);
        ComOwnership.Release(ref _defaultDepth);
        ComOwnership.Release(ref _overlayDepth);
        ComOwnership.Release(ref _worldLineDepth);
        ComOwnership.Release(ref _alphaBlend);
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

    /// <summary>
    /// Brings up the device, the immediate context and the debug-layer queues.
    /// Knows nothing about presentation.
    /// </summary>
    /// <remarks>
    /// <b>Split from the swap chain because a composited surface has no swap
    /// chain at all</b>, and because the shape of that split is already settled
    /// one file over: D3D12 has had <c>CreateDevice</c> and
    /// <c>CreateQueueAndSwapChain</c> apart since it was written. A surface the
    /// engine does not present to still needs every one of these.
    /// </remarks>
    private void CreateDevice(IRenderSurface surface)
    {
        bool composited = surface.Kind == RenderSurfaceKind.Composited;
        if (!composited && (surface.Kind != RenderSurfaceKind.Win32 || surface.NativeHandle == 0))
        {
            throw new InvalidOperationException(
                $"The D3D11 backend needs a Win32 surface with an HWND, or a composited surface it does not " +
                $"present to; this one is {surface.Kind}. On another platform, or for a surface that offers only " +
                "a GL context, use the OpenGL backend.");
        }

        D3DFeatureLevel featureLevel = default;
        D3DFeatureLevel[] requested = [D3DFeatureLevel.Level110];

        // Try with the debug layer first (requires the Windows Graphics Tools
        // optional feature). If it fails (E_FAIL when SDK debug layer missing),
        // fall back to release-mode creation so we don't lock out dev machines.
        const uint baseFlags = (uint)CreateDeviceFlag.BgraSupport;
        const uint debugFlags = baseFlags | (uint)CreateDeviceFlag.Debug;

        // Null means the system default. With an explicit adapter the driver
        // type must be Unknown: D3D11 refuses Hardware plus an adapter.
        ComPtr<IDXGIAdapter> chosenAdapter = DxgiAdapters.Find(_dxgi, PreferredAdapter, _logger, out string adapterName);
        AdapterName = adapterName;
        D3DDriverType driverType = chosenAdapter.Handle is null ? D3DDriverType.Hardware : D3DDriverType.Unknown;

        fixed (D3DFeatureLevel* featureLevels = requested)
        {
            // Only when asked for: validation is not free, and it was previously
            // always attempted. See Renderer.EnableDebugLayer.
            int hr = EnableDebugLayer ? _d3d11.CreateDevice(
                chosenAdapter,
                driverType,
                Software: 0,
                Flags: debugFlags,
                pFeatureLevels: featureLevels,
                FeatureLevels: (uint)requested.Length,
                SDKVersion: D3D11Api.SdkVersion,
                ppDevice: ref _device,
                pFeatureLevel: ref featureLevel,
                ppImmediateContext: ref _context) : -1;

            if (hr < 0)
            {
                if (EnableDebugLayer)
                    _logger.LogInformation("D3D11 debug layer unavailable (hr=0x{Hr:X}); creating without it.", hr);
                else
                    _logger.LogInformation("D3D11 debug layer off (not requested).");

                SilkMarshal.ThrowHResult(_d3d11.CreateDevice(
                    chosenAdapter,
                    driverType,
                    Software: 0,
                    Flags: baseFlags,
                    pFeatureLevels: featureLevels,
                    FeatureLevels: (uint)requested.Length,
                    SDKVersion: D3D11Api.SdkVersion,
                    ppDevice: ref _device,
                    pFeatureLevel: ref featureLevel,
                    ppImmediateContext: ref _context));

                EnsureDeviceCreated();
            }
            else
            {
                EnsureDeviceCreated();
                DebugLayerActive = true;
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
    }

    /// <summary>
    /// Refuses a device that was reported created and is not there.
    /// </summary>
    /// <remarks>
    /// <b>D3D11CreateDevice can return a non-negative HRESULT and still hand back
    /// a null device.</b> It happens under driver resource pressure, and in this
    /// tree it happened whenever a second device or a GL context was coming up in
    /// another process at the same moment. Unchecked, the very next line asks that
    /// null for an interface, so the symptom is a NullReferenceException raised
    /// from inside QueryInterface with nothing anywhere naming the actual cause:
    /// it reads as a bug in the renderer rather than as the environment refusing a
    /// device. SilkMarshal.ThrowHResult cannot catch it, because by the driver's
    /// own account nothing failed.
    /// </remarks>
    private void EnsureDeviceCreated()
    {
        if (_device.Handle is not null) return;

        throw new GraphicsDeviceLostException(
            "D3D11CreateDevice reported success and returned no device. That is the driver " +
            "declining to create one, usually under resource pressure from another device or " +
            "context coming up at the same moment, and it is not a state this renderer can " +
            "continue from.");
    }

    /// <summary>Creates the swap chain for a window surface, and takes Alt+Enter off DXGI.</summary>
    private void CreateSwapChain(nint hwnd, int width, int height)
    {
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

        // World-line state: DepthMode.TestNoWriteEqual. LessEqual because a
        // grid on a floor is coplanar by construction; write OFF because these
        // lines are alpha-blended now, and a translucent pixel has no business
        // in the depth buffer - a plane of one-pixel lines that wrote depth
        // would slice whatever the pipeline submits after it.
        var worldLineDesc = new DepthStencilDesc
        {
            DepthEnable = 1,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunc.LessEqual,
            StencilEnable = 0,
        };
        ID3D11DepthStencilState* worldLine = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateDepthStencilState(&worldLineDesc, &worldLine));
        _worldLineDepth = ComOwnership.Own(worldLine);

        // Straight alpha over the lit target, the world-line lane's blend.
        // One cached state swapped around the flush, the exact idiom the depth
        // states above use.
        var blendDesc = new BlendDesc();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.SrcAlpha,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.InvSrcAlpha,
            BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ID3D11BlendState* alphaBlend = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateBlendState(&blendDesc, &alphaBlend));
        _alphaBlend = ComOwnership.Own(alphaBlend);
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
        _bindCache.Reset();

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

    /// <summary>
    /// Swaps in a rasterizer state carrying <paramref name="bias"/>. D3D11's
    /// two fields are the same two quantities <see cref="DepthBias"/> carries.
    /// </summary>
    protected override void ApplyDepthBias(DepthBias bias)
    {
        var context = (ID3D11DeviceContext*)_context.Handle;
        if (context is null) return;

        if (bias.IsZero)
        {
            context->RSSetState((ID3D11RasterizerState*)_solidRasterizer.Handle);
            return;
        }

        if (_biasedRasterizer.Handle is null || _biasedRasterizerFor != bias)
        {
            ComOwnership.Release(ref _biasedRasterizer);
            var desc = new RasterizerDesc
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Back,
                FrontCounterClockwise = 1,
                DepthBias = bias.Constant,
                // Unclamped on purpose: the clamp exists to stop a near-edge-on
                // triangle asking for an unbounded offset, and a shadow caster
                // seen edge-on from the light contributes nothing to shade
                // anyway, so the offset is harmless where the clamp would bite.
                DepthBiasClamp = 0f,
                SlopeScaledDepthBias = bias.SlopeScaled,
                DepthClipEnable = 1,
                ScissorEnable = 0,
                MultisampleEnable = 0,
                AntialiasedLineEnable = 0,
            };
            ID3D11RasterizerState* state = null;
            SilkMarshal.ThrowHResult(
                ((ID3D11Device*)_device.Handle)->CreateRasterizerState(&desc, &state));
            _biasedRasterizer = ComOwnership.Own(state);
            _biasedRasterizerFor = bias;
        }

        context->RSSetState((ID3D11RasterizerState*)_biasedRasterizer.Handle);
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
