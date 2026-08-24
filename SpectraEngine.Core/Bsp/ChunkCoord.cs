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

    /// <summary>
    /// A Z-order (Morton) key for this cell: the bits of X, Y and Z interleaved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Spatial locality in one number.</b> Cells that are near each other in
    /// space have near keys, which lexicographic order does NOT give: sorting by
    /// (x, y, z) makes a run of consecutive cells a long thin line along z, and a
    /// bounding box around such a run is nearly the width of the world. A run of
    /// Morton-ordered cells is a compact block, which is what makes a box over a
    /// run worth testing against a frustum.
    /// </para>
    /// <para>
    /// <b>Separate from <see cref="CompareTo"/> on purpose.</b> That ordering is
    /// relied on elsewhere for deterministic dirty-cell sets, where "ascending
    /// cell order" means the obvious thing and a spatial curve would not.
    /// </para>
    /// <para>
    /// Coordinates are biased into unsigned range before interleaving, because
    /// the world is unbounded in both directions and a negative cell must sort
    /// below a positive one rather than above every one. 21 bits each covers
    /// roughly a million cells per axis, or 33 million world units at the
    /// current cell size, which is past the point where float precision fails
    /// anyway.
    /// </para>
    /// </remarks>
    public ulong MortonKey => Interleave(Bias(X)) | (Interleave(Bias(Y)) << 1) | (Interleave(Bias(Z)) << 2);

    private const int MortonBits = 21;
    private const int MortonBias = 1 << (MortonBits - 1);

    private static uint Bias(int value) =>
        (uint)Math.Clamp(value + MortonBias, 0, (1 << MortonBits) - 1);

    // Spreads 21 low bits so each occupies every third position.
    private static ulong Interleave(uint value)
    {
        ulong x = value & 0x1FFFFFUL;
        x = (x | (x << 32)) & 0x1F00000000FFFFUL;
        x = (x | (x << 16)) & 0x1F0000FF0000FFUL;
        x = (x | (x << 8)) & 0x100F00F00F00F00FUL;
        x = (x | (x << 4)) & 0x10C30C30C30C30C3UL;
        x = (x | (x << 2)) & 0x1249249249249249UL;
        return x;
    }

    private static int FloorToCell(float value) => (int)MathF.Floor(value / CellSize);
}
