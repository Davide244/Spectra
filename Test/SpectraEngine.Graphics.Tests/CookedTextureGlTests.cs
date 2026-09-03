using BCnEncoder.Encoder;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Images;
using Spectra.Kitchen.Rules;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.OpenGL;
using System;
using System.Linq;
using Texture = SpectraEngine.Core.Graphics.Texture;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// A PNG cooked to a <c>.simage</c>, read back, uploaded and sampled against a
/// real driver.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only place the whole cooked-texture chain is one claim.</b> The
/// writer lives in <c>Spectra.Kitchen</c> because no shipped game writes a pack;
/// the reader and the uploader live in Core because every shipped game reads one.
/// Each half has its own tests and each passes with the other broken: a container
/// nothing can upload still round-trips through its own reader, and an uploader
/// still uploads a hand-built payload. What only a driver can answer is whether
/// the bytes the cooker actually produces become the picture the author drew.
/// </para>
/// <para>
/// <b>The fixture is cooked here rather than checked in</b>, so an encoder or
/// container change re-runs through this oracle instead of leaving a stale
/// artifact passing. It is the engine's own
/// <see cref="TextureOrientationProbe"/> image, which is asymmetric on both axes
/// - the reason that file exists at all - so a vertical flip, a horizontal flip
/// and a transpose are three different readings rather than one.
/// </para>
/// <para>
/// <b>Every failure in this area renders a picture rather than raising
/// anything.</b> A cooked image uploaded upside down draws a world upside down and
/// logs nothing; a mip chain uploaded at the wrong indices is correct up close and
/// wrong at distance. So the instrument is a rendered picture read texel by texel,
/// and the falsification test below is what proves the instrument can report the
/// other answer.
/// </para>
/// </remarks>
[Collection(GlRendererCollection.Name)]
public sealed class CookedTextureGlTests
{
    private readonly GlRendererFixture _fixture;

    private const int TargetSize = 16;

    public CookedTextureGlTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void A_cooked_image_samples_the_same_way_up_as_the_loose_file_it_came_from()
    {
        // THE test that would catch a reintroduced flip. The cooked path performs
        // no row flip at load and cannot: BC6H and BC7 blocks cannot be reversed
        // without a full decode and re-encode. The flip that establishes the
        // engine's v = 0 convention therefore happens once, at cook time, through
        // the same ImageDecoder the loose path uses - so the two agree by
        // construction, and this is what says so out loud.
        TextureOrientationProbe.Reading loose = Render(UploadLoose());
        TextureOrientationProbe.Reading cooked = Render(UploadCooked());

        cooked.MatchesAuthoredImage.ShouldBeTrue(
            $"the cooked image rendered as: {cooked}. Verdict: {cooked.Verdict}");

        // Stated as an equality as well, because "both upright" and "both agree"
        // are different claims and the second is the one a future convention change
        // must keep: if v = 0 ever moves to the top of the picture, these two must
        // move together or a level renders half one way up and half the other.
        cooked.ShouldBe(loose);
    }

    [Fact]
    public void A_cooked_image_whose_rows_were_flipped_before_encoding_reads_as_flipped()
    {
        // The falsification. "The cooked one matches" is also what a blind
        // instrument reports, so the same path with the source rows reversed has to
        // come back FLIPPED or the test above proves nothing. Mirrored BEFORE the
        // encode rather than after, because after is exactly the thing block
        // compression makes impossible.
        DecodedImage image = DecodeFixture();
        var mirrored = new DecodedImage(
            MirrorRows(image), image.Width, image.Height, image.Channels, image.Format);

        TextureOrientationProbe.Reading reading = Render(Upload(Encode(mirrored)));

        reading.IsVerticallyFlipped.ShouldBeTrue($"a mirrored cook rendered as: {reading}");
    }

    [Fact]
    public void A_cooked_image_reaches_the_GPU_with_no_decode_and_a_supplied_mip_chain()
    {
        byte[] cooked = Cook();
        SimageInfo info = SimageReader.Read(cooked, "cooked.simage");

        DecodedImage loose = DecodeFixture();
        info.Width.ShouldBe(loose.Width);
        info.Height.ShouldBe(loose.Height);

        // Block compression rather than a copy: BC7 is one byte per texel where
        // the decoded source is three, and the whole chain still costs less than
        // the base level did.
        info.Format.ShouldBe(TextureFormat.Bc7);
        info.PayloadBytes.ShouldBeLessThan(loose.Width * loose.Height * loose.Channels);

        // The chain is SUPPLIED, which is what tells the backend not to build one -
        // and it cannot build one for a compressed format anyway, so a cooked image
        // with a single level would be a texture that goes to mush at distance with
        // nothing reporting it.
        info.MipCount.ShouldBe(4);

        Texture texture = Upload(cooked);
        try
        {
            texture.Width.ShouldBe(loose.Width);
            texture.Height.ShouldBe(loose.Height);
            texture.Format.ShouldBe(TextureFormat.Bc7);
        }
        finally
        {
            _fixture.Renderer.DestroyTexture(texture);
        }
    }

