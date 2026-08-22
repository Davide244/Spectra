namespace SpectraEngine.Core.Graphics;

/// <summary>
/// How a draw's output is combined with what is already in the render target.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the engine selects anything but <see cref="Opaque"/> today. The
/// mode exists now because it belongs in the pipeline-state cache key, and a key
/// that omits a piece of pipeline state is the exact failure this milestone
/// exists to prevent: a cached pipeline handed back for a draw that wanted
/// different state, which the debug layer catches sometimes and not others.
/// </para>
/// <para>
/// The blend factors are the standard non-premultiplied ones. Sorting, and the
/// fact that sorted alpha is correct-ish rather than correct, is a separate
/// problem that belongs to whoever builds the draw list, not here.
/// </para>
/// </remarks>
public enum BlendMode
{
    /// <summary>No blending. Source replaces destination.</summary>
    Opaque,

    /// <summary>Source alpha over destination, with alpha accumulated.</summary>
    AlphaBlend,
}
