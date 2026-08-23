using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// Offscreen render targets against a real driver: does a pass actually land in
/// the target, and does the target survive a resize.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading a pixel back is the only assertion that means anything here.</b>
/// A target that is never bound, a viewport sized from the window instead of
/// from the target, and a clear that goes to the back buffer instead all leave
/// every object in this API looking correct and every call returning success.
/// What they change is the contents of a texture, so that is what gets checked.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class GlRenderTargetTests
{
    private readonly GlRendererFixture _fixture;

    public GlRenderTargetTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void A_pass_lands_in_the_target_and_not_on_the_window()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(new RenderTargetDesc(32, 32));
        try
        {
            // A colour nothing else in this process clears to, so a pixel that
            // reads back as this one cannot have come from somewhere else.
            var green = new System.Numerics.Vector4(0f, 1f, 0f, 1f);

            renderer.BeginPass(target, PassClear.To(green));
            renderer.PassSize.X.ShouldBe(32);
            renderer.PassSize.Y.ShouldBe(32);
            renderer.EndPass();

            ReadPixel(target).ShouldBe((0, 255, 0));
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void The_pass_size_comes_from_the_target_not_the_window()
    {
        // The trap the whole seam exists to avoid: every pipeline used to build
        // its viewport and its camera aspect from the window size, which is
        // right only while the window is the only target. The fixture's window
        // is 64x64 and this target is not square, so a leaked window size would
        // show up as the wrong aspect here.
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(new RenderTargetDesc(80, 20));
        try
        {
            renderer.BeginPass(target, PassClear.Keep);

            renderer.PassSize.X.ShouldBe(80);
            renderer.PassSize.Y.ShouldBe(20);
            renderer.PassAspectRatio!.Value.ShouldBe(4f, 1e-6f);

            renderer.EndPass();
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void A_resize_keeps_the_colour_texture_identity()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(new RenderTargetDesc(16, 16));
        try
        {
            Texture before = target.ColorTexture;
            uint handleBefore = ((OpenGLTexture)before).Handle;

            target.Resize(48, 24);

            // Identity, not equality: a material that sampled this target holds
            // this exact object. Replacing it on resize is how every editor
            // viewport would end up pointing at a destroyed texture.
            target.ColorTexture.ShouldBeSameAs(before);
            ((OpenGLTexture)target.ColorTexture).Handle.ShouldBe(handleBefore);

            before.Width.ShouldBe(48);
            before.Height.ShouldBe(24);
            target.Width.ShouldBe(48);
            target.Height.ShouldBe(24);
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void A_resized_target_still_renders()
    {
        // The framebuffer has to be re-completed after its attachment's storage
        // is respecified. Skipping that leaves an incomplete FBO, which draws
        // nothing and reports no error until something reads the result.
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(new RenderTargetDesc(16, 16));
        try
        {
            target.Resize(64, 8);

            var blue = new System.Numerics.Vector4(0f, 0f, 1f, 1f);
            renderer.BeginPass(target, PassClear.To(blue));
            renderer.PassSize.X.ShouldBe(64);
            renderer.EndPass();

            ReadPixel(target).ShouldBe((0, 0, 255));
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void A_resize_to_the_same_size_is_a_no_op()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(new RenderTargetDesc(24, 24));
        try
        {
            // Called every frame by anything that keeps a target matched to a
            // viewport, so it has to be free rather than a reallocation.
            target.Resize(24, 24);
            target.Width.ShouldBe(24);
            target.Height.ShouldBe(24);
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void An_srgb_target_encodes_what_is_written_into_it()
    {
        // Same hardware conversion R2 gave the back buffer, now on a texture
        // something else will sample. 0.2140 linear is byte 128.
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(
            new RenderTargetDesc(8, 8, ColorSpace: TextureColorSpace.Srgb));
        try
        {
            target.ColorTexture.ColorSpace.ShouldBe(TextureColorSpace.Srgb);

            renderer.BeginPass(target, PassClear.To(new System.Numerics.Vector4(0.2140f, 0.2140f, 0.2140f, 1f)));
            renderer.EndPass();

            (int r, _, _) = ReadPixel(target);
            r.ShouldBeInRange(127, 129);
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void A_linear_target_leaves_what_is_written_alone()
    {
        // The control for the test above, and the case an HDR chain needs: a
        // target carrying light values between passes must not encode.
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(
            new RenderTargetDesc(8, 8, ColorSpace: TextureColorSpace.Linear));
        try
        {
            renderer.BeginPass(target, PassClear.To(new System.Numerics.Vector4(0.2140f, 0.2140f, 0.2140f, 1f)));
            renderer.EndPass();

            (int r, _, _) = ReadPixel(target);
            r.ShouldBeInRange(53, 56); // 0.2140 * 255, unconverted
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void Ending_a_target_pass_puts_the_window_back()
    {
        // Otherwise the next pipeline that forgets to open a pass keeps drawing
        // into a texture nobody is looking at, and the window simply stops
        // updating with no error anywhere.
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(new RenderTargetDesc(16, 16));
        try
        {
            renderer.BeginPass(target, PassClear.Keep);
            renderer.EndPass();

            _fixture.Gl.GetInteger(GLEnum.DrawFramebufferBinding, out int bound);
            bound.ShouldBe(0);
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void A_target_cannot_be_destroyed_while_a_pass_is_drawing_into_it()
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(new RenderTargetDesc(16, 16));

        renderer.BeginPass(target, PassClear.Keep);
        Should.Throw<InvalidOperationException>(() => renderer.DestroyRenderTarget(target));
        renderer.EndPass();

        renderer.DestroyRenderTarget(target);
    }

    [Fact]
    public void An_hdr_target_stores_values_above_one()
    {
        // The entire reason for an intermediate target. An 8-bit buffer clamps
        // this to 1.0 and the tone curve downstream then has nothing left to
        // work with; the whole point of rendering offscreen first is that the
        // highlights survive as far as the resolve.
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(
            new RenderTargetDesc(8, 8, TextureFormat.Rgba16Float));
        try
        {
            target.ColorTexture.Format.ShouldBe(TextureFormat.Rgba16Float);
            // A float format has no sRGB variant, so a request for one resolves
            // to linear rather than throwing.
            target.ColorTexture.ColorSpace.ShouldBe(TextureColorSpace.Linear);

            renderer.BeginPass(target, PassClear.To(new System.Numerics.Vector4(4f, 2f, 0.5f, 1f)));
            renderer.EndPass();

            (float r, float g, float b) = ReadPixelFloat(target);
            r.ShouldBe(4f, 0.01f);
            g.ShouldBe(2f, 0.01f);
            b.ShouldBe(0.5f, 0.01f);
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void An_eight_bit_target_clamps_the_same_value()
    {
        // The control, and the evidence that the test above is measuring the
        // format rather than the clear. Same clear, an Rgba8 target: 4.0 comes
        // back as 1.0, which is what an LDR intermediate would cost.
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget target = renderer.CreateRenderTarget(new RenderTargetDesc(8, 8));
        try
        {
            renderer.BeginPass(target, PassClear.To(new System.Numerics.Vector4(4f, 2f, 0.5f, 1f)));
            renderer.EndPass();

            (int r, int g, _) = ReadPixel(target);
            r.ShouldBe(255);
            g.ShouldBe(255);
        }
        finally
        {
            renderer.DestroyRenderTarget(target);
        }
    }

    [Fact]
    public void A_float_format_is_refused_as_an_uploaded_texture()
    {
        // Nothing decodes an image file to half-floats, so a byte array cannot
        // fill one. Refusing beats reinterpreting the bytes, which produces a
        // texture full of denormals and no error anywhere.
        var pixels = new byte[] { 255, 255, 255, 255 };

        Should.Throw<ArgumentOutOfRangeException>(() => _fixture.Renderer.CreateTexture(
            pixels, 1, 1, TextureFormat.Rgba16Float, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp));
    }

    // Reads texel (0,0) of an HDR target, as floats. The byte reader below
    // cannot see what makes an HDR target HDR: it clamps to [0,1] on the way
    // out and quantises what survives.
    private unsafe (float R, float G, float B) ReadPixelFloat(RenderTarget target)
    {
        GL gl = _fixture.Gl;
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        gl.FramebufferTexture2D(
            FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, ((OpenGLTexture)target.ColorTexture).Handle, 0);

        var pixel = new float[4];
        fixed (float* p = pixel)
            gl.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, p);

        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        gl.DeleteFramebuffer(fbo);
        return (pixel[0], pixel[1], pixel[2]);
    }

    // Reads texel (0,0) of a target's colour attachment as bytes.
    private unsafe (int R, int G, int B) ReadPixel(RenderTarget target)
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
        return (pixel[0], pixel[1], pixel[2]);
    }
}
