using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.IO;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

public abstract class Renderer
{
    internal readonly ILogger<Renderer> _logger;
    private readonly IShaderCompiler _shaderCompiler;

    public abstract GraphicsBackend Backend { get; }

    // GLFW answers window-size queries only on the thread that created the
    // window, so the size is latched here: the engine seeds it on the main
    // thread before the render thread starts and refreshes it from the
    // FramebufferResize event (fired during DoEvents, also main thread).
    // Everything render-side reads the latch instead of touching IWindow.
    private readonly object _framebufferSizeLock = new();
    private Vector2D<int> _framebufferSize;

    /// <summary>
    /// The window's framebuffer size as last reported by the main thread.
    /// Render-side code (backends, pipelines) must read this instead of
    /// <see cref="IWindow.FramebufferSize"/>, which GLFW only allows querying
    /// on the thread that owns the window.
    /// </summary>
    public Vector2D<int> FramebufferSize
    {
        get { lock (_framebufferSizeLock) return _framebufferSize; }
    }

    /// <summary>
    /// The same latched framebuffer size as <see cref="FramebufferSize"/>, but
    /// in plain ints. Layers that must not reference Silk.NET — the editing
    /// assembly and any future non-Silk host — read the viewport through this
    /// overload, because merely naming <see cref="Vector2D{T}"/> would drag
    /// Silk.NET.Maths into their assembly references.
    /// </summary>
    public void GetFramebufferSize(out int width, out int height)
    {
        lock (_framebufferSizeLock)
        {
            width = _framebufferSize.X;
            height = _framebufferSize.Y;
        }
    }

    /// <summary>
    /// Publishes a new framebuffer size to the render side. Main thread only —
    /// the engine calls this once before the render thread starts and then
    /// from the window's FramebufferResize event.
    /// </summary>
    internal void SetFramebufferSize(Vector2D<int> size)
    {
        lock (_framebufferSizeLock)
            _framebufferSize = size;
    }

    /// <summary>
    /// The graphics API the host window should be created with. OpenGL needs
    /// <see cref="GraphicsAPI.Default"/> (a real GL context); D3D and Vulkan
    /// backends want <see cref="GraphicsAPI.None"/> and create their own
    /// device against the native window handle.
    /// </summary>
    public virtual GraphicsAPI WindowApi => GraphicsAPI.Default;

    /// <summary>
    /// Makes any thread-affine context (e.g. an OpenGL context) current on the
    /// calling thread. Called once at the start of the render loop. Backends
    /// without thread-current state (D3D, Vulkan) leave this empty.
    /// </summary>
    public virtual void AcquireContext(IRenderSurface surface) => surface.GLContext?.MakeCurrent();

    /// <summary>
    /// Releases the context from the calling thread; mirror of
    /// <see cref="AcquireContext"/>. Called at render-loop shutdown.
    /// </summary>
    public virtual void ReleaseContext(IRenderSurface surface) => surface.GLContext?.Clear();

    /// <summary>
    /// Presents the most recently rendered frame to the window. OpenGL swaps
    /// buffers via the GL context; D3D11/12 call IDXGISwapChain::Present.
    /// </summary>
    public virtual void Present(IRenderSurface surface) => surface.GLContext?.SwapBuffers();

    /// <summary>
    /// A backend-provided shader suitable for general lit geometry. Available
    /// after <see cref="Initialize"/> has run.
    /// </summary>
    public ShaderProgram? DefaultShader { get; protected set; }

