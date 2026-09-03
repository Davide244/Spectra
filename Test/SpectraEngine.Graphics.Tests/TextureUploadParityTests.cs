using SpectraEngine.Core.Graphics;
using System;
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The measurement both parity suites run: two textures built from the same
/// bytes by the two entry points, drawn through the same quad, compared byte
/// for byte.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rendering is the only honest comparison here.</b> A texture object exposes
/// its size, its format and its resolved colour space and nothing about the
/// texels the driver actually holds, so asserting on those would pass for a
/// texture uploaded at the wrong pitch, into the wrong level, or through the
/// wrong internal format. The picture is the artefact both paths exist to
/// produce.
/// </para>
/// <para>
/// <b>Exact equality, not a tolerance.</b> Two identical uploads drawn through
/// one shader into one target in one session differ by nothing at all; a
/// tolerance here would let a genuine one-code difference through, which is what
/// a wrong internal format on a near-white texture looks like.
/// </para>
/// </remarks>
internal static class TextureUploadParity
{
    /// <summary>Small enough to read whole, large enough to catch a sheared row.</summary>
    internal const int TargetSize = 16;

    /// <summary>
    /// An asymmetric RGBA8 source, tightly packed, row 0 first. Deliberately not
    /// a flat colour: a texture where every texel is the same is identical under
    /// any pitch, any flip and any transpose.
    /// </summary>
    internal static byte[] BuildSource(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i + 0] = (byte)(x * 255 / Math.Max(1, width - 1));
                pixels[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[i + 2] = (byte)((x + y) % 2 == 0 ? 20 : 200);
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>
    /// Draws <paramref name="source"/> over a fresh target and returns the whole
    /// picture as RGBA8, row 0 at the bottom.
    /// </summary>
    internal static byte[] RenderWhole(Renderer renderer, Texture source)
    {
        RenderTarget output = renderer.CreateRenderTarget(
            new RenderTargetDesc(TargetSize, TargetSize, TextureFormat.Rgba8, TextureColorSpace.Linear));
        try
        {
            renderer.DrawOrientationQuad(source, output, OrientationQuad.Coverage.Full);
            var picture = new byte[TargetSize * TargetSize * 4];
            renderer.ReadTargetPixels(output, picture);
            return picture;
        }
        finally
        {
            renderer.DestroyRenderTarget(output);
        }
    }

    /// <summary>
    /// An 8x8 RGB8 image with four distinct quadrants, laid out row 0 first with
    /// row 0 at the bottom of the picture, exactly as <c>ImageDecoder</c>
    /// produces.
    /// </summary>
    internal static byte[] BuildRgb8Quadrants(int size)
    {
        // Named as the picture is seen, which is why row 0 gets the BOTTOM pair:
        // the engine's convention is that texel row 0 is sampled at v = 0.
        ReadOnlySpan<byte> topLeft = [255, 0, 0];
        ReadOnlySpan<byte> topRight = [0, 255, 0];
        ReadOnlySpan<byte> bottomLeft = [0, 0, 255];
        ReadOnlySpan<byte> bottomRight = [255, 255, 0];

        var pixels = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
        {
            bool upperHalf = y >= size / 2;
            for (int x = 0; x < size; x++)
            {
                bool rightHalf = x >= size / 2;
                ReadOnlySpan<byte> texel = upperHalf
                    ? (rightHalf ? topRight : topLeft)
                    : (rightHalf ? bottomRight : bottomLeft);
                texel.CopyTo(pixels.AsSpan((y * size + x) * 3, 3));
            }
        }
        return pixels;
    }

    /// <summary>
    /// An RGB8 source with a generated mip chain must still arrive upright.
    /// </summary>
    /// <remarks>
    /// <b>The one live combination whose arithmetic changes shape on the way
    /// through.</b> No API has a 24-bit texture format, so the payload is
    /// rewritten as RGBA8 and the levels it describes are rewritten with it;
    /// anything downstream that goes on measuring rows against the REQUESTED
    /// format computes three quarters of the real stride. D3D12 in particular
    /// then rebuilds the chain in software off that stride. Nothing throws for
    /// certain and nothing logs; the picture shears. <c>ImageDecoder</c> emits
    /// this format for every three-channel PNG, so it is not a hypothetical.
    /// </remarks>
    internal static void AssertRgb8WithMipsIsUpright(Renderer renderer, string backend)
    {
        const int size = 8;
        byte[] pixels = BuildRgb8Quadrants(size);

        Texture source = renderer.CreateTexture(
            pixels, size, size, TextureFormat.Rgb8, TextureColorSpace.Linear,
            TextureFilter.LinearMipmap, TextureWrap.Clamp);

        try
        {
            // Magnified onto a larger target, so the base level is what is being
            // read and the generated levels only have to exist.
            byte[] picture = RenderWhole(renderer, source);

            var reading = new TextureOrientationProbe.Reading(
                ClassifyAt(picture, 0, TargetSize - 1),
                ClassifyAt(picture, TargetSize - 1, TargetSize - 1),
                ClassifyAt(picture, 0, 0),
                ClassifyAt(picture, TargetSize - 1, 0));

            reading.MatchesAuthoredImage.ShouldBeTrue(
                $"{backend} rendered an RGB8 mipmapped upload as: {reading}. Verdict: {reading.Verdict}");
        }
        finally
        {
            renderer.DestroyTexture(source);
        }
    }

    /// <summary>Classifies one texel of a whole-target readback. Row 0 is the bottom.</summary>
    internal static TextureOrientationProbe.Quadrant ClassifyAt(byte[] picture, int x, int y)
    {
        int i = (y * TargetSize + x) * 4;
        return TextureOrientationProbe.Classify(picture[i], picture[i + 1], picture[i + 2]);
    }

    /// <summary>
    /// The whole assertion: the old single-span call and an equivalent
    /// descriptor must produce the same picture, and that picture must not be
    /// blank.
    /// </summary>
    internal static void AssertBothPathsAgree(Renderer renderer, string backend)
    {
        const int width = 8;
        const int height = 8;
        byte[] pixels = BuildSource(width, height);

        Texture viaSpan = renderer.CreateTexture(
            pixels, width, height, TextureFormat.Rgba8, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp);

        TextureMipDesc[] mips = [new TextureMipDesc(width, height, 0, width * 4)];
        Texture viaDesc = renderer.CreateTexture(new TextureUploadDesc(
            TextureFormat.Rgba8, TextureColorSpace.Linear, pixels, mips,
            TextureFilter.Nearest, TextureWrap.Clamp));

        try
        {
            byte[] fromSpan = RenderWhole(renderer, viaSpan);
            byte[] fromDesc = RenderWhole(renderer, viaDesc);

            // A blank picture agrees with a blank picture perfectly, so the
            // reference is checked for variation before the two are compared -
            // the same guard --viewport-compare makes for the same reason.
            ViewportCompare.HasVariation(fromSpan).ShouldBeTrue(
                $"{backend} rendered a flat picture, so the comparison below would prove nothing.");

            for (int i = 0; i < fromSpan.Length; i++)
            {
                if (fromSpan[i] == fromDesc[i]) continue;

                int texel = i / 4;
                throw new Xunit.Sdk.XunitException(
                    $"{backend}: the two upload paths disagree at texel " +
                    $"({texel % TargetSize}, {texel / TargetSize}) channel {i % 4}: " +
                    $"span path {fromSpan[i]}, descriptor path {fromDesc[i]}.");
            }
        }
        finally
        {
            renderer.DestroyTexture(viaDesc);
            renderer.DestroyTexture(viaSpan);
        }
    }
}

/// <summary>
/// The two <c>CreateTexture</c> entry points agree on OpenGL.
/// </summary>
/// <remarks>
/// The old overload is now expressed over the new one, so this is a claim about
/// that expression rather than about two implementations - which is exactly the
/// claim worth pinning, because the day somebody reintroduces a second path this
/// is what notices.
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class TextureUploadParityGlTests
{
    private readonly GlRendererFixture _fixture;

    public TextureUploadParityGlTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void An_uncompressed_texture_is_the_same_through_either_entry_point()
        => TextureUploadParity.AssertBothPathsAgree(_fixture.Renderer, "OpenGL");

    [Fact]
    public void An_RGB8_upload_with_a_generated_chain_is_upright()
        => TextureUploadParity.AssertRgb8WithMipsIsUpright(_fixture.Renderer, "OpenGL");
}

/// <summary>
/// The same claim on both D3D backends.
/// </summary>
/// <remarks>
/// On <see cref="D3DDeviceCollection"/> rather than a collection of its own: see
/// that type for the measured reason two device-creating collections must not
/// run at once.
/// </remarks>
[Collection(D3DDeviceCollection.Name)]
public sealed class TextureUploadParityD3DTests
{
    private readonly SharedTargetD3D11Fixture _d3d11;
    private readonly SharedTargetD3D12Fixture _d3d12;

