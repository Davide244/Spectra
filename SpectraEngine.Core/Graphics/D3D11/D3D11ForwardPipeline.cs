using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Maths;
using SpectraEngine.Core.Scene;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// Default D3D11 rendering strategy: clear, walk the scene tree drawing each
/// mesh with its material's shader, then flush the debug-draw overlay.
/// Mirrors <c>OpenGL.ForwardPipeline</c> step-for-step.
/// </summary>
public sealed unsafe class D3D11ForwardPipeline : ID3D11RenderPipeline
{
    private D3D11Renderer? _renderer;

    public string Name => "Forward";

    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f));

    public void Initialize(D3D11Renderer renderer) => _renderer = renderer;

    public void Execute(in D3D11RenderContext context)
    {
        var ctx = (ID3D11DeviceContext*)context.Context.Handle;
        var rtvPtr = (ID3D11RenderTargetView*)context.BackBufferRtv.Handle;
        var dsvPtr = (ID3D11DepthStencilView*)context.DepthView.Handle;

        Vector2D<int> size = context.Window.FramebufferSize;
        var viewport = new Viewport
        {
            TopLeftX = 0, TopLeftY = 0,
            Width = size.X, Height = size.Y,
            MinDepth = 0f, MaxDepth = 1f,
        };
        ctx->RSSetViewports(1, &viewport);

        // Same colour as OpenGL's CornflowerBlue so swapping backends looks
        // visually identical when the geometry is unchanged.
        Span<float> clearColor = stackalloc float[4] { 0.392f, 0.584f, 0.929f, 1f };
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

        DrawNode(context.Scene.Root, camera);

        _renderer!.FlushDebugDraw(camera);
    }

    private void DrawNode(SceneNode node, Camera camera)
    {
        if (node.MeshRenderer is { } meshRenderer)
        {
            var material = meshRenderer.Material;
            var shader = material.Shader;

            // Same shader uniforms as the OpenGL forward path, only the
            // projection needs the GL→D3D Z remap before being uploaded.
            shader.SetUniform("uModel", node.WorldMatrix);
            shader.SetUniform("uView", camera.View);
            shader.SetUniform("uProjection", camera.Projection * D3D11Renderer.GlToD3dClipZ);
            shader.SetUniform("uLightDir", LightDirection);
            material.Apply();
            shader.Use();

            meshRenderer.Mesh.Draw();
        }

        var children = node.Children;
        for (int i = 0; i < children.Count; i++)
            DrawNode(children[i], camera);
    }

    public void Dispose() { }
}
