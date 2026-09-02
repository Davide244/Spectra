using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// Resolves a wire's target name to the entities it means: an exact name, a
/// trailing-<c>*</c> prefix, or one of the runtime forms
/// <c>!self</c> / <c>!activator</c> / <c>!caller</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Duplicate names are legal and firing at a name fires EVERY match.</b> That
/// is not a tolerated accident, it is how a level says "all the lights in this
/// room": the scene tree already allows duplicate names, <c>targetname</c> IS
/// the node's name, and refusing duplicates here would mean the entity system
/// disagreeing with the tree about what a legal scene is.
/// </para>
/// <para>
/// <b>The order matches are delivered in is SCENE TRAVERSAL ORDER</b>, which is
/// the only total order over nodes this engine recognises (it is also the static
/// world's placement-slot order). Buckets are therefore sorted by pre-order
/// position at resolve time rather than kept sorted: a reparent changes the
/// order and raises neither a membership nor a rename event, so a maintained
/// ordering would go stale with nothing to correct it. Comparing two nodes costs
/// O(depth) and a bucket holding more than a handful of entities is a level
/// naming a dozen things the same.
/// </para>
/// <para>
/// <b>Maintained from the scene's own events, and the handlers touch NOTHING but
/// this index.</b> Scene membership events fire in the middle of the ownership
/// walk, where a structural edit corrupts the traversal; anything an entity wants
/// to do about a node arriving or leaving goes through the world's deferred
/// spawn and despawn queues instead.
/// </para>
/// </remarks>
public sealed class TargetNameIndex : IDisposable
{
    /// <summary>The entity whose output is firing.</summary>
    public const string SelfToken = "!self";

    /// <summary>Whoever started the chain.</summary>
    public const string ActivatorToken = "!activator";

    /// <summary>The entity that fired the output being delivered.</summary>
    public const string CallerToken = "!caller";

    // Node id, never node reference. Undo of a delete rebuilds the node as a NEW
    // object carrying the OLD id, so a reference-keyed map would fail to
    // recognise the restored node and the entity would be orphaned silently.
    private readonly Dictionary<Guid, Entity> _byNodeId = [];

    // Name to the entities currently ATTACHED under it. An entity whose node has
    // left the graph keeps its _byNodeId mapping and loses its bucket entry,
    // which is exactly what makes a delete-then-undo round trip work.
    private readonly Dictionary<string, List<Entity>> _byName = new(StringComparer.Ordinal);

    private readonly Scene.Scene _scene;
    private bool _disposed;

    /// <summary>Subscribes to <paramref name="scene"/>'s membership and rename events.</summary>
    public TargetNameIndex(Scene.Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _scene = scene;
        scene.NodeAdded += OnNodeAdded;
        scene.NodeRemoved += OnNodeRemoved;
        scene.NodeRenamed += OnNodeRenamed;
    }

    /// <summary>How many distinct names currently have at least one entity.</summary>
    public int NameCount
    {
        get
        {
            int names = 0;
            foreach (List<Entity> bucket in _byName.Values)
            {
                if (bucket.Count > 0)
                    names++;
            }

            return names;
        }
    }

    /// <summary>How many entities this index knows about, listed or not.</summary>
    public int EntityCount => _byNodeId.Count;

    /// <summary>Unsubscribes from the scene. The index is unusable afterwards.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _scene.NodeAdded -= OnNodeAdded;
        _scene.NodeRemoved -= OnNodeRemoved;
        _scene.NodeRenamed -= OnNodeRenamed;

        // The listing marker lives on the entity, so it has to be cleared here
        // or an instance handed to another index would claim to be listed in it
        // already and never be added.
        foreach (Entity entity in _byNodeId.Values)
            entity.IndexedName = null;

