using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System;

namespace SpectraEngine.Core.Graphics.D3D12;

/// <summary>
/// An upload-heap buffer of per-instance data, its vertex buffer view, and the
/// combined two-slot layout the PSO is compiled against.
/// </summary>
/// <remarks>
/// <para>
/// <b>D3D12 has no standalone input layout object: it lives inside the PSO.</b>
/// So an instanced draw does not bind a different layout, it selects a different
/// pipeline, and the layout has to reach <c>GetPso</c> as part of the key. That
/// is why the combined layout is built here and handed over at draw time, and
/// why <c>D3D12VertexLayout.Element</c> carries the slot and the rate: the PSO
/// cache compares elements structurally, so an instanced layout produces a
/// distinct pipeline without the cache having to learn a new concept.
/// </para>
/// <para>
/// <b>Persistently mapped, unlike a mesh.</b> A mesh is written once at
/// creation; this is rewritten every frame, and mapping and unmapping an upload
/// resource per frame is pure overhead on a heap that is CPU-visible for its
/// whole life. The read range stays empty because nothing reads it back.
/// </para>
/// </remarks>
internal sealed unsafe class D3D12InstanceBuffer : InstanceBuffer
{
    private ComPtr<ID3D12Resource> _buffer;
    private void* _mapped;
    private bool _disposed;

    /// <summary>The combined slot-0 + slot-1 layout, for the PSO key.</summary>
    internal D3D12VertexLayout CombinedLayout { get; }

    /// <summary>The view binding this buffer into slot 1.</summary>
    internal VertexBufferView View { get; }

    internal D3D12InstanceBuffer(
        ComPtr<ID3D12Device> device,
        int capacityInstances,
        ReadOnlySpan<VertexAttribute> vertexAttributes,
        ReadOnlySpan<VertexAttribute> instanceAttributes,
        int floatsPerInstance)
    {
        Capacity = capacityInstances;
        FloatsPerInstance = floatsPerInstance;

        uint stride = (uint)(floatsPerInstance * sizeof(float));
        uint bytes = (uint)(capacityInstances * stride);

        CombinedLayout = BuildCombinedLayout(vertexAttributes, instanceAttributes);

        var heap = new HeapProperties { Type = HeapType.Upload };
        var desc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,
            Width = bytes,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutRowMajor,
            Flags = ResourceFlags.None,
        };

        ID3D12Resource* res = null;
        SilkMarshal.ThrowHResult(((ID3D12Device*)device.Handle)->CreateCommittedResource(
            &heap, HeapFlags.None, &desc, ResourceStates.GenericRead, null,
            SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)&res));
        _buffer = ComOwnership.Own(res);

        var readRange = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
        void* mapped = null;
        SilkMarshal.ThrowHResult(((ID3D12Resource*)_buffer.Handle)->Map(0, &readRange, &mapped));
        _mapped = mapped;

        View = new VertexBufferView
        {
            BufferLocation = ((ID3D12Resource*)_buffer.Handle)->GetGPUVirtualAddress(),
            SizeInBytes = bytes,
            StrideInBytes = stride,
        };
    }

    private static D3D12VertexLayout BuildCombinedLayout(
        ReadOnlySpan<VertexAttribute> vertexAttributes,
        ReadOnlySpan<VertexAttribute> instanceAttributes)
    {
        var elements = new D3D12VertexLayout.Element[vertexAttributes.Length + instanceAttributes.Length];
        int next = 0;

        uint offset = 0;
        for (int i = 0; i < vertexAttributes.Length; i++)
        {
            elements[next++] = new D3D12VertexLayout.Element(
                vertexAttributes[i].Location, FormatFor(vertexAttributes[i].ComponentCount), offset,
                VertexAttribute.VertexSlot, PerInstance: false);
            offset += vertexAttributes[i].ComponentCount * sizeof(float);
        }

        // The vertex stride, kept as the layout's own: slot 1's stride travels
        // on the vertex buffer view instead, and a layout has room for one.
        uint vertexStride = offset;

        // Offsets restart at zero, because they are offsets within slot 1.
        offset = 0;
        for (int i = 0; i < instanceAttributes.Length; i++)
        {
            elements[next++] = new D3D12VertexLayout.Element(
                instanceAttributes[i].Location, FormatFor(instanceAttributes[i].ComponentCount), offset,
                VertexAttribute.InstanceSlot, PerInstance: true);
            offset += instanceAttributes[i].ComponentCount * sizeof(float);
        }

        return new D3D12VertexLayout(elements, vertexStride);
    }

    private static Format FormatFor(uint componentCount) => componentCount switch
    {
        1 => Format.FormatR32Float,
        2 => Format.FormatR32G32Float,
        3 => Format.FormatR32G32B32Float,
        4 => Format.FormatR32G32B32A32Float,
        _ => throw new ArgumentOutOfRangeException(
            nameof(componentCount), $"Unsupported component count {componentCount}"),
    };

    /// <inheritdoc/>
    public override void Update(ReadOnlySpan<float> data, int instanceCount)
    {
        ValidateUpdate(data, instanceCount);
        if (instanceCount == 0 || _mapped is null)
            return;

        fixed (float* src = data)
            System.Buffer.MemoryCopy(src, _mapped, View.SizeInBytes, (long)data.Length * sizeof(float));
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_mapped is not null && _buffer.Handle is not null)
        {
            ((ID3D12Resource*)_buffer.Handle)->Unmap(0, null);
            _mapped = null;
        }
        ComOwnership.Release(ref _buffer);
    }
}
