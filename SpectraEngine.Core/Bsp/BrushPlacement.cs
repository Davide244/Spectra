using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// An immutable pairing of a brush with the world transform that places it —
/// the unit of a static-world snapshot. The render thread captures one
/// placement per brush node (the node's world matrix at snapshot time), and
/// the background CSG compile reads <em>only</em> these placements: never
/// <see cref="Bsp.Brush.Transform"/>, never live scene state. Brush planes,
/// faces, and bounds are immutable after construction, so sharing the
/// <see cref="Brush"/> reference across threads is safe.
/// </summary>
/// <param name="Brush">The brush being placed; its local geometry is immutable.</param>
/// <param name="Transform">
/// World-from-local placement, captured at snapshot time. Must be rigid
/// (rotation + translation only) — the CSG epsilon scheme assumes unit-length
/// plane normals, so callers validate rigidity before building placements.
/// </param>
public readonly record struct BrushPlacement(Brush Brush, Matrix4x4 Transform)
{
    /// <summary>Axis-aligned world-space bounds of the brush under this placement.</summary>
    public Aabb WorldBounds => Brush.LocalBounds.Transform(Transform);
}
