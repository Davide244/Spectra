using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Snaps polygon vertices to a fine grid so logically-equal points produced by
/// independent floating-point paths collapse to the same exact float.
/// <para>
/// Each brush face computes its corners by clipping its own seed quad, so the
/// same logical corner emerges from a different sequence of <see cref="Vector3.Lerp"/>
/// calls per face and lands within ~1 ULP of itself. The rasterizer's fill rule
/// only guarantees crack-free coverage when shared edge endpoints are bit-identical;
/// snapping makes them so.
/// </para>
/// </summary>
public static class VertexSnapper
{
    /// <summary>
    /// Snap grid in world units. Chosen well above floating-point noise
    /// (~1e-7 at unit-metre scale) and well below visible precision.
    /// </summary>
    public const float GridSize = 1e-4f;

    /// <summary>
    /// Returns a new polygon array with every vertex snapped to the grid.
    /// Inputs are not modified.
    /// </summary>
    public static Polygon[] Snap(IReadOnlyList<Polygon> polygons)
    {
        var result = new Polygon[polygons.Count];
        for (int i = 0; i < polygons.Count; i++)
        {
            var oldVerts = polygons[i].Vertices;
            var newVerts = new Vector3[oldVerts.Count];
            for (int j = 0; j < oldVerts.Count; j++)
                newVerts[j] = SnapVertex(oldVerts[j]);
            result[i] = new Polygon(newVerts, polygons[i].Surface);
        }
        return result;
    }

    private static Vector3 SnapVertex(Vector3 v) => new(
        MathF.Round(v.X / GridSize) * GridSize,
        MathF.Round(v.Y / GridSize) * GridSize,
        MathF.Round(v.Z / GridSize) * GridSize);
}
