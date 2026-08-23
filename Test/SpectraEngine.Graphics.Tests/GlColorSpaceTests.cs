using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
// Silk.NET.OpenGL has its own Texture; every bare mention here is the engine's.
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// R2's OpenGL half, against a real driver.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves of GL's sRGB story are things a driver can refuse.</b> The
/// D3D backends name an sRGB format and the runtime either accepts it or fails
/// loudly; GL instead has a state enable that silently does nothing unless the
/// default framebuffer happens to have been created sRGB-capable, and Silk.NET
/// exposes no way to ask GLFW for that. So this is the one part of the
/// milestone whose correctness cannot be established by reading the code.
/// </para>
/// <para>
/// The texture half is checked the same way: the internal format a driver
/// actually gave a texture is queryable, and it is what decides whether the
/// sampler decodes.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class GlColorSpaceTests
{
    private readonly GlRendererFixture _fixture;

    public GlColorSpaceTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Output_reaches_the_display_encoded_by_one_route_or_the_other()
    {
        // Stated as a fact about the picture rather than about the mechanism.
        // Which route is in use is a property of the driver and the windowing
        // library, and both are allowed to change; whether the picture is
        // encoded is not.
        _fixture.Renderer.FramebufferSrgb.ShouldBeTrue(
            "neither the window framebuffer nor the offscreen fallback is encoding, so shader " +
            "output reaches the display uncorrected and will not match D3D11 or D3D12");
    }

    [Fact]
    public unsafe void A_linear_clear_lands_on_the_window_as_its_display_value()
    {
        // The end-to-end assertion, and the only one that would have caught the
        // problem this milestone actually ran into: enabling GL_FRAMEBUFFER_SRGB
        // on a framebuffer that was never created sRGB-capable succeeds, reports
        // no error, and does nothing at all. Reading a pixel back is the only
        // way to tell that apart from working.
        //
        // 0.2140 linear is 0.5 sRGB is byte 128. If encoding were skipped the
        // byte would be 55, and every surface in the engine would be that much
        // too dark.
        GL gl = _fixture.Gl;
        const int W = 8, H = 8;
        const float Linear = 0.2140f;

        bool offscreen = _fixture.Renderer.BeginSrgbTargetForTest(W, H);
        try
        {
            gl.Viewport(0, 0, W, H);
            gl.ClearColor(Linear, Linear, Linear, 1f);
            gl.Clear((uint)ClearBufferMask.ColorBufferBit);
        }
        finally
        {
            if (offscreen)
                _fixture.Renderer.PresentSrgbTargetForTest(W, H);
        }

        var pixel = new byte[4];
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        fixed (byte* p = pixel)
            gl.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        // One code of slack for the driver's rounding of the transfer function.
        ((int)pixel[0]).ShouldBeInRange(127, 129);
        ((int)pixel[1]).ShouldBeInRange(127, 129);
        ((int)pixel[2]).ShouldBeInRange(127, 129);
    }

    [Fact]
    public unsafe void A_colour_texture_gets_an_srgb_internal_format()
    {
        // 2x2 so the query has a real level 0 to answer about.
        ReadOnlySpan<byte> pixels = [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255];

        Texture texture = _fixture.Renderer.CreateTexture(
            pixels, 2, 2, TextureFormat.Rgba8, TextureColorSpace.Srgb,
            TextureFilter.Linear, TextureWrap.Clamp);

        // GL_SRGB8_ALPHA8 is what makes the hardware decode BEFORE filtering,
        // which is the entire reason R2 is a format flag and not a pow() in the
        // fragment shader.
        InternalFormat(texture).ShouldBe((int)GLEnum.Srgb8Alpha8);
        texture.ColorSpace.ShouldBe(TextureColorSpace.Srgb);

        _fixture.Renderer.DestroyTexture(texture);
    }

    [Fact]
    public unsafe void A_data_texture_is_left_alone()
    {
        ReadOnlySpan<byte> pixels = [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255];

        Texture texture = _fixture.Renderer.CreateTexture(
            pixels, 2, 2, TextureFormat.Rgba8, TextureColorSpace.Linear,
            TextureFilter.Linear, TextureWrap.Clamp);

        // A normal map decoded as sRGB bends every surface it is applied to, so
        // the opt-out has to reach the driver and not merely the C# enum.
        InternalFormat(texture).ShouldBe((int)GLEnum.Rgba8);
        texture.ColorSpace.ShouldBe(TextureColorSpace.Linear);

        _fixture.Renderer.DestroyTexture(texture);
    }

    [Fact]
    public unsafe void A_single_channel_texture_asked_for_srgb_stays_linear()
    {
        ReadOnlySpan<byte> pixels = [0, 64, 128, 255];

        // There is no GL_SR8. Asking for one must degrade rather than throw or
        // produce some other format, and it must report what it did.
        Texture texture = _fixture.Renderer.CreateTexture(
            pixels, 2, 2, TextureFormat.R8, TextureColorSpace.Srgb,
            TextureFilter.Nearest, TextureWrap.Clamp);

        InternalFormat(texture).ShouldBe((int)GLEnum.R8);
        texture.ColorSpace.ShouldBe(TextureColorSpace.Linear);

        _fixture.Renderer.DestroyTexture(texture);
    }

    // What the driver says it stored, rather than what we asked it to. Reaching
    // through the internal texture type keeps this out of the engine's public
    // surface: nothing in a shipped build needs to read a texture back.
    private unsafe int InternalFormat(Texture texture)
    {
        GL gl = _fixture.Gl;
        gl.BindTexture(TextureTarget.Texture2D, ((OpenGLTexture)texture).Handle);

        int format = 0;
        gl.GetTexLevelParameter(GLEnum.Texture2D, 0, GLEnum.TextureInternalFormat, &format);

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return format;
    }
}
