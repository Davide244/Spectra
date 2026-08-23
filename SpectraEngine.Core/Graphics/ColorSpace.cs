using System;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Conversion between sRGB-encoded and linear colour values.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lighting is arithmetic, and arithmetic needs linear numbers.</b> A pixel
/// in a PNG is not a quantity of light: it is a display code, mapped through the
/// sRGB transfer function so that the 256 available codes are spent where a
/// human eye can tell them apart. Averaging two such codes, or multiplying one
/// by a cosine, produces a number that means nothing. So every colour entering
/// the shader is decoded to linear first, all the shading happens there, and the
/// result is encoded back on the way to the display.
/// </para>
/// <para>
/// <b>Almost none of that conversion happens here.</b> Texture decode and
/// display encode are done by the sampler and the render target, in hardware,
/// which is both free and more correct than a shader could be: hardware decodes
/// <i>before</i> filtering, so a bilinear tap and a mip level are averages of
/// light rather than averages of display codes. This class exists for the
/// handful of colours that arrive as numbers rather than as texels, and those
/// are the ones a person typed: a <c>color</c> in a <c>.spectramat</c>, and the
/// clear colour.
/// </para>
/// <para>
/// <b>The piecewise curve, not <c>pow(2.2)</c>.</b> The two agree in the
/// midtones and diverge badly near black, which is exactly where banding is
/// visible and where hardware and this code must agree or a texel and a typed
/// colour will not match.
/// </para>
/// </remarks>
public static class ColorSpace
{
    /// <summary>Decodes one sRGB-encoded channel value in 0..1 to linear.</summary>
    /// <remarks>
    /// Values outside 0..1 pass through the same formula rather than being
    /// clamped: the curve is monotonic and well-defined either side, and an
    /// authored colour above 1 is a deliberate over-bright, not an error.
    /// </remarks>
    public static float SrgbToLinear(float value) =>
        value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    /// <summary>Encodes one linear channel value in 0..1 as sRGB.</summary>
    public static float LinearToSrgb(float value) =>
        value <= 0.0031308f
            ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;

    /// <summary>Decodes an RGB triple from sRGB to linear.</summary>
    public static Vector3 SrgbToLinear(Vector3 color) =>
        new(SrgbToLinear(color.X), SrgbToLinear(color.Y), SrgbToLinear(color.Z));

    /// <summary>Encodes an RGB triple from linear to sRGB.</summary>
    public static Vector3 LinearToSrgb(Vector3 color) =>
        new(LinearToSrgb(color.X), LinearToSrgb(color.Y), LinearToSrgb(color.Z));

    /// <summary>
    /// Decodes an RGBA colour from sRGB to linear, <b>leaving alpha alone</b>.
    /// </summary>
    /// <remarks>
    /// Alpha is coverage, not light. It is stored linearly even inside an sRGB
    /// texture format, and running it through the curve would make every
    /// half-transparent surface the wrong transparency.
    /// </remarks>
    public static Vector4 SrgbToLinear(Vector4 color) =>
        new(SrgbToLinear(color.X), SrgbToLinear(color.Y), SrgbToLinear(color.Z), color.W);
}

/// <summary>
/// The colours the backends clear to, in <b>linear</b> values.
/// </summary>
/// <remarks>
/// <para>
/// One definition for all three backends. Each used to carry its own literal,
/// and the numbers were only equal by inspection; converting them to linear
/// three separate times is exactly the kind of edit where one of them gets
/// missed and a backend quietly renders a different sky.
/// </para>
/// <para>
/// <b>These are linear because the render target encodes.</b> Both the D3D
/// <c>ClearRenderTargetView</c> path and GL's <c>glClear</c> apply the sRGB
/// transfer function on the way into an sRGB target, so handing them the
/// display code would encode it twice and wash the background out.
/// </para>
/// </remarks>
public static class ClearColors
{
    /// <summary>
    /// Cornflower blue, the engine's empty-scene background since the first
    /// frame it ever drew. <c>#6495ED</c> as a display colour.
    /// </summary>
    public static readonly Vector4 Sky = ColorSpace.SrgbToLinear(new Vector4(0.392f, 0.584f, 0.929f, 1f));

    /// <summary>
    /// Black, for the wireframe pipeline's contrast background. Identical in
    /// both spaces, which is why this one needed no conversion.
    /// </summary>
    public static readonly Vector4 Wireframe = new(0f, 0f, 0f, 1f);
}
