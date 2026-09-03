using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.DXGI;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The combined two-slot input layout, against a real D3D11 device.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists to check one assumption that the whole D3D11 instancing path
/// rests on.</b> <c>D3D11InstanceBuffer</c> builds its layout against the
/// DEFAULT shader's signature, exactly as <c>D3D11Mesh</c> already builds mesh
/// layouts, because the instanced shader may not exist when the buffer is
/// created. That is only valid if D3D permits a layout to declare elements the
/// vertex shader does not read, and rejects only the reverse. If that is wrong,
/// <c>CreateInputLayout</c> returns E_INVALIDARG and every instanced draw on
/// this backend fails at creation.
/// </para>
/// <para>
/// <b>No swap chain and no window.</b> <c>D3D11CreateDevice</c> with a null
/// adapter and no chain gives a device that can create layouts, which is all
/// this needs; the engine's own D3D fixtures need a window because they
/// present, and this deliberately does not. A machine with no D3D11 device at
/// all skips rather than fails, since that is an absent capability rather than
/// a defect.
/// </para>
/// <para>
/// <b>In the D3D11 collection with the shared-target tests</b>, because two
/// classes acquiring Silk.NET's D3D11 API at once race; see
/// <see cref="SharedTargetD3D11Collection"/> for what that looked like.
/// </para>
/// </remarks>
[Collection(SharedTargetD3D11Collection.Name)]
public sealed unsafe class D3D11InstancedLayoutTests
{
    private static string InstancedSource =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "InstancedVertex.spectrashade"));

    [Fact]
    public void A_layout_naming_both_slots_is_valid_against_the_instanced_shader()
    {
        using var device = TryCreateDevice();
        if (device is null)
            return;

        byte[] vs = CompileVertexStage(InstancedSource);
        VertexAttribute[] all = ShaderInputs(InstancedSource);

        CreateLayout(
            device.Value,
            VertexAttribute.ForSlot(all, VertexAttribute.VertexSlot),
            VertexAttribute.ForSlot(all, VertexAttribute.InstanceSlot),
            vs).ShouldBeTrue();
    }

    [Fact]
    public void A_layout_naming_both_slots_is_valid_against_a_shader_that_reads_only_the_first()
    {
        // The assumption the engine relies on: extra elements are permitted.
        // The lit shader declares TEXCOORD0..2 and the layout supplies
        // TEXCOORD0..7, which is how an instance buffer can be created before
        // any instanced shader exists.
        using var device = TryCreateDevice();
        if (device is null)
            return;

        byte[] litVs = CompileVertexStage(BaseShaders.Lit);
        VertexAttribute[] all = ShaderInputs(InstancedSource);

        CreateLayout(
            device.Value,
            VertexAttribute.ForSlot(all, VertexAttribute.VertexSlot),
            VertexAttribute.ForSlot(all, VertexAttribute.InstanceSlot),
            litVs).ShouldBeTrue();
    }

    [Fact]
    public void A_layout_missing_what_the_shader_reads_is_rejected()
    {
        // The other direction, asserted so the test above means something: if
        // D3D accepted this too, the first two would pass with any layout at
        // all and would be proving nothing.
        using var device = TryCreateDevice();
        if (device is null)
            return;

        byte[] vs = CompileVertexStage(InstancedSource);
        VertexAttribute[] all = ShaderInputs(InstancedSource);

        CreateLayout(
            device.Value,
            VertexAttribute.ForSlot(all, VertexAttribute.VertexSlot),
            instanceAttributes: [],
            vs).ShouldBeFalse("the shader reads TEXCOORD3..7, which this layout does not supply");
    }

    // --- helpers -------------------------------------------------------------

    private static VertexAttribute[] ShaderInputs(string source)
    {
        PipelineBlob blob = new SpectraShadeCompiler()
            .Compile(source, [GraphicsBackend.D3D11])
            .GetPipeline(GraphicsBackend.D3D11)
            .ShouldNotBeNull();

        return VertexAttribute.FromShaderInputs(blob.VertexInputs);
    }

    private static byte[] CompileVertexStage(string spectraShade)
    {
        PipelineBlob blob = new SpectraShadeCompiler()
            .Compile(spectraShade, [GraphicsBackend.D3D11])
            .GetPipeline(GraphicsBackend.D3D11)
            .ShouldNotBeNull();

        string hlsl = Encoding.ASCII.GetString(blob.VertexData!);
        byte[] source = Encoding.ASCII.GetBytes(hlsl);

        using var compiler = D3DCompiler.GetApi();
        ComPtr<ID3D10Blob> code = default;
        ComPtr<ID3D10Blob> errors = default;

        fixed (byte* pSrc = source)
        fixed (byte* pProfile = "vs_5_0\0"u8)
        fixed (byte* pEntry = "main\0"u8)
        {
            int hr = compiler.Compile(
                pSrc, (nuint)source.Length, (byte*)null, null,
                ref Unsafe.NullRef<ID3DInclude>(), pEntry, pProfile, 0u, 0u,
                ref code, ref errors);
            hr.ShouldBeGreaterThanOrEqualTo(0);
        }

        var bytes = new byte[(int)code.GetBufferSize()];
        new Span<byte>(code.GetBufferPointer(), bytes.Length).CopyTo(bytes);
        code.Dispose();
        errors.Dispose();
        return bytes;
    }

    private static readonly byte[] Texcoord =
        [(byte)'T', (byte)'E', (byte)'X', (byte)'C', (byte)'O', (byte)'O', (byte)'R', (byte)'D', 0];

    private static bool CreateLayout(
        ComPtr<ID3D11Device> device,
        ReadOnlySpan<VertexAttribute> vertexAttributes,
        ReadOnlySpan<VertexAttribute> instanceAttributes,
        byte[] vsBytecode)
    {
        int total = vertexAttributes.Length + instanceAttributes.Length;
        Span<InputElementDesc> elements = stackalloc InputElementDesc[total];

        fixed (byte* semName = Texcoord)
        {
            int next = 0;
            uint offset = 0;
            for (int i = 0; i < vertexAttributes.Length; i++)
            {
                elements[next++] = Element(semName, vertexAttributes[i], offset, perInstance: false);
                offset += vertexAttributes[i].ComponentCount * sizeof(float);
            }

            offset = 0;
            for (int i = 0; i < instanceAttributes.Length; i++)
            {
                elements[next++] = Element(semName, instanceAttributes[i], offset, perInstance: true);
                offset += instanceAttributes[i].ComponentCount * sizeof(float);
            }

            ID3D11InputLayout* layout = null;
            int hr;
            fixed (InputElementDesc* pElements = elements)
            fixed (byte* pBytecode = vsBytecode)
            {
                hr = ((ID3D11Device*)device.Handle)->CreateInputLayout(
                    pElements, (uint)total, pBytecode, (nuint)vsBytecode.Length, &layout);
            }

            if (layout is not null)
                layout->Release();

            return hr >= 0;
        }
    }

    private static InputElementDesc Element(byte* semName, in VertexAttribute attr, uint offset, bool perInstance) =>
        new()
        {
            SemanticName = semName,
            SemanticIndex = attr.Location,
            Format = attr.ComponentCount switch
            {
                1 => Format.FormatR32Float,
                2 => Format.FormatR32G32Float,
                3 => Format.FormatR32G32B32Float,
                _ => Format.FormatR32G32B32A32Float,
            },
            InputSlot = perInstance ? VertexAttribute.InstanceSlot : VertexAttribute.VertexSlot,
            AlignedByteOffset = offset,
            InputSlotClass = perInstance
                ? InputClassification.PerInstanceData
                : InputClassification.PerVertexData,
            InstanceDataStepRate = perInstance ? 1u : 0u,
        };

    // The API object is kept alive for the process rather than disposed per
    // call: Silk's D3D11 wrapper owns the loaded native library, and releasing
    // it while a device created through it is still being used takes the
    // function table with it.
    private static readonly D3D11 Api = D3D11.GetApi(null);

    private static ComPtr<ID3D11Device>? TryCreateDevice()
    {
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;

        int hr = Api.CreateDevice(
            (IDXGIAdapter*)null, D3DDriverType.Hardware, (nint)0, 0u,
            (D3DFeatureLevel*)null, 0u, D3D11.SdkVersion, &device, null, &context);

        if (context is not null)
            context->Release();

        if (hr < 0 || device is null)
            return null;

        return ComOwnership.Own(device);
    }
}
