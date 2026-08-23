using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.IO;

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
    public virtual void AcquireContext(IWindow window) => window.GLContext?.MakeCurrent();

    /// <summary>
    /// Releases the context from the calling thread; mirror of
    /// <see cref="AcquireContext"/>. Called at render-loop shutdown.
    /// </summary>
    public virtual void ReleaseContext(IWindow window) => window.GLContext?.Clear();

    /// <summary>
    /// Presents the most recently rendered frame to the window. OpenGL swaps
    /// buffers via the GL context; D3D11/12 call IDXGISwapChain::Present.
    /// </summary>
    public virtual void Present(IWindow window) => window.GLContext?.SwapBuffers();

    /// <summary>
    /// A backend-provided shader suitable for general lit geometry. Available
    /// after <see cref="Initialize"/> has run.
    /// </summary>
    public ShaderProgram? DefaultShader { get; protected set; }

    /// <summary>
    /// Per-frame primitive accumulator for debug visualisations. Callers push
    /// lines/boxes/arrows before <see cref="Render"/>; the renderer uploads and
    /// draws them after the main scene pass. The engine clears it each frame.
    /// </summary>
    public DebugDraw DebugDraw { get; } = new();

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

    public virtual void Initialize(IWindow window)
    {
        _logger.LogInformation("Renderer initialized");
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

    public abstract Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes);

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
