using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// A node in the scene graph: a named element with a local <see cref="Transform"/>,
/// an optional renderable, and a parent/child hierarchy. World transforms are
/// derived by composing local transforms down the tree and are cached until the
/// node (or an ancestor) changes.
/// </summary>
public class SceneNode
{
    private readonly List<SceneNode> _children = [];
    private Transform _localTransform = Transform.Identity;
    private Matrix4x4 _worldMatrix = Matrix4x4.Identity;
    private bool _worldDirty = true;

    public SceneNode(string name = "Node")
    {
        Name = name;
    }

    public string Name { get; set; }

    public SceneNode? Parent { get; private set; }

    public IReadOnlyList<SceneNode> Children => _children;

    /// <summary>Renderable geometry attached to this node, if any.</summary>
    public MeshRenderer? MeshRenderer { get; set; }

    /// <summary>The node's transform relative to its parent.</summary>
    public Transform LocalTransform
    {
        get => _localTransform;
        set { _localTransform = value; MarkWorldDirty(); }
    }

    public Vector3 LocalPosition
    {
        get => _localTransform.Position;
        set { _localTransform.Position = value; MarkWorldDirty(); }
    }

    public Quaternion LocalRotation
    {
        get => _localTransform.Rotation;
        set { _localTransform.Rotation = value; MarkWorldDirty(); }
    }

    public Vector3 LocalScale
    {
        get => _localTransform.Scale;
        set { _localTransform.Scale = value; MarkWorldDirty(); }
    }

    /// <summary>The node's accumulated world matrix (local composed with all ancestors).</summary>
    public Matrix4x4 WorldMatrix
    {
        get
        {
            if (_worldDirty)
            {
                var local = _localTransform.Model;
                _worldMatrix = Parent is null ? local : local * Parent.WorldMatrix;
                _worldDirty = false;
            }
            return _worldMatrix;
        }
    }

    public Vector3 WorldPosition => WorldMatrix.Translation;

    /// <summary>Attaches an existing node as a child, detaching it from any previous parent.</summary>
    public SceneNode AddChild(SceneNode child)
    {
        child.Parent?._children.Remove(child);
        child.Parent = this;
        _children.Add(child);
        child.MarkWorldDirty();
        return child;
    }

    /// <summary>Creates a new child node and attaches it.</summary>
    public SceneNode CreateChild(string name = "Node")
    {
        var node = new SceneNode(name);
        return AddChild(node);
    }

    public void RemoveChild(SceneNode child)
    {
        if (_children.Remove(child))
        {
            child.Parent = null;
            child.MarkWorldDirty();
        }
    }

    /// <summary>Enumerates this node and all of its descendants, depth-first.</summary>
    public IEnumerable<SceneNode> Traverse()
    {
        yield return this;
        foreach (var child in _children)
            foreach (var descendant in child.Traverse())
                yield return descendant;
    }

    // Eagerly propagates the dirty flag. A child's cached world matrix depends on
    // its parent's, so any change must invalidate the whole subtree. Cheap for the
    // shallow trees we have today; revisit if hierarchies grow deep.
    private void MarkWorldDirty()
    {
        _worldDirty = true;
        for (int i = 0; i < _children.Count; i++)
            _children[i].MarkWorldDirty();
    }
}
