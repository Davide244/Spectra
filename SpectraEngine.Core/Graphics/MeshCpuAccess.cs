namespace SpectraEngine.Core.Graphics;

/// <summary>
/// What CPU-side geometry a mesh keeps after its GPU upload.
/// </summary>
/// <remarks>
/// Retention is opt-in per creation because the two mesh populations want
/// opposite answers. A mesh NODE's mesh is raycast for picking, measured for
/// BVH bounds, and drawn as a debug wireframe, all of which read
/// <see cref="Mesh.Positions"/>/<see cref="Mesh.Indices"/> on the CPU. A
/// static-world chunk mesh or a part-brush mesh is read by nothing: chunks are
/// culled by the compiled artifact's render bounds and queried through the
/// BSP, and parts are picked through their brush planes. Retaining copies for
/// them anyway was both per-swap garbage on the render thread (the compiler
/// re-creates dirty chunk meshes every frame a world brush moves) and a
/// permanent second copy of all world geometry on the managed heap.
/// <see cref="Mesh.LocalBounds"/> is computed either way, straight off the
/// upload data, because every mesh needs its extents.
/// </remarks>
public enum MeshCpuAccess
{
    /// <summary>Keep positions, normals and indices for CPU readers.</summary>
    Retained,

    /// <summary>
    /// GPU-only: <see cref="Mesh.Positions"/>, <see cref="Mesh.Normals"/> and
    /// <see cref="Mesh.Indices"/> stay empty; <see cref="Mesh.LocalBounds"/>
    /// is still computed.
    /// </summary>
    None,
}