    [Fact]
    public void The_upload_takes_the_CALLERS_colour_space_rather_than_the_files()
    {
        // The cooker writes the UNORM vkFormat deliberately, because whether a block
        // of bytes is colour or data is a property of the material SLOT: one cooked
        // artifact has to serve an albedo in one material and a mask in another,
        // which is exactly why AssetManager's cache key carries the colour space.
        byte[] cooked = Cook();
        SimageReader.Read(cooked, "cooked.simage").DeclaredColorSpace.ShouldBe(TextureColorSpace.Linear);

        Texture asColour = Upload(cooked, TextureColorSpace.Srgb);
        try
        {
            asColour.ColorSpace.ShouldBe(TextureColorSpace.Srgb);
        }
        finally
        {
            _fixture.Renderer.DestroyTexture(asColour);
        }
    }

    // --- helpers -------------------------------------------------------------

    // Through the real rule, over the engine's real content root, so what is
    // measured is what scook writes rather than a shape assembled for the test.
    private static byte[] Cook()
    {
        var context = new RuleContext(
            ContentRoot.Path, TextureOrientationProbe.TexturePath, CookProfile.Ship);

        new ImageRule().Cook(context);

        context.Diagnostics.ShouldBeEmpty();
        return context.Emissions.Single().Payload;
    }

    // The same container the rule writes, over pixels this test chose. Used only
    // by the falsification, which needs a source the rule cannot be asked for.
    private static byte[] Encode(DecodedImage image)
    {
        byte[][] levels = ImageBlockEncoder.Encode(
            image, TextureFormat.Bc7, CompressionQuality.Balanced);

        return Ktx2Writer.Write(
            TextureFormat.Bc7,
            image.Width,
            image.Height,
            levels,
            SimageRowOrder.BottomUp,
            EngineInfo.TextureFormatVersion);
    }

    private Texture UploadCooked() => Upload(Cook());

    private Texture Upload(byte[] cooked, TextureColorSpace colorSpace = TextureColorSpace.Linear)
    {
        SimageInfo info = SimageReader.Read(cooked, "cooked.simage");

        // Nearest, so the base level is what reaches the screen and no mip
        // selection can turn a level-index bug into a slightly blurrier pass.
        return _fixture.Renderer.CreateTexture(new TextureUploadDesc(
            info.Format, colorSpace, cooked, info.Mips, TextureFilter.Nearest, TextureWrap.Clamp));
    }

    private Texture UploadLoose()
    {
        DecodedImage image = DecodeFixture();

        // Linear on both sides, so the only transform between the file's bytes and
        // the read-back bytes is the tone curve, which is monotone per channel and
        // cannot turn one quadrant colour into another.
        return _fixture.Renderer.CreateTexture(
            image.Pixels, image.Width, image.Height, image.Format, TextureColorSpace.Linear,
            TextureFilter.Nearest, TextureWrap.Clamp);
    }

    private TextureOrientationProbe.Reading Render(Texture source)
    {
        OpenGLRenderer renderer = _fixture.Renderer;
        RenderTarget output = renderer.CreateRenderTarget(
            new RenderTargetDesc(TargetSize, TargetSize, TextureFormat.Rgba8, TextureColorSpace.Linear));

        try
        {
            renderer.DrawOrientationQuad(source, output, OrientationQuad.Coverage.Full);

            return new TextureOrientationProbe.Reading(
                Read(renderer, output, 0, TargetSize - 1),
                Read(renderer, output, TargetSize - 1, TargetSize - 1),
                Read(renderer, output, 0, 0),
                Read(renderer, output, TargetSize - 1, 0));
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

    private static byte[] MirrorRows(DecodedImage image)
    {
        var mirrored = new byte[image.Height * image.Stride];
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> source = image.Pixels.Slice((image.Height - 1 - y) * image.Stride, image.Stride);
            source.CopyTo(mirrored.AsSpan(y * image.Stride, image.Stride));
        }

        return mirrored;
    }

    private static DecodedImage DecodeFixture() => ImageDecoder.DecodeFile(
        System.IO.Path.Combine(ContentRoot.Path, "Textures", "orientation_probe.png"));
}
