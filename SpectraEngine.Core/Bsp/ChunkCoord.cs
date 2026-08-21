using System;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Integer coordinates of one cell in the sparse static-world chunk grid. A
/// cell is the axis-aligned cube <c>[coord * CellSize, (coord + 1) * CellSize)</c>
/// per axis — floor semantics, so a position exactly on a cell boundary belongs
/// to the cell on the positive side. Coordinates are unbounded and may be
/// negative: the world has no extents (open-world pillar — brushes go anywhere,
/// Roblox-style, never inside a sealed hull).
/// </summary>
public readonly record struct ChunkCoord(int X, int Y, int Z) : IComparable<ChunkCoord>
{
    /// <summary>
    /// Edge length of a chunk cell in world units. PINNED at 32: large enough
    /// that typical room-scale editing touches a handful of cells, small enough
    /// that a one-brush edit never drags city-block amounts of geometry through
    /// a recompile. A power of two, so position/CellSize is an exact mantissa
    /// rescale and cell classification cannot wobble at representable
    /// boundaries. Do not change without documenting why — the incremental
    /// compile stages (W2–W4) and their oracle tests are calibrated to it.
    /// </summary>
    public const float CellSize = 32.0f;

    /// <summary>The cell containing <paramref name="worldPosition"/> (floor per axis).</summary>
    public static ChunkCoord FromPosition(Vector3 worldPosition) => new(
        FloorToCell(worldPosition.X), FloorToCell(worldPosition.Y), FloorToCell(worldPosition.Z));

    /// <summary>Minimum (inclusive) world-space corner of the cell.</summary>
    public Vector3 MinCorner => new(X * CellSize, Y * CellSize, Z * CellSize);

    /// <summary>
    /// Maximum world-space corner of the cell — exclusive for point
    /// classification (a point exactly here belongs to the next cell), computed
    /// as <c>(coord + 1) * CellSize</c> rather than <c>MinCorner + CellSize</c>
    /// so it is bit-identical to the neighbouring cell's <see cref="MinCorner"/>.
    /// </summary>
    public Vector3 MaxCorner => new((X + 1) * CellSize, (Y + 1) * CellSize, (Z + 1) * CellSize);

    /// <summary>The cell's world-space box, <see cref="MinCorner"/>..<see cref="MaxCorner"/>.</summary>
    public Aabb Bounds => new(MinCorner, MaxCorner);

    /// <summary>
    /// Lexicographic X → Y → Z order. This is THE deterministic enumeration
    /// order for chunked consumers: whenever per-cell work must be combined
    /// into an ordered whole (surface concatenation, mesh assembly, dirty-set
    /// reporting), cells are sorted by this comparison.
    /// </summary>
    public int CompareTo(ChunkCoord other)
    {
        int c = X.CompareTo(other.X);
        if (c != 0) return c;
        c = Y.CompareTo(other.Y);
        return c != 0 ? c : Z.CompareTo(other.Z);
    }

    private static int FloorToCell(float value) => (int)MathF.Floor(value / CellSize);
}
