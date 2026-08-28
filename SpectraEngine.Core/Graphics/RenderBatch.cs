using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// A run of draws collapsed into one: the same mesh and the same material,
/// differing only in world matrix.
/// </summary>
/// <remarks>
/// <para>
/// <b>The transforms are a contiguous slice of the view's own instance array,
/// not a list per batch.</b> One array uploaded once per frame is what lets a
/// batch be drawn without a per-batch buffer, and the slice is why the batches
/// have to be filled in batch order rather than in emission order: a batch's
/// instances must be adjacent for the draw to name them as a range.
/// </para>
/// </remarks>
/// <param name="Mesh">The geometry every instance in this batch draws.</param>
/// <param name="Material">The material every instance draws it with.</param>
/// <param name="Offset">Index of this batch's first transform in the view's instance array.</param>
/// <param name="Count">How many instances the batch carries.</param>
public readonly record struct RenderBatch(Mesh Mesh, Material? Material, int Offset, int Count);

/// <summary>
/// Identity of a batch: the mesh and material a draw would bind.
/// </summary>
/// <remarks>
/// <b>Reference identity, deliberately.</b> Two structurally identical meshes
/// are still two GPU buffers and cannot be drawn as one batch, and two materials
/// that happen to carry equal parameters still bind separately. Grouping by
/// anything but the object a draw would actually bind is how a batch ends up
/// drawing the wrong thing.
/// </remarks>
internal readonly record struct RenderBatchKey(Mesh Mesh, Material? Material);
