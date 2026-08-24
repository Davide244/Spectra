using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// Draws interleaved (position + colour) debug line vertices as a line list.
/// Vertices are written into fresh slices of the renderer's frame upload ring
/// each call, so no per-batch buffer management or lifetime tracking is needed.
/// Matches the D3D11/OpenGL line batch contract.
/// </summary>
internal sealed unsafe class D3D12LineBatch : IDisposable
{
    private const int FloatsPerVertex = 6;
    private const uint StrideBytes = FloatsPerVertex * sizeof(float);

    private static readonly D3D12VertexLayout LineLayout = new(
        [
            new D3D12VertexLayout.Element(0, Format.FormatR32G32B32Float, 0),
            new D3D12VertexLayout.Element(1, Format.FormatR32G32B32Float, 12),
        ],
        StrideBytes);

    private readonly D3D12Renderer _renderer;
    private readonly D3D12ShaderProgram _shader;

    public D3D12LineBatch(D3D12Renderer renderer, D3D12ShaderProgram shader)
    {
        _renderer = renderer;
        _shader = shader;
    }

    public void Draw(ReadOnlySpan<float> interleaved, uint vertexCount)
    {
        if (vertexCount == 0) return;
        var list = _renderer.CurrentList;
        if (list is null) return;

        uint byteSize = vertexCount * StrideBytes;
        var slice = _renderer.AllocUpload(byteSize, 4);
        fixed (float* src = interleaved)
        {
            System.Buffer.MemoryCopy(src, slice.Cpu, byteSize, byteSize);
        }

        // Debug lines always rasterize solid, regardless of the active pipeline.
        // DepthMode.None is what makes editor overlays draw on top of the
        // geometry they describe, which is the only way what you can see stays
        // what you can pick. It used to be a flag set once on the shader; it is
        // now stated at the draw, where it is true.
        // The pass decides the target configuration, not this draw: a PSO
        // compiled for the back buffer is invalid for an offscreen target with
        // a different format, and D3D12 validates the pair at draw time.
        var target = _renderer.CurrentTargetState;
        var pso = _shader.GetPso(
            LineLayout, FillMode.Solid, PrimitiveTopologyType.Line,
            DepthMode.None, BlendMode.Opaque, DepthBias.None, in target);
        list->SetPipelineState(pso);
        list->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);

        var view = new VertexBufferView
        {
            BufferLocation = slice.GpuVa,
            SizeInBytes = byteSize,
            StrideInBytes = StrideBytes,
        };
        list->IASetVertexBuffers(0, 1, &view);
        list->DrawInstanced(vertexCount, 1, 0, 0);
    }

    public void Dispose()
    {
    }
}
