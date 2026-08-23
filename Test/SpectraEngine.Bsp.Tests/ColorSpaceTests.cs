using System;
using System.Numerics;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The sRGB transfer function, pinned against values that do not come from this
/// codebase.
/// </summary>
/// <remarks>
/// <para>
/// <b>Round-tripping is not enough on its own.</b> Any pair of mutually inverse
/// functions round-trips, including <c>pow(2.2)</c> and <c>pow(1/2.2)</c>, which
/// is exactly the wrong curve and the one everybody reaches for. So the anchors
/// below are the published sRGB constants, and the comparison against
/// <c>pow(2.2)</c> is a deliberate negative: it fails if the implementation ever
/// drifts to the approximation.
/// </para>
/// <para>
/// This matters because the engine has two decoders that must agree: this one,
/// used for colours typed into a <c>.spectramat</c>, and the hardware sampler,
/// used for every texel. If they disagree, a flat-coloured surface and a
/// flat-coloured texture stop matching.
/// </para>
/// </remarks>
public sealed class ColorSpaceTests
{
    [Fact]
    public void The_endpoints_are_fixed_points()
    {
        // Black and white must survive exactly, or every clear colour and every
        // white fallback texture shifts.
        ColorSpace.SrgbToLinear(0f).ShouldBe(0f);
        ColorSpace.SrgbToLinear(1f).ShouldBe(1f, 1e-6f);
        ColorSpace.LinearToSrgb(0f).ShouldBe(0f);
        ColorSpace.LinearToSrgb(1f).ShouldBe(1f, 1e-6f);
    }

    [Fact]
    public void Mid_grey_decodes_to_the_published_value()
    {
        // 0.5 sRGB is 0.2140 linear: the single most-cited value of the curve,
        // and the one that shows the whole point. Half the display range is a
        // fifth of the light.
        ColorSpace.SrgbToLinear(0.5f).ShouldBe(0.2140f, 5e-4f);

        // 128/255, the byte a paint program calls mid grey.
        ColorSpace.SrgbToLinear(128 / 255f).ShouldBe(0.2158f, 5e-4f);
    }

    [Fact]
    public void The_linear_toe_is_a_straight_line_not_a_power_curve()
    {
        // Below 0.04045 the curve is exactly value/12.92. This segment is the
        // whole difference between the real function and pow(2.2), it is where
        // banding lives, and a pow-based implementation gets it visibly wrong.
        ColorSpace.SrgbToLinear(0.04f).ShouldBe(0.04f / 12.92f, 1e-7f);
        ColorSpace.SrgbToLinear(0.02f).ShouldBe(0.02f / 12.92f, 1e-7f);

        // Same claim, stated as a refusal: pow(2.2) is off by more than an
        // order of magnitude down here.
        float approximation = MathF.Pow(0.02f, 2.2f);
        ColorSpace.SrgbToLinear(0.02f).ShouldBeGreaterThan(approximation * 5f);
    }

    [Fact]
    public void Encode_inverts_decode_across_the_range()
    {
        for (int i = 0; i <= 255; i++)
        {
            float srgb = i / 255f;
            ColorSpace.LinearToSrgb(ColorSpace.SrgbToLinear(srgb))
                .ShouldBe(srgb, 1e-5f, $"code {i}");
        }
    }

    [Fact]
    public void Alpha_is_never_converted()
    {
        // Alpha is coverage, not light. It is stored linearly even inside an
        // sRGB texture format, and running it through the curve would make every
        // half-transparent surface the wrong transparency.
        Vector4 converted = ColorSpace.SrgbToLinear(new Vector4(0.5f, 0.5f, 0.5f, 0.5f));

        converted.W.ShouldBe(0.5f);
        // ...while the colour channels visibly moved: 0.5 sRGB is 0.214 linear.
        converted.X.ShouldBe(0.2140f, 5e-4f);
    }

    [Fact]
    public void The_sky_clear_colour_is_linear_cornflower_blue()
    {
        // The three backends share one definition now. What pins it is that
        // encoding it back gives the display colour the engine has always
        // cleared to.
        ClearColors.Sky.X.ShouldBeLessThan(0.392f);
        ColorSpace.LinearToSrgb(ClearColors.Sky.X).ShouldBe(0.392f, 1e-4f);
        ColorSpace.LinearToSrgb(ClearColors.Sky.Y).ShouldBe(0.584f, 1e-4f);
        ColorSpace.LinearToSrgb(ClearColors.Sky.Z).ShouldBe(0.929f, 1e-4f);
        ClearColors.Sky.W.ShouldBe(1f);

        // Black is its own image under the curve, which is why the wireframe
        // pipelines needed no conversion at all.
        ClearColors.Wireframe.ShouldBe(new Vector4(0f, 0f, 0f, 1f));
    }

    [Fact]
    public void Only_multi_channel_formats_can_be_srgb()
    {
        // There is no one-channel sRGB format in DXGI or GL. This is the single
        // place that rule lives, so that all three backends degrade identically
        // instead of one throwing and another silently working.
        TextureFormatInfo.SupportsSrgb(TextureFormat.Rgba8).ShouldBeTrue();
        TextureFormatInfo.SupportsSrgb(TextureFormat.Rgb8).ShouldBeTrue();
        TextureFormatInfo.SupportsSrgb(TextureFormat.R8).ShouldBeFalse();

        TextureFormatInfo.Resolve(TextureFormat.Rgba8, TextureColorSpace.Srgb)
            .ShouldBe(TextureColorSpace.Srgb);
        TextureFormatInfo.Resolve(TextureFormat.R8, TextureColorSpace.Srgb)
            .ShouldBe(TextureColorSpace.Linear);

        // A linear request is never promoted, whatever the format supports.
        TextureFormatInfo.Resolve(TextureFormat.Rgba8, TextureColorSpace.Linear)
            .ShouldBe(TextureColorSpace.Linear);
    }
}
