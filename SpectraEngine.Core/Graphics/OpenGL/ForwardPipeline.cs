using Silk.NET.OpenGL;
using SpectraEngine.Core.Scene;
using System.Drawing;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// The default rendering strategy: clear, walk the scene tree drawing each
/// mesh with its material's shader, then flush the debug-draw overlay with
/// depth-test off so wires sit on top of geometry.
/// </summary>
public sealed class ForwardPipeline : IOpenGLRenderPipeline
{
    private OpenGLRenderer? _renderer;

    public string Name => "Forward";

    /// <summary>Direction the single scene light comes <em>from</em>, in world space.</summary>
    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f));

    public void Initialize(OpenGLRenderer renderer)
    {
        _renderer = renderer;
    }

    public void Execute(in OpenGLRenderContext context)
    {
        var gl = context.Gl;
        var size = context.Window.FramebufferSize;
        gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);

        gl.ClearColor(Color.CornflowerBlue);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        if (context.Scene is null)
            return;

        var camera = context.Scene.Camera;
        if (size.Y > 0)
            camera.AspectRatio = size.X / (float)size.Y;

        DrawNode(gl, context.Scene.Root, camera);

        _renderer!.FlushDebugDraw(camera);
    }

    private void DrawNode(GL gl, SceneNode node, Camera camera)
    {
        if (node.MeshRenderer is { } meshRenderer)
        {
            var material = meshRenderer.Material;
            var shader = material.Shader;

            shader.Use();
            shader.SetUniform("uModel", node.WorldMatrix);
            shader.SetUniform("uView", camera.View);
            shader.SetUniform("uProjection", camera.Projection);
            shader.SetUniform("uLightDir", LightDirection);
            material.Apply();

            meshRenderer.Mesh.Draw();
        }

        var children = node.Children;
        for (int i = 0; i < children.Count; i++)
            DrawNode(gl, children[i], camera);
    }

    public void Dispose()
    {
    }
}
