using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// A view frustum as six normalized planes whose normals point inward: a point
/// is inside when its signed distance to every plane is non-negative. Extracted
/// from a combined view-projection matrix via Gribb–Hartmann, adapted to the
/// System.Numerics row-vector convention the engine uses on the CPU
/// (clip = world · View · Projection), where each clip component is a dot
/// product of the point with a matrix <em>column</em>. An immutable value type —
/// safe to copy across threads; build one per cull pass from
/// <see cref="Camera.GetFrustum"/>.
/// </summary>
public readonly struct Frustum
{
    /// <summary>Left clip plane (inward normal points right).</summary>
    public readonly Plane Left;

    /// <summary>Right clip plane (inward normal points left).</summary>
    public readonly Plane Right;

    /// <summary>Bottom clip plane (inward normal points up).</summary>
    public readonly Plane Bottom;

    /// <summary>Top clip plane (inward normal points down).</summary>
    public readonly Plane Top;

    /// <summary>Near clip plane (inward normal points away from the camera).</summary>
    public readonly Plane Near;

    /// <summary>Far clip plane (inward normal points toward the camera).</summary>
    public readonly Plane Far;

    private Frustum(Plane left, Plane right, Plane bottom, Plane top, Plane near, Plane far)
    {
        Left = left;
        Right = right;
        Bottom = bottom;
        Top = top;
        Near = near;
        Far = far;
    }

    /// <summary>
    /// Extracts the six planes from a row-vector view-projection matrix
    /// (<c>clip = world · viewProjection</c>, i.e. <see cref="Camera.GetViewProjection"/>).
    /// </summary>
    public static Frustum FromViewProjection(in Matrix4x4 m)
    {
        // Row-vector Gribb–Hartmann: clip.x/y/z/w are dots of the point with
        // columns 1..4 of the matrix, so the inside-clip conditions
        //   -w <= x,  x <= w,  -w <= y,  y <= w,  0 <= z,  z <= w
        // turn into sums/differences of column 4 with columns 1–3. The near
        // plane is column 3 ALONE because Matrix4x4.CreatePerspectiveFieldOfView
        // maps depth to the D3D-style [0, 1] clip range (near plane lands at
        // z = 0), not OpenGL's [-1, 1]. Normalizing makes DotCoordinate a true
        // world-space distance, which the positive-vertex test relies on.
        return new Frustum(
            left: Plane.Normalize(new Plane(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41)),
            right: Plane.Normalize(new Plane(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41)),
            bottom: Plane.Normalize(new Plane(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42)),
            top: Plane.Normalize(new Plane(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42)),
            near: Plane.Normalize(new Plane(m.M13, m.M23, m.M33, m.M43)),
            far: Plane.Normalize(new Plane(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43)));
    }

    /// <summary>
    /// Conservative frustum-vs-box test for culling: returns false ONLY when the
    /// box lies fully outside at least one plane, so a box containing any visible
    /// point is never rejected. May return true for a box that straddles a corner
    /// region outside the true frustum — false positives merely cost a draw,
    /// false negatives would make geometry vanish.
    /// </summary>
    public bool Intersects(in Aabb box) =>
        !OutsidePlane(Left, box) && !OutsidePlane(Right, box) &&
        !OutsidePlane(Bottom, box) && !OutsidePlane(Top, box) &&
        !OutsidePlane(Near, box) && !OutsidePlane(Far, box);

    /// <summary>True when the point is on the inner side of all six planes (boundary counts as inside).</summary>
    public bool Contains(Vector3 point) =>
        Plane.DotCoordinate(Left, point) >= 0f &&
        Plane.DotCoordinate(Right, point) >= 0f &&
        Plane.DotCoordinate(Bottom, point) >= 0f &&
        Plane.DotCoordinate(Top, point) >= 0f &&
        Plane.DotCoordinate(Near, point) >= 0f &&
        Plane.DotCoordinate(Far, point) >= 0f;

    private static bool OutsidePlane(in Plane plane, in Aabb box)
    {
        // Positive-vertex trick: test only the box corner farthest along the
        // inward plane normal. If even that corner is behind the plane, the
        // whole box is — one dot product instead of eight per plane.
        var positive = new Vector3(
            plane.Normal.X >= 0f ? box.Max.X : box.Min.X,
            plane.Normal.Y >= 0f ? box.Max.Y : box.Min.Y,
            plane.Normal.Z >= 0f ? box.Max.Z : box.Min.Z);
        return Plane.DotCoordinate(plane, positive) < 0f;
    }
}
