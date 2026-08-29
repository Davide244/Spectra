namespace SpectraEngine.Core.Scene;

/// <summary>
/// Where a node's <see cref="MeshRenderer"/> came from: a model file, and which
/// submesh of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because a <c>Mesh</c> cannot say where it came from, and
/// nothing else could say it either.</b> <c>Renderer.CreateMesh</c> takes raw
/// vertex and index spans, so there is no moment at creation time when an
/// origin could be recorded even if <c>Mesh</c> had a field for it, and
/// <c>MeshRenderer</c> holds two live objects and nothing else.
/// <c>ModelInstantiator</c> knows the pair while it is building each node and
/// then throws it away, which is exactly the fact a map needs and cannot
/// otherwise recover.
/// </para>
/// <para>
/// <b>It is a descriptor, not a payload.</b> It describes the renderer the node
/// is carrying rather than being a thing the node draws, so it is written
/// alongside a <see cref="MeshRenderer"/> and means nothing without one. A node
/// whose mesh was built in code (from <c>Primitives</c>, or any other raw
/// array) has none, and that is permanent rather than unfinished: there is no
/// file to point at.
/// </para>
/// <para>
/// <b>The submesh index is positional into <c>ModelAsset.Meshes</c>, which is
/// index-aligned with <c>ModelData.Meshes</c>.</b> That makes it valid only
/// under the import options the model was loaded with, and those are pinned to
/// whichever caller loaded the path first. Re-exporting a model file can
/// therefore invalidate an index without changing the path, which is a property
/// of referencing geometry by position and not something this type can fix.
/// </para>
/// </remarks>
/// <param name="ModelPath">Content-root-relative path of the model file.</param>
/// <param name="MeshIndex">Index into the model's submesh list.</param>
public readonly record struct MeshSource(string ModelPath, int MeshIndex);
