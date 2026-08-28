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
/// <b>Built against the default shader's signature, exactly as mesh layouts
/// are.</b> D3D validates that a layout supplies everything the vertex shader
/// declares, and permits elements the shader does not read; so a layout carrying
/// TEXCOORD0 through TEXCOORD6 is valid against the lit shader (which reads the
/// first three) and equally valid at draw time under an instanced shader that
/// reads all seven. Creating it against the instanced shader instead would make
/// instance buffers un-creatable until that shader exists, for no gain.
/// </para>
/// <para>
/// <b>Dynamic with discard, not immutable.</b> The contents are rebuilt from the
/// view every frame, and mapping with
/// <see cref="Map.WriteDiscard"/> is what lets the driver hand back fresh
/// storage instead of waiting for in-flight draws to finish reading the old.
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
    public override void Update(ReadOnlySpan<float> data, int instanceCount)
    {
        ValidateUpdate(data, instanceCount);
        if (instanceCount == 0)
            return;

        var ctx = (ID3D11DeviceContext*)_context.Handle;
        MappedSubresource mapped;
        SilkMarshal.ThrowHResult(ctx->Map(
            (ID3D11Resource*)_buffer.Handle, 0, Map.WriteDiscard, 0, &mapped));

        fixed (float* src = data)
            System.Buffer.MemoryCopy(src, mapped.PData, (long)Capacity * Stride, (long)data.Length * sizeof(float));

        ctx->Unmap((ID3D11Resource*)_buffer.Handle, 0);
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
