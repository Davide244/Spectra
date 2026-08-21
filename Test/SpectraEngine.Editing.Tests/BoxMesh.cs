using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// A CPU-only <see cref="Mesh"/> with real positions and a real local AABB, and
/// no GPU resource behind it at all.
/// </summary>
/// <remarks>
/// <b>The resize tool measures a mesh node through exactly these two
/// properties</b> — <see cref="Mesh.Positions"/> (non-empty is what makes bounds
/// trustworthy, the same test <c>SceneBvh</c> applies) and
/// <see cref="Mesh.LocalBounds"/> — so a mesh node in the editing suite has to
/// carry them or it is testing the tool's no-bounds fallback by accident. Drawing
/// is a no-op: nothing here ever reaches a renderer.
/// </remarks>
internal sealed class BoxMesh : Mesh
{
    private BoxMesh(Vector3[] corners, Aabb bounds)
    {
        Positions = corners;
        LocalBounds = bounds;
        IndexCount = 0;
    }

    /// <summary>A box of the given half extents, centred on the mesh's origin.</summary>
    public static BoxMesh Centred(Vector3 halfExtents)
    {
        var min = -halfExtents;
        var max = halfExtents;
        Vector3[] corners =
        [
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(min.X, max.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z),
            new(max.X, min.Y, max.Z),
            new(min.X, max.Y, max.Z),
            new(max.X, max.Y, max.Z),
        ];
        return new BoxMesh(corners, new Aabb(min, max));
    }

    /// <summary>A box spanning an arbitrary, not necessarily centred, local range.</summary>
    public static BoxMesh Spanning(Vector3 min, Vector3 max)
    {
        Vector3[] corners =
        [
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(min.X, max.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z),
            new(max.X, min.Y, max.Z),
            new(min.X, max.Y, max.Z),
            new(max.X, max.Y, max.Z),
        ];
        return new BoxMesh(corners, new Aabb(min, max));
    }

    public override void Draw()
    {
    }

    public override void Dispose()
    {
    }
}
