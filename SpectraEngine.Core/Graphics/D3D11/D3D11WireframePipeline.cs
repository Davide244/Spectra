using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Maths;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// Wireframe variant of <see cref="D3D11ForwardPipeline"/>: same draw list,
/// but with a wireframe rasterizer state (no fill, no cull) bound for the
/// scene pass. Restored to solid before debug flush so lines rasterize normally.
/// </summary>
public sealed unsafe class D3D11WireframePipeline : ID3D11RenderPipeline
{
    private D3D11Renderer? _renderer;
    private ComPtr<ID3D11RasterizerState> _wireframeState;
    private ComPtr<ID3D11RasterizerState> _solidState;

    public string Name => "Wireframe";

    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f));

    public void Initialize(D3D11Renderer renderer)
    {
        _renderer = renderer;

        var dev = (ID3D11Device*)renderer.Device.Handle;

        var wireDesc = new RasterizerDesc
        {
            FillMode = FillMode.Wireframe,
            CullMode = CullMode.None,
            FrontCounterClockwise = 1,
            DepthClipEnable = 1,
        };
        ID3D11RasterizerState* wire = null;
        SilkMarshal.ThrowHResult(dev->CreateRasterizerState(&wireDesc, &wire));
        _wireframeState = ComOwnership.Own(wire);

        var solidDesc = new RasterizerDesc
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.Back,
            FrontCounterClockwise = 1,
            DepthClipEnable = 1,
        };
        ID3D11RasterizerState* solid = null;
        SilkMarshal.ThrowHResult(dev->CreateRasterizerState(&solidDesc, &solid));
        _solidState = ComOwnership.Own(solid);
    }

    public void Execute(in D3D11RenderContext context)
    {
        var ctx = (ID3D11DeviceContext*)context.Context.Handle;
        var rtvPtr = (ID3D11RenderTargetView*)context.BackBufferRtv.Handle;
        var dsvPtr = (ID3D11DepthStencilView*)context.DepthView.Handle;

        // Latched on the main thread by the engine — GLFW forbids querying the
        // window's framebuffer size from this (render) thread.
        Vector2D<int> size = context.Renderer.FramebufferSize;
        var viewport = new Viewport
        {
            TopLeftX = 0, TopLeftY = 0,
            Width = size.X, Height = size.Y,
            MinDepth = 0f, MaxDepth = 1f,
        };
        ctx->RSSetViewports(1, &viewport);

        // Clear to black for contrast against the wireframe lines.
        Span<float> clearColor = stackalloc float[4] { 0f, 0f, 0f, 1f };
        fixed (float* pColor = clearColor)
        {
            ctx->ClearRenderTargetView(rtvPtr, pColor);
        }
        ctx->ClearDepthStencilView(dsvPtr, (uint)(ClearFlag.Depth | ClearFlag.Stencil), 1.0f, 0);

        ctx->OMSetRenderTargets(1, &rtvPtr, dsvPtr);

        if (context.Scene is null) return;

        var camera = context.Scene.Camera;
        if (size.Y > 0)
            camera.AspectRatio = (float)size.X / size.Y;

        ctx->RSSetState((ID3D11RasterizerState*)_wireframeState.Handle);
        DrawView(context.View, camera);
        ctx->RSSetState((ID3D11RasterizerState*)_solidState.Handle);

        _renderer!.FlushDebugDraw(camera);
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
        shader.SetUniform("uProjection", camera.Projection * D3D11Renderer.GlToD3dClipZ);
        shader.SetUniform("uLightDir", LightDirection);
        material.Apply();
        shader.Use();

        mesh.Draw();
    }

    public void Dispose()
    {
        _wireframeState.Dispose();
        _solidState.Dispose();
    }
}
