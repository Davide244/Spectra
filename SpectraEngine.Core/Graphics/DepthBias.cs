namespace SpectraEngine.Core.Graphics;

/// <summary>
/// A depth offset the RASTERIZER applies to whatever it draws next.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the shadow-acne fix, and it is not interchangeable with the two
/// biases the light pass applies.</b> Acne is a surface comparing its own depth
/// against a stored depth that was sampled at a texel centre: within one texel
/// the stored value is constant while the receiver's depth ramps across it, so
/// their difference is a SAWTOOTH at exactly texel frequency, and wherever it
/// crosses zero the surface shadows itself. Pushing the receiver's sample
/// sideways (a normal offset) or subtracting a constant from the compared depth
/// both fight that sawtooth with a number that has to be big enough for the
/// worst texel on screen, which is why they detach shadows from their casters
/// long before they run out of acne to suppress.
/// </para>
/// <para>
/// <b>Biasing at raster time attacks the cause instead.</b>
/// <see cref="SlopeScaled"/> is multiplied by the primitive's own maximum depth
/// slope, which IS the height of that sawtooth, so each triangle is pushed back
/// by what that triangle actually needs and a triangle facing the light is not
/// pushed at all. And because it moves stored DEPTH rather than the sample
/// position, it does not move the shadow sideways by so much as a pixel: a
/// shadow's silhouette is unchanged, which is the whole difference from a
/// normal offset.
/// </para>
/// <para>
/// <b>The units are the API's, deliberately.</b> <see cref="Constant"/> is in
/// units of the depth buffer's smallest resolvable difference and
/// <see cref="SlopeScaled"/> is a plain multiplier, which is exactly what
/// <c>glPolygonOffset</c> takes and exactly what D3D's
/// <c>DepthBias</c>/<c>SlopeScaledDepthBias</c> take. Converting to world units
/// here would need the depth range and the format's precision, both of which
/// differ per cascade and per backend, and the conversion would then be wrong on
/// whichever backend it was not written against.
/// </para>
/// </remarks>
/// <param name="Constant">Depth-buffer units added unconditionally.</param>
/// <param name="SlopeScaled">Multiplier on the primitive's maximum depth slope.</param>
public readonly record struct DepthBias(int Constant, float SlopeScaled)
{
    /// <summary>No bias: what every pass other than the shadow map draws with.</summary>
    public static DepthBias None => default;

    /// <summary>Whether this bias would change anything at all.</summary>
    public bool IsZero => Constant == 0 && SlopeScaled == 0f;
}
