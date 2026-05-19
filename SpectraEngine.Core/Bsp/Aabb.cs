using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>An axis-aligned bounding box, used for broadphase overlap tests.</summary>
public readonly struct Aabb
{
    public readonly Vector3 Min;
    public readonly Vector3 Max;

    public Aabb(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public Vector3 Center => (Min + Max) * 0.5f;

    public Vector3 Size => Max - Min;

    /// <summary>True when this box overlaps <paramref name="other"/> (touching counts).</summary>
    public bool Intersects(in Aabb other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public Aabb Expanded(float margin) =>
        new(Min - new Vector3(margin), Max + new Vector3(margin));

    /// <summary>Builds the tightest box enclosing a set of points.</summary>
    public static Aabb FromPoints(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return new Aabb(min, max);
    }
}
