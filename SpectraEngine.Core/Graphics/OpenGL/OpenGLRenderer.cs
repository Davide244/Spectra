using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraEngine.Core.Scene;
using System;
using System.Drawing;
using System.Numerics;
using System.Text;

namespace SpectraEngine.Core.Graphics.OpenGL;

public class OpenGLRenderer : Renderer
{
    private GL? _gl;
    private IWindow? _window;
    private readonly List<Mesh> _meshes = [];
    private readonly List<ShaderProgram> _shaders = [];

    private Camera? _camera;
    private Transform _cubeTransform = Transform.Identity;
    private double _elapsed;

    public override GraphicsBackend Backend => GraphicsBackend.OpenGL;

    private const string DefaultVertexShader = """
        #version 330 core

        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec3 aNormal;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProjection;

        out vec3 vNormal;

        void main()
        {
            vNormal = mat3(uModel) * aNormal;
            gl_Position = uProjection * uView * uModel * vec4(aPosition, 1.0);
        }
        """;

    private const string DefaultFragmentShader = """
        #version 330 core

        in vec3 vNormal;
        out vec4 out_color;

        uniform vec3 uLightDir;
        uniform vec3 uBaseColor;

        void main()
        {
            vec3 n = normalize(vNormal);
            float ndotl = max(dot(n, normalize(-uLightDir)), 0.0);
            vec3 ambient = uBaseColor * 0.2;
            vec3 diffuse = uBaseColor * ndotl;
            out_color = vec4(ambient + diffuse, 1.0);
        }
        """;

    public OpenGLRenderer(ILogger<Renderer> logger) : base(logger)
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

        var size = window.FramebufferSize;
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        window.FramebufferResize += OnFramebufferResize;

        _camera = new Camera
        {
            Position = new Vector3(0f, 1.5f, 4f),
            AspectRatio = size.X / (float)size.Y,
        };
        _camera.LookAt(Vector3.Zero);

        var (cubeVertices, cubeIndices) = CreateCubeData();

        ReadOnlySpan<VertexAttribute> layout =
        [
            new(location: 0, componentCount: 3),
            new(location: 1, componentCount: 3),
        ];

        var shader = CreateShader(DefaultVertexShader, DefaultFragmentShader);
        _shaders.Add(shader);

        var mesh = CreateMesh(cubeVertices, cubeIndices, layout);
        _meshes.Add(mesh);

        _logger.LogInformation("Renderer initialized (OpenGL)");
    }

    public override void Render(double deltaTime)
    {
        _elapsed += deltaTime;

        _gl!.ClearColor(Color.CornflowerBlue);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        _cubeTransform.Rotation = Quaternion.CreateFromYawPitchRoll(
            (float)_elapsed * 0.6f,
            (float)_elapsed * 0.4f,
            0f);

        var shader = _shaders[0];
        shader.Use();
        shader.SetUniform("uModel", _cubeTransform.Model);
        shader.SetUniform("uView", _camera!.View);
        shader.SetUniform("uProjection", _camera.Projection);
        shader.SetUniform("uLightDir", Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f)));
        shader.SetUniform("uBaseColor", new Vector3(1f, 0.55f, 0.2f));

        _meshes[0].Draw();
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        _gl!.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        if (_camera is not null && size.Y > 0)
            _camera.AspectRatio = size.X / (float)size.Y;
    }

    public override void Shutdown()
    {
        if (_window is not null)
            _window.FramebufferResize -= OnFramebufferResize;

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

    private static (float[] vertices, uint[] indices) CreateCubeData()
    {
        float[] vertices =
        [
            -0.5f, -0.5f,  0.5f,   0f,  0f,  1f,
             0.5f, -0.5f,  0.5f,   0f,  0f,  1f,
             0.5f,  0.5f,  0.5f,   0f,  0f,  1f,
            -0.5f,  0.5f,  0.5f,   0f,  0f,  1f,

             0.5f, -0.5f, -0.5f,   0f,  0f, -1f,
            -0.5f, -0.5f, -0.5f,   0f,  0f, -1f,
            -0.5f,  0.5f, -0.5f,   0f,  0f, -1f,
             0.5f,  0.5f, -0.5f,   0f,  0f, -1f,

            -0.5f, -0.5f, -0.5f,  -1f,  0f,  0f,
            -0.5f, -0.5f,  0.5f,  -1f,  0f,  0f,
            -0.5f,  0.5f,  0.5f,  -1f,  0f,  0f,
            -0.5f,  0.5f, -0.5f,  -1f,  0f,  0f,

             0.5f, -0.5f,  0.5f,   1f,  0f,  0f,
             0.5f, -0.5f, -0.5f,   1f,  0f,  0f,
             0.5f,  0.5f, -0.5f,   1f,  0f,  0f,
             0.5f,  0.5f,  0.5f,   1f,  0f,  0f,

            -0.5f,  0.5f,  0.5f,   0f,  1f,  0f,
             0.5f,  0.5f,  0.5f,   0f,  1f,  0f,
             0.5f,  0.5f, -0.5f,   0f,  1f,  0f,
            -0.5f,  0.5f, -0.5f,   0f,  1f,  0f,

            -0.5f, -0.5f, -0.5f,   0f, -1f,  0f,
             0.5f, -0.5f, -0.5f,   0f, -1f,  0f,
             0.5f, -0.5f,  0.5f,   0f, -1f,  0f,
            -0.5f, -0.5f,  0.5f,   0f, -1f,  0f,
        ];

        uint[] indices = new uint[36];
        for (uint face = 0; face < 6; face++)
        {
            uint b = face * 4;
            uint i = face * 6;
            indices[i + 0] = b + 0;
            indices[i + 1] = b + 1;
            indices[i + 2] = b + 2;
            indices[i + 3] = b + 0;
            indices[i + 4] = b + 2;
            indices[i + 5] = b + 3;
        }

        return (vertices, indices);
    }
}
