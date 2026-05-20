using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Eliminates T-junctions in a polygon set by inserting any vertex that lies
/// on another polygon's edge into that edge's vertex list.
/// <para>
/// CSG carves often emit two surfaces that meet along the same geometric line
/// but with different vertex counts along it — e.g. a floor frame's edge has
/// corners where a pillar's footprint was cut, but the pillar's side face only
/// has its own two bottom corners along that same line. Without welding, the
/// rasteriser drops sub-pixel columns at the unmatched vertices and a 1-pixel
/// crack appears along the seam.
/// </para>
/// </summary>
public static class TJunctionWelder
{
    private const float Epsilon = Polygon.Epsilon;
    private const float EpsilonSq = Epsilon * Epsilon;

    /// <summary>
    /// Returns a new polygon array with T-junction vertices welded into every
    /// edge. Inputs are not modified; polygons with no T-junctions are reused
    /// as-is.
    /// </summary>
    public static Polygon[] Weld(IReadOnlyList<Polygon> polygons)
    {
        // Every vertex appearing anywhere is a candidate weld point. Duplicates
        // are harmless — the per-edge dedupe catches them.
        var allVertices = new List<Vector3>();
        foreach (Polygon poly in polygons)
            foreach (Vector3 v in poly.Vertices)
                allVertices.Add(v);

        var result = new Polygon[polygons.Count];
        for (int i = 0; i < polygons.Count; i++)
            result[i] = WeldOne(polygons[i], allVertices);
        return result;
    }

    private static Polygon WeldOne(Polygon poly, List<Vector3> allVertices)
    {
        var oldVerts = poly.Vertices;
        var newVerts = new List<Vector3>(oldVerts.Count);
        var insertions = new List<(float T, Vector3 V)>();

        for (int i = 0; i < oldVerts.Count; i++)
        {
            Vector3 a = oldVerts[i];
            Vector3 b = oldVerts[(i + 1) % oldVerts.Count];
            newVerts.Add(a);

            Vector3 ab = b - a;
            float abLengthSq = ab.LengthSquared();
            if (abLengthSq < EpsilonSq)
                continue;

            insertions.Clear();
            foreach (Vector3 v in allVertices)
            {
                // Skip the edge's own endpoints.
                if (Vector3.DistanceSquared(v, a) < EpsilonSq) continue;
                if (Vector3.DistanceSquared(v, b) < EpsilonSq) continue;

                // Project onto the line; reject if outside the open segment.
                float t = Vector3.Dot(v - a, ab) / abLengthSq;
                if (t <= 0f || t >= 1f) continue;

                // Reject if v isn't actually on the line (off to the side).
                Vector3 closest = a + ab * t;
                if (Vector3.DistanceSquared(closest, v) > EpsilonSq) continue;

                insertions.Add((t, v));
            }

            if (insertions.Count == 0)
                continue;

            insertions.Sort((x, y) => x.T.CompareTo(y.T));

            // Dedupe insertions that coincide (two different polygons can both
            // contribute the same T-vertex).
            Vector3 last = a;
            foreach (var (_, v) in insertions)
            {
                if (Vector3.DistanceSquared(v, last) < EpsilonSq)
                    continue;
                newVerts.Add(v);
                last = v;
            }
        }

        return newVerts.Count == oldVerts.Count
            ? poly
            : new Polygon(newVerts.ToArray(), poly.Surface);
    }
}
