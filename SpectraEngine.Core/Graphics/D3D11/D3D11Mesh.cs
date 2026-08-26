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
