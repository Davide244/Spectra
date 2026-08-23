using System;
using System.Runtime.CompilerServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// What HLSL actually does to the bytes of a constant buffer, measured by
/// compiling one and reflecting it.
/// </summary>
/// <remarks>
/// <para>
/// <b>No device, no window, no driver.</b> <c>D3DCompile</c> and the reflection
/// interfaces are pure library calls, which makes this the only D3D-side test in
/// the repo that can run anywhere, and the cheapest possible gate on the one
/// thing array uniforms turn on.
/// </para>
/// <para>
/// <b>Why it has to be measured rather than reasoned about.</b> The engine
/// learns every uniform's byte offset by reflecting compiled bytecode, so the
/// packing rules are the compiler's, not ours. A C# array whose stride happens
/// to differ from the shader's does not fail: it copies a contiguous run of
/// bytes into a strided layout and the shader reads garbage from element one
/// onwards, on D3D only, while OpenGL (which packs arrays tightly) renders it
/// perfectly. That asymmetry is what makes this worth a test rather than a
/// comment.
/// </para>
/// </remarks>
public sealed unsafe class CBufferPackingTests
{
    private const string Source = """
        cbuffer Packing : register(b0)
        {
            float    scalars[8];
            float3   positions[8];
            float4   colors[8];
            float4x4 cascades[2];
            float3   loose;
        };

        float4 main() : SV_Target0
        {
            return colors[0] + float4(positions[0], scalars[0]) + mul(cascades[0], float4(loose, 1));
        }
        """;

    [Theory]
    // A C# array of this type is a contiguous run of Stride-byte elements. The
    // shader reads Count elements at ElementStride. They agree only when the
    // two numbers match, and that is the whole of what decides which overloads
    // the engine may safely offer.
    [InlineData("scalars", 8, 4, 16)]     // float:    C# 4 bytes,  HLSL 16. MISMATCH.
    [InlineData("positions", 8, 12, 16)]  // float3:   C# 12 bytes, HLSL 16. MISMATCH.
    [InlineData("colors", 8, 16, 16)]     // float4:   agree.
    [InlineData("cascades", 2, 64, 64)]   // float4x4: agree.
    public void An_array_members_hlsl_stride_is_what_it_is(
        string name, int count, int managedStride, int expectedHlslStride)
    {
        Member member = Reflect(name);

        member.Elements.ShouldBe((uint)count);

        // Reflection reports the total size as (count - 1) * stride + tail,
        // because the last element is not padded out.
        int stride = expectedHlslStride;
        int tail = managedStride;
        ((int)member.Size).ShouldBe((count - 1) * stride + tail,
            $"{name} should occupy {count - 1} strides of {stride} plus a {tail}-byte tail");

        // The claim the engine acts on, stated directly.
        bool safeForBulkCopy = managedStride == expectedHlslStride;
        (member.Size == count * managedStride).ShouldBe(safeForBulkCopy);
    }

    [Fact]
    public void Only_vec4_and_mat4_arrays_can_be_bulk_copied()
    {
        // Restated as the rule the API enforces, so a future reader sees why the
        // other overloads do not exist rather than assuming nobody got round to
        // them. A float array copied naively would put element 1's bytes where
        // the shader expects element 0's padding.
        Reflect("colors").Size.ShouldBe(8u * 16u);
        Reflect("cascades").Size.ShouldBe(2u * 64u);

        Reflect("scalars").Size.ShouldNotBe(8u * 4u);
        Reflect("positions").Size.ShouldNotBe(8u * 12u);
    }

    [Fact]
    public void Array_members_start_on_a_sixteen_byte_boundary()
    {
        // Which is why an array may be addressed by its own offset without the
        // engine reasoning about what precedes it.
        (Reflect("scalars").Offset % 16).ShouldBe(0u);
        (Reflect("positions").Offset % 16).ShouldBe(0u);
        (Reflect("colors").Offset % 16).ShouldBe(0u);
        (Reflect("cascades").Offset % 16).ShouldBe(0u);
    }

    [Fact]
    public void Matrices_are_column_major_which_is_the_engine_s_unwritten_contract()
    {
        // The engine uploads System.Numerics matrices, which are row-major in
        // memory, with no transpose on any backend. That is correct only because
        // fxc packs cbuffer matrices column-major by default and GLSL's mat4 is
        // column-major too, so the same bytes mean the same matrix.
        //
        // It works today by coincidence of three defaults lining up, and nothing
        // said so. Adding D3DCOMPILE_PACK_MATRIX_ROW_MAJOR, a row_major
        // qualifier in the generator, or a move to DXC with -Zpr would transpose
        // every matrix in the engine at once and the scene would still render,
        // just wrong. This test goes red instead.
        Reflect("cascades").Class.ShouldBe(D3DShaderVariableClass.D3DSvcMatrixColumns);
    }

    private readonly record struct Member(uint Offset, uint Size, uint Elements, D3DShaderVariableClass Class);

    private static Member Reflect(string name)
    {
        using var compiler = D3DCompiler.GetApi();
        byte[] bytecode = Compile(compiler);

        ID3D11ShaderReflection* refl = null;
        Guid iid = ID3D11ShaderReflection.Guid;
        fixed (byte* p = bytecode)
            SilkMarshal.ThrowHResult(compiler.Reflect(p, (nuint)bytecode.Length, &iid, (void**)&refl));

        try
        {
            ID3D11ShaderReflectionConstantBuffer* cb = refl->GetConstantBufferByIndex(0);
            ID3D11ShaderReflectionVariable* variable = cb->GetVariableByName(name);

            ShaderVariableDesc varDesc = default;
            SilkMarshal.ThrowHResult(variable->GetDesc(&varDesc));

            ID3D11ShaderReflectionType* type = variable->GetType();
            ShaderTypeDesc typeDesc = default;
            SilkMarshal.ThrowHResult(type->GetDesc(&typeDesc));

            return new Member(varDesc.StartOffset, varDesc.Size, typeDesc.Elements, typeDesc.Class);
        }
        finally
        {
            refl->Release();
        }
    }

    private static byte[] Compile(D3DCompiler compiler)
    {
        byte[] source = Encoding.ASCII.GetBytes(Source);
        ComPtr<ID3D10Blob> code = default;
        ComPtr<ID3D10Blob> errors = default;

        fixed (byte* pSrc = source)
        fixed (byte* pProfile = "ps_5_0\0"u8)
        fixed (byte* pEntry = "main\0"u8)
        fixed (byte* pName = "packing\0"u8)
        {
            // Flag words 0, 0: exactly what the engine passes, so this measures
            // the engine's packing and not some other configuration's.
            int hr = compiler.Compile(
                pSrc, (nuint)source.Length, pName, null,
                ref Unsafe.NullRef<ID3DInclude>(), pEntry, pProfile, 0u, 0u,
                ref code, ref errors);

            if (hr < 0)
            {
                string message = errors.Handle is null
                    ? "(no error blob)"
                    : Encoding.ASCII.GetString((byte*)errors.GetBufferPointer(), (int)errors.GetBufferSize());
                throw new InvalidOperationException($"HLSL compile failed ({hr:X}): {message}");
            }
        }

        var bytes = new byte[(int)code.GetBufferSize()];
        new ReadOnlySpan<byte>(code.GetBufferPointer(), bytes.Length).CopyTo(bytes);
        code.Dispose();
        errors.Dispose();
        return bytes;
    }
}
