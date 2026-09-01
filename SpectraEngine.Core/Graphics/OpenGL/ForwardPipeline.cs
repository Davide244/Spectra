using Silk.NET.OpenGL;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// The default rendering strategy: clear, draw the frame's pre-culled
/// <see cref="RenderView"/> items with their materials' shaders, then flush
/// the debug-draw overlay with depth-test off so wires sit on top of geometry.
/// </summary>
public sealed class ForwardPipeline : IOpenGLRenderPipeline
{
    private OpenGLRenderer? _renderer;

    public string Name => "Forward";

    /// <summary>
    /// Ambient light level, added to every surface regardless of the lights.
    /// </summary>
    /// <remarks>
    /// A uniform rather than the constant it used to be in the shader: with
    /// more than one light, a floor that every light stacks on top of makes
    /// the scene brighter with each light added even where none of them
    /// reach.
    /// </remarks>
    public float Ambient { get; set; } = 0.18f;

    public void Initialize(OpenGLRenderer renderer)
    {
        _renderer = renderer;
    }

    public void Execute(in OpenGLRenderContext context)
    {
        // The clear colour is linear because the target encodes; see ClearColors.
        context.Renderer.BeginPass(context.Renderer.FrameTarget, PassClear.To(ClearColors.Sky));
        try
        {
            if (context.Scene is null)
                return;

            var camera = context.Scene.Camera;
            // From the PASS, not the window: the two are the same only while
            // every pass goes to the back buffer.
            if (context.Renderer.PassAspectRatio is { } aspect)
                camera.AspectRatio = aspect;

            DrawView(context.View, camera);

            // The world-line lane, INSIDE this pass, because this pass owns the
            // scene's depth. A ground grid is world content and must be
            // occluded by the geometry it lies under; the depth-off overlay
            // that carries gizmo handles would draw it straight through walls.
            context.Renderer.FlushWorldLines(camera, gbuffer: false);
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
                DrawRenderable(item.Mesh, material, item.World, camera, view);
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
                DrawRenderable(item.Mesh, material, item.World, camera, view);
        }
    }

    private void DrawRenderable(Mesh mesh, Material material, Matrix4x4 model, Camera camera, RenderView view)
    {
        // A material with no program (the fallback built before a renderer had
        // one, or a shader that failed to resolve) is skipped rather than
        // dereferenced: one bad material must not take the frame down.
        if (material.Shader is not { } shader) return;

        shader.Use();
        shader.SetUniform("uModel", model);
        shader.SetUniform("uView", camera.View);
        shader.SetUniform("uProjection", camera.Projection);
        LightUpload.Apply(shader, view, Ambient);
        material.Apply();

        mesh.Draw();
    }

    public void Dispose()
    {
    }
}
