using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// A compiled static world: the result of running CSG over a set of brushes.
/// Holds the carved visible surfaces and a BSP tree for spatial queries, and
/// can produce a render mesh — all from one <see cref="Build"/> call.
/// </summary>
public sealed class CsgWorld
{
    private CsgWorld(IReadOnlyList<Polygon> surfaces, BspTree bsp)
    {
        Surfaces = surfaces;
        Bsp = bsp;
    }

    /// <summary>The visible exterior surfaces of the brush union.</summary>
    public IReadOnlyList<Polygon> Surfaces { get; }

    /// <summary>A solid-leaf BSP tree over the carved geometry.</summary>
    public BspTree Bsp { get; }

    /// <summary>Carves the brushes and builds the BSP tree from the result.</summary>
    public static CsgWorld Build(IReadOnlyList<Brush> brushes)
    {
        Polygon[] surfaces = Csg.Carve(brushes);
        BspTree bsp = BspTree.BuildFromSurfaces(surfaces);
        return new CsgWorld(surfaces, bsp);
    }

    /// <summary>
    /// Builds a position+normal render mesh (6 floats per vertex) from the
    /// carved surfaces, matching the engine's standard vertex layout.
    /// </summary>
    public (float[] Vertices, uint[] Indices) BuildMesh()
    {
        var vertices = new List<float>();
        var indices = new List<uint>();

        foreach (Polygon poly in Surfaces)
        {
            Vector3 normal = poly.Surface.Normal;
            uint baseIndex = (uint)(vertices.Count / 6);

            foreach (Vector3 v in poly.Vertices)
            {
                vertices.Add(v.X); vertices.Add(v.Y); vertices.Add(v.Z);
                vertices.Add(normal.X); vertices.Add(normal.Y); vertices.Add(normal.Z);
            }

            for (uint i = 1; i + 1 < poly.VertexCount; i++)
            {
                indices.Add(baseIndex);
                indices.Add(baseIndex + i);
                indices.Add(baseIndex + i + 1);
            }
        }

        return (vertices.ToArray(), indices.ToArray());
    }
}