    public TextureUploadParityD3DTests(
        SharedTargetD3D11Fixture d3d11, SharedTargetD3D12Fixture d3d12)
    {
        _d3d11 = d3d11;
        _d3d12 = d3d12;
    }

    private void RequireD3D11() => Assert.SkipWhen(
        !_d3d11.Available,
        $"no usable D3D11 device in this process: {_d3d11.UnavailableReason}");

    private void RequireD3D12() => Assert.SkipWhen(
        !_d3d12.Available,
        $"no usable D3D12 device in this process: {_d3d12.UnavailableReason}");

    [Fact]
    public void An_uncompressed_texture_is_the_same_through_either_entry_point_on_D3D11()
    {
        RequireD3D11();
        TextureUploadParity.AssertBothPathsAgree(_d3d11.Renderer, "D3D11");
    }

    [Fact]
    public void An_uncompressed_texture_is_the_same_through_either_entry_point_on_D3D12()
    {
        RequireD3D12();
        TextureUploadParity.AssertBothPathsAgree(_d3d12.Renderer, "D3D12");
    }

    [Fact]
    public void An_RGB8_upload_with_a_generated_chain_is_upright_on_D3D11()
    {
        RequireD3D11();
        TextureUploadParity.AssertRgb8WithMipsIsUpright(_d3d11.Renderer, "D3D11");
    }

