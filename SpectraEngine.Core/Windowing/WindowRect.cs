namespace SpectraEngine.Core.Windowing;

/// <summary>
/// A window or display rectangle in virtual-screen pixels: top-left origin,
/// y growing downward — the convention every desktop windowing backend uses.
/// </summary>
/// <remarks>
/// Plain ints rather than <c>Rectangle&lt;int&gt;</c> so the window-mode seam
/// (<see cref="IWindowModeTarget"/>) names no Silk.NET type: the adapter that
/// does is the only piece a future non-Silk host has to rewrite.
/// </remarks>
/// <param name="X">Left edge, in virtual-screen pixels.</param>
/// <param name="Y">Top edge, in virtual-screen pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct WindowRect(int X, int Y, int Width, int Height)
{
    /// <summary>True when the rectangle covers at least one pixel in both axes.</summary>
    public bool IsPositive => Width > 0 && Height > 0;
}
