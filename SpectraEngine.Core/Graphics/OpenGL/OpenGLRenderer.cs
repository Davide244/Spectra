using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpectraEngine.Core.Graphics.OpenGL;

public class OpenGLRenderer : Renderer
{
    private GL? _gl;
    private IWindow? _window;

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

    public OpenGLRenderer(ILogger<Renderer> logger, IShaderCompiler shaderCompiler)
        : base(logger, shaderCompiler)
    {
    }

    public override void Initialize(IWindow window)
    {
        _window = window;
        _gl = window.CreateOpenGL();

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
        DefaultShader = BaseShaders.LitPath is { } litPath
            ? CreateShaderFromFile(litPath)
            : CreateShaderFromSource(BaseShaders.Lit);
        _debugShader = BaseShaders.DebugLinePath is { } debugPath
            ? CreateShaderFromFile(debugPath)
            : CreateShaderFromSource(BaseShaders.DebugLine);
        _lineBatch = new OpenGLLineBatch(_gl);

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

        if (_pipelines.Count == 0 || _gl is null || _window is null)
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

        ResolveTo(sceneTarget.ColorTexture, null, scene);
    }

    protected override void DrawFullscreen(PostPass pass)
    {
        GL gl = _gl!;
        Mesh triangle = EnsureFullscreenTriangle();

        // Both are ambient state somebody else set: WireframePipeline leaves
        // polygon mode on Line, and depth testing is on for the scene.
        gl.Disable(EnableCap.DepthTest);
        gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        // Use FIRST on this backend: glUniform writes into the active program,
        // so staging before it would write into whatever was bound last.
        pass.Shader.Use();
        pass.ApplyTo(pass.Shader);
        triangle.Draw();

        gl.Enable(EnableCap.DepthTest);
    }

    // The sRGB fallback is what "the back buffer" means on this backend when
    // the window's own framebuffer is linear, so it belongs here rather than
    // around the pipeline: a pipeline asks for the back buffer and gets
    // whichever framebuffer is actually carrying the frame.
    private bool _passUsedSrgbTarget;

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

        gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);

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
        ReleaseResolveResources();

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

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        var mesh = OpenGLMesh.Create(_gl!, vertices, indices, attributes);
        mesh.Unregister = () => _meshes.Remove(mesh);
        _meshes.Add(mesh);
        return mesh;
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
