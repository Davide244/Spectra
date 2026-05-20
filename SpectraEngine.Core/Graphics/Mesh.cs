using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Base class for renderer-owned mesh resources. Subclasses own the GPU
/// handles; the base also keeps a CPU-side copy of positions, normals,
/// indices, and a local AABB so debug visualisations and bounds queries can
/// work without round-tripping through the GPU.
/// </summary>
public abstract class Mesh : IDisposable
{
    public uint IndexCount { get; protected set; }

    /// <summary>Per-vertex positions in the mesh's local frame.</summary>
    public IReadOnlyList<Vector3> Positions { get; protected set; } = [];

    /// <summary>Per-vertex normals; empty if the mesh was created without a normal attribute.</summary>
    public IReadOnlyList<Vector3> Normals { get; protected set; } = [];

    /// <summary>The index buffer, three entries per triangle.</summary>
    public IReadOnlyList<uint> Indices { get; protected set; } = [];

    /// <summary>AABB enclosing <see cref="Positions"/> in the mesh's local frame.</summary>
    public Aabb LocalBounds { get; protected set; }

    public abstract void Draw();

    public abstract void Dispose();
}
