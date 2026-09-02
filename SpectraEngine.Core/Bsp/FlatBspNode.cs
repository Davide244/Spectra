using System.Numerics;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// One INTERNAL node of a solid-leaf BSP tree in its flat, blittable form: the
/// shape the compiled map format stores and the shape
/// <see cref="FlatBspTree"/> queries directly.
/// </summary>
/// <remarks>
/// The layout is a file format, not an implementation detail. Raw bytes are
/// cast into this struct, so its size is pinned by a test rather than assumed:
/// <c>Plane</c> is 16 bytes and this struct is 24, and neither is a documented
/// contract of <see cref="System.Numerics.Plane"/>.
///
/// Holding a real <see cref="System.Numerics.Plane"/> rather than four loose
/// floats is what makes answer-identity with the live <see cref="BspTree"/> a
/// structural property: both forms call the identical
/// <see cref="Plane.DotCoordinate"/> on the identical value, so there is no
/// argument to have about float evaluation order.
///
/// Children are Quake-encoded: a value at or above zero is an index into the
/// node array, and the two negative codes are the leaves themselves. Leaves
/// therefore occupy no array slots at all, and a solid-leaf BSP is roughly half
/// leaves.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct FlatBspNode
{
    /// <summary>Child code: the convex region on that side is empty space.</summary>
    public const int EmptyLeaf = -1;

    /// <summary>Child code: the convex region on that side is solid.</summary>
    public const int SolidLeaf = -2;

    /// <summary>The splitting plane. Points at or in front of it take <see cref="Front"/>.</summary>
    public readonly Plane Plane;

    /// <summary>The child on the plane's normal side: a node index, or a leaf code.</summary>
    public readonly int Front;

    /// <summary>The child behind the plane: a node index, or a leaf code.</summary>
    public readonly int Back;

    public FlatBspNode(Plane plane, int front, int back)
    {
        Plane = plane;
        Front = front;
        Back = back;
    }

    /// <summary>True when a child value is a leaf code rather than a node index.</summary>
    public static bool IsLeaf(int child) => child < 0;

    /// <summary>True when a child value is the solid-leaf code.</summary>
    public static bool IsSolid(int child) => child == SolidLeaf;
}
