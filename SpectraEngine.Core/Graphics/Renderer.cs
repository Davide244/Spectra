using Microsoft.Extensions.Logging;
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

    public virtual void Render(Scene.Scene? scene, double deltaTime)
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
    /// Uploads <paramref name="pixels"/> as a 2D texture in the given format and
    /// returns a renderer-owned handle. Pixel data is expected as tightly packed
    /// rows from bottom-left to top-right (OpenGL convention).
    /// </summary>
    public abstract Texture CreateTexture(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureFilter filter = TextureFilter.Linear,
        TextureWrap wrap = TextureWrap.Repeat);

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
