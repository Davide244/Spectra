using SpectraEngine.Core.Bsp;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// The pure-CPU result of importing a model file: engine-ready vertex/index
/// arrays split into single-material submeshes, the material descriptions those
/// submeshes reference, and the node hierarchy that places them.
/// </summary>
/// <remarks>
/// <para><b>Why a CPU-only artifact.</b> Exactly like the static-world compile,
/// model import runs on the thread pool and must not touch anything the render
/// thread owns — no <see cref="Graphics.Mesh"/>, no <see cref="Graphics.Texture"/>,
/// no <see cref="Graphics.Material"/>. Everything here is plain values and
/// arrays, so the whole graph can be built off-thread and handed across the
/// upload queue by reference. <see cref="AssetManager"/> turns it into GPU
/// resources on the render thread; <c>ModelInstantiator</c> turns the hierarchy
/// into scene nodes.</para>
/// <para><b>Immutable by contract.</b> The arrays are exposed directly so the
/// GPU upload is zero-copy — the same deal <see cref="Bsp.ChunkSubmesh"/>
/// makes. Never mutate them.</para>
/// </remarks>
public sealed class ModelData
{
    internal ModelData(
        string sourcePath,
        ModelMesh[] meshes,
        ModelMaterial[] materials,
        ModelNode root,
        Aabb localBounds,
        string[] warnings)
    {
        SourcePath = sourcePath;
        Meshes = meshes;
        Materials = materials;
        Root = root;
        LocalBounds = localBounds;
        Warnings = warnings;

        int vertices = 0;
        int indices = 0;
        for (int i = 0; i < meshes.Length; i++)
        {
            vertices += meshes[i].VertexCount;
            indices += meshes[i].Indices.Length;
        }
        VertexCount = vertices;
        IndexCount = indices;
    }

    /// <summary>Absolute path of the file this was imported from.</summary>
    public string SourcePath { get; }

    /// <summary>
    /// Every drawable piece of the model, one per (source mesh, material) pair —
    /// the importer never merges two materials into one array, because one
    /// submesh is one GPU mesh and one draw call.
    /// </summary>
    public IReadOnlyList<ModelMesh> Meshes { get; }

    /// <summary>
    /// The materials <see cref="ModelMesh.MaterialIndex"/> indexes into. Kept
    /// index-aligned with the source file's material table — including slots no
    /// mesh references, which importers routinely emit — so an index read out of
    /// the file always means what the file said it meant.
    /// </summary>
    public IReadOnlyList<ModelMaterial> Materials { get; }

    /// <summary>
    /// Root of the imported hierarchy. Always present, even for a flat file: a
    /// format with no scene graph of its own still yields one root holding every
    /// mesh.
    /// </summary>
    public ModelNode Root { get; }

    /// <summary>
    /// AABB enclosing the whole model in its own space — every submesh's
    /// geometry transformed by the accumulated transform of the nodes that
    /// reference it, so a part placed by a node transform is enclosed where it
    /// actually sits rather than where its raw vertices are.
    /// </summary>
    public Aabb LocalBounds { get; }

    /// <summary>Total vertices across every submesh.</summary>
    public int VertexCount { get; }

    /// <summary>Total indices across every submesh (three per triangle).</summary>
    public int IndexCount { get; }

