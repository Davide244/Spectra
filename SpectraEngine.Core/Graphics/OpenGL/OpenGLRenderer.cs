using Microsoft.Extensions.Logging;
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
    private readonly List<Mesh> _meshes = [];
    private readonly List<ShaderProgram> _shaders = [];
    private readonly List<Texture> _textures = [];
    private readonly List<IOpenGLRenderPipeline> _pipelines = [];
    private int _pipelineIndex;
    private OpenGLLineBatch? _lineBatch;
    private ShaderProgram? _debugShader;

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

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);

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

    public override void Render(Scene.Scene? scene, double deltaTime)
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
            Window = _window,
            Scene = scene,
            DeltaTime = deltaTime,
        };
        _pipelines[_pipelineIndex].Execute(context);
    }

    /// <summary>
    /// Uploads and draws the accumulated <see cref="Renderer.DebugDraw"/> lines
    /// with depth-test off. Called by pipelines after their main scene pass.
    /// </summary>
    internal void FlushDebugDraw(Scene.Camera camera)
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

    public override void Shutdown()
    {
        foreach (var pipeline in _pipelines)
            pipeline.Dispose();
        _pipelines.Clear();

        _lineBatch?.Dispose();
        _lineBatch = null;
        _debugShader = null;

        foreach (var mesh in _meshes)
            mesh.Dispose();
        _meshes.Clear();

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
        _meshes.Add(mesh);
        return mesh;
    }

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels, int width, int height,
        TextureFormat format, TextureFilter filter, TextureWrap wrap)
    {
        var texture = OpenGLTexture.Create(_gl!, pixels, width, height, format, filter, wrap);
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
