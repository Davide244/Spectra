using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraEngine.Core.Graphics.D3D11;
using SpectraShade.Compiler;
using System;
using System.IO;
using System.Numerics;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// An instanced draw, checked in pixels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other check in this chain passes with instancing completely
/// broken.</b> The shader compiles, the layout is created, the draw is
/// submitted, no debug-layer message appears and a picture comes out. What goes
/// wrong is that the attribute divisor is missing, or the input element says
/// <c>PerVertexData</c>, and then every instance reads instance zero's data and
/// they all land on top of each other. Nothing reports that, so it has to be
/// looked at.
/// </para>
/// <para>
/// <b>Three instances, three positions, three colours, and both are read.</b>
/// The position comes from a per-instance <c>mat4</c> and the colour from a
/// per-instance <c>vec4</c>, so the test fails if either kind of instance
/// attribute stops advancing: with the divisor gone, only the leftmost quad is
/// drawn and it is red, so both the green and the blue samples read background.
/// The gap sample is what fails in the other direction, if a quad is drawn at
/// the wrong scale or the whole layout is misread.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class InstancedDrawGlTests
{
    private readonly GlRendererFixture _fixture;

    public InstancedDrawGlTests(GlRendererFixture fixture) => _fixture = fixture;

    private const int Width = 24;
    private const int Height = 4;
    private const int Instances = 3;

    // Where each instance's quad lands, and one point between two of them.
    private const int LeftX = 4;
    private const int MiddleX = 12;
    private const int RightX = 19;
    private const int GapX = 15;
    private const int SampleY = 2;

    private static string Source =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "InstancedVertex.spectrashade"));

    // A quad narrow enough that the three instances do not touch: local x spans
    // 0.5 of NDC and they are 0.6 apart.
    private static readonly float[] QuadVertices =
    [
        -0.25f, -0.9f, 0f,   0f, 0f, 1f,   0f, 0f,
         0.25f, -0.9f, 0f,   0f, 0f, 1f,   1f, 0f,
         0.25f,  0.9f, 0f,   0f, 0f, 1f,   1f, 1f,
        -0.25f,  0.9f, 0f,   0f, 0f, 1f,   0f, 1f,
    ];

    private static readonly uint[] QuadIndices = [0, 1, 2, 0, 2, 3];

    private static readonly Vector4[] Tints =
    [
        new(1f, 0f, 0f, 1f),
        new(0f, 1f, 0f, 1f),
        new(0f, 0f, 1f, 1f),
    ];

    private static readonly float[] OffsetsX = [-0.6f, 0f, 0.6f];

    [Fact]
    public void Three_instances_land_in_three_places_with_three_colours()
    {
        OpenGLRenderer renderer = _fixture.Renderer;

        // The layout comes from the SHADER, through the signature the compiler
        // reports, rather than from a constant written to match it by hand.
        // That is the whole point of the reported signature, so the test that
        // proves the draw works should exercise it too.
        PipelineBlob blob = new SpectraShadeCompiler()
            .Compile(Source, [GraphicsBackend.OpenGL])
            .GetPipeline(GraphicsBackend.OpenGL)
            .ShouldNotBeNull();

        VertexAttribute[] all = VertexAttribute.FromShaderInputs(blob.VertexInputs);
        VertexAttribute[] perInstance = VertexAttribute.ForSlot(all, VertexAttribute.InstanceSlot);

        // mat4 (16) + vec4 (4).
        perInstance.Length.ShouldBe(5);

        ShaderProgram shader = renderer.CreateShaderFromSource(Source);
        Mesh mesh = renderer.CreateMesh(QuadVertices, QuadIndices, VertexAttribute.StandardLayout);
        InstanceBuffer instances = renderer.CreateInstanceBuffer(Instances, perInstance, shader);
        RenderTarget output = renderer.CreateRenderTarget(new RenderTargetDesc(Width, Height));

        try
        {
            instances.Update(BuildInstanceData(), Instances);

            renderer.BeginPass(output, PassClear.To(new Vector4(0f, 0f, 0f, 1f)));
            shader.Use();
            shader.SetUniform("viewProjection", Matrix4x4.Identity);
            mesh.DrawInstanced(instances, Instances);
            renderer.EndPass();

            byte[] pixels = ReadPixels(output);

            // Each instance in its own place, in its own colour.
            Channel(pixels, LeftX, 0).ShouldBeGreaterThan(200, "instance 0 is red, on the left");
            Channel(pixels, MiddleX, 1).ShouldBeGreaterThan(200, "instance 1 is green, in the middle");
            Channel(pixels, RightX, 2).ShouldBeGreaterThan(200, "instance 2 is blue, on the right");

            // And nowhere else. Without a divisor all three stack on the left,
            // which the two samples above already catch; this catches the
            // opposite failure of a quad drawn at the wrong scale.
            Channel(pixels, GapX, 0).ShouldBeLessThan(50);
            Channel(pixels, GapX, 1).ShouldBeLessThan(50);
            Channel(pixels, GapX, 2).ShouldBeLessThan(50);
        }
        finally
        {
            renderer.DestroyRenderTarget(output);
            instances.Dispose();
            renderer.DestroyMesh(mesh);
            shader.Dispose();
        }
    }

    [Fact]
    public void Drawing_zero_instances_draws_nothing_rather_than_throwing()
    {
        // A batch can be culled to empty between being formed and being
        // submitted, and making every caller guard is how one site forgets.
        OpenGLRenderer renderer = _fixture.Renderer;
        ShaderProgram shader = renderer.CreateShaderFromSource(Source);
        Mesh mesh = renderer.CreateMesh(QuadVertices, QuadIndices, VertexAttribute.StandardLayout);
        InstanceBuffer instances = renderer.CreateInstanceBuffer(
            Instances, VertexAttribute.StandardInstanceLayout, shader);
        RenderTarget output = renderer.CreateRenderTarget(new RenderTargetDesc(Width, Height));

        try
        {
            renderer.BeginPass(output, PassClear.To(new Vector4(0f, 0f, 0f, 1f)));
            mesh.DrawInstanced(instances, 0);
            renderer.EndPass();

            ReadPixels(output)[0].ShouldBe((byte)0);
        }
        finally
        {
            renderer.DestroyRenderTarget(output);
            instances.Dispose();
            renderer.DestroyMesh(mesh);
            shader.Dispose();
        }
    }

    // --- The layout contract -------------------------------------------------

    [Fact]
    public void An_instance_buffer_refuses_a_per_vertex_layout()
    {
        // Bound to the instance buffer, a per-vertex attribute reads one element
        // per INSTANCE, which renders as garbage with nothing reporting why.
        ShaderProgram shader = _fixture.Renderer.CreateShaderFromSource(Source);
        try
        {
            Should.Throw<ArgumentException>(() =>
                _fixture.Renderer.CreateInstanceBuffer(4, VertexAttribute.StandardLayout, shader));
        }
        finally
        {
            shader.Dispose();
        }
    }

    [Fact]
    public void An_update_that_does_not_match_the_count_is_refused()
    {
        ShaderProgram shader = _fixture.Renderer.CreateShaderFromSource(Source);
        InstanceBuffer instances = _fixture.Renderer.CreateInstanceBuffer(
            4, VertexAttribute.StandardInstanceLayout, shader);
        try
        {
            // Short by one matrix. Drawn rather than refused, this reads past
            // the data the caller supplied.
            Should.Throw<ArgumentException>(() => instances.Update(new float[32], 3));

            // And past the buffer's own capacity.
            Should.Throw<ArgumentOutOfRangeException>(() => instances.Update(new float[80], 5));
        }
        finally
        {
            instances.Dispose();
            shader.Dispose();
        }
    }

    [Fact]
    public void A_buffer_from_another_backend_is_refused()
    {
        // The cast would otherwise be an InvalidCastException from inside a
        // draw call, one frame after the mistake was made.
        OpenGLRenderer renderer = _fixture.Renderer;
        Mesh mesh = renderer.CreateMesh(QuadVertices, QuadIndices, VertexAttribute.StandardLayout);
        try
        {
            Should.Throw<ArgumentException>(() => mesh.DrawInstanced(new AlienBuffer(), 1));
        }
        finally
        {
            renderer.DestroyMesh(mesh);
        }
    }

    private sealed class AlienBuffer : InstanceBuffer
    {
        public AlienBuffer()
        {
            Capacity = 1;
            FloatsPerInstance = 16;
        }

        public override int Append(ReadOnlySpan<float> data, int instanceCount) => 0;
        public override void Dispose() { }
    }

    // --- Several writes in one frame -----------------------------------------

    [Fact]
    public void Appending_twice_gives_two_distinct_ranges()
    {
        // The shadow pass writes once per cascade into ONE buffer, and D3D12
        // records the whole frame into a single command list submitted at the
        // end: a second write at offset zero retroactively changes what the
        // first cascade's already-recorded draws will read. Appending is what
        // makes sharing a buffer within a frame correct, and the offsets are
        // the whole of it.
        ShaderProgram shader = _fixture.Renderer.CreateShaderFromSource(Source);
        InstanceBuffer instances = _fixture.Renderer.CreateInstanceBuffer(
            8, VertexAttribute.StandardInstanceLayout, shader);
        try
        {
            instances.BeginFrame();
            instances.Cursor.ShouldBe(0);

            instances.Append(new float[3 * 16], 3).ShouldBe(0);
            instances.Cursor.ShouldBe(3);
            instances.Remaining.ShouldBe(5);

            // Past the first range, not on top of it.
            instances.Append(new float[2 * 16], 2).ShouldBe(3);
            instances.Cursor.ShouldBe(5);
        }
        finally
        {
            instances.Dispose();
            shader.Dispose();
        }
    }

    [Fact]
    public void A_new_frame_rewinds_to_the_start()
    {
        ShaderProgram shader = _fixture.Renderer.CreateShaderFromSource(Source);
        InstanceBuffer instances = _fixture.Renderer.CreateInstanceBuffer(
            8, VertexAttribute.StandardInstanceLayout, shader);
        try
        {
            instances.BeginFrame();
            instances.Append(new float[4 * 16], 4);

            instances.BeginFrame();

            instances.Cursor.ShouldBe(0);
            instances.Remaining.ShouldBe(8);
            instances.Append(new float[16], 1).ShouldBe(0);
        }
        finally
        {
            instances.Dispose();
            shader.Dispose();
        }
    }

    [Fact]
    public void Appending_past_the_end_is_refused_rather_than_wrapping()
    {
        // A wrap would silently draw one pass's geometry with another pass's
        // transforms, which is a picture rather than an error.
        ShaderProgram shader = _fixture.Renderer.CreateShaderFromSource(Source);
        InstanceBuffer instances = _fixture.Renderer.CreateInstanceBuffer(
            4, VertexAttribute.StandardInstanceLayout, shader);
        try
        {
            instances.BeginFrame();
            instances.Append(new float[3 * 16], 3);

            instances.Remaining.ShouldBe(1);
            Should.Throw<ArgumentOutOfRangeException>(() => instances.Append(new float[2 * 16], 2));
        }
        finally
        {
            instances.Dispose();
            shader.Dispose();
        }
    }

    [Fact]
    public void Two_appended_ranges_draw_their_own_instances()
    {
        // The pixel form of the same claim: write instance 0's transform, then
        // instances 1 and 2 in a SECOND append, and draw only the second range.
        // If the second write had landed at offset zero, the range drawn here
        // would be instance 0's data and the two quads would be in the wrong
        // places wearing the wrong colours.
        OpenGLRenderer renderer = _fixture.Renderer;
        ShaderProgram shader = renderer.CreateShaderFromSource(Source);
        Mesh mesh = renderer.CreateMesh(QuadVertices, QuadIndices, VertexAttribute.StandardLayout);

        PipelineBlob blob = new SpectraShadeCompiler()
            .Compile(Source, [GraphicsBackend.OpenGL])
            .GetPipeline(GraphicsBackend.OpenGL)
            .ShouldNotBeNull();
        VertexAttribute[] perInstance = VertexAttribute.ForSlot(
            VertexAttribute.FromShaderInputs(blob.VertexInputs), VertexAttribute.InstanceSlot);

        InstanceBuffer instances = renderer.CreateInstanceBuffer(Instances, perInstance, shader);
        RenderTarget output = renderer.CreateRenderTarget(new RenderTargetDesc(Width, Height));

        try
        {
            float[] all = BuildInstanceData();
            instances.BeginFrame();
            instances.Append(all.AsSpan(0, 20), 1).ShouldBe(0);
            int second = instances.Append(all.AsSpan(20, 40), 2);
            second.ShouldBe(1);

            renderer.BeginPass(output, PassClear.To(new Vector4(0f, 0f, 0f, 1f)));
            shader.Use();
            shader.SetUniform("viewProjection", Matrix4x4.Identity);
            mesh.DrawInstanced(instances, 2, second);
            renderer.EndPass();

            byte[] pixels = ReadPixels(output);

            // Instances 1 and 2 only: green in the middle, blue on the right,
            // and nothing red on the left where instance 0 would have gone.
            Channel(pixels, MiddleX, 1).ShouldBeGreaterThan(200);
            Channel(pixels, RightX, 2).ShouldBeGreaterThan(200);
            Channel(pixels, LeftX, 0).ShouldBeLessThan(50, "instance 0 was not in the drawn range");
        }
        finally
        {
            renderer.DestroyRenderTarget(output);
            instances.Dispose();
            renderer.DestroyMesh(mesh);
            shader.Dispose();
        }
    }

    // --- helpers -------------------------------------------------------------

    // 20 floats per instance: the world matrix, then the tint. Written in
    // declaration order, which is the order the attributes were reported in.
    private static float[] BuildInstanceData()
    {
        var data = new float[Instances * 20];
        for (int i = 0; i < Instances; i++)
        {
            Matrix4x4 model = Matrix4x4.CreateTranslation(OffsetsX[i], 0f, 0f);
            int at = i * 20;

            // Raw row order, no transpose: GL reads the 16 floats as columns,
            // so this hands the shader the transpose, which is exactly what
            // turns .NET's row-vector convention into GLSL's column-vector one.
            // The same thing UniformMatrix4(..., transpose: false) already does
            // for uModel, so both paths agree.
            data[at + 0] = model.M11; data[at + 1] = model.M12; data[at + 2] = model.M13; data[at + 3] = model.M14;
            data[at + 4] = model.M21; data[at + 5] = model.M22; data[at + 6] = model.M23; data[at + 7] = model.M24;
            data[at + 8] = model.M31; data[at + 9] = model.M32; data[at + 10] = model.M33; data[at + 11] = model.M34;
            data[at + 12] = model.M41; data[at + 13] = model.M42; data[at + 14] = model.M43; data[at + 15] = model.M44;

            data[at + 16] = Tints[i].X;
            data[at + 17] = Tints[i].Y;
            data[at + 18] = Tints[i].Z;
            data[at + 19] = Tints[i].W;
        }
        return data;
    }

    private static int Channel(byte[] pixels, int x, int channel) =>
        pixels[((SampleY * Width) + x) * 4 + channel];

    private unsafe byte[] ReadPixels(RenderTarget target)
    {
        GL gl = _fixture.Gl;
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        gl.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, ((OpenGLTexture)target.ColorTexture!).Handle, 0);

        var pixels = new byte[Width * Height * 4];
        fixed (byte* p = pixels)
            gl.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.DeleteFramebuffer(fbo);
        return pixels;
    }
}
