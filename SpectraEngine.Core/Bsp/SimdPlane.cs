using System;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// SIMD-accelerated plane/point classification. Signed distances follow the
/// <see cref="Plane"/> convention: positive in front of the plane (normal side),
/// negative behind. Processes <see cref="Vector{T}.Count"/> points per step on
/// vector-capable hardware and falls back to scalar code for the tail.
/// </summary>
public static class SimdPlane
{
    /// <summary>
    /// Writes the signed distance from each point in <paramref name="points"/>
    /// to <paramref name="plane"/> into <paramref name="distances"/>.
    /// </summary>
    public static void SignedDistances(Plane plane, ReadOnlySpan<Vector3> points, Span<float> distances)
    {
        if (distances.Length < points.Length)
            throw new ArgumentException("Destination span is smaller than the point count.", nameof(distances));

        int width = Vector<float>.Count;
        int i = 0;

        if (Vector.IsHardwareAccelerated && points.Length >= width)
        {
            var normalX = new Vector<float>(plane.Normal.X);
            var normalY = new Vector<float>(plane.Normal.Y);
            var normalZ = new Vector<float>(plane.Normal.Z);
            var offset = new Vector<float>(plane.D);

            Span<float> xs = stackalloc float[width];
            Span<float> ys = stackalloc float[width];
            Span<float> zs = stackalloc float[width];

            for (; i + width <= points.Length; i += width)
            {
                // Deinterleave the lane block into structure-of-arrays form.
                for (int lane = 0; lane < width; lane++)
                {
                    Vector3 p = points[i + lane];
                    xs[lane] = p.X;
                    ys[lane] = p.Y;
                    zs[lane] = p.Z;
                }

                var vx = new Vector<float>(xs);
                var vy = new Vector<float>(ys);
                var vz = new Vector<float>(zs);
                Vector<float> result = vx * normalX + vy * normalY + vz * normalZ + offset;
                result.CopyTo(distances.Slice(i, width));
            }
        }

        for (; i < points.Length; i++)
            distances[i] = Plane.DotCoordinate(plane, points[i]);
    }
}
