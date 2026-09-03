using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

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
    /// Whether <see cref="Present"/> waits for the display's vertical blank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, on in the editor shell, and both defaults are
    /// deliberate.</b> The demo is the measurement instrument — every number in
    /// <c>docs/performance.md</c> is a frame time, and a frame time taken under
    /// vsync measures the display, not the engine. An editor viewport is the
    /// opposite case: presenting thousands of frames a second saturates a core,
    /// multiplies the per-frame allocation rate into real gen0 pressure (and a
    /// gen0 pause stops the UI thread too), and buys nothing a monitor can
    /// show. Every DCC viewport paces to the display for exactly these reasons.
    /// </para>
    /// <para>
    /// Volatile, because a host may flip it while the render thread runs; each
    /// backend reads it at <see cref="Present"/> time (OpenGL re-applies its
    /// context-state swap interval there when the value has changed, since GL's
    /// interval is not a per-present argument).
    /// </para>
    /// </remarks>
    public bool VSync
    {
        get => _vsync;
        set => _vsync = value;
    }

    private volatile bool _vsync;

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
    /// The frame's world-space lines, drawn WITH depth testing, inside the pass
    /// that owns the scene's depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second lane, because the overlay's depth-off rule is right for what
    /// the overlay carries and wrong for this.</b> Gizmo handles and the
    /// selection outline are editor chrome and must never be hidden by the
    /// geometry they describe - a handle you can see and cannot pick is worse
    /// than no handle. A ground grid is the opposite: it is part of the world,
    /// it lies at y = 0, and one drawn through the walls of a room is not a
    /// grid, it is a fault.
    /// </para>
    /// <para>
    /// <b>These lines go THROUGH the tone curve and the overlay's do not</b>,
    /// which is correct for the same reason: a grid is lit scene content and
    /// should dim as the exposure rises, while a handle is a display colour
    /// that must not change brightness because the camera turned. Colours
    /// pushed here are therefore authored in LINEAR light and tuned against the
    /// ACES curve, not copied from the overlay's display values.
    /// </para>
    /// <para>
    /// Cleared once per frame by the engine, exactly as <see cref="DebugDraw"/>
    /// is, and flushed by whichever pipeline is running from inside its own
    /// depth-owning pass.
    /// </para>
    /// </remarks>
    public DebugDraw WorldLines { get; } = new();

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
        // Said here rather than per backend: every backend's Initialize asks
        // BaseShaders for a source path and quietly takes the embedded copy when
        // there is none, so this is the one site all three share and it runs
        // before the first such ask. The call latches, so a second renderer in
        // one process does not repeat it.
        BaseShaders.LogHotReloadState(logger);
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
    /// <summary>
    /// Compiles <paramref name="spectraShadeSource"/> and returns a program built
    /// from its INSTANCED vertex stage, or null if the source declares none.
    /// </summary>
    /// <remarks>
    /// <b>One source, two programs.</b> The instanced stage is emitted by the
    /// compiler from the same file whenever it marks a uniform
    /// <c>[PerInstance]</c>, so this is how a renderer gets the batched twin
    /// without anyone having authored one. Null is an ordinary answer, not a
    /// failure: most shaders do not declare a per-instance uniform.
    /// </remarks>
    public ShaderProgram? TryCreateInstancedShaderFromSource(string spectraShadeSource)
    {
        ReadOnlySpan<GraphicsBackend> targets = [Backend];
        CompiledShaderFile compiled = _shaderCompiler.Compile(spectraShadeSource, targets);
        PipelineBlob? blob = compiled.GetPipeline(Backend);
        if (blob?.InstancedVertexData is null || blob.FragmentData is null)
            return null;

        return CreateShader(
            System.Text.Encoding.UTF8.GetString(blob.InstancedVertexData),
            System.Text.Encoding.UTF8.GetString(blob.FragmentData));
    }

    public ShaderProgram CreateShaderFromFile(string absolutePath)
    {
        string source = File.ReadAllText(absolutePath);
        ShaderProgram program = CreateShaderFromSource(source);
        HotReloader.Register(absolutePath, program);
        return program;
    }

    /// <summary>
    /// Where shaders come from: the mounted content stack, or null for a
    /// renderer nobody handed one. Set before <see cref="Initialize"/>.
    /// </summary>
    /// <remarks>
    /// <b>An <see cref="IContentSource"/> rather than the asset manager</b>,
    /// because a shader is bytes at a content path and nothing about it needs a
    /// cache, an upload pump or a GPU. Null is an ordinary value and every test
    /// fixture uses it: the built-ins then come from the copies embedded in this
    /// assembly, which is what every build did before packs existed.
    /// </remarks>
    public IContentSource? ShaderContent { get; set; }

    /// <summary>
    /// Builds one of the engine's built-in shaders (<see cref="BaseShaders"/>
    /// file names), preferring a cooked blob from the content stack over
    /// compiling source.
    /// </summary>
    /// <remarks>
    /// <b>Every built-in goes through here, and that is what makes a cooked
    /// build cheap.</b> The eight sites that used to spell out
    /// "source path if there is one, else the embedded copy" each compiled at
    /// startup no matter what a pack carried, so a shipped game paid for the
    /// SpectraShade front end on every launch with the answer already in the
    /// file beside it.
    /// </remarks>
    public ShaderProgram CreateBaseShader(string fileName)
    {
        ResolvedShader resolved = BaseShaderResolver.ResolveBuiltIn(ShaderContent, fileName, Backend, _logger);
        if (resolved.Cooked is { } cooked) return CreateShader(cooked);

        ShaderProgram program = CreateShaderFromSource(resolved.Source!);

        // Only where there is a file to watch. A packed source has none, and
        // registering the path it would have had watches a file that is not
        // there rather than failing.
        if (resolved.WatchPath is { } watch) HotReloader.Register(watch, program);
        return program;
    }

    /// <summary>
    /// The instanced twin of a built-in, or null when this shader declares no
    /// per-instance uniform.
    /// </summary>
    /// <remarks>
    /// A cooked blob already carries the twin (the compiler emits both stages
    /// from one source), so the cooked path builds it with no compilation at
    /// all; the source path compiles as before. Null is an ordinary answer that
    /// disables batching for that pass, never a broken frame.
    /// </remarks>
    public ShaderProgram? TryCreateInstancedBaseShader(string fileName)
    {
        ResolvedShader resolved = BaseShaderResolver.ResolveBuiltIn(ShaderContent, fileName, Backend, _logger);
        if (resolved.Cooked is not { } cooked)
            return TryCreateInstancedShaderFromSource(resolved.Source!);

        if (cooked.InstancedVertexData is null || cooked.FragmentData is null) return null;

        return CreateShader(
            System.Text.Encoding.UTF8.GetString(cooked.InstancedVertexData),
            System.Text.Encoding.UTF8.GetString(cooked.FragmentData));
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
    /// When set, the frame's resolve is run a SECOND time into this target,
    /// beside the one that goes to whatever is being presented, in the same
    /// frame and the same command list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the double-sRGB-encode gate, and it exists because that
    /// failure produces no error of any kind.</b> A shared present target is a
    /// UNORM resource wearing an <c>_SRGB</c> render-target view, so the write
    /// encodes exactly once and a consumer that decodes on sample gets the
    /// picture back. Anything downstream that encodes again washes the frame
    /// out: no exception, no HRESULT, no debug-layer message, and the only
    /// witness is somebody looking at it. Resolving the same source into an
    /// ordinary <see cref="TextureFormat.Rgba8"/> sRGB target - which is
    /// byte-for-byte what the window's back buffer holds - gives the comparison
    /// something to be wrong against.
    /// </para>
    /// <para>
    /// <b>Same frame and same command list, deliberately</b>, for the reason
    /// <see cref="ProbeTarget"/> already states: two executions inside one
    /// frame is a rehearsal of the shape post-processing needs, and calling
    /// <see cref="Render"/> twice would reset D3D12's command allocator while
    /// the GPU may still be reading the list it recorded. It also makes the two
    /// pictures the SAME picture rather than two frames of an animation, which
    /// is the whole of what makes a byte comparison meaningful.
    /// </para>
    /// <para>
    /// <b>Needs <see cref="HdrEnabled"/>.</b> With HDR off the pipeline draws
    /// straight into the presented target and there is no intermediate to
    /// resolve a second time from, so the second resolve simply does not
    /// happen; <see cref="ViewportCompareProbe"/> refuses up front rather than
    /// letting a run report a comparison it never made.
    /// </para>
    /// </remarks>
    public RenderTarget? CompareTarget { get; set; }

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

    // ---- Shared colour targets ---------------------------------------------
    //
    // Both D3D backends implement all of this; OpenGL does not. Every member is
    // virtual with a refusing default rather than abstract, so a backend that
    // has not been taught to share answers "no" instead of failing to compile,
    // and a caller has one answer to check rather than a capability table to
    // consult first.
    //
    // The two D3D routes are NOT the same shape, and the contract here is what
    // hides that. D3D11 draws straight into a shared keyed-mutex texture of its
    // own; a D3D12-created handle is refused by the import this feeds (measured:
    // E_NOINTERFACE), so D3D12 renders into a private target and copies it into
    // a texture a D3D11On12 bridge owns. What a host sees is one handle, one
    // generation and one key protocol either way.

    /// <summary>
    /// The keyed-mutex key the PRODUCER acquires and the consumer releases.
    /// </summary>
    /// <remarks>
    /// <b>Both constants live here so the two sides cannot each invent one.</b>
    /// A keyed mutex has no notion of which key means what; the numbers are
    /// pure convention, and a consumer that picked the other pair would deadlock
    /// on its second frame with nothing anywhere reporting a disagreement. Zero
    /// is also the key a freshly created keyed mutex starts released on, which
    /// is why the producer is the side that takes it first.
    /// </remarks>
    public const ulong SharedProducerKey = 0;

    /// <summary>
    /// The keyed-mutex key the producer releases and the CONSUMER acquires. See
    /// <see cref="SharedProducerKey"/>.
    /// </summary>
    public const ulong SharedConsumerKey = 1;

    /// <summary>
    /// A colour target something outside this renderer can import, named by an
    /// NT handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Generation"/> is what makes the handle safe to hold.</b> A
    /// shared target is never resized in place: it is destroyed and recreated
    /// under a new generation, so a consumer holding an older one knows its
    /// handle names a resource that is gone rather than sampling it. The
    /// wrapper identity <see cref="RenderTarget"/> guarantees across a resize
    /// deliberately stops at this boundary, and the note there says so.
    /// </para>
    /// <para>
    /// <b>The size travels WITH the handle</b>, because it is a property of the
    /// shared resource rather than of the renderer. Read separately, the two
    /// can be paired a frame apart - a fresh handle against a stale size - and
    /// the result is a correctly bound texture read at the wrong extent.
    /// </para>
    /// </remarks>
    /// <param name="NtHandle">The shared resource's NT handle, or zero when there is none.</param>
    /// <param name="Width">Pixel width of the shared resource.</param>
    /// <param name="Height">Pixel height of the shared resource.</param>
    /// <param name="Generation">
    /// Bumped every time the resource behind the handle is recreated. A consumer
    /// re-imports when it changes and never before.
    /// </param>
    public readonly record struct SharedTargetHandle(nint NtHandle, int Width, int Height, int Generation);

    /// <summary>
    /// The handle of this renderer's shared colour target, if it has one. True
    /// on D3D11 when the surface is <see cref="RenderSurfaceKind.Composited"/>;
    /// false everywhere else.
    /// </summary>
    public virtual bool TryGetSharedHandle(out SharedTargetHandle handle)
    {
        handle = default;
        return false;
    }

    /// <summary>
    /// The consumer has stopped using <paramref name="generation"/> and every
    /// generation before it, so the resources behind them may be freed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The other half of <see cref="SharedTargetHandle.Generation"/>, and the
    /// only thing that makes a resize of a shared target safe.</b> A resize
    /// retires the old resource rather than freeing it, because the consumer may
    /// be sampling it this instant and freeing it underneath throws nothing on
    /// either side. Without this call every retired generation is held until a
    /// hard cap forces it out, which is reported but is still a full-screen
    /// surface per resize step of a drag.
    /// </para>
    /// <para>
    /// Render thread only, like every other member here. A host learns of the
    /// consumer's release on its own thread and posts it across the same way it
    /// posts everything else.
    /// </para>
    /// </remarks>
    public virtual void NotifySharedTargetReleased(int generation)
    {
    }

    /// <summary>
    /// Takes the shared target's key for this frame's write. False means the key
    /// did not arrive within <paramref name="timeoutMs"/> and nothing was
    /// acquired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The key protocol, both halves in one place because neither half means
    /// anything alone:</b> the producer acquires <see cref="SharedProducerKey"/>
    /// and releases <see cref="SharedConsumerKey"/>; the consumer acquires
    /// <see cref="SharedConsumerKey"/> and releases
    /// <see cref="SharedProducerKey"/>. Whoever holds the mutex is the only side
    /// touching the texture, and the key it releases is what says whose turn is
    /// next. Releasing the key you acquired instead of the other one deadlocks
    /// both sides on the following frame.
    /// </para>
    /// <para>
    /// <b>Every touch of the texture belongs inside the bracket, not just the
    /// draws.</b> Measured: a clear issued against a keyed-mutex resource whose
    /// key is not held completes with <c>S_OK</c>, raises nothing on the debug
    /// layer, and writes nothing at all - the readback is zeros. There is no
    /// diagnostic anywhere for getting this wrong, only a picture that never
    /// changes, so the bracket goes around the whole of the frame's work on the
    /// shared target rather than around the draw calls alone.
    /// </para>
    /// <para>
    /// <b>A timeout is not an error, and blocking on it is.</b> It means the
    /// consumer has not taken its turn - it is not being drawn, so it never
    /// acquired key 1 and never released key 0 - and a render thread that waits
    /// for that stalls the whole engine on something which may not come back at
    /// all. The correct response is to skip this frame's shared write and carry
    /// on rendering; the consumer keeps the last frame it was given, which is
    /// the right picture for something nobody is looking at.
    /// </para>
    /// </remarks>
    /// <param name="timeoutMs">How long to wait for the key before giving up on this frame.</param>
    public virtual bool BeginSharedWrite(int timeoutMs = 100) => false;

    // ---- How long the producer spent WAITING for its turn ------------------
    //
    // The mutex is the clock for a composited viewport, which means the engine
    // cannot start a frame until the consumer has released key 0, and the
    // consumer releases it from a continuation on the shell's UI dispatcher.
    // So engine frame rate is coupled to UI-thread latency by construction,
    // and the "58 to 60 fps" this design was signed off with was measured AT
    // REST, with nothing else asking the dispatcher for anything.
    //
    // That coupling is invisible from every instrument the engine already has:
    // frame time includes the wait, so a stalled producer and a slow producer
    // report the same number, and there is no debug-layer message for either.
    // This separates them. A frame rate that falls while this stays near zero
    // is the engine's own cost; a frame rate that falls while this rises is the
    // consumer not taking its turn, and no amount of render optimisation will
    // touch it.
    private long _sharedAcquireTicks;
    private long _sharedAcquirePeakTicks;
    private int _sharedAcquireSamples;

    /// <summary>
    /// Records one producer-side wait for the shared target's key. Called by a
    /// backend from inside <see cref="BeginSharedWrite"/>, around the acquire
    /// and nothing else.
    /// </summary>
    protected void RecordSharedAcquireWait(long ticks)
    {
        _sharedAcquireTicks += ticks;
        _sharedAcquireSamples++;
        if (ticks > _sharedAcquirePeakTicks) _sharedAcquirePeakTicks = ticks;
    }

    /// <summary>
    /// Takes the producer's acquire-wait statistics since the last drain, in
    /// milliseconds, and resets them.
    /// </summary>
    /// <remarks>
    /// <b>The PEAK is the number that matters and the average is the one that
    /// hides it.</b> A viewport missing one vsync in three averages a third of
    /// the stall it actually suffers, which reads as a small cost rather than
    /// as an intermittent freeze; both are reported so the shape is visible.
    /// Draining rather than sampling, because the reader publishes on an
    /// interval and a peak that survived into the next window would be
    /// attributed to the wrong moment.
    /// </remarks>
    public void DrainSharedAcquireWait(out float averageMs, out float peakMs)
    {
        double toMs = 1000.0 / Stopwatch.Frequency;
        averageMs = _sharedAcquireSamples > 0
            ? (float)(_sharedAcquireTicks * toMs / _sharedAcquireSamples)
            : 0f;
        peakMs = (float)(_sharedAcquirePeakTicks * toMs);

        _sharedAcquireTicks = 0;
        _sharedAcquirePeakTicks = 0;
        _sharedAcquireSamples = 0;
    }

    /// <summary>
    /// Releases the shared target's key with the consumer's value, handing the
    /// texture over. Called only after <see cref="BeginSharedWrite"/> returned
    /// true.
    /// </summary>
    public virtual void EndSharedWrite()
    {
    }

    // ---- The consumer's half, for a run that has no consumer ----------------
    //
    // Both members below stand in for a compositor, and both are internal
    // because nothing in a game or a shell is on this side of the handshake:
    // a real consumer owns its own device and does this through the imported
    // handle. They exist so --viewport-compare can measure what that consumer
    // WOULD see without needing one, and because with no consumer at all the
    // producer's second frame simply times out - the key it released is key 1,
    // and nothing hands key 0 back.

    /// <summary>
    /// Takes the consumer's turn and immediately gives it back: acquire
    /// <see cref="SharedConsumerKey"/>, release <see cref="SharedProducerKey"/>.
    /// False means the turn never arrived, or there is no shared target.
    /// </summary>
    /// <remarks>
    /// <b>A heartbeat, not a read.</b> Without it a headless run writes exactly
    /// one shared frame and every frame after it skips the write on a
    /// <c>WAIT_TIMEOUT</c>, which is correct behaviour answering a question
    /// nobody asked - and would leave the shared texture holding a picture
    /// several frames older than the one a comparison had just resolved beside
    /// it, i.e. a guaranteed false failure with a plausible-looking cause.
    /// </remarks>
    internal virtual bool TakeSharedConsumerTurn(int timeoutMs = 100) => false;

    /// <summary>
    /// Reads back what an importer of the shared handle would see, as tightly
    /// packed 8-bit RGBA in the picture-space row order
    /// <see cref="ReadTargetPixels"/> defines. False means there is no shared
    /// target, or its key never came back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"What the consumer would see" is the only question with one answer on
    /// both backends</b>, and that is why this is a member here rather than a
    /// readback of some target. On D3D11 the present target IS the shared
    /// texture, so this is <see cref="ReadTargetPixels"/> of it. On D3D12 it is
    /// not: the frame lands in a private D3D12 target and a D3D11On12 bridge
    /// copies it into a texture D3D11 created, so reading the present target
    /// there would measure everything except the copy - which is precisely
    /// where a second encode would live.
    /// </para>
    /// <para>
    /// <b>The key is held across the read.</b> A keyed-mutex resource WRITTEN
    /// without its key completes with <c>S_OK</c> and writes nothing, measured;
    /// what a read without it returns is not defined anywhere, and a
    /// measurement taken outside the protocol is not a measurement of the
    /// protocol. Taking the turn here is also what hands key 0 back, so the
    /// frame after a read is not skipped.
    /// </para>
    /// </remarks>
    internal virtual bool TryReadSharedPixels(Span<byte> destination, int timeoutMs = 100) => false;

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

        _resolveShader ??= CreateBaseShader(BaseShaders.PostResolveFileName);
        _resolvePass ??= new PostPass(_resolveShader);

        _resolvePass
            .SetUniform("uExposure", Exposure)
            .SetTexture("uSource", 0, source);

        // Keep, not clear: the triangle covers every pixel, so clearing would be
        // work with no observable effect.
        BeginPass(output, PassClear.Keep);
        try
        {
            DrawFullscreen(_resolvePass, EnsureFullscreenTriangle());

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
    /// Draws <paramref name="geometry"/> with <paramref name="pass"/>'s program
    /// and values, with depth testing off and solid fill.
    /// </summary>
    /// <remarks>
    /// Abstract rather than shared because the order of <c>Use()</c> and the
    /// uniform writes differs per backend, and because each has its own ambient
    /// raster state to neutralise. See <see cref="PostPass"/>. The geometry is
    /// a parameter rather than always <see cref="EnsureFullscreenTriangle"/>
    /// because the orientation measurement needs a quad with UVs that carry no
    /// per-backend adjustment; see <see cref="OrientationQuad"/>.
    /// </remarks>
    protected abstract void DrawFullscreen(PostPass pass, Mesh geometry);

    /// <summary>
    /// Runs one resolve, for tests. Internal because nothing in a game drives a
    /// resolve directly, and because the orientation of a full-screen pass is
    /// not observable any other way: it produces no error when it is wrong, only
    /// an upside-down picture.
    /// </summary>
    internal void ResolveForTest(Texture source, RenderTarget? output) => ResolveTo(source, output, null);

    /// <summary>The shared clip-space triangle, for tests that drive their own shader over it.</summary>
    internal Mesh EnsureFullscreenTriangleForTest() => EnsureFullscreenTriangle();

    /// <summary>Clears one target outside a frame, for tests that have no scene to render.</summary>
    /// <remarks>
    /// <b>The command scope is why this exists at all.</b> D3D11's immediate
    /// context is always recording, so a test there can drive
    /// <see cref="BeginPass(RenderTarget?, in PassClear)"/> straight from the
    /// fixture; on D3D12 a pass outside a frame writes into a closed command
    /// list and does nothing, silently. Shared rather than per-backend so a
    /// test cannot prove its own arrangement instead of the engine's.
    /// </remarks>
    internal void ClearForTest(RenderTarget target, Vector4 color)
    {
        ArgumentNullException.ThrowIfNull(target);

        BeginOutOfFrameCommands();
        try
        {
            BeginPass(target, PassClear.To(color));
            EndPass();
        }
        finally
        {
            EndOutOfFrameCommands();
        }
    }

    // ---- Texture orientation, measured -------------------------------------

    private Mesh? _orientationQuadFull;
    private Mesh? _orientationQuadTopHalf;
    private PostPass? _orientationPass;

    /// <summary>
    /// Draws <paramref name="source"/> over <paramref name="target"/> through
    /// <see cref="OrientationQuad"/>: a clip-space quad whose UVs carry no
    /// per-backend adjustment. The target is cleared to opaque black first, so
    /// anything the quad does not cover reads as black.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The instrument for a question the code cannot answer.</b> Whether an
    /// uploaded texture arrives the same way up on all three backends is not
    /// decidable by reading either the upload path or the sampler: every call
    /// succeeds either way and the only difference is the picture. So it is
    /// drawn and read back. See <see cref="ReadTargetPixel"/> for the other
    /// half.
    /// </para>
    /// <para>
    /// Safe outside a frame, which is what lets a diagnostic call it: each
    /// backend opens whatever command scope it needs around the draw and
    /// finishes it before returning.
    /// </para>
    /// </remarks>
    internal void DrawOrientationQuad(Texture source, RenderTarget target, OrientationQuad.Coverage coverage)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        // Everything that creates a GPU resource happens BEFORE the command
        // scope opens: on D3D12 a mesh upload and a shader's first pipeline
        // state both execute and wait on their own, which a recording command
        // list cannot survive.
        _resolveShader ??= CreateBaseShader(BaseShaders.PostResolveFileName);
        _orientationPass ??= new PostPass(_resolveShader);

        Mesh quad = coverage == OrientationQuad.Coverage.TopHalf
            ? _orientationQuadTopHalf ??= CreateOrientationQuad(coverage)
            : _orientationQuadFull ??= CreateOrientationQuad(coverage);

        // Exposure pinned at 1 rather than read from the renderer: the run this
        // measures may have set any exposure it likes, and a probe whose answer
        // depends on that is not a probe.
        _orientationPass
            .SetUniform("uExposure", 1f)
            .SetTexture("uSource", 0, source);

        BeginOutOfFrameCommands();
        try
        {
            BeginPass(target, PassClear.To(new Vector4(0f, 0f, 0f, 1f)));
            try
            {
                DrawFullscreen(_orientationPass, quad);
            }
            finally
            {
                EndPass();
            }
        }
        finally
        {
            EndOutOfFrameCommands();
        }
    }

    private Mesh CreateOrientationQuad(OrientationQuad.Coverage coverage) => CreateMesh(
        OrientationQuad.BuildVertices(coverage),
        OrientationQuad.Indices,
        VertexAttribute.StandardLayout,
        MeshCpuAccess.None);

    /// <summary>
    /// Reads one texel of <paramref name="target"/>'s colour attachment back to
    /// the CPU, as 8-bit RGBA.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The coordinates are PICTURE space, not memory order, and that is the
    /// whole contract.</b> <paramref name="x"/> counts from the left edge and
    /// <paramref name="y"/> counts from the BOTTOM - the edge a vertex at clip
    /// y = -1 rasterises to, which is the bottom of the viewport on every
    /// backend. Each backend converts from that to its own row order, because
    /// stating the answer in memory order would make a comparison between two
    /// backends meaningless: the disagreement being looked for and the
    /// convention used to look would be the same quantity.
    /// </para>
    /// <para>
    /// Render thread only, and synchronous: it stalls until the GPU has finished
    /// whatever wrote the texel. A diagnostic and a test path, never a frame
    /// path.
    /// </para>
    /// </remarks>
    internal abstract (byte R, byte G, byte B, byte A) ReadTargetPixel(RenderTarget target, int x, int y);

    /// <summary>
    /// Reads a rectangle of <paramref name="target"/>'s colour attachment back
    /// to the CPU as tightly packed 8-bit RGBA.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same picture-space contract <see cref="ReadTargetPixel"/> states,
    /// and the destination follows it too</b>: <paramref name="x"/> counts from
    /// the left edge, <paramref name="y"/> from the BOTTOM, and row 0 of
    /// <paramref name="destination"/> is the bottom row of the region. Each
    /// backend converts from its own row order, because stating the answer in
    /// memory order would make a comparison between two backends meaningless -
    /// the disagreement being looked for and the convention used to look would
    /// be the same quantity.
    /// </para>
    /// <para>
    /// <b>This is the plural form because the singular one cannot be used for a
    /// whole picture.</b> Every D3D implementation of
    /// <see cref="ReadTargetPixel"/> creates a staging resource, copies one
    /// texel and maps it, which is fine for the four corners a diagnostic asks
    /// about and is 921,600 device round trips for a 1280x720 frame. Every real
    /// backend therefore overrides this with one copy and one map; the default
    /// below is correct rather than fast, and exists so a renderer with no GPU
    /// under it (a test's stand-in) answers this without being taught to.
    /// </para>
    /// <para>
    /// Render thread only, and synchronous: it stalls until the GPU has
    /// finished whatever wrote the region. A diagnostic and a test path, never
    /// a frame path.
    /// </para>
    /// </remarks>
    /// <param name="target">The target to read. Must have a colour attachment.</param>
    /// <param name="x">Left edge of the region, in texels from the left of the picture.</param>
    /// <param name="y">Bottom edge of the region, in texels from the BOTTOM of the picture.</param>
    /// <param name="width">Region width in texels.</param>
    /// <param name="height">Region height in texels.</param>
    /// <param name="destination">
    /// Receives <c>width * height * 4</c> bytes, rows bottom-first, RGBA order.
    /// May be longer; anything past the region is left alone.
    /// </param>
    internal virtual void ReadTargetPixels(
        RenderTarget target, int x, int y, int width, int height, Span<byte> destination)
    {
        PixelReadback.ValidateRegion(target, x, y, width, height, destination);

        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                (byte r, byte g, byte b, byte a) = ReadTargetPixel(target, x + column, y + row);
                int offset = ((row * width) + column) * 4;
                destination[offset] = r;
                destination[offset + 1] = g;
                destination[offset + 2] = b;
                destination[offset + 3] = a;
            }
        }
    }

    /// <summary>Reads the whole of <paramref name="target"/>. See <see cref="ReadTargetPixels"/>.</summary>
    internal void ReadTargetPixels(RenderTarget target, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(target);
        ReadTargetPixels(target, 0, 0, target.Width, target.Height, destination);
    }

    /// <summary>
    /// Opens a command scope for work issued outside a frame, on backends where
    /// that is a thing. Immediate-mode backends need nothing and do nothing.
    /// </summary>
    /// <remarks>
    /// Not a general facility: it exists so a diagnostic can draw one quad
    /// between frames. Nothing may nest inside it and nothing in the frame path
    /// calls it.
    /// </remarks>
    protected virtual void BeginOutOfFrameCommands()
    {
    }

    /// <summary>
    /// Closes the scope <see cref="BeginOutOfFrameCommands"/> opened and blocks
    /// until the GPU has executed it.
    /// </summary>
    protected virtual void EndOutOfFrameCommands()
    {
    }

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
        _shadowInstancedShader = null;

        // Disposed rather than dropped: unlike the shaders above (which the
        // renderer's own program list owns and releases), the instance buffer
        // is held only here, so nothing else would free it.
        _shadowInstances?.Dispose();
        _shadowInstances = null;
        _shadowInstanceCapacity = 0;
        _shadowInstanceHighWater = 0;

        _gbufferInstancedShader = null;
        _geometryInstances?.Dispose();
        _geometryInstances = null;
        _geometryInstanceCapacity = 0;
        _geometryInstanceHighWater = 0;

        _fullscreenTriangle = null;
        _orientationQuadFull = null;
        _orientationQuadTopHalf = null;
        _orientationPass = null;
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
    private ShaderProgram? _shadowInstancedShader;
    private readonly RenderView _shadowView = new();

    // One buffer for the whole shadow pass, grown to the largest batch any
    // cascade needs and never shrunk. Per batch would be a GPU allocation
    // inside the frame; per cascade would be four times the uploads for the
    // same data.
    private InstanceBuffer? _shadowInstances;
    private int _shadowInstanceCapacity;
    private int _shadowInstanceHighWater;

    // Instances this frame's cascades have asked for so far. Reset per frame,
    // and summed rather than maxed because they share one buffer.
    private int _shadowInstanceDemand;

    // Cleared when the compiler produced no instanced stage, so the frame draws
    // every caster the ordinary way instead of silently dropping batches.
    private bool _shadowBatchingAvailable = true;

    // The geometry pass's own set, deliberately a SECOND buffer rather than a
    // share of the shadow one: the two passes want different capacities (the
    // shadow buffer holds every cascade's demand summed) and a shared buffer
    // would make either pass's growth a hazard for the other's open draws.
    private ShaderProgram? _gbufferInstancedShader;
    private InstanceBuffer? _geometryInstances;
    private int _geometryInstanceCapacity;
    private int _geometryInstanceHighWater;
    private int _geometryInstanceDemand;
    private bool _geometryBatchingAvailable = true;

    /// <summary>
    /// Geometry-pass draws that batching removed this frame. Zero in a scene
    /// with nothing repeated, which is most of them.
    /// </summary>
    /// <remarks>
    /// Reported for the same reason <see cref="ShadowDrawsSaved"/> is: a broken
    /// batch path and a scene with nothing to batch look identical from the
    /// outside, and only a number tells them apart.
    /// </remarks>
    public int GeometryDrawsSaved { get; private set; }

    /// <summary>
    /// Shadow-caster draws that batching removed this frame. Reported so a
    /// scene that stops batching says so, rather than only getting slower.
    /// </summary>
    public int ShadowDrawsSaved { get; private set; }

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
        ShadowDrawsSaved = 0;
        if (!ShadowsEnabled) return -1;

        int index = FindShadowCaster(view);
        if (index < 0) return -1;

        ShadowMap map = _shadowMap ??= new ShadowMap(this);
        var direction = new Vector3(
            view.Lights[index].PositionRange.X,
            view.Lights[index].PositionRange.Y,
            view.Lights[index].PositionRange.Z);

        if (!map.Fit(scene.Camera, direction)) return -1;

        _shadowShader ??= CreateBaseShader(BaseShaders.ShadowDepthFileName);

        // The instanced twin of the SAME shader. Nobody wrote it: the compiler
        // emits it because ShadowDepth marks uModel [PerInstance], and a cooked
        // blob already carries it.
        _shadowInstancedShader ??= TryCreateInstancedBaseShader(BaseShaders.ShadowDepthFileName);

        // No instanced variant means no batching, not a broken frame: the
        // unbatched path below draws every caster either way.
        if (_shadowInstancedShader is null)
            _shadowBatchingAvailable = false;

        // Sizing and the cursor reset both live in BeginFrameInstanceBuffers,
        // at the top of the frame: this method runs once per pipeline execution
        // and a frame can execute the pipeline more than once (the offscreen
        // probe does), all into one command list on D3D12.

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

                // Batched first, then whatever no batch claimed. Items is NOT
                // drawn here: the two together are exactly its contents, and
                // drawing it as well would double every caster.
                DrawShadowBatches(_shadowView, lightClip);
                DrawShadowCasters(_shadowView.SingleItems, lightClip);

                // World chunks are never batched, and cannot be: each chunk is
                // unique geometry drawn once at identity, so there is nothing
                // repeated to collapse.
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

    // Every batch in the view, as one instanced draw each.
    //
    // The transforms are uploaded ONCE for the whole view, not once per batch:
    // RenderView lays every batch's instances out contiguously in one array
    // precisely so that a batch is a range within a single upload rather than a
    // buffer of its own.
    private void DrawShadowBatches(RenderView view, in Matrix4x4 lightClip)
    {
        if (view.Batches.Count == 0)
            return;

        if (!_shadowBatchingAvailable)
        {
            DrawShadowBatchesUnbatched(view, lightClip);
            return;
        }

        ReadOnlySpan<Matrix4x4> transforms = view.InstanceTransforms;

        // Accumulated ACROSS cascades, because they share one buffer within a
        // frame and the demand is therefore the sum rather than the largest.
        _shadowInstanceDemand += transforms.Length;
        _shadowInstanceHighWater = Math.Max(_shadowInstanceHighWater, _shadowInstanceDemand);

        if (_shadowInstances is null || _shadowInstances.Remaining < transforms.Length)
        {
            // Not enough room left this frame, so these batches are drawn one
            // instance at a time. Correct, merely unbatched, and it self-corrects
            // next frame once the high-water mark is applied.
            DrawShadowBatchesUnbatched(view, lightClip);
            return;
        }

        // APPENDED, not overwritten. This runs once per cascade and on D3D12 the
        // whole frame is a single command list submitted at the end, so writing
        // every cascade at offset zero left the first three drawing the fourth's
        // transforms: silent, and on that backend only. See InstanceBuffer.
        int baseInstance = _shadowInstances.Append(
            MemoryMarshal.Cast<Matrix4x4, float>(transforms), transforms.Length);

        ShaderProgram shader = _shadowInstancedShader!;
        if (BindsProgramBeforeUniforms) shader.Use();
        shader.SetUniform("uLightViewProjection", lightClip);
        if (!BindsProgramBeforeUniforms) shader.Use();

        for (int i = 0; i < view.Batches.Count; i++)
        {
            RenderBatch batch = view.Batches[i];

            // No material, exactly as the unbatched path: a shadow map records
            // where a surface is, not what it looks like.
            batch.Mesh.DrawInstanced(_shadowInstances, batch.Count, baseInstance + batch.Offset);
            ShadowCasterCount += batch.Count;
        }

        ShadowDrawsSaved += view.DrawsSaved;
    }

    // The batches this frame could not fit, drawn one instance at a time.
    // Their items are not in SingleItems, so without this they would simply not
    // be drawn, and a caster missing from a shadow map is a hole in the shadow
    // with nothing reporting it.
    private void DrawShadowBatchesUnbatched(RenderView view, in Matrix4x4 lightClip)
    {
        ReadOnlySpan<Matrix4x4> transforms = view.InstanceTransforms;
        ShaderProgram shader = _shadowShader!;

        for (int i = 0; i < view.Batches.Count; i++)
        {
            RenderBatch batch = view.Batches[i];
            for (int n = 0; n < batch.Count; n++)
            {
                if (BindsProgramBeforeUniforms) shader.Use();
                shader.SetUniform("uModel", transforms[batch.Offset + n]);
                shader.SetUniform("uLightViewProjection", lightClip);
                if (!BindsProgramBeforeUniforms) shader.Use();

                batch.Mesh.Draw();
                ShadowCasterCount++;
            }
        }
    }

    /// <summary>
    /// Sizes both instance buffers and rewinds them. Called once at the top of
    /// a frame, by every backend's <c>Render</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Once per FRAME, not once per pipeline execution, and the difference is
    /// visible only on D3D12.</b> A frame can run the pipeline more than once -
    /// with <c>ProbeTarget</c> set it renders the scene into the probe and then
    /// again into the window - and on that backend the whole frame is one
    /// command list submitted at the end. Rewinding between the two would put
    /// the second run's transforms under the first run's already-recorded draws.
    /// It happens to be harmless today because both runs draw the same scene,
    /// which is luck rather than design.
    /// </para>
    /// <para>
    /// <b>Growth happens HERE and nowhere else</b>, for the same reason the
    /// shadow cascades never grow between themselves: freeing a buffer the open
    /// list references removes the device. A run that then finds too little room
    /// left draws its batches unbatched, which is correct and self-corrects next
    /// frame once the high-water mark is applied.
    /// </para>
    /// </remarks>
    protected void BeginFrameInstanceBuffers()
    {
        EnsureShadowInstanceCapacity();
        EnsureGeometryInstanceCapacity();

        _shadowInstanceDemand = 0;
        _geometryInstanceDemand = 0;
        GeometryDrawsSaved = 0;
        _shadowInstances?.BeginFrame();
        _geometryInstances?.BeginFrame();
    }

    /// <summary>
    /// Sizes the shadow instance buffer to the largest batch total any cascade
    /// needed last frame. Called BEFORE the shadow pass opens.
    /// </summary>
    /// <remarks>
    /// <b>Growing inside the pass is a device hang, not a stall.</b> Cascades
    /// are drawn one after another into one open pass, so a reallocation
    /// between them frees a resource the command list already references. D3D11
    /// executes immediately and survives it; D3D12 executes at submit and the
    /// device is removed with DXGI_ERROR_DEVICE_HUNG, which is how this was
    /// found. Sizing from the previous frame's high-water mark keeps every
    /// allocation outside the pass, and a frame whose demand jumped simply
    /// draws those batches unbatched once.
    /// <para>
    /// Powers of two, and never shrunk: a scene whose batch sizes wobble would
    /// otherwise reallocate continuously, and the buffer is one allocation for
    /// the run.
    /// </para>
    /// </remarks>
    private void EnsureShadowInstanceCapacity()
    {
        int needed = _shadowInstanceHighWater;
        if (needed <= 0 || (_shadowInstances is not null && _shadowInstanceCapacity >= needed))
            return;

        int capacity = Math.Max(64, _shadowInstanceCapacity);
        while (capacity < needed)
            capacity *= 2;

        _shadowInstances?.Dispose();
        _shadowInstances = CreateInstanceBuffer(
            capacity, VertexAttribute.StandardInstanceLayout, _shadowInstancedShader!);
        _shadowInstanceCapacity = capacity;
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
        _gbufferShader ??= CreateBaseShader(BaseShaders.GBufferFillFileName);

    /// <summary>
    /// The instanced twin of the G-buffer program, generated from the same
    /// source because <c>GBufferFill</c> marks <c>uModel</c> as
    /// <c>[PerInstance]</c>. Null when this backend's toolchain did not produce
    /// one, which disables batching rather than the pass.
    /// </summary>
    private ShaderProgram? EnsureGBufferInstancedShader()
    {
        if (_gbufferInstancedShader is not null || !_geometryBatchingAvailable)
            return _gbufferInstancedShader;

        _gbufferInstancedShader = TryCreateInstancedBaseShader(BaseShaders.GBufferFillFileName);

        // Asked for once. Retrying every frame would recompile a shader that
        // has already said no, in the frame loop.
        if (_gbufferInstancedShader is null)
            _geometryBatchingAvailable = false;

        return _gbufferInstancedShader;
    }

    /// <summary>
    /// Everything a deferred geometry pass draws: repeated meshes as one
    /// instanced draw each, then whatever no batch claimed, then the static
    /// world's chunks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shared by all three backends rather than written once per pipeline.</b>
    /// The two things that genuinely differ are which side of the uniform writes
    /// the program is bound on (<see cref="BindsProgramBeforeUniforms"/>) and the
    /// clip-space z convention (<see cref="ClipZCorrection"/>), and both are
    /// already stated once. Three copies of this loop is three places for the
    /// batch path to be adopted differently.
    /// </para>
    /// <para>
    /// <b>Call <see cref="PrepareGeometryInstancing"/> before opening the pass.</b>
    /// </para>
    /// </remarks>
    internal void DrawGeometry(RenderView view, Scene.Camera camera, ShaderProgram shader)
    {
        Matrix4x4 projection = camera.Projection * ClipZCorrection;

        DrawGeometryBatches(view, camera, projection, shader);

        // What no batch claimed. Batches and SingleItems together are exactly
        // Items, so drawing Items as well would double every surface.
        DrawGeometryItems(view.SingleItems, camera, projection, shader);

        // World chunks are never batched and cannot be: each chunk is unique
        // geometry drawn once at identity, so there is nothing repeated to
        // collapse.
        DrawGeometryItems(view.WorldItems, camera, projection, shader);
    }

    /// <summary>
    /// Compiles the instanced G-buffer program if this is the first frame that
    /// wanted it. Must run OUTSIDE the geometry pass.
    /// </summary>
    internal void PrepareGeometryInstancing() => EnsureGBufferInstancedShader();

    private void DrawGeometryBatches(
        RenderView view, Scene.Camera camera, in Matrix4x4 projection, ShaderProgram fallback)
    {
        if (view.Batches.Count == 0)
            return;

        ReadOnlySpan<Matrix4x4> transforms = view.InstanceTransforms;
        _geometryInstanceDemand += transforms.Length;
        _geometryInstanceHighWater = Math.Max(_geometryInstanceHighWater, _geometryInstanceDemand);

        if (_gbufferInstancedShader is null || _geometryInstances is null
            || _geometryInstances.Remaining < transforms.Length)
        {
            DrawGeometryBatchesUnbatched(view, camera, projection, fallback);
            return;
        }

        int baseInstance = _geometryInstances.Append(
            MemoryMarshal.Cast<Matrix4x4, float>(transforms), transforms.Length);

        ShaderProgram shader = _gbufferInstancedShader;
        int saved = 0;

        for (int i = 0; i < view.Batches.Count; i++)
        {
            RenderBatch batch = view.Batches[i];

            // Skipped exactly as the item loops skip it: a material is what
            // supplies the parameters this pass writes, and a batch without one
            // has nothing to say.
            if (batch.Material is not { } material)
                continue;

            // PER BATCH, AND INSIDE THE LOOP. Hoisting the view and projection
            // above it looks like an obvious saving and is wrong: on the D3D
            // backends the writes are staged into a constant shadow that Use()
            // flushes, so every batch after the first would draw with the
            // previous batch's material. On OpenGL it would appear to work,
            // which is the worse half.
            if (BindsProgramBeforeUniforms) shader.Use();
            shader.SetUniform("uView", camera.View);
            shader.SetUniform("uProjection", projection);
            material.ApplyTo(shader);
            if (!BindsProgramBeforeUniforms) shader.Use();

            // No uModel: it arrives per instance through the vertex input the
            // generated stage declares, which is the whole point of the variant.
            batch.Mesh.DrawInstanced(_geometryInstances, batch.Count, baseInstance + batch.Offset);
            saved += batch.Count - 1;
        }

        // Counted from what was actually drawn rather than from view.DrawsSaved,
        // which does not know about the skip above.
        GeometryDrawsSaved += saved;
    }

    // The batches this frame could not batch, drawn one instance at a time.
    // Their items are not in SingleItems, so without this they would simply not
    // be drawn.
    private void DrawGeometryBatchesUnbatched(
        RenderView view, Scene.Camera camera, in Matrix4x4 projection, ShaderProgram shader)
    {
        ReadOnlySpan<Matrix4x4> transforms = view.InstanceTransforms;

        for (int i = 0; i < view.Batches.Count; i++)
        {
            RenderBatch batch = view.Batches[i];
            if (batch.Material is not { } material)
                continue;

            for (int n = 0; n < batch.Count; n++)
                DrawGeometryOne(batch.Mesh, material, transforms[batch.Offset + n], camera, projection, shader);
        }
    }

    private void DrawGeometryItems(
        System.Collections.Generic.IReadOnlyList<RenderItem> items,
        Scene.Camera camera, in Matrix4x4 projection, ShaderProgram shader)
    {
        for (int i = 0; i < items.Count; i++)
        {
            RenderItem item = items[i];
            if (item.Material is { } material)
                DrawGeometryOne(item.Mesh, material, item.World, camera, projection, shader);
        }
    }

    private void DrawGeometryOne(
        Mesh mesh, Material material, in Matrix4x4 model,
        Scene.Camera camera, in Matrix4x4 projection, ShaderProgram shader)
    {
        // Unlike the forward path there is nothing to skip on: a material with
        // no program of its own is still a perfectly good parameter set, and the
        // program being filled is the pass's.
        if (BindsProgramBeforeUniforms) shader.Use();
        shader.SetUniform("uModel", model);
        shader.SetUniform("uView", camera.View);
        shader.SetUniform("uProjection", projection);
        material.ApplyTo(shader);
        if (!BindsProgramBeforeUniforms) shader.Use();

        mesh.Draw();
    }

    /// <summary>
    /// Sizes the geometry instance buffer to last frame's high-water mark.
    /// Frame boundary only; see <see cref="BeginFrameInstanceBuffers"/>.
    /// </summary>
    private void EnsureGeometryInstanceCapacity()
    {
        int needed = _geometryInstanceHighWater;
        if (needed <= 0 || _gbufferInstancedShader is null
            || (_geometryInstances is not null && _geometryInstanceCapacity >= needed))
        {
            return;
        }

        int capacity = Math.Max(64, _geometryInstanceCapacity);
        while (capacity < needed)
            capacity *= 2;

        _geometryInstances?.Dispose();
        _geometryInstances = CreateInstanceBuffer(
            capacity, VertexAttribute.StandardInstanceLayout, _gbufferInstancedShader);
        _geometryInstanceCapacity = capacity;
    }

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

        _lightShader ??= CreateBaseShader(BaseShaders.DeferredLightFileName);
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
            // NOT PassSize: the G-buffer follows the window and this pass may be
            // running into a target of some other size, in which case the shader
            // has to snap its reads to a texel centre so the depth it samples and
            // the ray it reconstructs along describe the same point. See
            // DeferredLight.FragmentMain.
            .SetUniform("uGBufferSize", new Vector2(gbuffer.Width, gbuffer.Height))
            // uv to clip-space xy, matching the V flip FullscreenTriangle bakes
            // into its vertices for this backend. Sent rather than assumed: the
            // wrong sign here is an upside-down reconstruction that throws
            // nothing and logs nothing.
            .SetUniform("uUvToNdc", TargetOriginIsTopLeft
                ? new Vector4(2f, -2f, -1f, 1f)
                : new Vector4(2f, 2f, -1f, -1f))
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
            DrawFullscreen(_lightPass, EnsureFullscreenTriangle());
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
    /// Draws <see cref="WorldLines"/> alpha-blended with no depth write, using
    /// <paramref name="program"/>. Called inside an already-open pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two configurations, told apart by <paramref name="gbuffer"/>.</b>
    /// Null is the forward/wireframe half: the open pass owns the scene's live
    /// depth, so the backend depth-tests in hardware
    /// (<see cref="DepthMode.TestNoWriteEqual"/> — LessEqual for the coplanar
    /// grid-on-floor case, no write because a translucent pixel has no
    /// business in the depth buffer). Non-null is the deferred half: the open
    /// pass is the lit HDR target whose own depth is stale, so the backend
    /// binds the G-buffer's depth as an ordinary texture and the shader
    /// compares and discards per pixel (<see cref="DepthMode.None"/> in
    /// hardware) — sampling it is free of new pass concepts because a render
    /// target's depth is sampleable by contract, and it is already in a
    /// shader-readable state after the light pass read it.
    /// </para>
    /// <para>
    /// The fade metadata (<see cref="DebugDraw.FadeCenter"/> and friends) and
    /// the deferred extras are uploaded by each backend at its own point in
    /// the documented <c>Use()</c> order.
    /// </para>
    /// </remarks>
    /// <param name="nudge">
    /// How far toward the camera to bias the line, as a fraction of its
    /// distance. Passed rather than pre-written, because the order of
    /// <c>Use()</c> and the uniform writes differs per backend.
    /// </param>
    protected abstract void FlushWorldLinesCore(
        Scene.Camera camera, ShaderProgram program, float nudge, GBuffer? gbuffer);

    /// <summary>
    /// Draws this frame's <see cref="WorldLines"/> into the open forward or
    /// wireframe scene pass — the pass that owns the scene's live depth.
    /// Submitted last in that pass, like all translucency.
    /// </summary>
    public void FlushWorldLines(Scene.Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (WorldLines.VertexCount == 0)
            return;

        // The nudge goes to the BACKEND, not written here, because the order
        // of Use() and the uniform writes differs per backend and is already
        // documented as a trap: on OpenGL glUniform writes into the ACTIVE
        // program, so a value staged before Use() lands in whichever program
        // was bound last - silently, and the line simply renders unbiased.
        FlushWorldLinesCore(camera, EnsureWorldLineShader(), WorldLineDepthNudge, null);
    }

    /// <summary>
    /// Draws this frame's <see cref="WorldLines"/> over the lit deferred
    /// frame: its own pass on <see cref="FrameTarget"/>, after
    /// <see cref="DrawDeferredLightPass"/>, blended, with the depth test done
    /// in the shader against the G-buffer's depth.
    /// </summary>
    /// <remarks>
    /// <b>This replaced drawing lines INTO the G-buffer as opaque
    /// five-attachment overwrites.</b> That model rendered a line by replacing
    /// the surface pixel under it, so its "fade" was a colour lerp toward
    /// black — which over a lit floor barely changes a dark line's contrast,
    /// and cannot fade at all. Real transparency needs a blend against the lit
    /// result, and the lit result only exists after the light pass.
    /// </remarks>
    public void FlushWorldLinesDeferred(Scene.Camera camera, GBuffer gbuffer)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(gbuffer);
        if (WorldLines.VertexCount == 0)
            return;

        ShaderProgram program = EnsureWorldLineBlendShader();

        // Keep, not clear: the light pass has already painted every pixel of
        // this target, and these lines go over the picture.
        BeginPass(FrameTarget, PassClear.Keep);
        try
        {
            FlushWorldLinesCore(camera, program, WorldLineDepthNudge, gbuffer);
        }
        finally
        {
            EndPass();
        }
    }

    /// <summary>
    /// ndc.xy → uv for this backend's targets: the inverse of the light pass's
    /// <c>uUvToNdc</c>, carrying the same per-backend V flip. Sent rather than
    /// assumed for the same reason that one is.
    /// </summary>
    protected Vector4 NdcToUv => TargetOriginIsTopLeft
        ? new Vector4(0.5f, -0.5f, 0.5f, 0.5f)
        : new Vector4(0.5f, 0.5f, 0.5f, 0.5f);

    /// <summary>
    /// How far toward the camera a world line is nudged, as a fraction of its
    /// distance to it.
    /// </summary>
    /// <remarks>
    /// See <c>WorldLine.spectrashade</c>: LessEqual alone is necessary and not
    /// sufficient for a coplanar line, because a large quad's interpolated depth
    /// and a line's land an ulp apart in whichever direction the rasteriser
    /// happens to choose, and half the line's pixels then fail the test. Eight
    /// millimetres at ten units.
    /// </remarks>
    public float WorldLineDepthNudge { get; set; } = 0.0008f;

    private ShaderProgram? _worldLineShader;
    private ShaderProgram? _worldLineBlendShader;

    private ShaderProgram EnsureWorldLineShader() =>
        _worldLineShader ??= CreateBaseShader(BaseShaders.WorldLineFileName);

    /// <summary>
    /// Compiles the deferred (post-light, shader-depth-tested) world-line
    /// program on first use.
    /// </summary>
    /// <remarks>
    /// Lazily, because a frame that draws no world lines - every shipped game -
    /// must not pay for a program it never binds, and because compiling one
    /// inside an open pass would be a state change in the middle of a recorded
    /// command list. The first grid frame therefore compiles it outside a pass:
    /// see the pipelines, which call PrepareWorldLines before opening theirs.
    /// </remarks>
    private ShaderProgram EnsureWorldLineBlendShader() =>
        _worldLineBlendShader ??= CreateBaseShader(BaseShaders.WorldLineBlendFileName);

    /// <summary>
    /// Compiles whatever the world-line flush will need, before a pass opens.
    /// </summary>
    public void PrepareWorldLines(bool gbuffer)
    {
        if (WorldLines.VertexCount == 0)
            return;

        if (gbuffer)
            _ = EnsureWorldLineBlendShader();
        else
            _ = EnsureWorldLineShader();
    }

    /// <summary>
    /// Draws the debug overlay into <paramref name="output"/> (the window when
    /// it is null), in its own pass on top of whatever the frame already
    /// rendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="output"/> must be whatever the frame actually resolved
    /// into.</b> On a composited surface there is no back buffer at all, so an
    /// overlay that kept drawing to the window would draw to a null render
    /// target view: no error, no debug-layer message, and a viewport with no
    /// gizmo handles in it. It defaults to the window so the two backends that
    /// only ever draw there need not say so.
    /// </para>
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
    protected void DrawOverlay(Scene.Scene? scene, RenderTarget? output = null)
    {
        if (scene is null || DebugDraw.VertexCount == 0) return;

        BeginPass(output, PassClear.Keep);
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

    /// <summary>
    /// Every registered pipeline's name, in registration order — what a UI
    /// offers where the rotation key offers a blind cycle. Empty before
    /// <see cref="Initialize"/>, and virtual rather than abstract so a test
    /// fake with no pipelines has nothing to implement.
    /// </summary>
    /// <remarks>
    /// The set is fixed once registration ends, so backends cache the list and
    /// return the same instance every call: it rides every
    /// <see cref="Hosting.FrameSnapshot"/>, where a fresh list per publish
    /// would be render-thread garbage for a value that never changes.
    /// </remarks>
    public virtual IReadOnlyList<string> PipelineNames => Array.Empty<string>();

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
    /// Creates a buffer able to hold <paramref name="capacityInstances"/>
    /// instances of <paramref name="attributes"/>, for
    /// <see cref="Mesh.DrawInstanced"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every attribute must name <see cref="VertexAttribute.InstanceSlot"/>.</b>
    /// A per-vertex attribute here would be bound to the instance buffer and
    /// read one element per instance instead of per vertex, which is a mesh
    /// that renders as garbage with nothing reporting why, so it is refused.
    /// </para>
    /// <para>
    /// <b><paramref name="program"/> is the shader the buffer will be drawn
    /// under, and D3D11 genuinely needs it.</b> An input layout there is
    /// validated against a vertex shader signature at creation and is only
    /// usable under a shader whose inputs it satisfies. An earlier version of
    /// this built the layout against the DEFAULT shader, reasoning that extra
    /// elements are permitted; creation did succeed, and then every instanced
    /// draw failed with "the input stage requires Semantic/Index (TEXCOORD,3)
    /// as input, but it is not provided by the output stage". Permitted extra
    /// elements are not the same thing as a layout that carries them into
    /// another shader. GL and D3D12 ignore this parameter: GL binds attributes
    /// into the vertex array and D3D12 into the PSO, so neither needs a
    /// signature here.
    /// </para>
    /// </remarks>
    public abstract InstanceBuffer CreateInstanceBuffer(
        int capacityInstances,
        ReadOnlySpan<VertexAttribute> attributes,
        ShaderProgram program);

    /// <summary>
    /// Throws if <paramref name="attributes"/> is not a valid instance layout.
    /// Shared by every backend's <see cref="CreateInstanceBuffer"/>, and the
    /// reason none of them re-states the rule.
    /// </summary>
    protected static int ValidateInstanceLayout(
        int capacityInstances, ReadOnlySpan<VertexAttribute> attributes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityInstances);

        if (attributes.Length == 0)
            throw new ArgumentException("An instance layout needs at least one attribute.", nameof(attributes));

        int floats = 0;
        for (int i = 0; i < attributes.Length; i++)
        {
            if (attributes[i].InputSlot != VertexAttribute.InstanceSlot)
            {
                throw new ArgumentException(
                    $"Attribute at location {attributes[i].Location} names input slot " +
                    $"{attributes[i].InputSlot}, but an instance buffer binds " +
                    $"{VertexAttribute.InstanceSlot}.",
                    nameof(attributes));
            }

            floats += (int)attributes[i].ComponentCount;
        }

        return floats;
    }

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
    /// rows, <b>row 0 first and row 0 at v = 0</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All three backends agree about which way up an uploaded texture is,
    /// and that is measured rather than reasoned about.</b> Row 0 of this span
    /// is the row sampled at v = 0 on OpenGL, D3D11 and D3D12 alike:
    /// <c>glTexImage2D</c>'s first row and a <c>SubresourceData</c>'s first row
    /// are both the v = 0 end. The bottom-left-versus-top-left origin difference
    /// the engine documents elsewhere is a fact about RENDER TARGETS - surfaces
    /// filled by rasterisation, where GL writes the bottom of the picture into
    /// row 0 and D3D writes the top - which is why
    /// <see cref="FullscreenTriangle"/> flips V on D3D and why nothing here
    /// does. Confusing the two is what makes this look like a per-backend
    /// problem when it is not.
    /// </para>
    /// <para>
    /// <b>The engine's convention is that v = 0 is the BOTTOM of the
    /// picture</b>, which is what <c>ImageDecoder.FlipRowsInPlace</c>
    /// establishes: image files store rows top-down, the decoder reverses them,
    /// so a mesh whose v grows upward renders the file the way it was authored.
    /// Since no backend disagrees, a future change of convention - the cooked
    /// block-compressed path cannot flip rows at all, so it will arrive
    /// top-down - belongs in exactly ONE place, the V axis of UV generation,
    /// applied identically everywhere, and never per backend at upload. See
    /// <c>docs/formats-and-pipeline.md</c> section 2.2.
    /// </para>
    /// <para>
    /// Guarded rather than asserted in prose: <c>TextureOrientationGlTests</c>
    /// pins OpenGL against a real driver, and <c>--offscreen-probe</c> prints a
    /// verdict line per backend for the two that have no headless fixture. Both
    /// draw the asymmetric fixture named by
    /// <see cref="TextureOrientationProbe"/>; an upside-down texture raises no
    /// error anywhere, so a picture is the only instrument there is.
    /// </para>
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
    public Texture CreateTexture(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter = TextureFilter.Linear,
        TextureWrap wrap = TextureWrap.Repeat)
        => CreateTexture(TextureUploadDesc.SingleLevel(
            pixels, width, height, format, colorSpace, filter, wrap));

    /// <summary>
    /// Uploads a texture from an explicit per-mip layout over one payload: what
    /// a cooked, block-compressed image arrives as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only upload path, and the single-span overload above is
    /// expressed over it.</b> An uncompressed RGBA texture is a one-mip
    /// descriptor with a tight pitch, which is not a special case of anything -
    /// it is the same call with less in it. Two paths would mean a fix for a
    /// padded row pitch, or for the colour space of a supplied chain, landing in
    /// one of them; and the older path is the one every existing caller uses, so
    /// it would be the one that kept working while the new one rotted.
    /// </para>
    /// <para>
    /// <b>The descriptor is validated HERE rather than in each backend</b>, so a
    /// malformed layout is refused with the same message on all three and no
    /// backend can be the one that reads past the end of a mapped view instead.
    /// See <see cref="TextureUploadDesc.Validate"/> for what is checked and why
    /// the message names the mip.
    /// </para>
    /// <para>
    /// Orientation, colour space and the "no default for the colour space"
    /// reasoning are all unchanged from the overload above; a compressed payload
    /// is subject to exactly the same contract, except that it cannot flip its
    /// own rows, which is the case that documented paragraph was written for.
    /// </para>
    /// </remarks>
    public Texture CreateTexture(in TextureUploadDesc desc)
    {
        desc.Validate();
        return CreateTextureCore(in desc);
    }

    /// <summary>
    /// The backend's half of <see cref="CreateTexture(in TextureUploadDesc)"/>,
    /// reached only after validation and responsible for registering the result
    /// with the creating renderer's tracking list.
    /// </summary>
    protected abstract Texture CreateTextureCore(in TextureUploadDesc desc);

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
