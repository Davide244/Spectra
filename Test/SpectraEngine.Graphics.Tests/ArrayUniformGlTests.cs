using System.Numerics;
using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// Array uniforms reaching the GPU, proved by reading a pixel back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every layer below this one fails quietly.</b> A wrong location, a wrong
/// element count, a wrong stride: none of them raise anything, on any backend.
/// The shader simply reads zeros or somebody else's bytes. So the assertion is a
/// pixel whose value could only have come from a specific element of a specific
/// array.
/// </para>
/// <para>
/// <b>OpenGL is the only backend this can be done on today</b>, because neither
/// D3D backend has pixel readback. <c>CBufferPackingTests</c> covers the D3D
/// layout half without a device; the upload half is still eye-only there, and
/// that gap is worth remembering when reading a green suite.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class ArrayUniformGlTests
{
    private readonly GlRendererFixture _fixture;

    public ArrayUniformGlTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    // Reads one element of a vec4 array and writes it out, so the pixel IS the
    // array element. The index is a uniform, which also exercises dynamic
    // indexing rather than a constant the compiler could fold away.
    private const string PickSource = """
        struct VertexInput {
            [Location(0)] vec3 position;
            [Location(1)] vec3 normal;
            [Location(2)] vec2 uv;
        }

        struct VertexOutput {
            [Position] vec4 position;
            vec2 uv;
        }

        struct FragmentInput {
            vec2 uv;
        }

        shader Pick {
            [Binding(0)] cbuffer Args {
                vec4[4] uValues;
                int uIndex;
            }

            [Vertex]
            VertexOutput VertexMain(VertexInput input) {
                var output = new VertexOutput();
                output.position = vec4(input.position, 1.0);
                output.uv = input.uv;
                return output;
            }

            [Fragment] [Target(0)]
            vec4 FragmentMain(FragmentInput input) {
                return uValues[uIndex];
            }
        }
        """;

    [Fact]
    public void Every_element_of_a_vec4_array_arrives_where_the_shader_expects_it()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        ShaderProgram shader = renderer.CreateShaderFromSource(PickSource);
        RenderTarget output = renderer.CreateRenderTarget(new RenderTargetDesc(4, 4));

        // Distinct, and distinct in the red channel alone, so an off-by-one in
        // the stride reads as a different number rather than a similar one.
        Vector4[] values =
        [
            new(0.2f, 0f, 0f, 1f),
            new(0.4f, 0f, 0f, 1f),
            new(0.6f, 0f, 0f, 1f),
            new(0.8f, 0f, 0f, 1f),
        ];

        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                // Clear, not Keep: the clear is what initialises depth, and the
                // triangle sits at z = 0 against an uninitialised depth buffer
                // otherwise. That reads back as the clear colour with nothing
                // to say why.
                renderer.BeginPass(output, PassClear.To(new Vector4(0f, 0f, 0f, 1f)));
                shader.Use();
                shader.SetUniform("uValues", values);
                shader.SetUniform("uIndex", i);
                renderer.EnsureFullscreenTriangleForTest().Draw();
                renderer.EndPass();

                int expected = (int)System.MathF.Round(values[i].X * 255f);
                int actual = ReadRed(output);
                actual.ShouldBeInRange(expected - 2, expected + 2,
                    $"element {i} should have arrived intact; a stride error would " +
                    "read a neighbouring element instead");
            }
        }
        finally
        {
            renderer.DestroyRenderTarget(output);
            shader.Dispose();
        }
    }

    [Fact]
    public void A_matrix_array_arrives_untransposed()
    {
        // The engine uploads System.Numerics matrices with no transpose on any
        // backend, and that is only correct because GLSL's mat4 is column-major
        // and the memory layouts happen to agree. A transposed upload puts the
        // translation into the wrong row, which this catches: the triangle is
        // translated off screen and the pass writes nothing.
        const string Source = """
            struct VertexInput {
                [Location(0)] vec3 position;
                [Location(1)] vec3 normal;
                [Location(2)] vec2 uv;
            }
            struct VertexOutput { [Position] vec4 position; vec2 uv; }
            struct FragmentInput { vec2 uv; }

            shader MatPick {
                [Binding(0)] cbuffer Args {
                    mat4[2] uMatrices;
                }

                [Vertex]
                VertexOutput VertexMain(VertexInput input) {
                    var output = new VertexOutput();
                    output.position = uMatrices[1] * vec4(input.position, 1.0);
                    output.uv = input.uv;
                    return output;
                }

                [Fragment] [Target(0)]
                vec4 FragmentMain(FragmentInput input) { return vec4(1.0, 0.0, 0.0, 1.0); }
            }
            """;

        OpenGLRenderer renderer = _fixture.Renderer;
        ShaderProgram shader = renderer.CreateShaderFromSource(Source);
        RenderTarget output = renderer.CreateRenderTarget(new RenderTargetDesc(4, 4));

        Matrix4x4[] matrices =
        [
            // Element 0 is a decoy that would push the triangle off screen, so
            // reading the wrong element also fails this test.
            Matrix4x4.CreateTranslation(10f, 10f, 0f),
            Matrix4x4.Identity,
        ];

        try
        {
            renderer.BeginPass(output, PassClear.To(new Vector4(0f, 0f, 0f, 1f)));
            shader.Use();
            shader.SetUniform("uMatrices", matrices);
            renderer.EnsureFullscreenTriangleForTest().Draw();
            renderer.EndPass();

            ReadRed(output).ShouldBeGreaterThan(200,
                "the identity in element 1 should leave the triangle covering the target");
        }
        finally
        {
            renderer.DestroyRenderTarget(output);
            shader.Dispose();
        }
    }

    private unsafe int ReadRed(RenderTarget target)
    {
        GL gl = _fixture.Gl;
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        gl.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, ((OpenGLTexture)target.ColorTexture).Handle, 0);

        var pixel = new byte[4];
        fixed (byte* p = pixel)
            gl.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.DeleteFramebuffer(fbo);
        return pixel[0];
    }
}
