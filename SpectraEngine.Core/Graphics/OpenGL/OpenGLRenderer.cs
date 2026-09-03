using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace SpectraEngine.Core.Graphics.OpenGL;

public class OpenGLRenderer : Renderer
{
    private GL? _gl;
    private IRenderSurface? _surface;

    // Every resource built by the Create* factories is tracked here so
    // Shutdown can free stragglers. Meshes/textures leave early through
    // Renderer.DestroyMesh/DestroyTexture via the Unregister callback handed
    // out at creation. Unsynchronized: creation and destruction both happen
    // on the render thread.
    private readonly List<Mesh> _meshes = [];
    private readonly List<ShaderProgram> _shaders = [];
    private readonly List<Texture> _textures = [];
    private readonly List<IOpenGLRenderPipeline> _pipelines = [];
    private readonly List<RenderTarget> _renderTargets = [];
    private int _pipelineIndex;
    private OpenGLLineBatch? _lineBatch;
    private ShaderProgram? _debugShader;
    private OpenGLSrgbTarget? _srgbTarget;

    public override GraphicsBackend Backend => GraphicsBackend.OpenGL;

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

    public OpenGLRenderer(ILogger<Renderer> logger, IShaderCompiler shaderCompiler)
        : base(logger, shaderCompiler)
    {
    }

    public override void Initialize(IRenderSurface surface)
    {
        _surface = surface;
        _gl = GL.GetApi(surface.GLContext
            ?? throw new InvalidOperationException(
                "The OpenGL backend needs a surface carrying a GL context; this one has none. " +
                "A window created with GraphicsAPI.None, or an embedded surface offering only a " +
                "native handle, can only drive a D3D backend."));

        // No framebuffer-size handling here: the engine seeds the base-class
        // latch on the main thread before this thread starts and feeds it from
        // the window's resize event. Querying window.FramebufferSize on this
        // (render) thread would race the main thread's glfwPollEvents.

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);

        EnableFramebufferSrgb(_gl);

        // Prefer source-on-disk so the dev build hot-reloads on save.
        // Deployed builds (where the source tree isn't present) silently fall
        // back to the embedded resource and keep the shader frozen.
        DefaultShader = CreateBaseShader(BaseShaders.LitFileName);
        _debugShader = CreateBaseShader(BaseShaders.DebugLineFileName);
        _lineBatch = new OpenGLLineBatch(_gl);

        // Deferred FIRST, so it is the default: it is the only path with a
        // real BRDF and the only one with shadows. Forward stays in the
        // rotation for what deferred structurally cannot do.
        RegisterPipeline(new DeferredPipeline());
        RegisterPipeline(new ForwardPipeline());
        RegisterPipeline(new WireframePipeline());

