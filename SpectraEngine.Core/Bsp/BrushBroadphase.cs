using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Broadphase overlap detection over brush bounding boxes. A sort-and-sweep on
/// the X axis cuts brush-pair testing from O(n²) toward O(n log n), feeding the
/// CSG carve only genuine candidates instead of every other brush.
/// </summary>
public static class BrushBroadphase
{
    /// <summary>
    /// Returns, for each brush, the indices of every other brush whose bounding
    /// box overlaps it.
    /// </summary>
    public static int[][] FindOverlaps(IReadOnlyList<Aabb> bounds)
    {
        int n = bounds.Count;
        var neighbors = new List<int>[n];
        for (int i = 0; i < n; i++)
            neighbors[i] = [];

        // Process brushes in order of ascending minimum X.
        var order = new int[n];
        for (int i = 0; i < n; i++)
            order[i] = i;
        Array.Sort(order, (a, b) => bounds[a].Min.X.CompareTo(bounds[b].Min.X));

        // Brushes whose X span is still open at the current sweep position.
        var active = new List<int>();

        for (int s = 0; s < n; s++)
        {
            int i = order[s];
            float minX = bounds[i].Min.X;

            // Retire brushes the sweep line has passed (swap-remove; order is irrelevant).
            for (int a = active.Count - 1; a >= 0; a--)
            {
                if (bounds[active[a]].Max.X < minX)
                {
                    active[a] = active[^1];
                    active.RemoveAt(active.Count - 1);
                }
            }

            // Anything still active overlaps on X; confirm Y and Z.
            foreach (int j in active)
            {
                if (bounds[i].Intersects(bounds[j]))
                {
                    neighbors[i].Add(j);
                    neighbors[j].Add(i);
                }
            }

            active.Add(i);
        }

        var result = new int[n][];
        for (int i = 0; i < n; i++)
            result[i] = neighbors[i].ToArray();
        return result;
    }
}