    /// <summary>
    /// Whether to ask the graphics API for its validation layer when the device
    /// is created. Set before <see cref="Initialize"/>; ignored afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Defaults to on in a Debug build and off in Release, and both halves
    /// matter.</b> The engine's own error gate reads
    /// <see cref="DebugLayerErrorCount"/>, and on D3D that number only exists
    /// because the validation layer produces it: turning it off silently would
    /// make <c>--offscreen-probe</c> pass by having nothing to report. But
    /// validation is not free and it is not subtle. D3D12's layer validates
    /// every command-list call, and it measured roughly five times the cost on
    /// the static-world swap alone.
    /// </para>
    /// <para>
    /// It was previously always on, with a fallback only for machines that had
    /// no Graphics Tools installed. That means every measurement anyone took on
    /// a developer machine included validation, and a shipped build would have
    /// carried it too.
    /// </para>
    /// </remarks>
    public bool EnableDebugLayer { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// Substring of the graphics adapter to run on, or null for the system
    /// default. Set before <see cref="Initialize"/>.
    /// </summary>
    /// <remarks>
    /// <b>Not a preference, a measurement tool.</b> A machine with a discrete
    /// card and an integrated one is the cheapest integrated-GPU test rig there
    /// is, and "how does this run on weak hardware" is otherwise a question
    /// nobody can answer without buying the weak hardware. Matched
    /// case-insensitively against the adapter description, so "Intel" or "UHD"
    /// picks the integrated part on most machines.
    /// </remarks>
    public string? PreferredAdapter { get; set; }

    /// <summary>Description of the adapter actually in use, once a device exists.</summary>
    public string AdapterName { get; protected set; } = "unknown";

    /// <summary>Whether the validation layer is actually running. False when it was not asked for or not available.</summary>
    public bool DebugLayerActive { get; protected set; }

    /// <summary>
    /// Per-frame primitive accumulator for debug visualisations. Callers push
    /// lines/boxes/arrows before <see cref="Render"/>; the renderer uploads and
    /// draws them after the main scene pass. The engine clears it each frame.
    /// </summary>
    public DebugDraw DebugDraw { get; } = new();

    /// <summary>
    /// Where each frame's time goes, phase by phase. Off unless asked for: the
    /// scopes cost a branch each, but a profile nobody reads is still work.
    /// </summary>
    public Diagnostics.FrameProfiler Profiler { get; } = new();

    /// <summary>GPU meshes created over this renderer's life. Diagnostics.</summary>
    /// <remarks>
    /// Counted because mesh creation turned out to be the single most expensive
    /// thing the engine did per frame on D3D12, and a rate is the only way to
    /// see that from a log: a static scene should create almost none.
    /// </remarks>
    public long MeshesCreated { get; private protected set; }

    /// <summary>GPU meshes destroyed over this renderer's life.</summary>
    public long MeshesDestroyed { get; private protected set; }

    /// <summary>
    /// Buffers a backend is holding for reuse rather than releasing. Zero on
    /// backends that do not pool.
    /// </summary>
    /// <remarks>
    /// Reported so a pool that grows without bound is visible as a number rather
    /// than as memory: it converges on the scene's high-water mark and is never
    /// trimmed.
    /// </remarks>
    public virtual int PooledBufferCount => 0;

    /// <summary>
    /// Hot-reloads shaders created via <see cref="CreateShaderFromFile"/> when
    /// the source file changes on disk. The render loop must call
    /// <see cref="ShaderHotReloader.PumpPendingReloads"/> once per frame.
    /// </summary>
    public ShaderHotReloader HotReloader { get; }

    protected Renderer(ILogger<Renderer> logger, IShaderCompiler shaderCompiler)
    {
        _logger = logger;
        _shaderCompiler = shaderCompiler;
        HotReloader = new ShaderHotReloader(logger, shaderCompiler, Backend);
    }

    /// <summary>
    /// Compiles SpectraShade source for this renderer's backend and creates a
    /// shader program from the result.
    /// </summary>
    public ShaderProgram CreateShaderFromSource(string spectraShadeSource)
    {
        ReadOnlySpan<GraphicsBackend> targets = [Backend];
        CompiledShaderFile compiled = _shaderCompiler.Compile(spectraShadeSource, targets);
        PipelineBlob blob = compiled.GetPipeline(Backend)
            ?? throw new InvalidOperationException(
                $"SpectraShade compilation produced no pipeline for {Backend}.");
        return CreateShader(blob);
    }

    /// <summary>
    /// Compiles a SpectraShade source file and registers the resulting program
    /// for hot-reload — saving the file recompiles and swaps it in without
    /// invalidating materials that reference it.
    /// </summary>
    public ShaderProgram CreateShaderFromFile(string absolutePath)
    {
        string source = File.ReadAllText(absolutePath);
        ShaderProgram program = CreateShaderFromSource(source);
        HotReloader.Register(absolutePath, program);
        return program;
    }

    public virtual void Initialize(IRenderSurface surface)
    {
        _logger.LogInformation("Renderer initialized");
    }

    // ---- render passes ---------------------------------------------------

    private Vector2D<int> _passSize;
    private bool _inPass;
    private int _currentTargetCount;
    private RenderTarget? _currentTarget;
    private RenderTarget[] _currentTargets = [];

    /// <summary>Maximum colour attachments a single pass may bind.</summary>
    /// <remarks>
    /// Eight, which is what both D3D backends allow and the minimum OpenGL
    /// guarantees, so this is the hardware's number rather than one of ours. A
    /// deferred G-buffer carrying albedo, normals, a material set, emissive and
    /// a per-shading-model custom channel already wants five, and a cap sitting
    /// exactly on the current layout is not a cap, it is a tripwire.
    /// </remarks>
    public const int MaxColorTargets = 8;

    /// <summary>
    /// The size of the target the current pass is drawing into.
    /// </summary>
    /// <remarks>
    /// <b>Pipelines must read this and not <see cref="FramebufferSize"/>.</b>
    /// They are the same number only while every pass goes to the window, which
    /// is the arrangement this seam exists to end. Anything computing a
    /// viewport or an aspect ratio from the window size renders a stretched
    /// picture the first time it draws somewhere else, and that failure is
    /// invisible until an offscreen target exists to trip over it.
    /// </remarks>
    public Vector2D<int> PassSize => _passSize;

    /// <summary>
    /// Aspect ratio of the current pass's target, or null when it has no
    /// height (a minimised window, mid-resize) and the ratio is undefined.
    /// </summary>
    public float? PassAspectRatio => _passSize.Y > 0 ? _passSize.X / (float)_passSize.Y : null;

    /// <summary>
    /// Points rendering at the window's back buffer, sets the viewport to it,
    /// and applies <paramref name="clear"/>. Must be matched by
    /// <see cref="EndPass"/>. Render thread only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of what a pipeline needs to know about where its
    /// output goes. Before it existed, all six pipelines reached into a
    /// backend-specific context for a render-target view, built their own
    /// viewport out of the window size, and issued their own clear, which meant
    /// six copies of the same decision and six places to change for every new
    /// kind of target.
    /// </para>
    /// <para>
    /// Passes do not nest. A mismatched pair throws rather than leaving the
    /// wrong target bound for the rest of the frame, because a silently wrong
    /// binding is a corrupt picture on one backend and a debug-layer message on
    /// another.
    /// </para>
    /// </remarks>
    public void BeginPass(in PassClear clear) => BeginPass((RenderTarget?)null, clear);

    /// <summary>
    /// Points rendering at <paramref name="target"/> (or the back buffer when it
    /// is null), sets the viewport to it, and applies <paramref name="clear"/>.
    /// Must be matched by <see cref="EndPass"/>. Render thread only.
    /// </summary>
    public void BeginPass(RenderTarget? target, in PassClear clear)
    {
        BeginPassChecked(target, [], clear);
    }

    /// <summary>
    /// Opens a pass writing to several colour attachments at once, which is what
    /// a deferred geometry pass needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate targets rather than one target with several attachments.</b>
    /// A <see cref="RenderTarget"/> stays one surface with one
    /// <see cref="RenderTarget.ColorTexture"/>, which is what makes an
    /// attachment bindable into a material with no new concept; grouping happens
    /// here, at the pass, which is where "where does this draw go" already
    /// lives.
    /// </para>
    /// <para>
    /// <b>Depth comes from the first target.</b> The others should be created
    /// with <c>Depth: false</c>; a depth buffer on each would be several
    /// full-screen surfaces allocated and never read.
    /// </para>
    /// <para>
    /// Every target must be the same size. They are written by one rasterisation
    /// and a mismatch is a driver error on some backends and a silently clipped
    /// attachment on others, so it is rejected here instead.
    /// </para>
    /// </remarks>
    public void BeginPass(ReadOnlySpan<RenderTarget> targets, in PassClear clear)
    {
        if (targets.Length == 0)
            throw new ArgumentException("A multi-target pass needs at least one target.", nameof(targets));
        if (targets.Length > MaxColorTargets)
            throw new ArgumentException(
                $"A pass may bind at most {MaxColorTargets} colour targets; got {targets.Length}.",
                nameof(targets));

        for (int i = 1; i < targets.Length; i++)
        {
            if (targets[i].Width != targets[0].Width || targets[i].Height != targets[0].Height)
            {
                throw new ArgumentException(
                    $"Every target in a pass must be the same size; target 0 is " +
                    $"{targets[0].Width}x{targets[0].Height} and target {i} is " +
                    $"{targets[i].Width}x{targets[i].Height}.",
                    nameof(targets));
            }
        }

        BeginPassChecked(targets[0], targets, clear);
    }

    private void BeginPassChecked(RenderTarget? target, ReadOnlySpan<RenderTarget> targets, in PassClear clear)
    {
        if (_inPass)
            throw new InvalidOperationException("BeginPass was called inside a pass; passes do not nest.");

        // The size comes from the target, and only falls back to the window
        // when there is no target. Everything downstream (viewport, aspect
        // ratio, and therefore the projection matrix and the frustum built from
        // it) reads PassSize, so getting this wrong renders a stretched picture
        // that nothing reports as an error.
        _passSize = target is null
            ? FramebufferSize
            : new Vector2D<int>(target.Width, target.Height);
        _currentTarget = target;
        if (_currentTargets.Length < targets.Length)
            _currentTargets = new RenderTarget[MaxColorTargets];
        _currentTargetCount = targets.Length;
        targets.CopyTo(_currentTargets);

        _inPass = true;
        BeginPassCore(target, _currentTargets.AsSpan(0, _currentTargetCount), clear);
    }

    /// <summary>Finishes the pass opened by <see cref="BeginPass"/>. Render thread only.</summary>
    public void EndPass()
    {
        if (!_inPass)
            throw new InvalidOperationException("EndPass was called without a matching BeginPass.");

        _inPass = false;
        RenderTarget? target = _currentTarget;
        var targets = _currentTargets.AsSpan(0, _currentTargetCount);
        _currentTarget = null;
        _currentTargetCount = 0;
        EndPassCore(target, targets);
    }

    /// <summary>
    /// Where the pipeline should render this frame: null for the window, which
    /// is every frame today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pipelines read this and pass it to <see cref="BeginPass(RenderTarget?, in PassClear)"/>,
    /// which is the whole of what it takes for a pipeline to work offscreen: an
    /// editor viewport, a material preview and a post-processing chain all just
    /// set it.
    /// </para>
    /// <para>
    /// Set by the renderer around <see cref="Render"/>, never by a pipeline.
    /// </para>
    /// </remarks>
    public RenderTarget? FrameTarget { get; protected set; }

    /// <summary>
    /// When set, every frame is rendered into this target <i>as well as</i> into
    /// the window, in the same command list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the offscreen gate, and it exists because two of three
    /// backends cannot be tested any other way.</b> OpenGL's render targets are
    /// verified by reading pixels back in <c>GlRenderTargetTests</c>; D3D11 and
    /// D3D12 have no headless device fixture, and the failures that matter there
    /// are precisely the ones that still produce a picture: a missed D3D12
    /// barrier is undefined data the debug layer reports, and a pipeline state
    /// compiled for the wrong render-target format is a validation error rather
    /// than a wrong pixel. Both debug layers are drained every frame, so driving
    /// a real offscreen pass through a real device turns them into the test.
    /// </para>
    /// <para>
    /// <b>Same command list, not a second frame.</b> Rendering twice by calling
    /// <see cref="Render"/> again would reset D3D12's command allocator while
    /// the GPU might still be reading the list it recorded, which is a use-after-free
    /// with no diagnostic at all. Doing both passes inside one frame is also the
    /// shape post-processing needs, so this is a rehearsal rather than a
    /// detour.
    /// </para>
    /// </remarks>
    public RenderTarget? ProbeTarget { get; set; }

    /// <summary>
    /// How many error or corruption messages a graphics debug layer has
    /// reported over this renderer's life. Always zero on backends and builds
    /// that have no debug layer.
    /// </summary>
    /// <remarks>
    /// <b>This is what turns the debug layer from a log into a gate.</b> The
    /// messages that matter most on D3D are the ones that do not stop the frame:
    /// a missing barrier, a pipeline state bound to a target it was not compiled
    /// for. Both render a picture, and both are reported here and nowhere else.
    /// Counting them lets a diagnostic run fail on its own instead of waiting
    /// for somebody to read the log.
    /// </remarks>
    public int DebugLayerErrorCount { get; private set; }

    /// <summary>Recorded by a backend's debug-layer drain. Render thread only.</summary>
    private protected void NoteDebugLayerErrors(int count) => DebugLayerErrorCount += count;

    // ---- HDR and the resolve -----------------------------------------------

    private RenderTarget? _sceneTarget;
    private Mesh? _fullscreenTriangle;
    private ShaderProgram? _resolveShader;
    private PostPass? _resolvePass;

    /// <summary>
    /// Whether the scene renders into a half-float target and is tone-mapped on
    /// the way to the window, rather than being written to the window directly.
    /// </summary>
    /// <remarks>
    /// On by default: with it off the engine is back to clamping every value
    /// above 1 at the moment it is shaded, which is the thing exposure and a
    /// tone curve exist to avoid. The switch exists so a backend or a driver
    /// that cannot do it has somewhere to fall back to, and so a bug here can
    /// be bisected without a rebuild.
    /// </remarks>
    public bool HdrEnabled { get; set; } = true;

    /// <summary>
    /// Linear multiplier applied to scene radiance before the tone curve. 1 is
    /// neutral; higher opens the exposure.
    /// </summary>
    public float Exposure { get; set; } = 1f;

    /// <summary>The scene's HDR target, once one has been created. Null while <see cref="HdrEnabled"/> is off.</summary>
    public RenderTarget? SceneTarget => _sceneTarget;

    /// <summary>
    /// Creates or resizes the HDR target the scene renders into, or returns null
    /// when there is nothing sensible to render into.
    /// </summary>
    /// <remarks>
    /// Sized to the window, deliberately. <c>Engine</c> seeds the camera's
    /// aspect ratio from the framebuffer before building the culling frustum,
    /// and the pipeline sets it again from the pass; a scene target of a
    /// different shape would make those two disagree and the frustum would stop
    /// matching what is drawn.
    /// </remarks>
    protected RenderTarget? EnsureSceneTarget()
    {
        Vector2D<int> size = FramebufferSize;
        // Minimised, or mid-resize. A zero-sized target is not creatable and the
        // frame has nothing to show anyway; the caller renders straight to the
        // window instead of resolving.
        if (size.X <= 0 || size.Y <= 0) return null;

        if (_sceneTarget is null)
        {
            _sceneTarget = CreateRenderTarget(new RenderTargetDesc(
                size.X, size.Y, TextureFormat.Rgba16Float, TextureColorSpace.Linear));
        }
        else
        {
            _sceneTarget.Resize(size.X, size.Y);
        }
        return _sceneTarget;
    }

    /// <summary>The shared clip-space triangle every full-screen pass draws.</summary>
    protected Mesh EnsureFullscreenTriangle() =>
        _fullscreenTriangle ??= CreateMesh(
            FullscreenTriangle.BuildVertices(Backend),
            FullscreenTriangle.Indices,
            VertexAttribute.StandardLayout,
            MeshCpuAccess.None);

    /// <summary>
    /// Draws <paramref name="source"/> to <paramref name="output"/> through the
    /// tone-mapping resolve. <paramref name="output"/> null means the window.
    /// </summary>
    protected void ResolveTo(Texture source, RenderTarget? output, Scene.Scene? scene)
    {
        using var measured = Profiler.Measure(Diagnostics.FramePhase.Resolve);

        _resolveShader ??= BaseShaders.PostResolvePath is { } path
            ? CreateShaderFromFile(path)
            : CreateShaderFromSource(BaseShaders.PostResolve);
        _resolvePass ??= new PostPass(_resolveShader);

        _resolvePass
            .SetUniform("uExposure", Exposure)
            .SetTexture("uSource", 0, source);

        // Keep, not clear: the triangle covers every pixel, so clearing would be
        // work with no observable effect.
        BeginPass(output, PassClear.Keep);
        try
        {
            DrawFullscreen(_resolvePass);

            // The overlay rides along in the same pass, on top of the resolved
            // image and outside the tone curve. See DrawOverlay for why it must
            // not go through the scene's pass.
            if (scene is not null && DebugDraw.VertexCount > 0)
                FlushDebugDrawCore(scene.Camera);
        }
        finally
        {
            EndPass();
        }
    }

    /// <summary>
    /// Draws the full-screen triangle with <paramref name="pass"/>'s program and
    /// values, with depth testing off and solid fill.
    /// </summary>
    /// <remarks>
    /// Abstract rather than shared because the order of <c>Use()</c> and the
    /// uniform writes differs per backend, and because each has its own ambient
    /// raster state to neutralise. See <see cref="PostPass"/>.
    /// </remarks>
    protected abstract void DrawFullscreen(PostPass pass);

    /// <summary>
    /// Runs one resolve, for tests. Internal because nothing in a game drives a
    /// resolve directly, and because the orientation of a full-screen pass is
    /// not observable any other way: it produces no error when it is wrong, only
    /// an upside-down picture.
    /// </summary>
    internal void ResolveForTest(Texture source, RenderTarget? output) => ResolveTo(source, output, null);

    /// <summary>The shared clip-space triangle, for tests that drive their own shader over it.</summary>
    internal Mesh EnsureFullscreenTriangleForTest() => EnsureFullscreenTriangle();

    /// <summary>
    /// Frees the intermediate targets and the shared full-screen machinery: the
    /// HDR target, the G-buffer, and the resolve's and light pass's own
    /// resources. Render thread, before the device goes away.
    /// </summary>
    protected void ReleaseFrameResources()
    {
        if (_sceneTarget is not null)
        {
            DestroyRenderTarget(_sceneTarget);
            _sceneTarget = null;
        }

        _gbuffer?.Dispose();
        _gbuffer = null;

        _shadowMap?.Dispose();
        _shadowMap = null;
        _shadowShader = null;

        _fullscreenTriangle = null;
        _resolveShader = null;
        _resolvePass = null;
        _gbufferShader = null;
        _lightShader = null;
        _lightPass = null;
    }

    // ---- The deferred path -------------------------------------------------

    private GBuffer? _gbuffer;
    private ShaderProgram? _gbufferShader;
    private ShaderProgram? _lightShader;
    private PostPass? _lightPass;

    /// <summary>
    /// Multiplied into a GL-convention projection matrix to produce this
    /// backend's clip Z. Identity on OpenGL, the 0..1 remap on both D3D
    /// backends.
    /// </summary>
    public virtual Matrix4x4 ClipZCorrection => Matrix4x4.Identity;

    /// <summary>
    /// Scale and bias turning a depth texel back into this backend's NDC z.
    /// </summary>
    /// <remarks>
    /// <b>The same fact as <see cref="ClipZCorrection"/>, stated for the way
    /// back.</b> A depth buffer always stores 0..1 on every API; what differs is
    /// what the projection put there. Where clip z runs -1..1 the texel must be
    /// scaled back out, and where it already runs 0..1 it is used as it stands.
    /// Getting this wrong reconstructs a world position on the right ray at the
    /// wrong distance, so the picture stays plausible and only the lighting is
    /// wrong, which is the reason it is a named property with a test rather than
    /// two magic numbers inside a pipeline.
    /// </remarks>
    public virtual Vector2 DepthToNdcZ => new(2f, -1f);

    /// <summary>
    /// Whether row zero of a render target is its TOP row, as it is on D3D and
    /// is not on OpenGL.
    /// </summary>
    /// <remarks>
    /// The same fact <see cref="FullscreenTriangle"/> bakes into its vertex data,
    /// stated once for everything else that has to sample a rasterised target by
    /// a coordinate it computed itself. A shadow lookup is the other case: get
    /// this wrong and the shadow is vertically mirrored on exactly one backend,
    /// which no debug layer reports because nothing about it is invalid.
    /// </remarks>
    public virtual bool TargetOriginIsTopLeft => true;

    // ---- shadows -----------------------------------------------------------

    private ShadowMap? _shadowMap;
    private ShaderProgram? _shadowShader;
    private readonly RenderView _shadowView = new();

    /// <summary>
    /// The depth offset the rasterizer is currently applying. Ambient state,
    /// like the fill mode, because it belongs to a PASS rather than to a draw.
    /// </summary>
    public DepthBias CurrentDepthBias { get; private set; }

    /// <summary>
    /// Sets the rasterizer's depth offset for the draws that follow. Idempotent.
    /// </summary>
    /// <remarks>
    /// Only the shadow pass uses this today, and it puts it back to
    /// <see cref="DepthBias.None"/> before it returns: a bias left on would
    /// offset the camera's own depth pass and produce z-fighting nobody would
    /// connect to shadows.
    /// </remarks>
    public void SetDepthBias(DepthBias bias)
    {
        if (bias == CurrentDepthBias) return;
        CurrentDepthBias = bias;
        ApplyDepthBias(bias);
    }

    /// <summary>
    /// Makes <paramref name="bias"/> current on this backend.
    /// </summary>
    /// <remarks>
    /// Immediate on OpenGL and D3D11, which have rasterizer state objects. On
    /// D3D12 the bias is baked into a pipeline state, so that backend records
    /// the value and lets it reach the PSO key instead.
    /// </remarks>
    protected virtual void ApplyDepthBias(DepthBias bias) { }

    /// <summary>
    /// Whether the frame's first directional light casts a shadow. On by
    /// default; off costs nothing and is the fastest way to tell a shadow bug
    /// from a lighting one.
    /// </summary>
    public bool ShadowsEnabled { get; set; } = true;

    /// <summary>How dark a shadowed surface goes: 0 no shadow at all, 1 fully unlit by the caster.</summary>
    /// <remarks>
    /// Slightly under 1 by default. Nothing in the engine bounces light yet, so
    /// a fully dark shadow is darker than any real one, and the flat ambient
    /// term is too crude to make up the difference on its own.
    /// </remarks>
    public float ShadowStrength { get; set; } = 0.85f;

    /// <summary>The directional shadow map, once one has been created.</summary>
    public ShadowMap? ShadowMap => _shadowMap;

    /// <summary>How many casters the last shadow pass drew. Reported by the periodic stats line.</summary>
    public int ShadowCasterCount { get; private set; }

    /// <summary>
    /// Renders the frame's shadow map, and returns the index of the light it was
    /// rendered for, or -1 when nothing cast.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Directional lights only, and only the first one.</b> A point light
    /// needs a cube map, which <see cref="CreateTexture"/> has no path for at
    /// all; a spot light needs a cone frustum and a second map. Both are real
    /// work rather than a loop bound, so this says one and means it.
    /// </para>
    /// <para>
    /// The pass runs BEFORE the geometry pass, so the light pass that follows
    /// reads a map from this frame rather than the last one. On D3D12 that
    /// ordering is also what keeps the barriers simple: the map goes to
    /// readable at <c>EndPass</c> and stays there.
    /// </para>
    /// </remarks>
    internal int RenderShadowMap(Scene.Scene scene, RenderView view)
    {
        ShadowCasterCount = 0;
        if (!ShadowsEnabled) return -1;

        int index = FindShadowCaster(view);
        if (index < 0) return -1;

        ShadowMap map = _shadowMap ??= new ShadowMap(this);
        var direction = new Vector3(
            view.Lights[index].PositionRange.X,
            view.Lights[index].PositionRange.Y,
            view.Lights[index].PositionRange.Z);

        if (!map.Fit(scene.Camera, direction)) return -1;

        _shadowShader ??= BaseShaders.ShadowDepthPath is { } path
            ? CreateShaderFromFile(path)
            : CreateShaderFromSource(BaseShaders.ShadowDepth);

        // Culled against the LIGHT, not the camera. See Scene.BuildShadowView
        // for why reusing the camera's list makes shadows flicker as you turn.
        // One pass over the whole atlas, one depth clear, then a viewport per
        // cascade. Clearing per cascade would clear the whole target each time
        // and wipe the cascades already drawn.
        using var measured = Profiler.Measure(Diagnostics.FramePhase.Shadows);

        BeginPass(map.Target, PassClear.DepthOnly);

        // THE ACNE FIX, and the reason the light pass gets to bias gently.
        // Slope-scaled at raster time pushes each caster back by what that
        // caster's own depth slope needs, which is the quantity that produces
        // acne in the first place; every alternative is a constant tuned for
        // the worst surface on screen and paid for by every other one. See
        // DepthBias.
        SetDepthBias(map.RasterBias);
        try
        {
            for (int cascade = 0; cascade < map.CascadeCount; cascade++)
            {
                (int x, int y, int size) = map.TileAt(cascade);
                SetPassViewport(x, y, size, size);

                Matrix4x4 lightViewProjection = map.LightViewProjectionAt(cascade);

                // Culled against THIS CASCADE, not once for the whole atlas. The
                // near cascade covers a few metres, so re-culling is most of
                // what makes it cheap: it draws a handful of casters where a
                // shared list would draw everything within the shadow distance.
                scene.BuildShadowView(lightViewProjection, _shadowView);

                Matrix4x4 lightClip = lightViewProjection * ClipZCorrection;
                DrawShadowCasters(_shadowView.Items, lightClip);
                DrawShadowCasters(_shadowView.WorldItems, lightClip);
            }
        }
        finally
        {
            SetDepthBias(DepthBias.None);
            EndPass();
        }

        return index;
    }

    // The first directional light in the view, which CollectLights has already
    // sorted to the front. Point lights are skipped rather than fitted badly.
    private static int FindShadowCaster(RenderView view)
    {
        ReadOnlySpan<RenderLight> lights = view.Lights;
        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i].IsDirectional)
                return i;
        }
        return -1;
    }

    private void DrawShadowCasters(System.Collections.Generic.IReadOnlyList<RenderItem> items, in Matrix4x4 lightClip)
    {
        ShaderProgram shader = _shadowShader!;
        for (int i = 0; i < items.Count; i++)
        {
            RenderItem item = items[i];

            // The material is not consulted at all: a shadow map records where a
            // surface is, not what it looks like. That is also why alpha-tested
            // foliage will need a second shader here and does not have one.
            if (BindsProgramBeforeUniforms) shader.Use();
            shader.SetUniform("uModel", item.World);
            shader.SetUniform("uLightViewProjection", lightClip);
            if (!BindsProgramBeforeUniforms) shader.Use();

            item.Mesh.Draw();
            ShadowCasterCount++;
        }
    }

    // Filled once: an array uniform has to be written whole, and a frame with
    // no shadow map still has to write something of the declared length.
    private static readonly Matrix4x4[] IdentityCascades =
        [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity];

    private static readonly Vector4[] EmptyCascadeRects = new Vector4[ShadowMap.MaxCascades];

    /// <summary>
    /// Whether a program must be bound before its uniforms are written.
    /// </summary>
    /// <remarks>
    /// True on OpenGL, where <c>glUniform</c> writes into the active program;
    /// false on both D3D backends, which stage into a constant shadow that
    /// <c>Use</c> flushes. The same split <see cref="PostPass"/> exists for,
    /// named here so a shared draw loop can honour it instead of every pipeline
    /// carrying its own copy of the order.
    /// </remarks>
    protected virtual bool BindsProgramBeforeUniforms => false;

    /// <summary>The deferred G-buffer, once one has been created.</summary>
    public GBuffer? GBuffer => _gbuffer;

    /// <summary>
    /// Creates or resizes the G-buffer, or returns null when there is nothing
    /// to render into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sized to the window, NOT to <see cref="FrameTarget"/>, and that is a
    /// correctness rule rather than a simplification.</b> A frame can render its
    /// pipeline more than once into differently sized targets (the offscreen
    /// probe does exactly that), and following the target would resize the
    /// G-buffer partway through recording a command list. On D3D12 a resize
    /// releases the old resource, and releasing one the open list already
    /// references is a use-after-free that the debug layer reports as
    /// "deleted prior to closing the command list".
    /// </para>
    /// <para>
    /// The cost is that a pass rendering into a differently sized target samples
    /// a window-sized G-buffer through 0..1 coordinates, so the picture is
    /// rescaled rather than re-rendered. That is only ever a probe, which is
    /// asserting that nothing went wrong rather than looking at the image.
    /// </para>
    /// </remarks>
    internal GBuffer? EnsureGBuffer()
    {
        Vector2D<int> size = FramebufferSize;
        int width = size.X;
        int height = size.Y;

        // Minimised, or mid-resize. Nothing is creatable and the frame has
        // nothing to show anyway.
        if (width <= 0 || height <= 0) return null;

        if (_gbuffer is null)
            _gbuffer = new GBuffer(this, width, height);
        else
            _gbuffer.Resize(width, height);

        return _gbuffer;
    }

    /// <summary>
    /// The program every surface is drawn with during a deferred geometry pass.
    /// </summary>
    /// <remarks>
    /// <b>The geometry pass overrides the material's own shader.</b> A material
    /// names a program that shades and returns a colour, which is exactly what a
    /// G-buffer pass must not do; what it needs from the material is the
    /// parameter set, which <see cref="Material.ApplyTo"/> hands over. The cost
    /// is that a material cannot yet customise its own G-buffer write, and the
    /// benefit is that every <c>.spectramat</c> written before deferred existed
    /// renders in both paths with no migration.
    /// </remarks>
    internal ShaderProgram EnsureGBufferShader() =>
        _gbufferShader ??= BaseShaders.GBufferFillPath is { } path
            ? CreateShaderFromFile(path)
            : CreateShaderFromSource(BaseShaders.GBufferFill);

    /// <summary>
    /// Shades the whole G-buffer into <see cref="FrameTarget"/> in one
    /// full-screen pass, and puts the sky back where no geometry was drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by all three backends rather than written once per pipeline: the
    /// only thing that differs is when a program may be bound, and
    /// <see cref="DrawFullscreen"/> already owns that difference.
    /// </para>
    /// <para>
    /// One pass over the screen for up to <see cref="RenderView.MaxLights"/>
    /// lights. That cap is the forward path's cap carried over, not a property
    /// of deferred: the version that removes it draws a bounding volume per
    /// light with additive blending, which needs blend state no backend has yet.
    /// </para>
    /// </remarks>
    internal void DrawDeferredLightPass(
        GBuffer gbuffer, RenderView view, Scene.Camera camera, float ambient, int shadowLightIndex)
    {
        using var measured = Profiler.Measure(Diagnostics.FramePhase.Lighting);

        _lightShader ??= BaseShaders.DeferredLightPath is { } path
            ? CreateShaderFromFile(path)
            : CreateShaderFromSource(BaseShaders.DeferredLight);
        _lightPass ??= new PostPass(_lightShader);

        // The transform the geometry pass actually used, inverted. Built from
        // the same two matrices and the same correction the geometry pass
        // uploads, so a change to either cannot leave the reconstruction
        // pointing at a different frustum than the one that was rasterised.
        Matrix4x4 worldToClip = camera.View * camera.Projection * ClipZCorrection;
        if (!Matrix4x4.Invert(worldToClip, out Matrix4x4 clipToWorld))
        {
            // A singular view-projection means a degenerate camera, which is a
            // frame with nothing to shade rather than something to throw over.
            _logger.LogWarning("Deferred light pass skipped: the view-projection matrix is not invertible.");
            return;
        }

        _lightPass
            .SetUniform("uInverseViewProjection", clipToWorld)
            .SetUniform("uCameraPosition", camera.Position)
            .SetUniform("uSkyColor", new Vector3(ClearColors.Sky.X, ClearColors.Sky.Y, ClearColors.Sky.Z))
            .SetUniform("uDepthToNdc", DepthToNdcZ)
            .SetTexture("uAlbedoAo", 0, gbuffer.Albedo)
            .SetTexture("uNormalRoughness", 1, gbuffer.NormalRoughness)
            .SetTexture("uMaterialData", 2, gbuffer.MaterialData)
            .SetTexture("uEmissive", 3, gbuffer.Emissive)
            .SetTexture("uDepth", 4, gbuffer.Depth);

        // The shadow map, or the G-buffer's own depth standing in for it. A
        // sampler slot must be bound with SOMETHING on every backend, and a
        // strength of zero is what actually turns the lookup off; leaving the
        // slot unbound instead means each backend improvises differently, which
        // is a per-backend picture rather than a per-backend no-op.
        bool casting = shadowLightIndex >= 0 && _shadowMap is not null;
        ShadowMap? map = casting ? _shadowMap : null;

        _lightPass
            .SetUniform("uShadowLightIndex", casting ? shadowLightIndex : -1)
            .SetUniform("uCascadeCount", map?.CascadeCount ?? 0)
            .SetUniform("uShadowStrength", casting ? ShadowStrength : 0f)
            .SetUniform("uShadowTexel", map?.TexelSize ?? 0f)
            .SetUniform("uShadowDepthBias", map?.CompareBias ?? 0f)
            .SetUniform("uShadowFilterRadius", map?.FilterRadius ?? 1f)
            .SetUniform("uTargetSize", new Vector2(PassSize.X, PassSize.Y))
            .SetTexture("uShadowMap", 5, map?.Depth ?? gbuffer.Depth);

        // The arrays must be written whole and must be the length the shader
        // declares, so an unfitted frame still uploads identities rather than
        // leaving the previous frame's matrices behind a count of zero.
        _lightPass
            .SetUniform("uWorldToShadow", map is not null ? map.WorldToShadow : IdentityCascades)
            .SetUniform("uCascadeRects", map is not null ? map.CascadeRects : EmptyCascadeRects);

        LightUpload.Apply(_lightPass, view, ambient);

        // Keep, not clear: the triangle covers every pixel and writes the sky
        // itself where the depth buffer says nothing was drawn.
        BeginPass(FrameTarget, PassClear.Keep);
        try
        {
            DrawFullscreen(_lightPass);
        }
        finally
        {
            EndPass();
        }
    }

    /// <summary>
    /// Draws the frame's accumulated <see cref="DebugDraw"/> lines with depth
    /// testing off. Called inside an already-open pass.
    /// </summary>
    protected abstract void FlushDebugDrawCore(Scene.Camera camera);

    /// <summary>
    /// Draws the debug overlay into the window, in its own pass on top of
    /// whatever the frame already rendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The overlay must not go through the scene's pass.</b> Gizmo handles,
    /// the selection highlight and the marquee are authored as display colours
    /// and mean exactly what they say; the moment the scene renders into an
    /// intermediate target they would be exposure-scaled and tone-mapped along
    /// with it, so a handle would change brightness depending on whether the
    /// camera happened to be looking at something bright. Editor chrome is not
    /// part of the picture being photographed.
    /// </para>
    /// <para>
    /// It costs the overlay nothing it uses: it draws with depth off, so it
    /// never reads scene depth and does not care that this pass has none.
    /// </para>
    /// </remarks>
    protected void DrawOverlay(Scene.Scene? scene)
    {
        if (scene is null || DebugDraw.VertexCount == 0) return;

        BeginPass(PassClear.Keep);
        try
        {
            FlushDebugDrawCore(scene.Camera);
        }
        finally
        {
            EndPass();
        }
    }

    /// <summary>
    /// Narrows the open pass to a sub-rectangle of its target, in texels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The y origin is the v = 0 edge of the texture, on every backend</b>,
    /// which sounds like it cannot be true and is. OpenGL measures a viewport
    /// from the bottom and its texture row zero IS the bottom; D3D measures from
    /// the top and its row zero is the top. The two conventions differ in the
    /// same direction and cancel, so one rectangle in texture space means the
    /// same region of the same texture on both.
    /// </para>
    /// <para>
    /// This exists for the shadow atlas, where several cascades share one map
    /// and each is rendered into its own quadrant. It is reset by the next
    /// <see cref="BeginPass"/>, which always sets the full-target viewport.
    /// </para>
    /// </remarks>
    public void SetPassViewport(int x, int y, int width, int height)
    {
        if (!_inPass)
            throw new InvalidOperationException("SetPassViewport was called outside a pass.");

        SetViewportCore(x, y, width, height);
    }

    /// <summary>Applies a viewport rectangle. See <see cref="SetPassViewport"/> for the y convention.</summary>
    protected abstract void SetViewportCore(int x, int y, int width, int height);

    /// <summary>Binds the target, sets the viewport, and clears. See <see cref="BeginPass"/>.</summary>
    /// <param name="target">The single target, or null for the window.</param>
    /// <param name="targets">
    /// The full attachment set for a multi-target pass, or empty when
    /// <paramref name="target"/> says everything. Backends that ignore it draw
    /// to one attachment, which is what every pass but the deferred geometry one
    /// wants.
    /// </param>
    protected abstract void BeginPassCore(
        RenderTarget? target, ReadOnlySpan<RenderTarget> targets, in PassClear clear);

    /// <summary>
    /// Whatever the target needs on the way out: nothing for a back buffer that
    /// the frame's own barriers already cover, a state transition back to
    /// readable for an offscreen one.
    /// </summary>
    protected abstract void EndPassCore(RenderTarget? target, ReadOnlySpan<RenderTarget> targets);

    /// <summary>
    /// Creates an offscreen render target. Render thread only, like every other
    /// GPU resource.
    /// </summary>
    /// <remarks>
    /// The returned target is owned by this renderer: release it through
    /// <see cref="DestroyRenderTarget"/>, never by disposing it directly, or it
    /// stays in the tracking list until shutdown.
    /// </remarks>
    public abstract RenderTarget CreateRenderTarget(in RenderTargetDesc desc);

    /// <summary>
    /// Disposes a target created by <see cref="CreateRenderTarget"/> and drops
    /// it from the creating renderer's tracking list. Same contract as
    /// <see cref="DestroyMesh"/>: render thread only.
    /// </summary>
    public void DestroyRenderTarget(RenderTarget target)
    {
        if (ReferenceEquals(target, _currentTarget))
            throw new InvalidOperationException(
                "A render target cannot be destroyed while a pass is drawing into it.");

        target.Unregister?.Invoke();
        target.Unregister = null;
        target.Dispose();
    }

    /// <summary>
    /// Renders one frame: <paramref name="view"/> is the engine-built,
    /// frustum-culled draw list for this frame (see
    /// <see cref="Scene.Scene.BuildRenderView"/>); <paramref name="scene"/>
    /// stays available for camera and debug access. Render thread only.
    /// </summary>
    public virtual void Render(Scene.Scene? scene, RenderView view, double deltaTime)
    {
    }

    public virtual void Shutdown()
    {
        HotReloader.Dispose();
        _logger.LogInformation("Renderer shut down");
    }

    /// <summary>Name of the rendering pipeline currently in use (e.g. "Forward", "Wireframe").</summary>
    public abstract string CurrentPipelineName { get; }

    /// <summary>Cycles to the next registered rendering pipeline. Returns the new pipeline's name.</summary>
    public abstract string NextPipeline();

    /// <summary>
    /// Switches to the pipeline whose <c>Name</c> matches
    /// <paramref name="name"/>, case-insensitively. Returns false and changes
    /// nothing when no pipeline has that name.
    /// </summary>
    /// <remarks>
    /// <b>Selecting by name is what lets an unattended run gate a pipeline.</b>
    /// The rotation key is fine for a person at a keyboard and useless to a
    /// smoke run or an offscreen probe, which is a real gap on D3D: the failures
    /// that matter there are debug-layer messages during a frame nobody
    /// watches, and a pipeline that is never selected is never checked.
    /// </remarks>
    public abstract bool TrySelectPipeline(string name);

    /// <summary>
    /// Uploads an interleaved vertex/index stream as a GPU mesh.
    /// <paramref name="cpuAccess"/> decides whether the mesh also keeps CPU
    /// copies for raycasts, BVH bounds and debug wireframes; pass
    /// <see cref="MeshCpuAccess.None"/> for meshes nothing reads back (chunk
    /// meshes, part-brush meshes, full-screen geometry), which the compiler
    /// churns every frame a world brush moves.
    /// </summary>
    public abstract Mesh CreateMesh(
        ReadOnlySpan<float> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes,
        MeshCpuAccess cpuAccess = MeshCpuAccess.Retained);

    /// <summary>
    /// Disposes a mesh created by <see cref="CreateMesh"/> and drops it from the
    /// creating renderer's tracking list. Meshes destroyed mid-run (e.g. the
    /// static world mesh, rebuilt on every CSG edit) must go through here rather
    /// than <see cref="Mesh.Dispose"/>, which would leave the dead instance in
    /// the list until shutdown. Render thread only — creation and destruction
    /// share that thread, which is why deregistration takes no lock.
    /// </summary>
    public void DestroyMesh(Mesh mesh)
    {
        MeshesDestroyed++;
        // The callback closes over the list of the renderer that created the
        // mesh, so this deregisters correctly even on the wrong renderer.
        mesh.Unregister?.Invoke();
        mesh.Unregister = null;
        mesh.Dispose();
    }

    /// <summary>
    /// Uploads <paramref name="pixels"/> as a 2D texture in the given format and
    /// returns a renderer-owned handle. Pixel data is expected as tightly packed
    /// rows from bottom-left to top-right (OpenGL convention).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="colorSpace"/> has no default on purpose.</b> Whether a
    /// block of bytes is colour or data is a fact about the content that this
    /// layer cannot infer, and every wrong answer is a rendering bug that looks
    /// like an art problem. A default would be a guess made silently at the one
    /// layer with the least information; asking makes each caller state what it
    /// knows.
    /// </para>
    /// <para>
    /// A request the format cannot carry degrades to linear rather than failing
    /// (see <see cref="TextureFormatInfo.Resolve"/>), identically on all three
    /// backends.
    /// </para>
    /// </remarks>
    public abstract Texture CreateTexture(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter = TextureFilter.Linear,
        TextureWrap wrap = TextureWrap.Repeat);

    /// <summary>
    /// Disposes a texture created by <see cref="CreateTexture"/> and drops it
    /// from the creating renderer's tracking list. Same contract as
    /// <see cref="DestroyMesh"/>: render thread only.
    /// </summary>
    public void DestroyTexture(Texture texture)
    {
        texture.Unregister?.Invoke();
        texture.Unregister = null;
        texture.Dispose();
    }

    public abstract ShaderProgram CreateShader(string vertexSource, string fragmentSource);

    /// <summary>
    /// Creates a shader program from a compiled SpectraShade blob for this renderer's backend.
    /// </summary>
    public abstract ShaderProgram CreateShader(PipelineBlob blob);

    /// <summary>
    /// Loads a .specshadecomp file and creates a shader program for this renderer's backend.
    /// </summary>
    public ShaderProgram LoadCompiledShader(string path)
    {
        var blob = ShaderFileReader.ReadPipelineFromFile(path, Backend)
            ?? throw new InvalidOperationException($"Compiled shader '{path}' has no data for {Backend}");
        return CreateShader(blob);
    }
}
