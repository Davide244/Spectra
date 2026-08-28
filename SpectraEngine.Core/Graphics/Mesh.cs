using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Base class for renderer-owned mesh resources. Subclasses own the GPU
/// handles; the base can also keep a CPU-side copy of positions, normals and
/// indices so debug visualisations, bounds queries and raycasts work without
/// round-tripping through the GPU. The copy is opt-in per creation; see
/// <see cref="MeshCpuAccess"/> for which meshes want it and which must not
/// pay for it.
/// </summary>
public abstract class Mesh : IDisposable
{
    public uint IndexCount { get; protected set; }

    /// <summary>
    /// Per-vertex positions in the mesh's local frame. Empty when the mesh was
    /// created with <see cref="MeshCpuAccess.None"/>.
    /// </summary>
    public IReadOnlyList<Vector3> Positions { get; protected set; } = [];

    /// <summary>Per-vertex normals; empty if the mesh was created without a normal attribute or without CPU access.</summary>
    public IReadOnlyList<Vector3> Normals { get; protected set; } = [];

    /// <summary>The index buffer, three entries per triangle. Empty without CPU access.</summary>
    public IReadOnlyList<uint> Indices { get; protected set; } = [];

    /// <summary>AABB enclosing the mesh's vertices in its local frame. Computed for every mesh, CPU access or not.</summary>
    public Aabb LocalBounds { get; protected set; }

    /// <summary>
    /// Whether <see cref="LocalBounds"/> describes real geometry. True once
    /// <see cref="InitializeCpuData"/> saw at least one position, whatever the
    /// CPU-access mode; false on a mesh with no position stream, whose default
    /// bounds mean nothing. Consumers deciding whether the bounds are usable
    /// must read THIS, not <see cref="Positions"/>: a GPU-only mesh has valid
    /// bounds and empty arrays, and inferring one from the other is how a
    /// correctly measured mesh ends up culled against a placeholder box.
    /// </summary>
    public bool HasLocalBounds { get; protected set; }

    /// <summary>
    /// Computes <see cref="LocalBounds"/> straight off the interleaved upload
    /// stream and, only under <see cref="MeshCpuAccess.Retained"/>,
    /// materialises <see cref="Positions"/>/<see cref="Normals"/>/<see cref="Indices"/>.
    /// One implementation for every backend, called from each Create path.
    /// </summary>
    protected void InitializeCpuData(
        ReadOnlySpan<float> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes,
        MeshCpuAccess cpuAccess)
    {
        // Positions live at location 0 and normals at location 1 by the
        // engine's layout convention; other attributes are ignored here.
        int stride = 0;
        int positionOffset = -1;
        int normalOffset = -1;
        for (int i = 0; i < attributes.Length; i++)
        {
            if (attributes[i].Location == 0) positionOffset = stride;
            else if (attributes[i].Location == 1) normalOffset = stride;
            stride += (int)attributes[i].ComponentCount;
        }

        int vertexCount = positionOffset >= 0 && stride > 0 ? vertices.Length / stride : 0;
        if (vertexCount == 0)
        {
            LocalBounds = new Aabb(Vector3.Zero, Vector3.Zero);
            return;
        }

        bool retain = cpuAccess == MeshCpuAccess.Retained;
        Vector3[] positions = retain ? new Vector3[vertexCount] : [];
        Vector3[] normals = retain && normalOffset >= 0 ? new Vector3[vertexCount] : [];

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < vertexCount; i++)
        {
            int b = i * stride;
            var p = new Vector3(
                vertices[b + positionOffset],
                vertices[b + positionOffset + 1],
                vertices[b + positionOffset + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);

            if (positions.Length != 0) positions[i] = p;
            if (normals.Length != 0)
                normals[i] = new Vector3(
                    vertices[b + normalOffset],
                    vertices[b + normalOffset + 1],
                    vertices[b + normalOffset + 2]);
        }

        LocalBounds = new Aabb(min, max);
        HasLocalBounds = true;
        if (retain)
        {
            Positions = positions;
            Normals = normals;
            Indices = indices.ToArray();
        }
    }

    /// <summary>
    /// Removes this mesh from the creating renderer's tracking list; the
    /// renderer hands it over at creation time and <see cref="Renderer.DestroyMesh"/>
    /// invokes it exactly once. Unsynchronized on purpose: resource creation
    /// and destruction both happen on the render thread.
    /// </summary>
    internal Action? Unregister { get; set; }

    public abstract void Draw();

    /// <summary>
    /// Draws this mesh <paramref name="instanceCount"/> times, with per-instance
    /// attributes read from <paramref name="instances"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bound shader must declare the instance attributes</b>, or the
    /// hardware feeds them nothing and every instance lands on top of the first.
    /// That is a picture rather than an error on all three backends, which is
    /// why the compiler reports a shader's inputs (see
    /// <see cref="VertexAttribute.FromShaderInputs"/>) instead of leaving the
    /// layout to be agreed by hand.
    /// </para>
    /// <para>
    /// A count of zero draws nothing and is not an error: a batch can be culled
    /// to empty between being formed and being submitted, and making the caller
    /// guard every call is how one site forgets.
    /// </para>
    /// </remarks>
    /// <param name="instances">The buffer holding per-instance attributes.</param>
    /// <param name="instanceCount">How many instances to draw.</param>
    /// <param name="firstInstance">
    /// Index of the first instance to read, so several batches can share one
    /// upload. D3D takes this natively as a start location; GL 3.3 has no
    /// <c>BaseInstance</c> at all (that is 4.2), so the GL backend expresses it
    /// by re-pointing the attributes at the right byte offset instead.
    /// </param>
    public abstract void DrawInstanced(InstanceBuffer instances, int instanceCount, int firstInstance = 0);

    public abstract void Dispose();
}
