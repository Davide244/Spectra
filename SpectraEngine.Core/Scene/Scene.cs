using System.Collections.Generic;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// The dynamic, hierarchical half of the engine's spatial model: a tree of
/// <see cref="SceneNode"/> instances rooted at <see cref="Root"/>, plus the
/// <see cref="Camera"/> it is viewed through. Static world geometry will instead
/// live in a BSP tree and be culled against this scene's camera.
/// </summary>
public sealed class Scene
{
    public Scene(string name = "Scene")
    {
        Name = name;
    }

    public string Name { get; set; }

    /// <summary>The implicit root node; never has a parent.</summary>
    public SceneNode Root { get; } = new("Root");

    public Camera Camera { get; } = new();

    /// <summary>Enumerates every node in the scene, depth-first from the root.</summary>
    public IEnumerable<SceneNode> Nodes => Root.Traverse();
}
