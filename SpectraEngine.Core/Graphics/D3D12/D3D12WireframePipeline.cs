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

    /// <summary>Ambient light level, added to every surface regardless of the lights.</summary>
    public float Ambient { get; set; } = 0.05f;

    public void Initialize(D3D12Renderer renderer) => _renderer = renderer;

    public void Execute(in D3D12RenderContext context)
    {
        var renderer = _renderer!;

        // Clear to black for contrast against the wireframe lines.
        renderer.BeginPass(renderer.FrameTarget, PassClear.To(ClearColors.Wireframe));
        try
        {
            if (context.Scene is null) return;

            var camera = context.Scene.Camera;
            // From the PASS, not the window: the two are the same only while
            // every pass goes to the back buffer.
            if (renderer.PassAspectRatio is { } aspect)
                camera.AspectRatio = aspect;

            renderer.CurrentFillMode = FillMode.Wireframe;
            DrawView(context.View, camera);
            renderer.CurrentFillMode = FillMode.Solid;

            // The world-line lane, INSIDE this pass, because this pass owns the
            // scene's depth. A ground grid is world content and must be
            // occluded by the geometry it lies under; the depth-off overlay
            // that carries gizmo handles would draw it straight through walls.
            renderer.FlushWorldLines(camera, gbuffer: false);
        }
        finally
        {
            renderer.EndPass();
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

        shader.SetUniform("uModel", model);
        shader.SetUniform("uView", camera.View);
        shader.SetUniform("uProjection", camera.Projection * D3D12Renderer.GlToD3dClipZ);
        LightUpload.Apply(shader, view, Ambient);
        material.Apply();
        shader.Use();

        mesh.Draw();
    }

    public void Dispose() { }
}
