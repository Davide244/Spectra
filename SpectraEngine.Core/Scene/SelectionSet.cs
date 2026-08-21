using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// The editor-facing selection service for one <see cref="Scene"/>
/// (<see cref="Scene.Selection"/>): an ordered set of that scene's nodes that
/// editor tools operate on — gizmos, property panels, delete/duplicate
/// commands. Only nodes attached to the owning scene can be selected; nodes
/// that leave the scene are deselected automatically.
/// </summary>
/// <remarks>
/// <b>Threading:</b> render thread only, like the <see cref="Scene"/> it
/// belongs to — selection changes are scene edits. <b>Re-entrancy:</b>
/// <see cref="SelectionChanged"/> handlers must not mutate the selection or
/// the scene graph from inside the event; observe and defer, the same rule as
/// the scene's own change events.
/// </remarks>
public sealed class SelectionSet
{
    private readonly Scene _scene;

    // The list is the source of iteration order (stable insertion order, so
    // editor UI doesn't reshuffle as the selection grows); the hash set makes
    // membership tests O(1) so Add/Toggle/Contains stay cheap on large
    // selections. Both always hold exactly the same nodes.
    private readonly List<SceneNode> _items = [];
    private readonly HashSet<SceneNode> _membership = [];

    internal SelectionSet(Scene scene)
    {
        _scene = scene;
        // Auto-deselect nodes leaving the scene: NodeRemoved fires once per
        // node in a removed subtree, so every selected descendant is dropped
        // individually (one SelectionChanged per node that was selected).
        scene.NodeRemoved += OnNodeRemoved;
    }

    /// <summary>
    /// The selected nodes in stable insertion order — the order they were
    /// selected in. Deselecting a node preserves the relative order of the
    /// rest. Exposed as a list so per-frame consumers (e.g. the selection
    /// highlight) can iterate by index without allocating an enumerator.
    /// </summary>
    public IReadOnlyList<SceneNode> Items => _items;

    /// <summary>Number of selected nodes.</summary>
    public int Count => _items.Count;

    /// <summary>
    /// Raised once per mutating operation that actually changed the selection:
    /// a <see cref="Select"/> replacing five nodes with one raises it once,
    /// and no-op operations (re-selecting the sole selected node, adding a
    /// duplicate, deselecting an unselected node, clearing an empty set) raise
    /// nothing.
    /// </summary>
    public event Action? SelectionChanged;

    /// <summary>True when the node is currently selected.</summary>
    public bool Contains(SceneNode node) => _membership.Contains(node);

    /// <summary>
    /// Single-select: replaces the whole selection with <paramref name="node"/>.
    /// Throws <see cref="ArgumentException"/> when the node does not belong to
    /// this scene.
    /// </summary>
    public void Select(SceneNode node)
    {
        RequireOwnNode(node);
        if (_items.Count == 1 && ReferenceEquals(_items[0], node))
            return; // Already exactly this selection — nothing changed.

        _items.Clear();
        _membership.Clear();
        _items.Add(node);
        _membership.Add(node);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Adds the node to the selection (multi-select); a no-op when it is
    /// already selected. Throws <see cref="ArgumentException"/> when the node
    /// does not belong to this scene.
    /// </summary>
    public void Add(SceneNode node)
    {
        RequireOwnNode(node);
        if (!_membership.Add(node))
            return;

        _items.Add(node);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Adds the node when unselected, removes it when selected. Always changes
    /// the selection, so it always raises <see cref="SelectionChanged"/>.
    /// Throws <see cref="ArgumentException"/> when the node does not belong to
    /// this scene.
    /// </summary>
    public void Toggle(SceneNode node)
    {
        RequireOwnNode(node);
        if (_membership.Add(node))
        {
            _items.Add(node);
        }
        else
        {
            _membership.Remove(node);
            _items.Remove(node);
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Removes the node from the selection; a no-op when it is not selected.
    /// </summary>
    public void Deselect(SceneNode node)
    {
        // Deliberately no ownership check: a foreign node simply is not in the
        // set, and the auto-deselect path arrives here AFTER the node's Owner
        // has already been cleared or repointed by the removal — requiring
        // ownership would reject exactly the nodes that must be dropped.
        if (!_membership.Remove(node))
            return;

        _items.Remove(node);
        SelectionChanged?.Invoke();
    }

    /// <summary>Empties the selection; a no-op when already empty.</summary>
    public void Clear()
    {
        if (_items.Count == 0)
            return;

        _items.Clear();
        _membership.Clear();
        SelectionChanged?.Invoke();
    }

    private void OnNodeRemoved(SceneNode node) => Deselect(node);

    // Every operation that can put a node INTO the set enforces that the node
    // is attached to this selection's scene — selection is a view over the
    // scene's graph, and a detached or foreign node has no meaning in it.
    private void RequireOwnNode(SceneNode node)
    {
        if (!ReferenceEquals(node.Owner, _scene))
        {
            throw new ArgumentException(
                node.Owner is null
                    ? $"Node '{node.Name}' is not attached to any scene and cannot be selected."
                    : $"Node '{node.Name}' belongs to scene '{node.Owner.Name}', not to '{_scene.Name}'.",
                nameof(node));
        }
    }
}
