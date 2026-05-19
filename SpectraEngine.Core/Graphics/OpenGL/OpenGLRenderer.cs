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

    private Vector3 _lightDirection = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f));

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

        DefaultShader = CreateShader(DefaultVertexShader, DefaultFragmentShader);

        _logger.LogInformation("Renderer initialized (OpenGL)");
    }

    public override void Render(Scene.Scene? scene, double deltaTime)
    {
        // The framebuffer size is applied here every frame rather than from a
        // resize event: this runs on the render thread where the GL context is
        // current, while resize events arrive on the OS-event thread.
        var size = _window!.FramebufferSize;
        _gl!.Viewport(0, 0, (uint)size.X, (uint)size.Y);

        _gl.ClearColor(Color.CornflowerBlue);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        if (scene is null)
            return;

        var camera = scene.Camera;
        if (size.Y > 0)
            camera.AspectRatio = size.X / (float)size.Y;

        DrawNode(scene.Root, camera);
    }

    private void DrawNode(SceneNode node, Camera camera)
    {
        if (node.MeshRenderer is { } meshRenderer)
        {
            var material = meshRenderer.Material;
            var shader = material.Shader;

            shader.Use();
            shader.SetUniform("uModel", node.WorldMatrix);
            shader.SetUniform("uView", camera.View);
            shader.SetUniform("uProjection", camera.Projection);
            shader.SetUniform("uLightDir", _lightDirection);
            material.Apply();

            meshRenderer.Mesh.Draw();
        }

        var children = node.Children;
        for (int i = 0; i < children.Count; i++)
            DrawNode(children[i], camera);
    }

    public override void Shutdown()
    {
        foreach (var mesh in _meshes)
            mesh.Dispose();
        _meshes.Clear();

        foreach (var shader in _shaders)
            shader.Dispose();
        _shaders.Clear();
        DefaultShader = null;

        _gl?.Dispose();
        _gl = null;

        _logger.LogInformation("Renderer shut down (OpenGL)");
    }

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        var mesh = OpenGLMesh.Create(_gl!, vertices, indices, attributes);
        _meshes.Add(mesh);
        return mesh;
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
