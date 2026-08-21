using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Decoding of the dev textures the repo actually ships. Every expectation here
/// is a property of the committed PNG files: the shipped set covers all three
/// GPU formats (RGBA8, RGB8, R8), and the known-pixel checks pin the row order,
/// since a decoder that forgot the top-down-to-bottom-up flip would still
/// produce the right dimensions and channel count.
/// </summary>
public sealed class ImageDecoderTests
{
    // Bottom-up storage: GetPixel(x, 0) is the BOTTOM row of the picture, so a
    // marker drawn along the file's top edge must show up at y = Height - 1.
    private const int GridSize = 128;
    private const int GridTop = GridSize - 1;

    [Fact]
    public void Dev_grid_decodes_as_rgba8_with_its_orientation_marks_in_the_right_corners()
    {
        DecodedImage image = Decode("dev_grid.png");

        image.Width.ShouldBe(128);
        image.Height.ShouldBe(128);
        image.Channels.ShouldBe(4);
        image.Format.ShouldBe(TextureFormat.Rgba8);
        image.Stride.ShouldBe(128 * 4);
        image.Pixels.Length.ShouldBe(128 * 128 * 4);

        // Green marks the file's top edge, red its left edge; after the flip the
        // green band has to sit at the TOP of the bottom-up buffer.
        ShouldBePixel(image, 0, GridTop, [60, 180, 75, 255]);
        ShouldBePixel(image, 64, GridTop, [60, 180, 75, 255]);
        // Left edge (red) away from the top band.
        ShouldBePixel(image, 0, 0, [220, 60, 50, 255]);
        // Interior background: not on a grid line, not in either edge band.
        ShouldBePixel(image, 8, GridTop - 8, [40, 44, 52, 255]);
        // A minor grid line every 16 texels.
        ShouldBePixel(image, 16, GridTop - 40, [90, 96, 110, 255]);
    }

    [Fact]
    public void Checker_gray_decodes_as_rgb8()
    {
        DecodedImage image = Decode("checker_gray.png");

        image.Width.ShouldBe(128);
        image.Height.ShouldBe(128);
        image.Channels.ShouldBe(3);
        image.Format.ShouldBe(TextureFormat.Rgb8);

        // Top-left of the file is a light cell; 128/16 = 8 cells per side, so
        // the bottom-left of the flipped buffer lands on a dark one.
        ShouldBePixel(image, 0, GridTop, [200, 200, 200]);
        ShouldBePixel(image, 0, 0, [120, 120, 120]);
        ShouldBePixel(image, 20, GridTop, [120, 120, 120]);
    }

    [Fact]
    public void Checker_orange_decodes_as_rgb8()
    {
        DecodedImage image = Decode("checker_orange.png");

        image.Width.ShouldBe(128);
        image.Height.ShouldBe(128);
        image.Channels.ShouldBe(3);
        image.Format.ShouldBe(TextureFormat.Rgb8);
        ShouldBePixel(image, 0, GridTop, [235, 140, 50]);
        ShouldBePixel(image, 20, GridTop, [150, 80, 25]);
    }

    [Fact]
    public void Wall_brick_decodes_as_rgb8()
    {
        DecodedImage image = Decode("wall_brick.png");

        image.Width.ShouldBe(128);
        image.Height.ShouldBe(128);
        image.Channels.ShouldBe(3);
        image.Format.ShouldBe(TextureFormat.Rgb8);
        ShouldBePixel(image, 0, GridTop, [198, 192, 182]);       // mortar course
        ShouldBePixel(image, 8, GridTop - 8, [146, 70, 56]);     // brick face
    }

    [Fact]
    public void Floor_tile_decodes_as_rgb8()
    {
        DecodedImage image = Decode("floor_tile.png");

        image.Width.ShouldBe(128);
        image.Height.ShouldBe(128);
        image.Channels.ShouldBe(3);
        image.Format.ShouldBe(TextureFormat.Rgb8);
        ShouldBePixel(image, 0, GridTop, [44, 50, 52]);          // grout
        ShouldBePixel(image, 8, GridTop - 8, [88, 112, 106]);    // tile face
    }

    [Fact]
    public void Gradient_mask_decodes_as_single_channel_r8()
    {
        DecodedImage image = Decode("gradient_mask.png");

        image.Width.ShouldBe(64);
        image.Height.ShouldBe(64);
        image.Channels.ShouldBe(1);
        image.Format.ShouldBe(TextureFormat.R8);
        image.Stride.ShouldBe(64);
        image.Pixels.Length.ShouldBe(64 * 64);

        // Horizontal ramp: value = x * 4, identical on every row.
        ShouldBePixel(image, 0, 0, [0]);
        ShouldBePixel(image, 10, 0, [40]);
        ShouldBePixel(image, 63, 63, [252]);
    }

    [Fact]
    public void Decoding_the_same_file_twice_yields_identical_bytes()
    {
        DecodedImage first = Decode("wall_brick.png");
        DecodedImage second = Decode("wall_brick.png");

        first.Pixels.SequenceEqual(second.Pixels).ShouldBeTrue();
    }

    [Fact]
    public void Rejects_bytes_that_are_not_an_image()
        => Should.Throw<InvalidDataException>(
            () => ImageDecoder.Decode("not an image at all"u8.ToArray(), "test"));

    [Fact]
    public void Reports_a_missing_file_as_an_io_failure()
        => Should.Throw<IOException>(
            () => ImageDecoder.DecodeFile(Path.Combine(ContentRoot.Path, "Textures", "does_not_exist.png")));

    private static DecodedImage Decode(string fileName)
        => ImageDecoder.DecodeFile(ContentRoot.ResolveAbsolute(ContentRoot.Path, $"Textures/{fileName}"));

    private static void ShouldBePixel(DecodedImage image, int x, int y, byte[] expected)
    {
        byte[] actual = image.GetPixel(x, y).ToArray();
        actual.ShouldBe(expected, $"pixel ({x}, {y}) of a {image.Width}x{image.Height} {image.Format} image");
    }
}
