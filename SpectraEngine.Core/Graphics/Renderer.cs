using Microsoft.Extensions.Logging;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using System;

namespace SpectraEngine.Core.Graphics;

public abstract class Renderer
{
    internal readonly ILogger<Renderer> _logger;

    public abstract GraphicsBackend Backend { get; }

    protected Renderer(ILogger<Renderer> logger)
    {
        _logger = logger;
    }

    public virtual void Initialize(IWindow window)
    {
        _logger.LogInformation("Renderer initialized");
    }

    public virtual void Render(double deltaTime)
    {
    }

    public virtual void Shutdown()
    {
        _logger.LogInformation("Renderer shut down");
    }

    public abstract Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes);

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
