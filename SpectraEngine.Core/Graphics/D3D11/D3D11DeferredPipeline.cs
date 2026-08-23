using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// Deferred shading on D3D11. Mirrors <c>OpenGL.DeferredPipeline</c>
/// step-for-step; see it for what the two passes are and what they cost.
/// </summary>
public sealed unsafe class D3D11DeferredPipeline : ID3D11RenderPipeline
{
    private D3D11Renderer? _renderer;

    public string Name => "Deferred";

    /// <summary>Ambient light level, added to every surface regardless of the lights.</summary>
    public float Ambient { get; set; } = 0.05f;

    public void Initialize(D3D11Renderer renderer) => _renderer = renderer;

    public void Execute(in D3D11RenderContext context)
    {
        D3D11Renderer renderer = context.Renderer;
        if (context.Scene is null) return;

        GBuffer? gbuffer = renderer.EnsureGBuffer();
        if (gbuffer is null) return;

        ShaderProgram surfaceShader = renderer.EnsureGBufferShader();
        Camera camera = context.Scene.Camera;

        // DEPTH ONLY, and the colour attachments are deliberately not cleared.
        // The depth buffer is the coverage mask: the light pass returns the sky
        // wherever depth is still 1, so no attachment is ever read at a pixel
        // this frame did not write. Clearing them anyway would be five
        // full-screen writes per frame for a result nothing looks at, and on
        // D3D12 it is slower still, because a clear to a value other than the
        // one the resource was created with takes the unoptimised path and says
        // so once per attachment per frame.
        renderer.BeginPass(gbuffer.Targets, PassClear.DepthOnly);
        try
        {
            // From the PASS, not the window: the two are the same only while
            // every pass goes to the back buffer.
            if (renderer.PassAspectRatio is { } aspect)
                camera.AspectRatio = aspect;

            DrawView(context.View, camera, surfaceShader);
        }
        finally
        {
            renderer.EndPass();
        }

        renderer.DrawDeferredLightPass(gbuffer, context.View, camera, Ambient);
    }

    private static void DrawView(RenderView view, Camera camera, ShaderProgram shader)
    {
        IReadOnlyList<RenderItem> items = view.Items;
        for (int i = 0; i < items.Count; i++)
        {
            RenderItem item = items[i];
            if (item.Material is { } material)
                DrawRenderable(item.Mesh, material, item.World, camera, shader);
        }

        IReadOnlyList<RenderItem> worldItems = view.WorldItems;
        for (int i = 0; i < worldItems.Count; i++)
        {
            RenderItem item = worldItems[i];
            if (item.Material is { } material)
                DrawRenderable(item.Mesh, material, item.World, camera, shader);
        }
    }

    private static void DrawRenderable(
        Mesh mesh, Material material, Matrix4x4 model, Camera camera, ShaderProgram shader)
    {
        // Set first, bind last: this backend stages uniforms into a constant
        // shadow that Use() flushes. The opposite of the OpenGL order, and the
        // reason DrawFullscreen is per-backend too.
        shader.SetUniform("uModel", model);
        shader.SetUniform("uView", camera.View);
        shader.SetUniform("uProjection", camera.Projection * D3D11Renderer.GlToD3dClipZ);
        material.ApplyTo(shader);
        shader.Use();

        mesh.Draw();
    }

    public void Dispose() { }
}