    /// <summary>
    /// Content problems that were degraded rather than raised: a texture path
    /// that escaped the content root, a mesh dropped for not being triangles, a
    /// node transform that would not decompose. The importer only throws when
    /// there is no usable model at all; everything survivable lands here and is
    /// logged by the caller.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// One single-material piece of a model: its own interleaved vertex array and
/// index array in the engine's standard 8-float layout (position, normal, uv —
/// see <see cref="Graphics.VertexAttribute.StandardLayout"/>), self-contained
/// and zero-based, ready to become one GPU mesh and one draw call.
/// </summary>
/// <param name="Name">The source mesh's name, for logs and editor UI.</param>
/// <param name="MaterialIndex">Index into <see cref="ModelData.Materials"/>.</param>
/// <param name="Vertices">Interleaved vertex data, 8 floats per vertex. Treat as immutable.</param>
/// <param name="Indices">Index data, three entries per triangle. Treat as immutable.</param>
/// <param name="LocalBounds">AABB of this piece's raw vertex positions.</param>
/// <param name="HadNormals">
/// False when the source file carried no normals and they were generated.
/// </param>
/// <param name="HadTextureCoordinates">
/// False when the source file carried no UV channel — the uv components are then
/// zero for every vertex, which samples one texel rather than leaving garbage in
/// the stream.
/// </param>
public readonly record struct ModelMesh(
    string Name,
    int MaterialIndex,
    float[] Vertices,
    uint[] Indices,
    Aabb LocalBounds,
    bool HadNormals,
    bool HadTextureCoordinates)
{
    /// <summary>Number of vertices, i.e. <see cref="Vertices"/> length over 8.</summary>
    public int VertexCount => Vertices.Length / ModelVertexLayout.FloatsPerVertex;

    /// <summary>Number of triangles, i.e. <see cref="Indices"/> length over 3.</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// A material as the model file described it — a name, an optional diffuse
/// texture, and a base colour. Deliberately not a <see cref="Graphics.Material"/>:
/// that needs a shader and GPU textures, which only the render thread may touch.
/// <see cref="AssetManager"/> resolves this into a real material at load time.
/// </summary>
/// <param name="Name">
/// The material's name in the source file. This is also the lookup key for an
/// engine-authored override: <c>Materials/&lt;name&gt;.spectramat</c> wins over
/// whatever the file itself said, which is how a designer re-shades imported
/// content without touching the model.
/// </param>
/// <param name="DiffuseTexturePath">
/// Content-root-relative path of the diffuse/base-colour texture, or null when
/// the material named none, named one that could not be located under the
/// content root, or named an embedded texture (not supported).
/// </param>
/// <param name="BaseColor">
/// The material's diffuse colour, defaulting to white when the file carried
/// none. Applied as the <c>uBaseColor</c> tint over the diffuse texture.
/// </param>
/// <param name="AssetPath">
/// The content path of the <c>.spectramat</c> this material IS, when the model
/// named one rather than describing one. Null for an imported material, which
/// describes itself and is matched to an override by <see cref="Name"/>.
/// </param>
/// <remarks>
/// <b><see cref="AssetPath"/> is what a COOKED model carries, and it is a path
/// rather than a name for one reason.</b> A cooked submesh's material reference
/// is a logical asset path resolved once, at cook time, and recorded; a loader
/// that rebuilt <c>Materials/&lt;name&gt;.spectramat</c> from the stem would be a
/// second spelling of a rule the cooker already applied, agreeing with it exactly
/// until a material lives somewhere else and then missing silently. The two
/// fields are therefore not alternatives: a name is a lookup KEY, a path is an
/// answer.
/// </remarks>
public readonly record struct ModelMaterial(
    string Name,
    string? DiffuseTexturePath,
    Vector3 BaseColor,
    string? AssetPath = null);

/// <summary>
/// A node in the imported hierarchy: a name, a local transform, the submeshes
/// attached to it, and its children. Mirrors <see cref="Scene.SceneNode"/>
/// closely enough that instantiation is a direct walk, but stays a pure value
/// tree so it can be built on the thread pool and instantiated many times.
/// </summary>
public sealed class ModelNode
{
    internal ModelNode(
        string name,
        Matrix4x4 localMatrix,
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        bool transformIsExact,
        int[] meshIndices,
        ModelNode[] children)
    {
        Name = name;
        LocalMatrix = localMatrix;
        Position = position;
        Rotation = rotation;
        Scale = scale;
        TransformIsExact = transformIsExact;
        MeshIndices = meshIndices;
        Children = children;
    }

    /// <summary>The node's name in the source file.</summary>
    public string Name { get; }

    /// <summary>
    /// The node's transform relative to its parent, exactly as the file stored
    /// it (already converted to the engine's row-vector convention).
    /// </summary>
    public Matrix4x4 LocalMatrix { get; }

    /// <summary>Translation component of <see cref="LocalMatrix"/>.</summary>
    public Vector3 Position { get; }

    /// <summary>Rotation component of <see cref="LocalMatrix"/>.</summary>
    public Quaternion Rotation { get; }

    /// <summary>Scale component of <see cref="LocalMatrix"/>.</summary>
    public Vector3 Scale { get; }

    /// <summary>
    /// False when <see cref="LocalMatrix"/> could not be decomposed into
    /// position/rotation/scale — a sheared or mirrored node, which the scene
    /// graph's TRS transform cannot represent. The components then hold an
    /// identity fallback and a warning was recorded; the raw matrix is still
    /// exact.
    /// </summary>
    public bool TransformIsExact { get; }

    /// <summary>
    /// Indices into <see cref="ModelData.Meshes"/> drawn at this node. Empty for
    /// pure grouping nodes, which most hierarchies are mostly made of.
    /// </summary>
    public IReadOnlyList<int> MeshIndices { get; }

    /// <summary>Child nodes, in the source file's order.</summary>
    public IReadOnlyList<ModelNode> Children { get; }
}

/// <summary>
/// The interleaved vertex layout every imported submesh uses, matching
/// <see cref="Graphics.VertexAttribute.StandardLayout"/>.
/// </summary>
public static class ModelVertexLayout
{
    /// <summary>Floats per vertex: position (3) + normal (3) + uv (2).</summary>
    public const int FloatsPerVertex = 8;

    /// <summary>Float offset of the position within a vertex.</summary>
    public const int PositionOffset = 0;

    /// <summary>Float offset of the normal within a vertex.</summary>
    public const int NormalOffset = 3;

    /// <summary>Float offset of the uv within a vertex.</summary>
    public const int TexCoordOffset = 6;
}
