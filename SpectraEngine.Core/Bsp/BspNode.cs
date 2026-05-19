using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A node in a solid-leaf BSP tree. Internal nodes hold a splitting plane and
/// front/back children; leaf nodes mark a convex region of space as solid or
/// empty.
/// </summary>
public sealed class BspNode
{
    private BspNode(Plane plane, BspNode front, BspNode back)
    {
        Plane = plane;
        Front = front;
        Back = back;
    }

    private BspNode(bool solid)
    {
        IsLeaf = true;
        IsSolid = solid;
    }

    /// <summary>True when this node is a leaf (a convex region), false for an internal split.</summary>
    public bool IsLeaf { get; }

    /// <summary>For a leaf, whether the region it bounds is solid.</summary>
    public bool IsSolid { get; }

    /// <summary>The splitting plane; meaningful only for internal nodes.</summary>
    public Plane Plane { get; }

    /// <summary>The child on the plane's normal side; null for leaves.</summary>
    public BspNode? Front { get; }

    /// <summary>The child behind the plane; null for leaves.</summary>
    public BspNode? Back { get; }

    public static BspNode Leaf(bool solid) => new(solid);

    public static BspNode Split(Plane plane, BspNode front, BspNode back) => new(plane, front, back);
}
