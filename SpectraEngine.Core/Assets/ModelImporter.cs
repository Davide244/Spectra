using Silk.NET.Assimp;
using SpectraEngine.Core.Bsp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
// Silk.NET.Assimp exports its own File/Material/Mesh/Node/Scene types, all of
// which collide with names this file legitimately needs. Aliasing both sides
// keeps every use site unambiguous at a glance.
using AiMaterial = Silk.NET.Assimp.Material;
using AiMesh = Silk.NET.Assimp.Mesh;
using AiNode = Silk.NET.Assimp.Node;
using AiScene = Silk.NET.Assimp.Scene;
using SysFile = System.IO.File;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// Reads a model file from disk and converts it into engine-ready
/// <see cref="ModelData"/>: interleaved vertex streams in the standard layout,
/// per-material submeshes, and the node hierarchy that places them.
/// </summary>
/// <remarks>
/// <para><b>Backend.</b> Import goes through Assimp (Silk.NET.Assimp), which is
/// P/Invoke over a native library — allowed under the AOT constraint for the
/// same reason the Silk.NET graphics backends are, and verified to resolve and
/// import at runtime before this was written. It buys OBJ, glTF/GLB, FBX,
/// COLLADA and a long tail of others for one dependency the build already
/// shipped. Everything Assimp-shaped is confined to this file: nothing
/// downstream of <see cref="ModelData"/> knows the importer exists.</para>
/// <para><b>Threading.</b> Pure CPU and free of engine state, so any thread may
/// call <see cref="Import"/> — the async asset path runs it on the thread pool.
/// The native library is loaded once and shared; Assimp's C import entry point
/// is re-entrant, so concurrent imports of different files are fine. The one
/// piece of process-global state is Assimp's last-error string, which is only
/// read on the failure path and only to decorate an exception whose message
/// already names our file.</para>
/// <para><b>Failure policy.</b> Mirrors the rest of the asset pipeline: content
/// problems that leave a usable model (an unresolvable texture, a non-triangle
/// mesh, a node transform that will not decompose) become
/// <see cref="ModelData.Warnings"/> entries; only "there is no model here"
/// throws.</para>
/// </remarks>
public static class ModelImporter
{
    // Bits 0-3 of aiMesh::mPrimitiveTypes are the primitive kinds; bit 4 is the
    // n-gon encoding hint, which says nothing about what we can draw.
    private const uint PrimitiveKindMask = 0xF;
    private const uint TrianglePrimitiveBit = (uint)PrimitiveType.Triangle;

    // A hierarchy deeper than this is a malformed or hostile file, not content:
    // the node walk is recursive, so cap it rather than risk the stack.
    private const int MaxNodeDepth = 256;

    // Enough warnings to diagnose a bad file, few enough that a pathological one
    // cannot turn into a multi-megabyte log line.
    private const int MaxWarnings = 64;

    // Loaded once for the process. Assimp is IDisposable only to release the
    // native handle, which we want held for as long as models can be loaded.
    private static readonly Lazy<Assimp> Api =
        new(Assimp.GetApi, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Imports the model at <paramref name="absolutePath"/>. Any thread.
    /// </summary>
    /// <param name="absolutePath">Absolute path of the model file.</param>
    /// <param name="contentRootPath">
    /// Absolute content root that texture references are re-expressed against —
    /// see <see cref="ModelMaterial.DiffuseTexturePath"/>.
    /// </param>
    /// <param name="options">Import tuning; null uses <see cref="ModelImportOptions.Default"/>.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is not a model this build can read, or holds no drawable
    /// geometry. The message names the file and carries Assimp's own diagnosis.
    /// </exception>
    public static unsafe ModelData Import(
        string absolutePath,
        string contentRootPath,
        ModelImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);
        ArgumentNullException.ThrowIfNull(contentRootPath);
        options ??= ModelImportOptions.Default;

        if (!SysFile.Exists(absolutePath))
            throw new FileNotFoundException($"Model '{absolutePath}' does not exist.", absolutePath);

        Assimp ai = Api.Value;
        var warnings = new List<string>();

        AiScene* scene = ai.ImportFile(absolutePath, options.BuildPostProcessFlags());
        if (scene is null)
        {
            // Assimp's diagnosis is far more useful than "import failed", and it
            // is the only place the real reason (unknown extension, truncated
            // buffer, unsupported feature) exists.
            throw new InvalidDataException(
                $"Could not import model '{absolutePath}': {ai.GetErrorStringS()}");
        }

        try
        {
            if (scene->MRootNode is null)
                throw new InvalidDataException($"Model '{absolutePath}' has no scene hierarchy.");

            if ((scene->MFlags & Assimp.SceneFlagsIncomplete) != 0)
                Warn(warnings, "the importer reported the scene as incomplete");

            (ModelMesh[] meshes, int[] remap) = ConvertMeshes(scene, warnings);
            if (meshes.Length == 0)
            {
                throw new InvalidDataException(
                    $"Model '{absolutePath}' contains no triangle geometry.");
            }

            ModelMaterial[] materials = ConvertMaterials(
                ai, scene, Path.GetDirectoryName(absolutePath) ?? string.Empty,
                Path.GetFullPath(contentRootPath), warnings);

            ModelNode root = ConvertNode(scene->MRootNode, remap, warnings, depth: 0);
            Aabb bounds = ComputeBounds(root, meshes);

            return new ModelData(
                absolutePath, meshes, materials, root, bounds, warnings.ToArray());
        }
        finally
        {
            // The native scene is owned by Assimp and must go back even when the
            // conversion above threw partway through.
            ai.ReleaseImport(scene);
        }
    }

