using SpectraEngine.Core.Graphics;
using System;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// What a texture upload descriptor refuses, and what the format table says
/// about colour space and block geometry.
/// </summary>
/// <remarks>
/// No device here on purpose. Every rule in this file decides bytes rather than
/// pixels, and the two failures it guards against - a read past the end of a
/// mapped payload, and an sRGB flag on a format that has none - are the two that
/// no picture can report: the first is an access violation with no managed
/// stack, and the second renders perfectly and is simply the wrong colour.
/// </remarks>
public sealed class TextureUploadDescTests
{
    [Fact]
    public void A_mip_whose_pitch_overruns_the_payload_is_refused_by_name()
    {
        // 16x16 BC7 is four block rows of 64 bytes. Declaring 128 needs 448
        // bytes at the last row's tight end, which the 256-byte payload cannot
        // serve - and reading it anyway is a walk off the end of a mapped view.
        var payload = new byte[256];
        TextureMipDesc[] mips =
        [
            new TextureMipDesc(16, 16, 0, 64),
            new TextureMipDesc(8, 8, 256, 128),
        ];

        var exception = Should.Throw<ArgumentException>(() => new TextureUploadDesc(
            TextureFormat.Bc7, TextureColorSpace.Linear, payload, mips).Validate());

        // The mip's index has to be in the message: a cooked chain is a dozen
        // levels of numbers nobody typed, and "the payload is too short" names
        // none of them.
        exception.Message.ShouldContain("Mip 1");
        exception.Message.ShouldContain("8x8");
    }

    [Fact]
    public void A_pitch_below_the_tight_one_is_refused_by_name()
    {
        // Padding is always upward, so a pitch under the tight one cannot
        // describe the row at all. Left unchecked it reads each row from inside
        // the previous one and produces a smear rather than an error.
        var payload = new byte[4096];
        TextureMipDesc[] mips = [new TextureMipDesc(16, 16, 0, 32)];

        var exception = Should.Throw<ArgumentException>(() => new TextureUploadDesc(
            TextureFormat.Bc7, TextureColorSpace.Linear, payload, mips).Validate());

        exception.Message.ShouldContain("Mip 0");
        exception.Message.ShouldContain("64");
    }

    [Fact]
    public void A_chain_that_does_not_halve_is_refused_by_name()
    {
        // The API decides each level's size from the base one, so a chain that
        // halves differently uploads into levels of the wrong dimensions: no
        // error, and a picture that is only wrong once it is minified.
        var payload = new byte[8192];
        TextureMipDesc[] mips =
        [
            new TextureMipDesc(16, 16, 0, 64),
            new TextureMipDesc(4, 4, 4096, 16),
        ];

        var exception = Should.Throw<ArgumentException>(() => new TextureUploadDesc(
            TextureFormat.Bc7, TextureColorSpace.Linear, payload, mips).Validate());

        exception.Message.ShouldContain("Mip 1");
        exception.Message.ShouldContain("8x8");
    }

    [Fact]
    public void A_tightly_packed_two_level_BC7_payload_is_accepted()
    {
        // The falsification for the three refusals above: the same shape, laid
        // out correctly, must pass. Without this a Validate that threw on
        // everything would look like a working guard.
        byte[] payload = Bc7Fixture.BuildTwoLevelPayload(padded: false, out TextureMipDesc[] mips);

        Should.NotThrow(() => new TextureUploadDesc(
            TextureFormat.Bc7, TextureColorSpace.Linear, payload, mips).Validate());
    }

    [Fact]
    public void A_padded_two_level_BC7_payload_is_accepted()
    {
        byte[] payload = Bc7Fixture.BuildTwoLevelPayload(padded: true, out TextureMipDesc[] mips);

        Should.NotThrow(() => new TextureUploadDesc(
            TextureFormat.Bc7, TextureColorSpace.Linear, payload, mips).Validate());
    }

    [Fact]
    public void A_payload_that_stops_after_the_last_row_is_accepted()
    {
        // The last row need only carry its real bytes, not a whole trailing
        // pitch: a writer that packs levels back to back stops there, and
        // demanding the padding would refuse a correct file. Same bound
        // GetCopyableFootprints reports as its total size.
        var payload = new byte[64 * 3 + 32];
        TextureMipDesc[] mips = [new TextureMipDesc(8, 16, 0, 64)];

        Should.NotThrow(() => new TextureUploadDesc(
            TextureFormat.Bc7, TextureColorSpace.Linear, payload, mips).Validate());
    }

    [Fact]
    public void A_float_format_is_still_refused_out_of_range()
    {
        // Unchanged from before this path existed, and deliberately a different
        // exception type from the layout refusals: callers test for it.
        var pixels = new byte[16];

        Should.Throw<ArgumentOutOfRangeException>(() => TextureUploadDesc
            .SingleLevel(pixels, 1, 1, TextureFormat.Rgba16Float, TextureColorSpace.Linear)
            .Validate());
    }

