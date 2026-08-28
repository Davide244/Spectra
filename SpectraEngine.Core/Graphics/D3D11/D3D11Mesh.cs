using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;

namespace SpectraEngine.Core.Graphics.D3D11;

internal sealed unsafe class D3D11Mesh : Mesh
{
    // Allocated once; the semantic strings are immutable interned UTF-8 spans
    // so InputElementDesc can hold pointers to them across the layout call.
    private static readonly byte[] TexcoordSemantic = new byte[] { (byte)'T', (byte)'E', (byte)'X', (byte)'C', (byte)'O', (byte)'O', (byte)'R', (byte)'D', 0 };

    private readonly ComPtr<ID3D11Buffer> _vertexBuffer;
    private readonly ComPtr<ID3D11Buffer> _indexBuffer;
    private readonly ComPtr<ID3D11InputLayout> _inputLayout;
    private readonly uint _stride;
    private bool _disposed;

    // Cached for the immediate context, set in Create.
    private readonly ComPtr<ID3D11DeviceContext> _context;

    private D3D11Mesh(
        ComPtr<ID3D11DeviceContext> context,
        ComPtr<ID3D11Buffer> vb,
        ComPtr<ID3D11Buffer> ib,
        ComPtr<ID3D11InputLayout> layout,
        uint stride,
        uint indexCount)
    {
        _context = context;
        _vertexBuffer = vb;
        _indexBuffer = ib;
        _inputLayout = layout;
        _stride = stride;
        IndexCount = indexCount;
    }

    internal static D3D11Mesh Create(
        ComPtr<ID3D11Device> device,
        ReadOnlySpan<float> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes,
        ReadOnlyMemory<byte> vsBytecodeForLayout,
        MeshCpuAccess cpuAccess)
    {
        ComPtr<ID3D11Buffer> vb = CreateBuffer(device, vertices, BindFlag.VertexBuffer);
        ComPtr<ID3D11Buffer> ib = CreateBuffer(device, indices, BindFlag.IndexBuffer);
        ComPtr<ID3D11InputLayout> layout = CreateInputLayout(device, attributes, vsBytecodeForLayout);

        uint stride = 0;
        for (int i = 0; i < attributes.Length; i++)
            stride += attributes[i].ComponentCount * sizeof(float);

        // GetImmediateContext hands out a counted reference like any Create*
        // call, so it is Own'd (not wrapped) and released by Dispose below.
        ID3D11DeviceContext* ctxPtr = null;
        ((ID3D11Device*)device.Handle)->GetImmediateContext(&ctxPtr);
        var ctx = ComOwnership.Own(ctxPtr);

        var mesh = new D3D11Mesh(ctx, vb, ib, layout, stride, (uint)indices.Length);
        mesh.InitializeCpuData(vertices, indices, attributes, cpuAccess);
        return mesh;
    }

    private static ComPtr<ID3D11Buffer> CreateBuffer<T>(ComPtr<ID3D11Device> device, ReadOnlySpan<T> data, BindFlag bind) where T : unmanaged
    {
        var desc = new BufferDesc
        {
            ByteWidth = (uint)(data.Length * sizeof(T)),
            Usage = Usage.Immutable,
            BindFlags = (uint)bind,
            CPUAccessFlags = 0,
            MiscFlags = 0,
            StructureByteStride = 0,
        };

        ID3D11Buffer* bufPtr = null;
        fixed (T* p = data)
        {
            var init = new SubresourceData { PSysMem = p };
            SilkMarshal.ThrowHResult(((ID3D11Device*)device.Handle)->CreateBuffer(&desc, &init, &bufPtr));
        }
        return ComOwnership.Own(bufPtr);
    }

