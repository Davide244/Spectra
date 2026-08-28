using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;

namespace SpectraEngine.Core.Graphics.D3D11;

/// <summary>
/// A dynamic D3D11 vertex buffer of per-instance data, plus the input layout
/// that binds it alongside a mesh's own vertices.
/// </summary>
/// <remarks>
/// <para>
/// <b>The combined input layout lives here rather than on the mesh</b>, and the
/// reason is which of the two varies. Every mesh in the engine is created with
/// <see cref="VertexAttribute.StandardLayout"/>, so a layout describing slot 0
/// is the same object for all of them, which is why <c>D3D11Mesh</c> can already
/// build one against the default shader and reuse it under every other shader.
/// What is new is slot 1, and that is this buffer's own description of itself,
/// so the combined layout is one object per instance layout rather than one per
/// mesh.
/// </para>
/// <para>
/// <b>Built against the signature of the program it will be DRAWN under, and
/// that is not negotiable.</b> An earlier version built it against the default
/// shader, reasoning that D3D permits a layout to declare elements the shader
/// does not read. Creation did succeed; every instanced draw then failed with
/// "the input stage requires Semantic/Index (TEXCOORD,3) as input, but it is not
/// provided by the output stage". A layout is bound to the signature it was
/// validated against, and permitted extra elements are not the same thing as a
/// layout that carries them into another shader.
/// </para>
/// <para>
/// <b>Dynamic, written by appending.</b> The frame's first write maps with
/// <see cref="Map.WriteDiscard"/> so the driver can rename the whole allocation
/// rather than wait for in-flight draws; later writes in the same frame map with
/// <see cref="Map.WriteNoOverwrite"/> at their own offset, which is what lets
/// several passes share one buffer without a later write changing what an
/// earlier draw reads.
/// </para>
/// </remarks>
internal sealed unsafe class D3D11InstanceBuffer : InstanceBuffer
{
    private static readonly byte[] TexcoordSemantic =
        [(byte)'T', (byte)'E', (byte)'X', (byte)'C', (byte)'O', (byte)'O', (byte)'R', (byte)'D', 0];

    private ComPtr<ID3D11Buffer> _buffer;
    private ComPtr<ID3D11InputLayout> _layout;
    private ComPtr<ID3D11DeviceContext> _context;
    private bool _disposed;

    /// <summary>Bytes between one instance and the next.</summary>
    internal uint Stride { get; }

    internal ID3D11Buffer* Buffer => (ID3D11Buffer*)_buffer.Handle;

    /// <summary>The layout describing slot 0 (the mesh) and slot 1 (this buffer) together.</summary>
    internal ID3D11InputLayout* Layout => (ID3D11InputLayout*)_layout.Handle;

    internal D3D11InstanceBuffer(
        ComPtr<ID3D11Device> device,
        int capacityInstances,
        ReadOnlySpan<VertexAttribute> vertexAttributes,
        ReadOnlySpan<VertexAttribute> instanceAttributes,
        int floatsPerInstance,
        ReadOnlyMemory<byte> vsBytecodeForLayout)
    {
        Capacity = capacityInstances;
        FloatsPerInstance = floatsPerInstance;
        Stride = (uint)(floatsPerInstance * sizeof(float));

        var desc = new BufferDesc
        {
            ByteWidth = (uint)(capacityInstances * Stride),
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.VertexBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
            MiscFlags = 0,
            StructureByteStride = 0,
        };

        ID3D11Buffer* bufPtr = null;
        SilkMarshal.ThrowHResult(((ID3D11Device*)device.Handle)->CreateBuffer(&desc, null, &bufPtr));
        _buffer = ComOwnership.Own(bufPtr);

        _layout = CreateCombinedLayout(device, vertexAttributes, instanceAttributes, vsBytecodeForLayout);

        ID3D11DeviceContext* ctxPtr = null;
        ((ID3D11Device*)device.Handle)->GetImmediateContext(&ctxPtr);
        _context = ComOwnership.Own(ctxPtr);
    }

