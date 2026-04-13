using Microsoft.Extensions.Logging;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Drawing;
using System.Text;

namespace SpectraEngine.Core.Graphics.OpenGL;

public class OpenGLRenderer : Renderer
{
    private GL? _gl;
    private readonly List<Mesh> _meshes = [];
    private readonly List<ShaderProgram> _shaders = [];

    public override GraphicsBackend Backend => GraphicsBackend.OpenGL;

    private const string DefaultVertexShader = """
        #version 330 core

        layout (location = 0) in vec3 aPosition;

        void main()
        {
            gl_Position = vec4(aPosition, 1.0);
        }
        """;

    private const string DefaultFragmentShader = """
        #version 330 core

        out vec4 out_color;

        void main()
        {
            out_color = vec4(1.0, 0.5, 0.2, 1.0);
        }
        """;

    public OpenGLRenderer(ILogger<Renderer> logger) : base(logger)
    {
    }

    public override void Initialize(IWindow window)
    {
        _gl = window.CreateOpenGL();

        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        // Demo quad — will come from scene/asset data later
        ReadOnlySpan<float> vertices =
        [
             0.5f,  0.5f, 0.0f,
             0.5f, -0.5f, 0.0f,
            -0.5f, -0.5f, 0.0f,
            -0.5f,  0.5f, 0.0f,
        ];

        ReadOnlySpan<uint> indices = [0, 1, 3, 1, 2, 3];

        ReadOnlySpan<VertexAttribute> layout =
        [
            new(location: 0, componentCount: 3),
        ];

        var shader = CreateShader(DefaultVertexShader, DefaultFragmentShader);
        _shaders.Add(shader);

        var mesh = CreateMesh(vertices, indices, layout);
        _meshes.Add(mesh);

        _logger.LogInformation("Renderer initialized (OpenGL)");
    }

    public override void Render(double deltaTime)
    {
        _gl!.ClearColor(Color.AliceBlue);
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        _shaders[0].Use();
        _meshes[0].Draw();
    }

    public override void Shutdown()
    {
        foreach (var mesh in _meshes)
            mesh.Dispose();
        _meshes.Clear();

        foreach (var shader in _shaders)
            shader.Dispose();
        _shaders.Clear();

        _gl?.Dispose();
        _gl = null;

        _logger.LogInformation("Renderer shut down (OpenGL)");
    }

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        return OpenGLMesh.Create(_gl!, vertices, indices, attributes);
    }

    public override ShaderProgram CreateShader(string vertexSource, string fragmentSource)
    {
        return OpenGLShaderProgram.Create(_gl!, vertexSource, fragmentSource);
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

        return OpenGLShaderProgram.Create(_gl!, vertexSource, fragmentSource);
    }
}
