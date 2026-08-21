using System;
using System.Numerics;

namespace SpectraEngine.Editing.Selection;

/// <summary>
/// An axis-aligned rectangle in viewport pixels, in the engine's screen
/// convention: origin top-left, y growing downward — the same coordinates
/// <see cref="Input.EditorInputFrame.CursorPosition"/> reports and
/// <c>Camera.ScreenPointToRay</c> consumes.
/// </summary>
/// <remarks>
/// Built from two drag corners in any order (<see cref="FromCorners"/>
/// normalizes), so the marquee behaves the same dragged up-left as down-right.
/// A readonly struct passed by value — building one allocates nothing.
/// </remarks>
public readonly struct ScreenRect
{
    /// <summary>Creates a rectangle from an already-normalized corner pair.</summary>
    public ScreenRect(Vector2 min, Vector2 max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>The top-left corner, in viewport pixels.</summary>
    public Vector2 Min { get; }

    /// <summary>The bottom-right corner, in viewport pixels.</summary>
    public Vector2 Max { get; }

    /// <summary>Width in pixels; never negative.</summary>
    public float Width => Max.X - Min.X;

    /// <summary>Height in pixels; never negative.</summary>
    public float Height => Max.Y - Min.Y;

    /// <summary>The rectangle's centre, in viewport pixels.</summary>
    public Vector2 Center => (Min + Max) * 0.5f;

    /// <summary>
    /// The longer of the two sides, in pixels — the measure a click-versus-drag
    /// threshold is applied to, so a drag that is long but one pixel tall still
    /// counts as a drag.
    /// </summary>
    public float LongestSide => MathF.Max(Width, Height);

    /// <summary>Normalizes two drag corners into a rectangle.</summary>
    public static ScreenRect FromCorners(Vector2 a, Vector2 b) =>
        new(Vector2.Min(a, b), Vector2.Max(a, b));

    /// <summary>True when this rectangle overlaps <paramref name="other"/> (touching counts).</summary>
    public bool Intersects(in ScreenRect other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y;

    /// <summary>True when <paramref name="other"/> lies entirely within this rectangle.</summary>
    public bool Contains(in ScreenRect other) =>
        other.Min.X >= Min.X && other.Max.X <= Max.X &&
        other.Min.Y >= Min.Y && other.Max.Y <= Max.Y;

    /// <summary>True when the point lies inside (edges count).</summary>
    public bool Contains(Vector2 point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y;
}
