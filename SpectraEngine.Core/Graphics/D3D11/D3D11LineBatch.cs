using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Runtime.CompilerServices;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// A dynamic vertex buffer that uploads interleaved (position + colour) debug
/// line vertices and draws them as <c>D3D_PRIMITIVE_TOPOLOGY_LINELIST</c>.
/// Grows on demand; reuses the existing allocation otherwise. Matches the
/// OpenGL line batch's external contract.
/// </summary>
internal sealed unsafe class D3D11LineBatch : IDisposable
{
    private const int FloatsPerVertex = 6;
    private const uint StrideBytes = FloatsPerVertex * sizeof(float);

    private readonly ComPtr<ID3D11Device> _device;
    private readonly ComPtr<ID3D11DeviceContext> _context;
    private readonly D3D11ShaderProgram _shader;
    private ComPtr<ID3D11Buffer> _vb;
    private ComPtr<ID3D11InputLayout> _inputLayout;
    private uint _vertexCapacity;
    private bool _disposed;

    public D3D11LineBatch(ComPtr<ID3D11Device> device, ComPtr<ID3D11DeviceContext> context, D3D11ShaderProgram shader)
    {
        _device = device;
        _context = context;
        _shader = shader;
        _inputLayout = CreateInputLayout(device, shader.VertexBytecode);
    }

    private static ComPtr<ID3D11InputLayout> CreateInputLayout(ComPtr<ID3D11Device> device, ReadOnlyMemory<byte> vsBytecode)
    {
        // Layout matches the DebugLine shader: location 0 = position (vec3),
        // location 1 = colour (vec3). SpectraShade emits these as TEXCOORD0/1.
        ReadOnlySpan<byte> sem = "TEXCOORD\0"u8;
        fixed (byte* semName = sem)
        {
            Span<InputElementDesc> elements = stackalloc InputElementDesc[2]
            {
                new InputElementDesc
                {
                    SemanticName = semName,
                    SemanticIndex = 0,
                    Format = Format.FormatR32G32B32Float,
                    InputSlot = 0,
                    AlignedByteOffset = 0,
                    InputSlotClass = InputClassification.PerVertexData,
                    InstanceDataStepRate = 0,
                },
                new InputElementDesc
                {
                    SemanticName = semName,
                    SemanticIndex = 1,
                    Format = Format.FormatR32G32B32Float,
                    InputSlot = 0,
                    AlignedByteOffset = 12,
                    InputSlotClass = InputClassification.PerVertexData,
                    InstanceDataStepRate = 0,
                },
            };

            using var pin = vsBytecode.Pin();
            ID3D11InputLayout* layoutPtr = null;
            fixed (InputElementDesc* pElements = elements)
            {
                SilkMarshal.ThrowHResult(((ID3D11Device*)device.Handle)->CreateInputLayout(
                    pElements, (uint)elements.Length, pin.Pointer, (nuint)vsBytecode.Length, &layoutPtr));
            }
            return new ComPtr<ID3D11InputLayout>(layoutPtr);
        }
    }

    public void Draw(ReadOnlySpan<float> interleaved, uint vertexCount)
    {
        if (vertexCount == 0) return;

        uint byteSize = vertexCount * StrideBytes;
        EnsureCapacity(vertexCount);

        var ctx = (ID3D11DeviceContext*)_context.Handle;
        MappedSubresource mapped = default;
        SilkMarshal.ThrowHResult(ctx->Map((ID3D11Resource*)_vb.Handle, 0, Map.WriteDiscard, 0, &mapped));
        fixed (float* src = interleaved)
        {
            System.Buffer.MemoryCopy(src, mapped.PData, byteSize, byteSize);
        }
        ctx->Unmap((ID3D11Resource*)_vb.Handle, 0);

        uint stride = StrideBytes;
        uint offset = 0;
        ID3D11Buffer* vb = (ID3D11Buffer*)_vb.Handle;
        ctx->IASetInputLayout((ID3D11InputLayout*)_inputLayout.Handle);
        ctx->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        ctx->IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyLinelist);
        ctx->Draw(vertexCount, 0);
    }

    private void EnsureCapacity(uint vertexCount)
    {
        if (vertexCount <= _vertexCapacity && _vb.Handle is not null) return;
        _vb.Dispose();

        var desc = new BufferDesc
        {
            ByteWidth = vertexCount * StrideBytes,
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.VertexBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
            MiscFlags = 0,
            StructureByteStride = 0,
        };
        ID3D11Buffer* bufPtr = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)_device.Handle)->CreateBuffer(&desc, null, &bufPtr));
        _vb = new ComPtr<ID3D11Buffer>(bufPtr);
        _vertexCapacity = vertexCount;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vb.Dispose();
        _inputLayout.Dispose();
    }
}