    [Theory]
    [InlineData(TextureFormat.Rgba8, true)]
    [InlineData(TextureFormat.Rgb8, true)]
    [InlineData(TextureFormat.R8, false)]
    [InlineData(TextureFormat.Bc1, true)]
    [InlineData(TextureFormat.Bc3, true)]
    [InlineData(TextureFormat.Bc4, false)]
    [InlineData(TextureFormat.Bc5, false)]
    [InlineData(TextureFormat.Bc6H, false)]
    [InlineData(TextureFormat.Bc7, true)]
    [InlineData(TextureFormat.Rgba16Float, false)]
    [InlineData(TextureFormat.Depth32Float, false)]
    public void The_format_table_knows_which_formats_have_an_sRGB_twin(
        TextureFormat format, bool expected)
    {
        // The whole table in one place, because the backends read it rather than
        // deciding for themselves and a wrong entry here is a picture that is
        // merely wrong on all three at once. BC4 and BC5 are single- and
        // two-channel data formats with no sRGB form in any API, and BC6H is
        // float, which has no integer codes to ration.
        TextureFormatInfo.SupportsSrgb(format).ShouldBe(expected);

        TextureFormatInfo.Resolve(format, TextureColorSpace.Srgb)
            .ShouldBe(expected ? TextureColorSpace.Srgb : TextureColorSpace.Linear);
        TextureFormatInfo.Resolve(format, TextureColorSpace.Linear)
            .ShouldBe(TextureColorSpace.Linear);
    }

    [Theory]
    [InlineData(TextureFormat.Bc1, 8)]
    [InlineData(TextureFormat.Bc3, 16)]
    [InlineData(TextureFormat.Bc4, 8)]
    [InlineData(TextureFormat.Bc5, 16)]
    [InlineData(TextureFormat.Bc6H, 16)]
    [InlineData(TextureFormat.Bc7, 16)]
    public void A_block_format_reports_its_block_geometry(TextureFormat format, int bytesPerBlock)
    {
        TextureFormatInfo.IsBlockCompressed(format).ShouldBeTrue();
        TextureFormatInfo.BlockWidth(format).ShouldBe(4);
        TextureFormatInfo.BlockHeight(format).ShouldBe(4);
        TextureFormatInfo.BytesPerBlock(format).ShouldBe(bytesPerBlock);

        // A 1x1 mip still costs a whole block, which is what makes the tail of
        // every chain the size it is.
        TextureFormatInfo.TightRowPitch(format, 1).ShouldBe(bytesPerBlock);
        TextureFormatInfo.RowCount(format, 1).ShouldBe(1);
        TextureFormatInfo.RowCount(format, 5).ShouldBe(2);
    }

    [Fact]
    public void BC6H_is_not_reported_as_a_float_format()
    {
        // Its channels are half-floats and IsFloat is not about the channels: it
        // is the guard that refuses a format a byte array cannot fill, and BC6H
        // is filled from blocks, which are bytes. Answering yes here would refuse
        // the one HDR format a cooker can produce.
        TextureFormatInfo.IsFloat(TextureFormat.Bc6H).ShouldBeFalse();
        TextureFormatInfo.IsFloat(TextureFormat.Rgba16Float).ShouldBeTrue();
        TextureFormatInfo.IsFloat(TextureFormat.Depth32Float).ShouldBeTrue();
    }

    [Fact]
    public void An_uncompressed_format_is_one_texel_per_block()
    {
        // So one set of arithmetic serves both families and no upload path needs
        // a compressed branch to work out how many rows it has.
        TextureFormatInfo.IsBlockCompressed(TextureFormat.Rgba8).ShouldBeFalse();
        TextureFormatInfo.BlockWidth(TextureFormat.Rgba8).ShouldBe(1);
        TextureFormatInfo.BlockHeight(TextureFormat.Rgba8).ShouldBe(1);
        TextureFormatInfo.TightRowPitch(TextureFormat.Rgba8, 7).ShouldBe(28);
        TextureFormatInfo.TightRowPitch(TextureFormat.Rgb8, 7).ShouldBe(21);
        TextureFormatInfo.TightRowPitch(TextureFormat.R8, 7).ShouldBe(7);
        TextureFormatInfo.RowCount(TextureFormat.Rgba8, 7).ShouldBe(7);
    }

    [Fact]
    public void A_single_level_descriptor_states_the_tight_pitch()
    {
        // The bridge the old single-span overload crosses. Its own contract is
        // that its rows are tightly packed, so the tight pitch is the caller's
        // stated pitch rather than a guess at somebody else's.
        var pixels = new byte[3 * 2 * 4];
        TextureUploadDesc desc = TextureUploadDesc.SingleLevel(
            pixels, 3, 2, TextureFormat.Rgba8, TextureColorSpace.Srgb);

        desc.MipCount.ShouldBe(1);
        desc.HasSuppliedMipChain.ShouldBeFalse();
        desc.Width.ShouldBe(3);
        desc.Height.ShouldBe(2);
        desc.Mips[0].RowPitch.ShouldBe(12);
        desc.Mips[0].Offset.ShouldBe(0);

        // Called directly rather than through Should.NotThrow: a ref struct
        // cannot be captured by a lambda, which is the price of the payload
        // being a span.
        desc.Validate();
    }
}
