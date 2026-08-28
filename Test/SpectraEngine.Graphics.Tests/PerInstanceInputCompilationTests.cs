using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// A shader with per-instance vertex inputs, compiled all the way to D3D
/// bytecode and to a real GL program.
/// </summary>
/// <remarks>
/// <para>
/// <b>The compiler suite asserts what this source generates; this asserts that
/// what it generates is accepted.</b> Both suites read the same fixture file
/// (linked, not copied) so the two claims cannot drift apart.
/// </para>
/// <para>
/// <b>The claim actually under test is the matrix input.</b> A <c>mat4</c> is
/// one field in the source and four consecutive attributes in both targets:
/// GLSL takes <c>in mat4</c> whole and assigns the next three locations itself,
/// and HLSL gives the four rows <c>TEXCOORD3</c> through <c>TEXCOORD6</c> from a
/// single declared semantic. Both of those are conventions rather than anything
/// the generator can verify, and getting either wrong produces a shader that
/// fails on one backend and not the other. That is worth a driver and a real
/// FXC rather than a string comparison.
/// </para>
/// <para>
/// The per-instance rate itself is deliberately invisible here, and that is the
/// correct outcome: neither target expresses it in shader text. It lives in
/// <c>glVertexAttribDivisor</c> and <c>InputSlotClass</c>, which is exactly why
/// the compiled blob reports it instead.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed unsafe class PerInstanceInputCompilationTests
{
    private readonly GlRendererFixture _fixture;

    public PerInstanceInputCompilationTests(GlRendererFixture fixture) => _fixture = fixture;

    private static string Source =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "InstancedVertex.spectrashade"));

    [Fact]
    public void An_instanced_shader_compiles_to_d3d_bytecode()
    {
        ReadOnlySpan<GraphicsBackend> targets = [GraphicsBackend.D3D11];
        CompiledShaderFile compiled = new SpectraShadeCompiler().Compile(Source, targets);
        PipelineBlob blob = compiled.GetPipeline(GraphicsBackend.D3D11).ShouldNotBeNull();

        using var compiler = D3DCompiler.GetApi();
        Compile(compiler, Encoding.ASCII.GetString(blob.VertexData!), "vs_5_0", "InstancedVertex");
        Compile(compiler, Encoding.ASCII.GetString(blob.FragmentData!), "ps_5_0", "InstancedVertex");
    }

    [Fact]
    public void An_instanced_shader_compiles_in_opengl()
    {
        _fixture.Renderer.CreateShaderFromSource(Source).ShouldNotBeNull();
    }

    [Fact]
    public void The_compiled_blob_reports_the_instanced_inputs()
    {
        // The end-to-end shape of the contract, through the public compiler
        // rather than a generator directly: this is what a renderer is handed.
        ReadOnlySpan<GraphicsBackend> targets = [GraphicsBackend.OpenGL];
        CompiledShaderFile compiled = new SpectraShadeCompiler().Compile(Source, targets);
        PipelineBlob blob = compiled.GetPipeline(GraphicsBackend.OpenGL).ShouldNotBeNull();

        blob.VertexInputs.Count.ShouldBe(5);

        uint perInstance = 0;
        foreach (VertexInputElement input in blob.VertexInputs)
        {
            if (input.Rate == VertexInputRate.PerInstance)
                perInstance += input.LocationSpan;
        }

        // Five locations of instance data: four for the matrix, one for the
        // tint. A renderer building one element per FIELD would make two, bind
        // a quarter of the matrix, and leave the rest reading whatever the
        // previous draw left in those attributes.
        perInstance.ShouldBe(5u);
    }

    private static void Compile(D3DCompiler compiler, string hlsl, string profile, string label)
    {
        byte[] source = Encoding.ASCII.GetBytes(hlsl);
        ComPtr<ID3D10Blob> code = default;
        ComPtr<ID3D10Blob> errors = default;

        fixed (byte* pSrc = source)
        fixed (byte* pProfile = Encoding.ASCII.GetBytes(profile + "\0"))
        fixed (byte* pEntry = "main\0"u8)
        fixed (byte* pName = Encoding.ASCII.GetBytes(label + "\0"))
        {
            int hr = compiler.Compile(
                pSrc, (nuint)source.Length, pName, null,
                ref Unsafe.NullRef<ID3DInclude>(), pEntry, pProfile, 0u, 0u,
                ref code, ref errors);

            if (hr < 0)
            {
                string message = errors.Handle is null
                    ? "(no error blob)"
                    : Encoding.ASCII.GetString((byte*)errors.GetBufferPointer(), (int)errors.GetBufferSize());
                errors.Dispose();
                code.Dispose();
                throw new InvalidOperationException(
                    $"{label} {profile} failed to compile ({hr:X}):{Environment.NewLine}{message}" +
                    $"{Environment.NewLine}--- generated HLSL ---{Environment.NewLine}{hlsl}");
            }
        }

        code.GetBufferSize().ShouldBeGreaterThan(0u);
        code.Dispose();
        errors.Dispose();
    }
}