    // ---- meshes ----------------------------------------------------------

    // Converts every triangle mesh and returns a source-index -> converted-index
    // remap so the node walk can drop references to meshes we skipped without
    // silently pointing at the wrong geometry.
    private static unsafe (ModelMesh[] Meshes, int[] Remap) ConvertMeshes(
        AiScene* scene, List<string> warnings)
    {
        uint sourceCount = scene->MNumMeshes;
        var remap = new int[sourceCount];
        var meshes = new List<ModelMesh>((int)sourceCount);

        for (uint i = 0; i < sourceCount; i++)
        {
            remap[i] = -1;
            AiMesh* mesh = scene->MMeshes[i];
            if (mesh is null) continue;

            // SortByPrimitiveType has already split mixed meshes, so a mesh that
            // is not purely triangles here is a point/line soup we cannot draw.
            if ((mesh->MPrimitiveTypes & PrimitiveKindMask) != TrianglePrimitiveBit)
            {
                Warn(warnings,
                    $"mesh '{NameOf(mesh->MName)}' is not triangle geometry and was skipped");
                continue;
            }

            if (mesh->MNumVertices == 0 || mesh->MNumFaces == 0 || mesh->MVertices is null)
                continue;

            remap[i] = meshes.Count;
            meshes.Add(ConvertMesh(mesh, warnings));
        }

        return (meshes.ToArray(), remap);
    }

    private static unsafe ModelMesh ConvertMesh(AiMesh* mesh, List<string> warnings)
    {
        int vertexCount = (int)mesh->MNumVertices;
        bool hasNormals = mesh->MNormals is not null;
        // Channel 0 only: the standard layout has exactly one uv, and a model
        // with lightmap uvs in channel 1 still wants its albedo uvs here.
        Vector3* uvs = mesh->MTextureCoords[0];
        bool hasUvs = uvs is not null;

        var vertices = new float[vertexCount * ModelVertexLayout.FloatsPerVertex];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int v = 0; v < vertexCount; v++)
        {
            int b = v * ModelVertexLayout.FloatsPerVertex;
            Vector3 position = mesh->MVertices[v];
            vertices[b + 0] = position.X;
            vertices[b + 1] = position.Y;
            vertices[b + 2] = position.Z;

            // GenerateSmoothNormals fills these for any triangle mesh that had
            // none, so the fallback is only reached if that step was disabled.
            Vector3 normal = hasNormals ? mesh->MNormals[v] : Vector3.UnitY;
            vertices[b + 3] = normal.X;
            vertices[b + 4] = normal.Y;
            vertices[b + 5] = normal.Z;

            // Missing uvs become (0,0) rather than being left out: the layout is
            // fixed, and one texel is a defined result where uninitialised
            // floats are not.
            if (hasUvs)
            {
                Vector3 uv = uvs[v];
                vertices[b + 6] = uv.X;
                vertices[b + 7] = uv.Y;
            }

            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        uint faceCount = mesh->MNumFaces;
        var indices = new List<uint>((int)faceCount * 3);
        for (uint f = 0; f < faceCount; f++)
        {
            Face face = mesh->MFaces[f];
            // Triangulate guarantees this; a face that still is not a triangle
            // would index past the vertex array, so drop it rather than trust it.
            if (face.MNumIndices != 3 || face.MIndices is null) continue;
            indices.Add(face.MIndices[0]);
            indices.Add(face.MIndices[1]);
            indices.Add(face.MIndices[2]);
        }

        string name = NameOf(mesh->MName);
        if (!hasUvs)
            Warn(warnings, $"mesh '{name}' has no texture coordinates; uvs are zeroed");

        return new ModelMesh(
            name,
            (int)mesh->MMaterialIndex,
            vertices,
            indices.ToArray(),
            new Aabb(min, max),
            hasNormals,
            hasUvs);
    }