        _logger.LogInformation("Renderer initialized (OpenGL, pipeline={Pipeline})", CurrentPipelineName);
    }

    /// <summary>
    /// Whether what reaches the display is sRGB-encoded, by either route.
    /// </summary>
    /// <remarks>
    /// This is the property worth asserting on, because it is a statement about
    /// the picture rather than about the mechanism. See
    /// <see cref="UsesSrgbTarget"/> for which route is in use.
    /// </remarks>
    public bool FramebufferSrgb { get; private set; }

    /// <summary>
    /// True when encoding comes from an offscreen sRGB buffer because the
    /// window's own framebuffer could not do it. See <see cref="OpenGLSrgbTarget"/>.
    /// </summary>
    public bool UsesSrgbTarget => _srgbTarget is not null;

    // GL's half of R2. The D3D backends get display encoding by naming an sRGB
    // format on the back-buffer view; GL needs a state enable AND a framebuffer
    // that can honour it, and the second half is not something to assume: the
    // enable is silently a no-op on a framebuffer that was not created
    // sRGB-capable. So enable, then ask the driver what the window's colour
    // encoding actually is, and stand up the offscreen fallback if the answer
    // is no.
    private void EnableFramebufferSrgb(GL gl)
    {
        // Stays enabled for the rest of the run either way. On a linear default
        // framebuffer it does nothing; on the fallback target it is what makes
        // the encode happen. Only the blit in Present turns it off, briefly.
        gl.Enable(EnableCap.FramebufferSrgb);

        if (QueryDefaultFramebufferSrgb(gl))
        {
            FramebufferSrgb = true;
            _logger.LogInformation("Framebuffer sRGB encoding enabled (window framebuffer)");
            return;
        }

        // Not a driver bug and not worth a warning: Silk.NET 2.23 has no way to
        // ask GLFW for an sRGB-capable window, so this is the ordinary case
        // rather than the exceptional one.
        _srgbTarget = new OpenGLSrgbTarget();
        FramebufferSrgb = true;
        _logger.LogInformation(
            "Framebuffer sRGB encoding enabled (offscreen target; the window framebuffer is linear)");
    }

    // Called when the fallback itself turns out to be unavailable, which leaves
    // the run uncorrected. That IS worth a warning, because it is the only state
    // in which this backend disagrees with the other two.
    private void AbandonSrgbTarget()
    {
        _srgbTarget = null;
        FramebufferSrgb = false;
        _logger.LogWarning(
            "The offscreen sRGB target could not be created; colour output will be uncorrected " +
            "and will not match the D3D backends");
    }

    // GL_FRAMEBUFFER_ATTACHMENT_COLOR_ENCODING on the default framebuffer's
    // GL_BACK_LEFT attachment: GL_SRGB (0x8C40) if writes are encoded,
    // GL_LINEAR (0x2601) if the enable is being ignored.
    private static unsafe bool QueryDefaultFramebufferSrgb(GL gl)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Clear any error the query would otherwise inherit, so a driver that
        // rejects the enum below is distinguishable from one that answers it.
        while (gl.GetError() != GLEnum.NoError) { }

        int encoding = 0;
        gl.GetFramebufferAttachmentParameter(
            GLEnum.Framebuffer,
            GLEnum.BackLeft,
            GLEnum.FramebufferAttachmentColorEncoding,
            &encoding);

        return gl.GetError() == GLEnum.NoError && encoding == (int)GLEnum.Srgb;
    }

    /// <summary>Adds <paramref name="pipeline"/> to the rotation; the first registered pipeline is the default.</summary>
    public void RegisterPipeline(IOpenGLRenderPipeline pipeline)
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

    /// <summary>
    /// Makes the context current on the render thread, and re-applies the swap
    /// interval there.
    /// </summary>
    /// <remarks>
    /// <b>The interval is set again HERE, not only where the window is
    /// created.</b> <c>glfwSwapInterval</c> acts on the context current on the
    /// CALLING thread, and this engine hands the context to a dedicated render
    /// thread after creation; an interval set on the main thread can therefore
    /// be applied to no context at all. The symptom is a frame time pinned to
    /// exactly the refresh interval with almost no work in it, which reads as a
    /// slow renderer rather than as a wait.
    /// </remarks>
    public override void AcquireContext(IRenderSurface surface)
    {
        base.AcquireContext(surface);
        _appliedSwapInterval = VSync ? 1 : 0;
        surface.GLContext?.SwapInterval(_appliedSwapInterval);
    }

    // GL's swap interval is CONTEXT state, not a per-present argument like the
    // DXGI sync interval, so a live VSync flip has to be re-applied — done at
    // Present, on the render thread, where the context is current.
    private int _appliedSwapInterval;

    /// <inheritdoc/>
    public override void Present(IRenderSurface surface)
    {
        int wanted = VSync ? 1 : 0;
        if (wanted != _appliedSwapInterval)
        {
            _appliedSwapInterval = wanted;
            surface.GLContext?.SwapInterval(wanted);
        }

        base.Present(surface);
    }

    /// <inheritdoc/>
    /// <remarks>True: glUniform writes into whichever program is active.</remarks>
    protected override bool BindsProgramBeforeUniforms => true;

    /// <inheritdoc/>
    /// <remarks>False: an OpenGL framebuffer's origin is bottom-left.</remarks>
    public override bool TargetOriginIsTopLeft => false;

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
        // Apply any shader source-file changes that came in since the last
        // frame. We're on the render thread here, so GL calls are safe.
        HotReloader.PumpPendingReloads();

        // Once per FRAME, and deliberately not once per pipeline execution:
        // a frame with ProbeTarget set runs the pipeline twice into one
        // command list. See Renderer.BeginFrameInstanceBuffers.
        BeginFrameInstanceBuffers();

        if (_pipelines.Count == 0 || _gl is null || _surface is null)
            return;

        var context = new OpenGLRenderContext
        {
            Renderer = this,
            Gl = _gl,
            Scene = scene,
            View = view,
            DeltaTime = deltaTime,
        };

        // The probe pass first, so the window still gets the last word and a
        // probe can never change what the user sees.
        if (ProbeTarget is { } probe)
        {
            FrameTarget = probe;
            _pipelines[_pipelineIndex].Execute(context);
        }

        RenderTarget? sceneTarget = HdrEnabled ? EnsureSceneTarget() : null;
        FrameTarget = sceneTarget;
        _pipelines[_pipelineIndex].Execute(context);

        if (sceneTarget is null)
        {
            // No HDR: the pipeline already drew straight to the window, so the
            // overlay just needs its own pass on top.
            DrawOverlay(scene);
            return;
        }

        ResolveTo(sceneTarget.ColorTexture!, null, scene);
    }

    protected override void DrawFullscreen(PostPass pass, Mesh geometry)
    {
        GL gl = _gl!;

        // Both are ambient state somebody else set: WireframePipeline leaves
        // polygon mode on Line, and depth testing is on for the scene.
        gl.Disable(EnableCap.DepthTest);
        gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        // Use FIRST on this backend: glUniform writes into the active program,
        // so staging before it would write into whatever was bound last.
        pass.Shader.Use();
        pass.ApplyTo(pass.Shader);
        geometry.Draw();

        gl.Enable(EnableCap.DepthTest);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The picture-space contract is free on this backend.</b> A GL
    /// framebuffer's origin is bottom-left, so the row a clip y = -1 vertex
    /// rasterises to is row 0, and that is exactly the row
    /// <c>glReadPixels(x, 0, ...)</c> returns - no flip anywhere. The two D3D
    /// backends have to convert.
    /// </remarks>
    internal override unsafe (byte R, byte G, byte B, byte A) ReadTargetPixel(
        RenderTarget target, int x, int y)
    {
        if (target.ColorTexture is not OpenGLTexture color)
            throw new ArgumentException("The target has no colour attachment to read.", nameof(target));

        GL gl = _gl!;
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        gl.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, color.Handle, 0);

        var pixel = new byte[4];
        fixed (byte* p = pixel)
            gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.DeleteFramebuffer(fbo);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The whole region in one call, and the row order is free here too.</b>
    /// A GL framebuffer's origin is bottom-left, so <c>glReadPixels</c> already
    /// returns rows bottom-first, which is the contract. Rows are tightly packed
    /// because RGBA8 rows are a multiple of four bytes wide at any width, which
    /// is what GL's default pack alignment of 4 asks for - a three-channel read
    /// would need the alignment changed and is why this one names its format.
    /// </remarks>
    internal override unsafe void ReadTargetPixels(
        RenderTarget target, int x, int y, int width, int height, Span<byte> destination)
    {
        PixelReadback.ValidateRegion(target, x, y, width, height, destination);
        if (target.ColorTexture is not OpenGLTexture color)
            throw new ArgumentException("The target has no colour attachment to read.", nameof(target));

        GL gl = _gl!;
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        gl.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, color.Handle, 0);

        fixed (byte* p = destination)
        {
            gl.ReadPixels(
                x, y, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.DeleteFramebuffer(fbo);
    }

    // The sRGB fallback is what "the back buffer" means on this backend when
    // the window's own framebuffer is linear, so it belongs here rather than
    // around the pipeline: a pipeline asks for the back buffer and gets
    // whichever framebuffer is actually carrying the frame.
    private bool _passUsedSrgbTarget;

    protected override void SetViewportCore(int x, int y, int width, int height) =>
        _gl!.Viewport(x, y, (uint)width, (uint)height);

    /// <summary>
    /// glPolygonOffset, whose two arguments are the same two quantities
    /// <see cref="DepthBias"/> carries, in the same order of meaning.
    /// </summary>
    /// <remarks>
    /// Disabled rather than set to zero when there is no bias: the enable is
    /// separate state on this backend, and leaving it on with zeroes still
    /// costs the rasterizer the offset path on some drivers.
    /// </remarks>
    protected override void ApplyDepthBias(DepthBias bias)
    {
        GL gl = _gl!;
        if (bias.IsZero)
        {
            gl.Disable(EnableCap.PolygonOffsetFill);
            return;
        }

        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(bias.SlopeScaled, bias.Constant);
    }

    protected override void BeginPassCore(
        RenderTarget? target, ReadOnlySpan<RenderTarget> targets, in PassClear clear)
    {
        GL gl = _gl!;
        Vector2D<int> size = PassSize;

        if (target is OpenGLRenderTarget offscreen)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, offscreen.Framebuffer);
            // The first target's framebuffer owns the depth attachment; the rest
            // are attached to it as extra colour buffers for the duration of
            // this pass.
            offscreen.BindExtraColorTargets(gl, targets);
            _passUsedSrgbTarget = false;
        }
        else
        {
            _passUsedSrgbTarget = _srgbTarget is not null && _srgbTarget.Begin(gl, size.X, size.Y);
            if (_srgbTarget is { Usable: false })
                AbandonSrgbTarget();
            if (!_passUsedSrgbTarget)
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        SetViewportCore(0, 0, size.X, size.Y);

        uint mask = 0;
        if (clear.Color is { } color)
        {
            gl.ClearColor(color.X, color.Y, color.Z, color.W);
            mask |= (uint)ClearBufferMask.ColorBufferBit;
        }
        if (clear.Depth is { } depth)
        {
            gl.ClearDepth(depth);
            mask |= (uint)(ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }
        if (mask != 0)
            gl.Clear(mask);
    }

    protected override void EndPassCore(RenderTarget? target, ReadOnlySpan<RenderTarget> targets)
    {
        if (target is OpenGLRenderTarget offscreen && targets.Length > 1)
            offscreen.UnbindExtraColorTargets(_gl!, targets);

        if (target is not null)
        {
            // Back to the window, so a pass that forgets to bind cannot silently
            // keep drawing into a texture nobody is looking at.
            _gl!.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return;
        }

        if (!_passUsedSrgbTarget) return;

        _srgbTarget!.Present(_gl!, PassSize.X, PassSize.Y);
        _passUsedSrgbTarget = false;
    }

    public override RenderTarget CreateRenderTarget(in RenderTargetDesc desc)
    {
        var target = new OpenGLRenderTarget(_gl!, desc);
        target.Unregister = () => _renderTargets.Remove(target);
        _renderTargets.Add(target);
        return target;
    }

    /// <summary>
    /// Uploads and draws the accumulated <see cref="Renderer.DebugDraw"/> lines
    /// with depth-test off. Called by pipelines after their main scene pass.
    /// </summary>
    protected override void FlushDebugDrawCore(Scene.Camera camera)
    {
        if (DebugDraw.VertexCount == 0 || _debugShader is null || _lineBatch is null || _gl is null)
            return;

        // Always-on-top: depth test off so debug lines don't fight the geometry
        // they describe. Restored afterwards so the next frame's main pass is depth-correct.
        _gl.Disable(EnableCap.DepthTest);

        _debugShader.Use();
        _debugShader.SetUniform("uView", camera.View);
        _debugShader.SetUniform("uProjection", camera.Projection);
        _lineBatch.Draw(DebugDraw.Vertices, (uint)DebugDraw.VertexCount);

        _gl.Enable(EnableCap.DepthTest);
    }

    /// <inheritdoc/>
    protected override void FlushWorldLinesCore(
        Scene.Camera camera, ShaderProgram program, float nudge, GBuffer? gbuffer)
    {
        if (_lineBatch is null || _gl is null)
            return;

        if (gbuffer is null)
        {
            // Forward/wireframe: the open pass owns the scene's live depth, so
            // the hardware tests. LEqual specifically: GL defaults to Less,
            // which rejects a grid lying exactly on the floor it describes -
            // the case the grid exists for - so leaving the default here would
            // have made the OpenGL backend disagree with the other two about
            // whether the grid is visible at all. Write OFF: a translucent
            // pixel has no business in the depth buffer, and a plane of
            // one-pixel lines that wrote depth would slice later geometry.
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(false);
        }
        else
        {
            // Deferred: the shader compares against the sampled G-buffer
            // depth, and the pass target's own depth attachment holds stale
            // data that must not participate.
            _gl.Disable(EnableCap.DepthTest);
        }

        // SEPARATE, to match the D3D backends' SrcBlendAlpha = One: the plain
        // BlendFunc applies SrcAlpha to the alpha channel too, and the stored
        // alpha would then be a*a + dst*(1-a) here against a + dst*(1-a) on
        // D3D — the one blend fact differing across three backends in a lane
        // whose premise is identical behaviour. Nothing reads FrameTarget's
        // alpha today (the resolve writes 1 and samples rgb), which is exactly
        // why the divergence would surface silently the day something does —
        // a composited viewport handing the texture to a compositor, say.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFuncSeparate(
            BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        // Use FIRST on this backend: glUniform writes into the active program,
        // so staging before it would write into whatever was bound last.
        program.Use();
        program.SetUniform("uView", camera.View);
        program.SetUniform("uProjection", camera.Projection);
        program.SetUniform("uCameraPosition", camera.Position);
        program.SetUniform("uDepthNudge", nudge);
        program.SetUniform("uFadeCenter", WorldLines.FadeCenter);
        program.SetUniform("uFadeStart", WorldLines.FadeStart);
        program.SetUniform("uFadeEnd", WorldLines.FadeEnd);
        program.SetUniform("uOpacity", WorldLines.Opacity);

        if (gbuffer is not null)
        {
            program.SetUniform("uNdcToUv", NdcToUv);
            program.SetUniform("uDepthToNdc", DepthToNdcZ);
            program.SetUniform("uGBufferSize", new Vector2(gbuffer.Width, gbuffer.Height));
            program.SetTexture("uDepth", 0, gbuffer.Depth);
        }

        _lineBatch.Draw(WorldLines.Vertices, (uint)WorldLines.VertexCount);

        // Restored, not left: all of it is context state and the next pass in
        // this frame is ordinary opaque geometry.
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
    }

    // The two halves of the frame wrapper in Render, reachable from the test
    // assembly so a test can draw one known colour through the real path and
    // read the window back. Internal rather than public: nothing in a game
    // drives these, and the seam exists because "the enable silently did
    // nothing" is not observable any other way.
    internal bool BeginSrgbTargetForTest(int width, int height)
        => _gl is not null && _srgbTarget is not null && _srgbTarget.Begin(_gl, width, height);

    internal void PresentSrgbTargetForTest(int width, int height)
    {
        if (_gl is not null)
            _srgbTarget?.Present(_gl, width, height);
    }

    public override void Shutdown()
    {
        foreach (var pipeline in _pipelines)
            pipeline.Dispose();
        _pipelines.Clear();

        _lineBatch?.Dispose();
        _lineBatch = null;
        _debugShader = null;

        if (_gl is not null) _srgbTarget?.Dispose(_gl);
        _srgbTarget = null;

        // Before the mesh/texture sweeps below: the triangle and the HDR target
        // are tracked in those lists and this only drops this renderer's
        // references to them.
        ReleaseFrameResources();

        foreach (var mesh in _meshes)
            mesh.Dispose();
        _meshes.Clear();

        // Before the textures: a target owns its colour attachment, and the
        // attachment is not in _textures, so the order only matters for
        // readability. Targets go first because they hold framebuffers that
        // name those textures.
        foreach (var target in _renderTargets)
            target.Dispose();
        _renderTargets.Clear();

        foreach (var texture in _textures)
            texture.Dispose();
        _textures.Clear();

        foreach (var shader in _shaders)
            shader.Dispose();
        _shaders.Clear();
        DefaultShader = null;

        _gl?.Dispose();
        _gl = null;

        base.Shutdown();
        _logger.LogInformation("Renderer shut down (OpenGL)");
    }

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes, MeshCpuAccess cpuAccess = MeshCpuAccess.Retained)
    {
        MeshesCreated++;
        var mesh = OpenGLMesh.Create(_gl!, vertices, indices, attributes, cpuAccess);
        mesh.Unregister = () => _meshes.Remove(mesh);
        _meshes.Add(mesh);
        return mesh;
    }

    /// <inheritdoc/>
    public override InstanceBuffer CreateInstanceBuffer(
        int capacityInstances, ReadOnlySpan<VertexAttribute> attributes, ShaderProgram program)
    {
        // program is unused: GL binds attributes into the vertex array, so a
        // buffer is not tied to a shader signature the way a D3D11 layout is.
        int floats = ValidateInstanceLayout(capacityInstances, attributes);
        return new OpenGLInstanceBuffer(_gl!, capacityInstances, attributes, floats);
    }

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels, int width, int height,
        TextureFormat format, TextureColorSpace colorSpace, TextureFilter filter, TextureWrap wrap)
    {
        var texture = OpenGLTexture.Create(_gl!, pixels, width, height, format, colorSpace, filter, wrap);
        texture.Unregister = () => _textures.Remove(texture);
        _textures.Add(texture);
        return texture;
    }

    public override ShaderProgram CreateShader(string vertexSource, string fragmentSource)
    {
        var shader = OpenGLShaderProgram.Create(_gl!, vertexSource, fragmentSource);
        _shaders.Add(shader);
        return shader;
    }

    public override ShaderProgram CreateShader(PipelineBlob blob)
    {
        if (blob.Backend != GraphicsBackend.OpenGL)
            throw new ArgumentException($"Expected OpenGL blob, got {blob.Backend}");

        if (blob.Format != ShaderDataFormat.SourceText)
            throw new ArgumentException($"OpenGL requires SourceText format, got {blob.Format}");

        string vertexSource = Encoding.UTF8.GetString(blob.VertexData
            ?? throw new InvalidOperationException("Compiled shader has no vertex stage"));
        string fragmentSource = Encoding.UTF8.GetString(blob.FragmentData
            ?? throw new InvalidOperationException("Compiled shader has no fragment stage"));

        var shader = OpenGLShaderProgram.Create(_gl!, vertexSource, fragmentSource);
        _shaders.Add(shader);
        return shader;
    }
}
