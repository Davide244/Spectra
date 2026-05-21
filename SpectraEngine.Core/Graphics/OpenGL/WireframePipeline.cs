using Silk.NET.OpenGL;
using SpectraEngine.Core.Scene;
using System.Drawing;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// Draws every mesh as wireframe by switching the polygon mode to
/// <c>GL_LINE</c> for the scene pass. Useful as a diagnostic and as a clear
/// demonstration that the render pipeline can be swapped at runtime.
/// </summary>
public sealed class WireframePipeline : IOpenGLRenderPipeline
{
    private OpenGLRenderer? _renderer;

    public string Name => "Wireframe";

    /// <summary>Direction the single scene light comes from; mostly visible at silhouette edges.</summary>
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

        gl.ClearColor(Color.Black);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        if (context.Scene is null)
            return;

        var camera = context.Scene.Camera;
        if (size.Y > 0)
            camera.AspectRatio = size.X / (float)size.Y;

        // Polygon mode is per-rasterizer state — flip into line mode for the
        // scene pass, restore so the debug overlay rasterizes normally (its
        // primitive is GL_LINES already, so polygon mode would otherwise be
        // irrelevant, but cull-face still applies to triangles).
        gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
        gl.Disable(EnableCap.CullFace);

        DrawNode(gl, context.Scene.Root, camera);

        gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        gl.Enable(EnableCap.CullFace);

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
