using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using SpectraEngine.Core.Bsp;
using System;
using System.Numerics;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// Vertex + index buffers on the upload heap with their D3D12 buffer views and
/// the mesh's <see cref="D3D12VertexLayout"/> (input layouts live in PSOs on
/// D3D12, so the layout travels with the mesh to the PSO cache at draw time).
/// Upload-heap placement trades a little GPU read bandwidth for not needing a
/// copy queue — fine at current scene sizes; revisit when meshes get large.
/// </summary>
internal sealed unsafe class D3D12Mesh : Mesh
{
    private readonly D3D12Renderer _renderer;
    private ComPtr<ID3D12Resource> _vertexBuffer;
    private ComPtr<ID3D12Resource> _indexBuffer;

    // The POOL bucket each buffer came from, which is what it must be returned
    // under: the buffer is at least this big and usually bigger than the data.
    private readonly uint _vertexCapacity;
    private readonly uint _indexCapacity;
    private readonly VertexBufferView _vbView;
    private readonly IndexBufferView _ibView;
    private bool _disposed;

    internal D3D12VertexLayout Layout { get; }

    internal D3D12Mesh(
        D3D12Renderer renderer,
        ReadOnlySpan<float> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes)
    {
        _renderer = renderer;

        var elements = new D3D12VertexLayout.Element[attributes.Length];
        uint offset = 0;
        for (int i = 0; i < attributes.Length; i++)
        {
            elements[i] = new D3D12VertexLayout.Element(
                attributes[i].Location,
                FormatFor(attributes[i].ComponentCount),
                offset);
            offset += attributes[i].ComponentCount * sizeof(float);
        }
        Layout = new D3D12VertexLayout(elements, offset);

        uint vbBytes = (uint)(vertices.Length * sizeof(float));
        uint ibBytes = (uint)(indices.Length * sizeof(uint));
        // Rented, not created. CreateCommittedResource costs about half a
        // millisecond per call on this backend, and the static-world compiler
        // frees and re-creates chunk meshes every frame a world brush moves.
        _vertexCapacity = D3D12Renderer.MeshBufferBucket(vbBytes);
        _indexCapacity = D3D12Renderer.MeshBufferBucket(ibBytes);
        _vertexBuffer = renderer.RentMeshBuffer(_vertexCapacity);
        _indexBuffer = renderer.RentMeshBuffer(_indexCapacity);
        CopyInto(_vertexBuffer, vertices, vbBytes);
        CopyInto(_indexBuffer, indices, ibBytes);

        _vbView = new VertexBufferView
        {
            BufferLocation = ((ID3D12Resource*)_vertexBuffer.Handle)->GetGPUVirtualAddress(),
            SizeInBytes = vbBytes,
            StrideInBytes = Layout.StrideBytes,
        };
        _ibView = new IndexBufferView
        {
            BufferLocation = ((ID3D12Resource*)_indexBuffer.Handle)->GetGPUVirtualAddress(),
            SizeInBytes = ibBytes,
            Format = Format.FormatR32Uint,
        };

        IndexCount = (uint)indices.Length;
        (Vector3[] positions, Vector3[] normals) = ExtractStreams(vertices, attributes);
        Positions = positions;
        Normals = normals;
        Indices = indices.ToArray();
        LocalBounds = ComputeBounds(positions);
    }

    private static void CopyInto<T>(ComPtr<ID3D12Resource> buffer, ReadOnlySpan<T> data, uint byteSize) where T : unmanaged
    {
        var res = (ID3D12Resource*)buffer.Handle;
        void* mapped = null;
        var readRange = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
        SilkMarshal.ThrowHResult(res->Map(0, &readRange, &mapped));
        fixed (T* src = data)
        {
            System.Buffer.MemoryCopy(src, mapped, byteSize, byteSize);
        }
        res->Unmap(0, null);
    }

    private static Format FormatFor(uint componentCount) => componentCount switch
    {
        1 => Format.FormatR32Float,
        2 => Format.FormatR32G32Float,
        3 => Format.FormatR32G32B32Float,
        4 => Format.FormatR32G32B32A32Float,
        _ => throw new ArgumentOutOfRangeException(nameof(componentCount), $"Unsupported component count {componentCount}"),
    };

    private static (Vector3[] Positions, Vector3[] Normals) ExtractStreams(
        ReadOnlySpan<float> vertices, ReadOnlySpan<VertexAttribute> attributes)
    {
        int stride = 0;
        int positionOffset = -1;
        int normalOffset = -1;
        for (int i = 0; i < attributes.Length; i++)
        {
            if (attributes[i].Location == 0) positionOffset = stride;
            else if (attributes[i].Location == 1) normalOffset = stride;
            stride += (int)attributes[i].ComponentCount;
        }

        if (positionOffset < 0 || stride == 0)
            return ([], []);

        int vertexCount = vertices.Length / stride;
        var positions = new Vector3[vertexCount];
        Vector3[] normals = normalOffset >= 0 ? new Vector3[vertexCount] : [];

        for (int i = 0; i < vertexCount; i++)
        {
            int b = i * stride;
            positions[i] = new Vector3(
                vertices[b + positionOffset],
                vertices[b + positionOffset + 1],
                vertices[b + positionOffset + 2]);
            if (normalOffset >= 0)
                normals[i] = new Vector3(
                    vertices[b + normalOffset],
                    vertices[b + normalOffset + 1],
                    vertices[b + normalOffset + 2]);
        }
        return (positions, normals);
    }

    private static Aabb ComputeBounds(Vector3[] positions)
    {
        if (positions.Length == 0) return new Aabb(Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < positions.Length; i++)
        {
            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }
        return new Aabb(min, max);
    }

    public override void Draw()
    {
        var list = _renderer.CurrentList;
        var program = _renderer.CurrentProgram;
        if (list is null || program is null) return;

        // From the open pass, not hardcoded: a PSO is compiled against the
        // render-target formats it will be bound with, so drawing the same mesh
        // into an offscreen target needs its own pipeline state.
        var target = _renderer.CurrentTargetState;
        var pso = program.GetPso(
            Layout, _renderer.CurrentFillMode, PrimitiveTopologyType.Triangle,
            _renderer.CurrentDepthMode, BlendMode.Opaque, _renderer.CurrentDepthBias, in target);
        _renderer.BindPipelineState(list, pso);
        _renderer.BindTopology(list, D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        fixed (VertexBufferView* vb = &_vbView)
        {
            list->IASetVertexBuffers(0, 1, vb);
        }
        fixed (IndexBufferView* ib = &_ibView)
        {
            list->IASetIndexBuffer(ib);
        }
        list->DrawIndexedInstanced(IndexCount, 1, 0, 0, 0);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Back to the pool rather than released: see D3D12Renderer's mesh
        // buffer pool for why this backend cannot afford to allocate them again.
        _renderer.ReturnMeshBuffer(_indexCapacity, _indexBuffer);
        _renderer.ReturnMeshBuffer(_vertexCapacity, _vertexBuffer);
        _indexBuffer = default;
        _vertexBuffer = default;
    }
}
