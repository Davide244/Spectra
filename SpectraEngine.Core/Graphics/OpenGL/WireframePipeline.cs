using Silk.NET.OpenGL;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;
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
        context.Renderer.BeginPass(context.Renderer.FrameTarget, PassClear.To(ClearColors.Wireframe));
        try
        {
            if (context.Scene is null)
                return;

            var camera = context.Scene.Camera;
            // From the PASS, not the window: the two are the same only while
            // every pass goes to the back buffer.
            if (context.Renderer.PassAspectRatio is { } aspect)
                camera.AspectRatio = aspect;

            // Polygon mode is per-rasterizer state — flip into line mode for the
            // scene pass, restore so the debug overlay rasterizes normally (its
            // primitive is GL_LINES already, so polygon mode would otherwise be
            // irrelevant, but cull-face still applies to triangles).
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            gl.Disable(EnableCap.CullFace);

            DrawView(context.View, camera);

            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            gl.Enable(EnableCap.CullFace);

            _renderer!.FlushDebugDraw(camera);
        }
        finally
        {
            context.Renderer.EndPass();
        }
    }

    // Draws the engine-built view: the flat, frustum-culled item list replaces
    // the recursive scene walk that used to live here (the walk now happens
    // once per frame in Scene.BuildRenderView, shared by every backend).
    private void DrawView(RenderView view, Camera camera)
    {
        IReadOnlyList<RenderItem> items = view.Items;
        for (int i = 0; i < items.Count; i++)
        {
            RenderItem item = items[i];
            if (item.Material is { } material)
                DrawRenderable(item.Mesh, material, item.World, camera);
        }

        // The derived static world's chunks arrive pre-culled like the items,
        // one item per (chunk, material) with the material already resolved by
        // the swap; chunk meshes are already in world space, so each draws with
        // the identity model matrix its item carries.
        IReadOnlyList<RenderItem> worldItems = view.WorldItems;
        for (int i = 0; i < worldItems.Count; i++)
        {
            RenderItem item = worldItems[i];
            if (item.Material is { } material)
                DrawRenderable(item.Mesh, material, item.World, camera);
        }
    }

    private void DrawRenderable(Mesh mesh, Material material, Matrix4x4 model, Camera camera)
    {
        // A material with no program (the fallback built before a renderer had
        // one, or a shader that failed to resolve) is skipped rather than
        // dereferenced: one bad material must not take the frame down.
        if (material.Shader is not { } shader) return;

        shader.Use();
        shader.SetUniform("uModel", model);
        shader.SetUniform("uView", camera.View);
        shader.SetUniform("uProjection", camera.Projection);
        shader.SetUniform("uLightDir", LightDirection);
        material.Apply();

        mesh.Draw();
    }

    public void Dispose()
    {
    }
}