    private static ComPtr<ID3D11InputLayout> CreateInputLayout(
        ComPtr<ID3D11Device> device,
        ReadOnlySpan<VertexAttribute> attributes,
        ReadOnlyMemory<byte> vsBytecode)
    {
        // SpectraShade's HLSL generator emits vertex inputs as TEXCOORD0..N,
        // where N is the attribute's Location. Match that exactly here.
        Span<InputElementDesc> elements = stackalloc InputElementDesc[attributes.Length];

        fixed (byte* semName = TexcoordSemantic)
        {
            uint offset = 0;
            for (int i = 0; i < attributes.Length; i++)
            {
                elements[i] = new InputElementDesc
                {
                    SemanticName = semName,
                    SemanticIndex = attributes[i].Location,
                    Format = FormatFor(attributes[i].ComponentCount),
                    InputSlot = 0,
                    AlignedByteOffset = offset,
                    InputSlotClass = InputClassification.PerVertexData,
                    InstanceDataStepRate = 0,
                };
                offset += attributes[i].ComponentCount * sizeof(float);
            }

            using var bytecodePin = vsBytecode.Pin();
            ID3D11InputLayout* layoutPtr = null;
            fixed (InputElementDesc* pElements = elements)
            {
                SilkMarshal.ThrowHResult(((ID3D11Device*)device.Handle)->CreateInputLayout(
                    pElements,
                    (uint)attributes.Length,
                    bytecodePin.Pointer,
                    (nuint)vsBytecode.Length,
                    &layoutPtr));
            }
            return ComOwnership.Own(layoutPtr);
        }
    }

    private static Format FormatFor(uint componentCount) => componentCount switch
    {
        1 => Format.FormatR32Float,
        2 => Format.FormatR32G32Float,
        3 => Format.FormatR32G32B32Float,
        4 => Format.FormatR32G32B32A32Float,
        _ => throw new ArgumentOutOfRangeException(nameof(componentCount), $"Unsupported component count {componentCount}"),
    };

    public override void Draw()
    {
        var ctx = (ID3D11DeviceContext*)_context.Handle;
        ID3D11Buffer* vb = (ID3D11Buffer*)_vertexBuffer.Handle;
        uint stride = _stride;
        uint offset = 0;
        ctx->IASetInputLayout((ID3D11InputLayout*)_inputLayout.Handle);
        ctx->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        ctx->IASetIndexBuffer((ID3D11Buffer*)_indexBuffer.Handle, Silk.NET.DXGI.Format.FormatR32Uint, 0);
        ctx->IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        ctx->DrawIndexed(IndexCount, 0, 0);
    }

    /// <inheritdoc/>
    public override void DrawInstanced(InstanceBuffer instances, int instanceCount, int firstInstance = 0)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (instanceCount <= 0)
            return;

        if (instances is not D3D11InstanceBuffer d3d)
            throw new ArgumentException("Instance buffer belongs to another backend.", nameof(instances));

        var ctx = (ID3D11DeviceContext*)_context.Handle;

        // Two buffers into two slots, described by the instance buffer's
        // combined layout rather than this mesh's slot-0-only one.
        ID3D11Buffer** buffers = stackalloc ID3D11Buffer*[2];
        buffers[0] = (ID3D11Buffer*)_vertexBuffer.Handle;
        buffers[1] = d3d.Buffer;

        uint* strides = stackalloc uint[2] { _stride, d3d.Stride };
        uint* offsets = stackalloc uint[2] { 0, 0 };

        ctx->IASetInputLayout(d3d.Layout);
        ctx->IASetVertexBuffers(0, 2, buffers, strides, offsets);
        ctx->IASetIndexBuffer((ID3D11Buffer*)_indexBuffer.Handle, Format.FormatR32Uint, 0);
        ctx->IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        ctx->DrawIndexedInstanced(IndexCount, (uint)instanceCount, 0, 0, (uint)firstInstance);

        // Slot 1 is left bound, which is harmless for a draw that ignores it but
        // not for the debug layer: an input layout naming only slot 0 with a
        // stale buffer still bound is a warning per draw into the same info
        // queue the engine reads for real errors.
        ID3D11Buffer* none = null;
        uint zero = 0;
        ctx->IASetVertexBuffers(VertexAttribute.InstanceSlot, 1, &none, &zero, &zero);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Every one of these owns exactly one reference (see ComOwnership), so
        // disposing here is what actually frees the GPU memory — and it has to
        // happen, because the static-world recompile destroys and recreates
        // chunk meshes continuously while brushes are edited.
        _inputLayout.Dispose();
        _indexBuffer.Dispose();
        _vertexBuffer.Dispose();
        _context.Dispose();
    }
}
