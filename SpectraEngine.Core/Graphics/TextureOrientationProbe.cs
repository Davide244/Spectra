namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The fixture and the vocabulary for the one question about texture upload
/// that no amount of reading the code can settle: do the three backends agree
/// about which way up a texture is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every texture the repo shipped before this one was symmetric</b> - a
/// checker, a grid, a flat colour - so a backend sampling rows in the opposite
/// order rendered a picture identical to a backend that did not, and no test,
/// no debug layer and no probe could have told them apart. The fixture is
/// therefore asymmetric on BOTH axes: four quadrants in four distinct hues, so
/// a vertical flip, a horizontal flip and a transpose are three different
/// readings rather than one.
/// </para>
/// <para>
/// The quadrant names are stated as the image is AUTHORED, i.e. as an image
/// viewer shows the file. Row 0 of the PNG is the top of the picture; row 0 of
/// the buffer <see cref="Assets.ImageDecoder"/> produces is the bottom of it,
/// because the decoder flips. That flip is shared by all three backends and so
/// cannot cause a disagreement between them - it decides what the ONE answer is,
/// not whether there is one.
/// </para>
/// </remarks>
public static class TextureOrientationProbe
{
    /// <summary>Content-relative path of the asymmetric fixture.</summary>
    public const string TexturePath = "Textures/orientation_probe.png";

    /// <summary>One of the fixture's four quadrant colours, or a reading that is none of them.</summary>
    public enum Quadrant
    {
        /// <summary>The texel matched none of the four authored colours.</summary>
        Unrecognised,

        /// <summary>The authored image's TOP-LEFT quadrant.</summary>
        Red,

        /// <summary>The authored image's TOP-RIGHT quadrant.</summary>
        Green,

        /// <summary>The authored image's BOTTOM-LEFT quadrant.</summary>
        Blue,

        /// <summary>The authored image's BOTTOM-RIGHT quadrant.</summary>
        Yellow,
    }

    // The measurement draws through the tone curve, which sends a full channel
    // to about 205 and leaves a zero channel at 0, so the bands are wide and a
    // texel landing between them is reported as Unrecognised rather than
    // rounded into whichever is nearer.
    private const int On = 120;
    private const int Off = 80;

    /// <summary>Names the quadrant a read-back texel came from, or <see cref="Quadrant.Unrecognised"/>.</summary>
    public static Quadrant Classify(byte r, byte g, byte b)
    {
        bool red = r >= On, green = g >= On, blue = b >= On;
        bool noRed = r <= Off, noGreen = g <= Off, noBlue = b <= Off;

        if (red && noGreen && noBlue) return Quadrant.Red;
        if (noRed && green && noBlue) return Quadrant.Green;
        if (noRed && noGreen && blue) return Quadrant.Blue;
        if (red && green && noBlue) return Quadrant.Yellow;
        return Quadrant.Unrecognised;
    }

    /// <summary>
    /// What the four corners of the rendered picture turned out to be. Corners
    /// are named as the PICTURE is seen, not as any buffer stores it.
    /// </summary>
    public readonly record struct Reading(
        Quadrant TopLeft, Quadrant TopRight, Quadrant BottomLeft, Quadrant BottomRight)
    {
        /// <summary>True when the picture shows the file the way an image viewer shows it.</summary>
        public bool MatchesAuthoredImage =>
            TopLeft == Quadrant.Red && TopRight == Quadrant.Green &&
            BottomLeft == Quadrant.Blue && BottomRight == Quadrant.Yellow;

        /// <summary>True when the picture is the authored image mirrored top to bottom.</summary>
        public bool IsVerticallyFlipped =>
            TopLeft == Quadrant.Blue && TopRight == Quadrant.Yellow &&
            BottomLeft == Quadrant.Red && BottomRight == Quadrant.Green;

        /// <summary>One line naming what landed where, for a log or a failure message.</summary>
        public override string ToString() =>
            $"top-left {TopLeft}, top-right {TopRight}, bottom-left {BottomLeft}, bottom-right {BottomRight}";

        /// <summary>The verdict in words: upright, flipped, or something else entirely.</summary>
        public string Verdict => MatchesAuthoredImage
            ? "UPRIGHT (the picture matches the authored image)"
            : IsVerticallyFlipped
                ? "FLIPPED vertically (the authored top row rendered at the bottom)"
                : "UNEXPECTED (neither upright nor a plain vertical flip)";
    }
}