    private static ComPtr<ID3D11InputLayout> CreateCombinedLayout(
        ComPtr<ID3D11Device> device,
        ReadOnlySpan<VertexAttribute> vertexAttributes,
        ReadOnlySpan<VertexAttribute> instanceAttributes,
        ReadOnlyMemory<byte> vsBytecode)
    {
        int total = vertexAttributes.Length + instanceAttributes.Length;
        Span<InputElementDesc> elements = stackalloc InputElementDesc[total];

        fixed (byte* semName = TexcoordSemantic)
        {
            int next = 0;

            uint offset = 0;
            for (int i = 0; i < vertexAttributes.Length; i++)
            {
                elements[next++] = new InputElementDesc
                {
                    SemanticName = semName,
                    SemanticIndex = vertexAttributes[i].Location,
                    Format = FormatFor(vertexAttributes[i].ComponentCount),
                    InputSlot = VertexAttribute.VertexSlot,
                    AlignedByteOffset = offset,
                    InputSlotClass = InputClassification.PerVertexData,
                    InstanceDataStepRate = 0,
                };
                offset += vertexAttributes[i].ComponentCount * sizeof(float);
            }

            // Offsets restart at zero: they are byte offsets within the element's
            // OWN slot, not within some concatenation of both buffers.
            offset = 0;
            for (int i = 0; i < instanceAttributes.Length; i++)
            {
                elements[next++] = new InputElementDesc
                {
                    SemanticName = semName,
                    SemanticIndex = instanceAttributes[i].Location,
                    Format = FormatFor(instanceAttributes[i].ComponentCount),
                    InputSlot = VertexAttribute.InstanceSlot,
                    AlignedByteOffset = offset,
                    // The two fields that ARE the feature. PerVertexData with a
                    // step rate of zero here draws every instance on top of the
                    // first, and reports nothing.
                    InputSlotClass = InputClassification.PerInstanceData,
                    InstanceDataStepRate = 1,
                };
                offset += instanceAttributes[i].ComponentCount * sizeof(float);
            }

            using var pin = vsBytecode.Pin();
            ID3D11InputLayout* layoutPtr = null;
            fixed (InputElementDesc* pElements = elements)
            {
                SilkMarshal.ThrowHResult(((ID3D11Device*)device.Handle)->CreateInputLayout(
                    pElements, (uint)total, pin.Pointer, (nuint)vsBytecode.Length, &layoutPtr));
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
        _ => throw new ArgumentOutOfRangeException(
            nameof(componentCount), $"Unsupported component count {componentCount}"),
    };

    /// <inheritdoc/>
    /// <remarks>
    /// The canonical dynamic-append pair: <see cref="Map.WriteDiscard"/> on a
    /// frame's first write lets the driver rename the whole allocation, and
    /// <see cref="Map.WriteNoOverwrite"/> afterwards promises it that earlier
    /// ranges are untouched, so appending never renames away what a draw
    /// already recorded against.
    /// </remarks>
    public override int Append(ReadOnlySpan<float> data, int instanceCount)
    {
        ValidateUpdate(data, instanceCount);
        int first = Cursor;
        if (instanceCount == 0)
            return first;

        var ctx = (ID3D11DeviceContext*)_context.Handle;
        MappedSubresource mapped;
        SilkMarshal.ThrowHResult(ctx->Map(
            (ID3D11Resource*)_buffer.Handle, 0,
            first == 0 ? Map.WriteDiscard : Map.WriteNoOverwrite, 0, &mapped));

        byte* dst = (byte*)mapped.PData + (long)first * Stride;
        fixed (float* src = data)
            System.Buffer.MemoryCopy(src, dst, (long)(Capacity - first) * Stride, (long)data.Length * sizeof(float));

        ctx->Unmap((ID3D11Resource*)_buffer.Handle, 0);

        Cursor = first + instanceCount;
        return first;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Through ComOwnership, which nulls the handle: each of these owns
        // exactly one reference, so a second release would be an over-release
        // rather than something a leak absorbs.
        ComOwnership.Release(ref _layout);
        ComOwnership.Release(ref _buffer);
        ComOwnership.Release(ref _context);
    }
}