    [Fact]
    public void An_RGB8_upload_with_a_generated_chain_is_upright_on_D3D12()
    {
        RequireD3D12();
        TextureUploadParity.AssertRgb8WithMipsIsUpright(_d3d12.Renderer, "D3D12");
    }

    [Fact]
    public void A_two_mip_BC7_texture_uploads_on_D3D11()
    {
        RequireD3D11();
        AssertBc7Uploads(_d3d11.Renderer);
    }

    [Fact]
    public void A_two_mip_BC7_texture_uploads_on_D3D12()
    {
        RequireD3D12();
        AssertBc7Uploads(_d3d12.Renderer);

        // The D3D12 path copies per row through GetCopyableFootprints, whose
        // destination pitch is 256-aligned; a whole-level memcpy is refused by
        // nothing and produces a sheared texture, so the debug layer's silence
        // is not the claim here - the picture below is.
        _d3d12.Present();
    }

    private static void AssertBc7Uploads(Renderer renderer)
    {
        foreach (bool padded in new[] { false, true })
        {
            byte[] payload = Bc7Fixture.BuildTwoLevelPayload(padded, out TextureMipDesc[] mips);
            Texture source = renderer.CreateTexture(new TextureUploadDesc(
                TextureFormat.Bc7, TextureColorSpace.Linear, payload, mips,
                TextureFilter.Nearest, TextureWrap.Clamp));

            try
            {
                byte[] picture = TextureUploadParity.RenderWhole(renderer, source);

                var reading = new TextureOrientationProbe.Reading(
                    TextureUploadParity.ClassifyAt(picture, 0, TextureUploadParity.TargetSize - 1),
                    TextureUploadParity.ClassifyAt(
                        picture, TextureUploadParity.TargetSize - 1, TextureUploadParity.TargetSize - 1),
                    TextureUploadParity.ClassifyAt(picture, 0, 0),
                    TextureUploadParity.ClassifyAt(picture, TextureUploadParity.TargetSize - 1, 0));

                reading.MatchesAuthoredImage.ShouldBeTrue(
                    $"a {(padded ? "padded" : "tight")} BC7 upload rendered as: {reading}. " +
                    $"Verdict: {reading.Verdict}");
            }
            finally
            {
                renderer.DestroyTexture(source);
            }
        }
    }
}
