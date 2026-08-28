using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// A CPU-only <see cref="Mesh"/> with real positions and a real local AABB, and
/// no GPU resource behind it at all.
/// </summary>
/// <remarks>
/// <b>The resize tool and the gizmo's selection box both measure a mesh node
/// through <see cref="Mesh.HasLocalBounds"/> and <see cref="Mesh.LocalBounds"/></b>,
/// so a mesh node in the editing suite has to carry both or it is testing the
/// no-bounds fallback by accident. <see cref="Mesh.Positions"/> is filled too,
/// because picking and the BVH read it, but it is no longer what makes the
/// bounds trustworthy: a real mesh computes its bounds off the upload stream
/// whether or not it keeps a CPU copy. Drawing is a no-op: nothing here ever
/// reaches a renderer.
/// </remarks>
internal sealed class BoxMesh : Mesh
{
    private BoxMesh(Vector3[] corners, Aabb bounds)
    {
        Positions = corners;
        LocalBounds = bounds;
        HasLocalBounds = true;
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

    public override void DrawInstanced(InstanceBuffer instances, int instanceCount)
    {
    }

    public override void Dispose()
    {
    }
}
