using Silk.NET.OpenGL;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using System;
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// A cooked, block-compressed texture uploaded through
/// <see cref="TextureUploadDesc"/> and sampled against a real driver.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the gate for the whole compressed path, because every way it can
/// fail renders a picture rather than raising anything.</b> A row pitch honoured
/// as if it were tight shears the image; a level uploaded at the wrong index
/// blurs or blacks it; an sRGB internal format where a linear one was asked for
/// changes every value and reports nothing. GL has no debug layer the engine
/// reads and D3D's counts errors that none of these produce, so the only
/// instrument is a rendered picture read back texel by texel.
/// </para>
/// <para>
/// The fixture is asymmetric on both axes for the reason
/// <see cref="TextureOrientationProbe"/> gives: a symmetric one makes a flip, a
/// transpose and a correct upload the same picture.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class CompressedTextureGlTests
{
    private readonly GlRendererFixture _fixture;

    private const int TargetSize = 16;

    public CompressedTextureGlTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void The_driver_offers_BPTC_at_all()
    {
        // Asked first and on its own, so a machine without BC7 says so in one
        // line instead of failing four pixel assertions that look like upload
        // bugs. BPTC is core in GL 4.2 and an ARB extension before it; the
        // fixture's context is whatever Silk's default asks for, so both routes
        // count.
        GL gl = _fixture.Gl;
        string version = gl.GetStringS(StringName.Version) ?? string.Empty;

        bool extension = false;
        gl.GetInteger(GetPName.NumExtensions, out int count);
        for (int i = 0; i < count && !extension; i++)
            extension = gl.GetStringS(StringName.Extensions, (uint)i) == "GL_ARB_texture_compression_bptc";

        extension.ShouldBeTrue(
            $"BC7 needs GL_ARB_texture_compression_bptc (core since 4.2); this context reports '{version}'.");
    }

    [Fact]
    public void A_two_mip_BC7_texture_samples_its_base_level()
    {
        // Nearest, so no mip is selected and the base level is what reaches the
        // screen: this measures the level 0 upload alone, and the level 1 test
        // below measures the other.
        AssertBaseLevelIsUpright(padded: false, TextureFilter.Nearest);
    }

    [Fact]
    public void A_padded_row_pitch_produces_the_same_picture()
    {
        // The declared pitch is 256 where the tight one is 64, which is exactly
        // the layout D3D12's own copy alignment produces and the case a backend
        // that recomputed the pitch would get wrong. Sheared rows would scatter
        // the quadrants; a whole-level memcpy would leave three quarters of the
        // texture undefined.
        AssertBaseLevelIsUpright(padded: true, TextureFilter.Nearest);
    }

    [Fact]
    public void The_second_level_of_a_supplied_chain_is_the_level_that_was_supplied()
    {
        // Minified hard enough that the level of detail lands past the end of the
        // chain and clamps to the last level there is. Every colour in level 0
        // has a dark channel and level 1 has none, so "bright everywhere" cannot
        // be level 0 arriving by another route - and an incomplete texture, the
        // failure this really guards, samples as black.
        OpenGLRenderer renderer = _fixture.Renderer;
        byte[] payload = Bc7Fixture.BuildTwoLevelPayload(padded: false, out TextureMipDesc[] mips);

        Texture source = renderer.CreateTexture(new TextureUploadDesc(
            TextureFormat.Bc7, TextureColorSpace.Linear, payload, mips,
            TextureFilter.LinearMipmap, TextureWrap.Clamp));

        // 16 texels across four pixels is a level-of-detail of 2, which with a
        // two-level chain clamps to level 1.
        const int minifiedSize = 4;
        RenderTarget output = renderer.CreateRenderTarget(
            new RenderTargetDesc(minifiedSize, minifiedSize, TextureFormat.Rgba8, TextureColorSpace.Linear));

        try
        {
            renderer.DrawOrientationQuad(source, output, OrientationQuad.Coverage.Full);

            for (int y = 0; y < minifiedSize; y++)
            {
                for (int x = 0; x < minifiedSize; x++)
                {
                    (byte r, byte g, byte b, _) = renderer.ReadTargetPixel(output, x, y);
                    string where = $"({x}, {y}) read {r}, {g}, {b}";
                    r.ShouldBeGreaterThan((byte)120, where);
                    g.ShouldBeGreaterThan((byte)120, where);
                    b.ShouldBeGreaterThan((byte)120, where);
                }
            }
        }
        finally
        {
            renderer.DestroyRenderTarget(output);
            renderer.DestroyTexture(source);
        }
    }

    [Fact]
    public void A_BC7_upload_reports_its_own_size_and_resolved_colour_space()
    {
        // The base level's size, not the payload's shape: a texture that
        // reported its block count would size every material's UV maths wrongly
        // and still render something.
        OpenGLRenderer renderer = _fixture.Renderer;
        byte[] payload = Bc7Fixture.BuildTwoLevelPayload(padded: false, out TextureMipDesc[] mips);

        Texture source = renderer.CreateTexture(new TextureUploadDesc(
            TextureFormat.Bc7, TextureColorSpace.Srgb, payload, mips,
            TextureFilter.Nearest, TextureWrap.Clamp));

        try
        {
            source.Width.ShouldBe(Bc7Fixture.BaseSize);
            source.Height.ShouldBe(Bc7Fixture.BaseSize);
            source.Format.ShouldBe(TextureFormat.Bc7);
            // BC7 has an sRGB form, so the request survives; BC4 and BC5 would
            // come back linear here, which TextureUploadDescTests pins.
            source.ColorSpace.ShouldBe(TextureColorSpace.Srgb);
        }
        finally
        {
            renderer.DestroyTexture(source);
        }
    }

    private void AssertBaseLevelIsUpright(bool padded, TextureFilter filter)
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        byte[] payload = Bc7Fixture.BuildTwoLevelPayload(padded, out TextureMipDesc[] mips);

        // Linear on both sides, so the only transform between the block's
        // decoded bytes and the read-back bytes is the tone curve, which is
        // monotone per channel and cannot turn one quadrant colour into another.
        Texture source = renderer.CreateTexture(new TextureUploadDesc(
            TextureFormat.Bc7, TextureColorSpace.Linear, payload, mips, filter, TextureWrap.Clamp));
        RenderTarget output = renderer.CreateRenderTarget(
            new RenderTargetDesc(TargetSize, TargetSize, TextureFormat.Rgba8, TextureColorSpace.Linear));

        try
        {
            renderer.DrawOrientationQuad(source, output, OrientationQuad.Coverage.Full);

            var reading = new TextureOrientationProbe.Reading(
                Read(renderer, output, 0, TargetSize - 1),
                Read(renderer, output, TargetSize - 1, TargetSize - 1),
                Read(renderer, output, 0, 0),
                Read(renderer, output, TargetSize - 1, 0));

            reading.MatchesAuthoredImage.ShouldBeTrue(
                $"a {(padded ? "padded" : "tight")} BC7 upload rendered as: {reading}. Verdict: {reading.Verdict}");
        }
        finally
        {
            renderer.DestroyRenderTarget(output);
            renderer.DestroyTexture(source);
        }
    }

    private static TextureOrientationProbe.Quadrant Read(
        Renderer renderer, RenderTarget target, int x, int y)
    {
        (byte r, byte g, byte b, _) = renderer.ReadTargetPixel(target, x, y);
        return TextureOrientationProbe.Classify(r, g, b);
    }
}