    // ---- materials -------------------------------------------------------

    private static unsafe ModelMaterial[] ConvertMaterials(
        Assimp ai, AiScene* scene, string modelDirectory, string contentRoot, List<string> warnings)
    {
        var materials = new ModelMaterial[scene->MNumMaterials];
        for (uint i = 0; i < scene->MNumMaterials; i++)
        {
            AiMaterial* material = scene->MMaterials[i];
            string name = ReadMaterialName(ai, material, i);
            string? texture = ReadDiffuseTexture(
                ai, material, name, modelDirectory, contentRoot, warnings);
            materials[i] = new ModelMaterial(name, texture, ReadBaseColor(ai, material));
        }
        return materials;
    }

    private static unsafe string ReadMaterialName(Assimp ai, AiMaterial* material, uint index)
    {
        AssimpString name = default;
        if (ai.GetMaterialString(material, Assimp.MaterialNameBase, 0, 0, &name) == Return.Success &&
            name.Length > 0)
        {
            return name.AsString;
        }

        // glTF and friends routinely leave materials unnamed; a stable synthetic
        // name keeps logs and .spectramat overrides addressable.
        return $"material{index}";
    }

    private static unsafe Vector3 ReadBaseColor(Assimp ai, AiMaterial* material)
    {
        Span<float> rgba = stackalloc float[4];
        rgba.Clear();
        uint count = 4;
        fixed (float* p = rgba)
        {
            if (ai.GetMaterialFloatArray(
                    material, Assimp.MaterialColorDiffuseBase, 0, 0, p, &count) != Return.Success ||
                count < 3)
            {
                // White, so a material with no colour of its own does not tint
                // its texture darker than the artist drew it.
                return Vector3.One;
            }
        }
        return new Vector3(rgba[0], rgba[1], rgba[2]);
    }

    // Diffuse first, base colour second: classic formats write the former, glTF
    // the latter (and Assimp's glTF reader publishes both, so the order only
    // matters for readers that publish one).
    private static unsafe string? ReadDiffuseTexture(
        Assimp ai,
        AiMaterial* material,
        string materialName,
        string modelDirectory,
        string contentRoot,
        List<string> warnings)
    {
        if (!TryReadTexturePath(ai, material, TextureType.Diffuse, out string raw) &&
            !TryReadTexturePath(ai, material, TextureType.BaseColor, out raw))
        {
            return null;
        }

        return ResolveTexturePath(raw, materialName, modelDirectory, contentRoot, warnings);
    }

    private static unsafe bool TryReadTexturePath(
        Assimp ai, AiMaterial* material, TextureType type, out string path)
    {
        path = string.Empty;
        if (ai.GetMaterialTextureCount(material, type) == 0) return false;

        AssimpString value = default;
        if (ai.GetMaterialString(
                material, Assimp.MaterialTextureBase, (uint)type, 0, &value) != Return.Success)
        {
            return false;
        }

        path = value.AsString;
        return path.Length > 0;
    }

