using Silk.NET.Direct3D12;
using Silk.NET.Maths;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// Wireframe variant of <see cref="D3D12ForwardPipeline"/>: same draw list, but
/// with <see cref="FillMode.Wireframe"/> selected for the scene pass (fill mode
/// lives in the PSO on D3D12, so the mode is just a per-draw PSO key here).
/// Restored to solid before the debug flush so lines rasterize normally.
/// </summary>
public sealed unsafe class D3D12WireframePipeline : ID3D12RenderPipeline
{
    private D3D12Renderer? _renderer;

    public string Name => "Wireframe";

    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f));

    public void Initialize(D3D12Renderer renderer) => _renderer = renderer;

    public void Execute(in D3D12RenderContext context)
    {
        var renderer = _renderer!;
        var list = renderer.CurrentList;

        // Latched on the main thread by the engine — GLFW forbids querying the
        // window's framebuffer size from this (render) thread.
        Vector2D<int> size = context.Renderer.FramebufferSize;
        renderer.SetViewportAndScissor(size.X, size.Y);

        // Clear to black for contrast against the wireframe lines.
        var rtv = context.BackBufferRtv;
        var dsv = context.DepthView;
        float* clearColor = stackalloc float[4] { 0f, 0f, 0f, 1f };
        list->ClearRenderTargetView(rtv, clearColor, 0, null);
        list->ClearDepthStencilView(dsv, ClearFlags.Depth | ClearFlags.Stencil, 1.0f, 0, 0, null);
        list->OMSetRenderTargets(1, &rtv, 0, &dsv);

        if (context.Scene is null) return;

        var camera = context.Scene.Camera;
        if (size.Y > 0)
            camera.AspectRatio = (float)size.X / size.Y;

        renderer.CurrentFillMode = FillMode.Wireframe;
        DrawView(context.View, camera);
        renderer.CurrentFillMode = FillMode.Solid;

        renderer.FlushDebugDraw(camera);
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

        shader.SetUniform("uModel", model);
        shader.SetUniform("uView", camera.View);
        shader.SetUniform("uProjection", camera.Projection * D3D12Renderer.GlToD3dClipZ);
        shader.SetUniform("uLightDir", LightDirection);
        material.Apply();
        shader.Use();

        mesh.Draw();
    }

    public void Dispose() { }
}
