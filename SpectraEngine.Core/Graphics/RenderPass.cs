using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// What a render pass does to its target before anything is drawn into it.
/// </summary>
/// <remarks>
/// <para>
/// Both fields are optional because "do not touch this attachment" is a real
/// and distinct instruction from "clear it to black". A second pass drawing an
/// overlay on top of a finished frame must not clear colour; a shadow pass has
/// no colour attachment to clear at all.
/// </para>
/// <para>
/// <b>Clearing depth clears stencil with it.</b> The two share one attachment
/// on every backend here, no pass uses stencil, and the alternative was three
/// backends that each cleared a slightly different set: OpenGL was doing colour
/// and depth while both D3D backends did depth and stencil. One rule that
/// cannot silently diverge is worth more than a bit that nothing reads.
/// </para>
/// </remarks>
/// <param name="Color">Colour to clear to, in <b>linear</b> values, or null to keep what is there.</param>
/// <param name="Depth">Depth to clear to, usually 1, or null to keep what is there.</param>
public readonly record struct PassClear(Vector4? Color, float? Depth)
{
    /// <summary>Clears colour to <paramref name="color"/> and depth to the far plane.</summary>
    public static PassClear To(Vector4 color) => new(color, 1f);

    /// <summary>Clears depth only. What a depth-only pass wants.</summary>
    public static PassClear DepthOnly => new(null, 1f);

    /// <summary>Keeps everything. What a pass drawing on top of a finished frame wants.</summary>
    public static PassClear Keep => new(null, null);
}