    /// <summary>
    /// Turns whatever the model file said about a texture into a content-root
    /// relative path, or null if it cannot be honoured.
    /// </summary>
    /// <remarks>
    /// Two lookups, in order: the path as written (relative to the model file,
    /// which is what every exporter means, and absolute paths resolve too), then
    /// the file's bare name under <c>Textures/</c>. The second is not sloppiness
    /// — exported models routinely carry the authoring machine's absolute paths,
    /// and re-pointing them at the engine's own texture folder is exactly what a
    /// human would do by hand.
    /// </remarks>
    private static string? ResolveTexturePath(
        string raw, string materialName, string modelDirectory, string contentRoot, List<string> warnings)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.StartsWith(Assimp.EmbeddedTexnamePrefix, StringComparison.Ordinal))
        {
            Warn(warnings,
                $"material '{materialName}' uses an embedded texture ('{trimmed}'), which is not supported");
            return null;
        }

        if (TryMakeContentRelative(
                CombineSafe(modelDirectory, trimmed), contentRoot, out string? asWritten))
        {
            return asWritten;
        }

        string fileName = Path.GetFileName(trimmed.Replace('\\', '/'));
        if (fileName.Length > 0 &&
            TryMakeContentRelative(
                Path.Combine(contentRoot, "Textures", fileName), contentRoot, out string? relocated))
        {
            Warn(warnings,
                $"material '{materialName}': texture '{trimmed}' was not found where the model points; " +
                $"using '{relocated}'");
            return relocated;
        }

        Warn(warnings,
            $"material '{materialName}': texture '{trimmed}' is not under the content root; " +
            "the material will fall back to the engine default");
        return null;
    }

    // Path.Combine already returns `relative` unchanged when it is rooted, which
    // is the behaviour we want for exporter-written absolute paths.
    private static string CombineSafe(string directory, string relative)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(directory, relative));
        }
        catch (ArgumentException)
        {
            // Invalid characters in the path the file claimed; nothing to resolve.
            return string.Empty;
        }
    }

    private static bool TryMakeContentRelative(
        string absolutePath, string contentRoot, out string? relativePath)
    {
        relativePath = null;
        if (absolutePath.Length == 0 || !SysFile.Exists(absolutePath)) return false;

        string relative = Path.GetRelativePath(contentRoot, absolutePath);
        // GetRelativePath returns the input unchanged for a different volume and
        // a "..\" walk for anything above the root; both mean "outside".
        if (Path.IsPathRooted(relative) ||
            relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        relativePath = ContentRoot.NormalizeRelativePath(relative);
        return true;
    }

    // ---- hierarchy -------------------------------------------------------

    private static unsafe ModelNode ConvertNode(
        AiNode* node, int[] meshRemap, List<string> warnings, int depth)
    {
        string name = NameOf(node->MName);

        // Assimp stores column-vector matrices (translation in the last COLUMN);
        // System.Numerics and the whole engine use row-vector ones. Transposing
        // is the entire conversion — get this wrong and every child node lands
        // somewhere plausible but wrong.
        Matrix4x4 local = Matrix4x4.Transpose(node->MTransformation);

        bool exact = Matrix4x4.Decompose(
            local, out Vector3 scale, out Quaternion rotation, out Vector3 position);
        if (!exact)
        {
            Warn(warnings,
                $"node '{name}' has a transform that is not a position/rotation/scale " +
                "(sheared or mirrored); instantiation will place it at the identity");
            scale = Vector3.One;
            rotation = Quaternion.Identity;
            position = Vector3.Zero;
        }

        int[] meshIndices = [];
        if (node->MNumMeshes > 0)
        {
            var kept = new List<int>((int)node->MNumMeshes);
            for (uint i = 0; i < node->MNumMeshes; i++)
            {
                uint source = node->MMeshes[i];
                if (source < meshRemap.Length && meshRemap[source] >= 0)
                    kept.Add(meshRemap[source]);
            }
            meshIndices = kept.ToArray();
        }

        ModelNode[] children = [];
        if (node->MNumChildren > 0)
        {
            if (depth >= MaxNodeDepth)
            {
                Warn(warnings,
                    $"node '{name}' is deeper than {MaxNodeDepth} levels; its children were dropped");
            }
            else
            {
                children = new ModelNode[node->MNumChildren];
                for (uint i = 0; i < node->MNumChildren; i++)
                    children[i] = ConvertNode(node->MChildren[i], meshRemap, warnings, depth + 1);
            }
        }

        return new ModelNode(name, local, position, rotation, scale, exact, meshIndices, children);
    }

    // Model-space bounds: each submesh contributes its own box transformed by
    // the accumulated transform of the node that draws it, so a part that a node
    // pushes 26 units up is enclosed 26 units up.
    private static Aabb ComputeBounds(ModelNode root, ModelMesh[] meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;

        var stack = new Stack<(ModelNode Node, Matrix4x4 World)>();
        stack.Push((root, root.LocalMatrix));
        while (stack.Count > 0)
        {
            (ModelNode node, Matrix4x4 world) = stack.Pop();

            IReadOnlyList<int> indices = node.MeshIndices;
            for (int i = 0; i < indices.Count; i++)
            {
                Aabb box = meshes[indices[i]].LocalBounds.Transform(world);
                min = Vector3.Min(min, box.Min);
                max = Vector3.Max(max, box.Max);
                any = true;
            }

            IReadOnlyList<ModelNode> children = node.Children;
            for (int i = 0; i < children.Count; i++)
                stack.Push((children[i], children[i].LocalMatrix * world));
        }

        return any ? new Aabb(min, max) : new Aabb(Vector3.Zero, Vector3.Zero);
    }

    // ---- small helpers ---------------------------------------------------

    private static string NameOf(AssimpString value) =>
        value.Length > 0 ? value.AsString : string.Empty;

    private static void Warn(List<string> warnings, string message)
    {
        // One pathological file must not be able to turn a warning list into the
        // biggest allocation in the process.
        if (warnings.Count < MaxWarnings)
            warnings.Add(message);
        else if (warnings.Count == MaxWarnings)
            warnings.Add($"further warnings suppressed after {MaxWarnings}");
    }
}