        _byNodeId.Clear();
        _byName.Clear();
    }

    /// <summary>The entity built for the node with this id, if there is one.</summary>
    public bool TryGetByNodeId(Guid nodeId, [MaybeNullWhen(false)] out Entity entity) =>
        _byNodeId.TryGetValue(nodeId, out entity);

    /// <summary>
    /// Appends every entity <paramref name="target"/> names to
    /// <paramref name="results"/>, in scene traversal order.
    /// </summary>
    /// <remarks>
    /// <b><c>!self</c> and <c>!caller</c> are the same entity in a connection</b>,
    /// because the entity a wire leaves IS the entity firing it. They stay
    /// separate tokens because the two questions diverge the moment a target is
    /// resolved from anywhere but a connection, and because a map author writes
    /// whichever one reads correctly.
    /// <para>
    /// Tokens and names are matched ORDINALLY, like every other name in this
    /// engine: a case-folding rule would need a culture to fold in and the same
    /// map would then mean different things on different machines.
    /// </para>
    /// </remarks>
    /// <param name="target">The wire's target name.</param>
    /// <param name="self">The entity the target is being resolved relative to.</param>
    /// <param name="activator">Whoever started the chain, or null.</param>
    /// <param name="caller">Whoever fired the output, or null.</param>
    /// <param name="results">Appended to; never cleared by this method.</param>
    public void Resolve(
        string? target,
        Entity? self,
        Entity? activator,
        Entity? caller,
        List<Entity> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (string.IsNullOrEmpty(target))
            return;

        if (target[0] == '!')
        {
            // A runtime form names at most one entity and needs no ordering.
            Entity? one = target switch
            {
                SelfToken => self,
                ActivatorToken => activator,
                CallerToken => caller,
                _ => null,
            };

            if (one is not null)
                results.Add(one);

            return;
        }

        int start = results.Count;

        if (target[^1] == '*')
        {
            ReadOnlySpan<char> prefix = target.AsSpan(0, target.Length - 1);
            foreach (KeyValuePair<string, List<Entity>> bucket in _byName)
            {
                if (bucket.Key.AsSpan().StartsWith(prefix, StringComparison.Ordinal))
                    results.AddRange(bucket.Value);
            }
        }
        else if (_byName.TryGetValue(target, out List<Entity>? exact))
        {
            results.AddRange(exact);
        }

        SortByTraversalOrder(results, start);
    }

    /// <summary>
    /// Takes ownership of <paramref name="entity"/>: it becomes resolvable by
    /// node id, and by name while its node is in the graph.
    /// </summary>
    internal void Register(Entity entity)
    {
        _byNodeId[entity.Node.Id] = entity;
        Relist(entity);
    }

    /// <summary>Forgets <paramref name="entity"/> entirely.</summary>
    internal void Unregister(Entity entity)
    {
        Unlist(entity);
        if (_byNodeId.TryGetValue(entity.Node.Id, out Entity? indexed) && ReferenceEquals(indexed, entity))
            _byNodeId.Remove(entity.Node.Id);
    }

    // A NODE ARRIVED. Re-check rather than assume, in both directions: this
    // fires for every node entering the graph, almost none of which are
    // entities, and the one that IS may be a node the index already lists (an
    // attach of a subtree that never left) or the same id restored as a fresh
    // object by an undo. A handler that dropped on removal and added blindly
    // here would double-list the first case and, if removal had also dropped the
    // id mapping, lose the second permanently and silently.
    private void OnNodeAdded(SceneNode node)
    {
        if (!_byNodeId.TryGetValue(node.Id, out Entity? entity))
            return;

        // The restored node is a different object carrying the old id. The
        // entity's back-reference must follow it, or every later read of
        // Node.Name answers from a node that is no longer in any scene.
        if (!ReferenceEquals(entity.Node, node))
            entity.RebindNode(node);

        Relist(entity);
    }

    private void OnNodeRemoved(SceneNode node)
    {
        if (!_byNodeId.TryGetValue(node.Id, out Entity? entity))
            return;

        // Identity-checked, mirroring the scene's own de-index: if two live
        // nodes ever share an id, the departing one must not unlist the entity
        // that belongs to the other.
        if (!ReferenceEquals(entity.Node, node))
            return;

        // The name bucket only, never the id mapping: the mapping is what lets
        // an undo of the delete put this entity back.
        Unlist(entity);
    }

    private void OnNodeRenamed(SceneNode node)
    {
        if (!_byNodeId.TryGetValue(node.Id, out Entity? entity))
            return;

        if (!ReferenceEquals(entity.Node, node))
            return;

        // Not currently listed means its node is out of the graph; renaming a
        // detached node must not put it back into name resolution.
        if (entity.IndexedName is null)
            return;

        Relist(entity);
    }

    private void Relist(Entity entity)
    {
        string name = entity.Node.Name;

        if (entity.IndexedName is { } current)
        {
            if (string.Equals(current, name, StringComparison.Ordinal))
                return;

            RemoveFromBucket(current, entity);
        }

        if (!_byName.TryGetValue(name, out List<Entity>? bucket))
        {
            bucket = [];
            _byName[name] = bucket;
        }

        if (!bucket.Contains(entity))
            bucket.Add(entity);

        entity.IndexedName = name;
    }

    private void Unlist(Entity entity)
    {
        if (entity.IndexedName is not { } current)
            return;

        RemoveFromBucket(current, entity);
        entity.IndexedName = null;
    }

    private void RemoveFromBucket(string name, Entity entity)
    {
        if (_byName.TryGetValue(name, out List<Entity>? bucket))
            bucket.Remove(entity);
    }

    // Insertion sort, deliberately: buckets are tiny, the comparison is O(depth)
    // rather than free, and it is STABLE - two nodes with no common ancestor
    // (which an entity whose node left the graph mid-tick can produce) compare
    // equal, and an unstable sort would order them differently from one run to
    // the next.
    private static void SortByTraversalOrder(List<Entity> results, int start)
    {
        for (int i = start + 1; i < results.Count; i++)
        {
            Entity current = results[i];
            int j = i - 1;
            while (j >= start && CompareTraversalOrder(results[j].Node, current.Node) > 0)
            {
                results[j + 1] = results[j];
                j--;
            }

            results[j + 1] = current;
        }
    }

    // Pre-order position, computed rather than stored. Walking to the common
    // ancestor and comparing sibling indices there is exactly what pre-order
    // means, and it stays correct across reparents, which no cached index does.
    private static int CompareTraversalOrder(SceneNode a, SceneNode b)
    {
        if (ReferenceEquals(a, b))
            return 0;

        int depthA = Depth(a);
        int depthB = Depth(b);

        SceneNode x = a;
        SceneNode y = b;
        for (int i = depthA; i > depthB; i--)
            x = x.Parent!;
        for (int i = depthB; i > depthA; i--)
            y = y.Parent!;

        // One is an ancestor of the other, and pre-order visits an ancestor
        // first.
        if (ReferenceEquals(x, y))
            return depthA > depthB ? 1 : -1;

        while (!ReferenceEquals(x.Parent, y.Parent))
        {
            if (x.Parent is null || y.Parent is null)
                return 0;

            x = x.Parent;
            y = y.Parent;
        }

        SceneNode? parent = x.Parent;
        if (parent is null)
            return 0;

        return SiblingIndex(parent, x).CompareTo(SiblingIndex(parent, y));
    }

    private static int Depth(SceneNode node)
    {
        int depth = 0;
        for (SceneNode? walk = node.Parent; walk is not null; walk = walk.Parent)
            depth++;

        return depth;
    }

    private static int SiblingIndex(SceneNode parent, SceneNode child)
    {
        IReadOnlyList<SceneNode> children = parent.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (ReferenceEquals(children[i], child))
                return i;
        }

        return int.MaxValue;
    }
}
