using SpectraEngine.Core.Assets;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// Turns a loaded <see cref="ModelAsset"/> into a <see cref="SceneNode"/>
/// subtree: one node per imported <see cref="ModelNode"/>, carrying that node's
/// local transform, with a <see cref="MeshRenderer"/> per submesh.
/// </summary>
/// <remarks>
/// <para><b>Instances share the asset.</b> The nodes reference the model's GPU
/// meshes and materials directly — instantiating the same model fifty times
/// creates fifty nodes and zero GPU resources. Which also means the subtree must
/// be removed from the scene before its model is unloaded; the asset manager
/// owns those meshes and will destroy them on
/// <see cref="AssetManager.UnloadModel"/>.</para>
/// <para><b>Build detached, attach once.</b> The subtree is assembled while it
/// belongs to no scene and attached in a single
/// <see cref="SceneNode.AddChild"/>. That is what makes it interact correctly
/// with the scene's events and spatial index: the one attach walks the finished
/// subtree in pre-order, raising <see cref="Scene.NodeAdded"/> for every node
/// and inserting each mesh-bearing one into the BVH with its world matrix
/// already composed. Assembling in place instead would raise an event per node
/// as it appeared and churn the index with half-built bounds.</para>
/// <para>Render thread only, like everything else that touches the scene graph.</para>
/// </remarks>
public static class ModelInstantiator
{
    /// <summary>
    /// Builds the subtree and attaches it under <paramref name="parent"/>,
    /// returning its root. The usual entry point — the attach is what registers
    /// the nodes with the parent's scene.
    /// </summary>
    /// <param name="parent">Node to attach under; may be any node in any scene.</param>
    /// <param name="model">A loaded model. Must be <see cref="ModelAsset.IsReady"/>.</param>
    /// <param name="name">
    /// Name for the root node; null uses the model's own root name, falling back
    /// to the file name.
    /// </param>
    /// <exception cref="InvalidOperationException">The model is not loaded yet.</exception>
    public static SceneNode InstantiateInto(SceneNode parent, ModelAsset model, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return parent.AddChild(Instantiate(model, name));
    }

    /// <summary>
    /// Builds the subtree without attaching it, returning its detached root. Use
    /// this to position or edit the instance before it becomes visible to the
    /// scene; attaching it later raises the membership events for the whole
    /// subtree at once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The model is not loaded yet.</exception>
    public static SceneNode Instantiate(ModelAsset model, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        ModelData data = model.Data
            ?? throw new InvalidOperationException(
                $"Model '{model.RelativePath}' is not loaded yet" +
                (model.Error is { } error ? $" ({error})" : "; pump the asset manager until it is ready") +
                ".");

        if (model.Meshes.Count != data.Meshes.Count)
        {
            throw new InvalidOperationException(
                $"Model '{model.RelativePath}' has {data.Meshes.Count} submesh(es) but " +
                $"{model.Meshes.Count} GPU mesh(es); the asset is in an inconsistent state.");
        }

        string rootName = FirstNonEmpty(name, data.Root.Name, model.RelativePath);
        // Depth is bounded by the importer (see ModelImporter.MaxNodeDepth), so
        // the recursion below cannot run away on hostile content.
        return BuildNode(data.Root, model, data, rootName);
    }

    // One scene node per imported node, then the submeshes.
    private static SceneNode BuildNode(
        ModelNode source, ModelAsset model, ModelData data, string name)
    {
        var node = new SceneNode(name)
        {
            LocalTransform = new Transform
            {
                Position = source.Position,
                Rotation = source.Rotation,
                Scale = source.Scale,
            },
        };

        AttachMeshes(node, source, model, data);

        IReadOnlyList<ModelNode> children = source.Children;
        for (int i = 0; i < children.Count; i++)
        {
            ModelNode child = children[i];
            node.AddChild(BuildNode(child, model, data, FirstNonEmpty(null, child.Name, "Node")));
        }

        return node;
    }

    // A scene node holds one renderable, so a model node drawing several
    // submeshes (one per material) becomes a node per submesh. Attaching the
    // single-submesh case directly keeps the common prop a one-node instance
    // instead of gratuitously nesting it.
    private static void AttachMeshes(
        SceneNode node, ModelNode source, ModelAsset model, ModelData data)
    {
        IReadOnlyList<int> meshIndices = source.MeshIndices;
        if (meshIndices.Count == 0)
            return;

        if (meshIndices.Count == 1)
        {
            node.MeshRenderer = CreateRenderer(model, data, meshIndices[0]);
            return;
        }

        for (int i = 0; i < meshIndices.Count; i++)
        {
            int meshIndex = meshIndices[i];
            ModelMesh mesh = data.Meshes[meshIndex];
            SceneNode part = node.CreateChild(FirstNonEmpty(null, mesh.Name, $"Submesh{i}"));
            part.MeshRenderer = CreateRenderer(model, data, meshIndex);
        }
    }

    private static MeshRenderer CreateRenderer(ModelAsset model, ModelData data, int meshIndex)
    {
        ModelMesh source = data.Meshes[meshIndex];
        return new MeshRenderer(model.Meshes[meshIndex], model.MaterialFor(in source));
    }

    private static string FirstNonEmpty(string? preferred, string fallback, string lastResort)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback;
        return lastResort;
    }
}
