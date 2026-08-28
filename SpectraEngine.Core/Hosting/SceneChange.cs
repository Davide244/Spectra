using SpectraEngine.Core.Scene;
using System;

namespace SpectraEngine.Core.Hosting;

/// <summary>What happened to one node between two frames.</summary>
public enum SceneChangeKind
{
    /// <summary>The node entered the graph.</summary>
    Added,

    /// <summary>The node left the graph.</summary>
    Removed,

    /// <summary>
    /// The node moved to a different parent, or to a different position under
    /// the same one, without leaving the graph.
    /// </summary>
    Reparented,
}

/// <summary>
/// One structural change to the scene graph, as a value a UI thread can hold
/// safely: ids and a name, never a <c>SceneNode</c>.
/// </summary>
/// <remarks>
/// <b>No node reference, deliberately.</b> A <c>SceneNode</c> belongs to the
/// render thread, which will keep mutating it the instant the frame ends;
/// handing one to a UI thread makes every property read a race, and the fields
/// a tree view actually wants (id, name, parent) are exactly the ones cheap to
/// copy. A shell that needs more asks for it through
/// <see cref="EngineHost.EnqueueCommand"/> and gets the answer on the thread
/// that owns it.
/// <para>
/// <b>The list is a log, not a diff.</b> Adds and removes are reported in the
/// order they happened, so a view that replays them in order matches the graph;
/// collapsing them would lose the ordering that makes a re-add distinguishable
/// from a move.
/// </para>
/// </remarks>
/// <param name="Kind">What happened.</param>
/// <param name="NodeId">The node it happened to.</param>
/// <param name="ParentId">
/// The node's parent after the change, or <see cref="Guid.Empty"/> for a node
/// that has none (a removed node, or a scene root).
/// </param>
/// <param name="Name">The node's name at the moment of the change.</param>
/// <param name="SiblingIndex">
/// The node's position among its parent's children after the change, or −1 when
/// it has no parent. A tree view needs this to insert in the right place, and
/// the engine needs it because sibling index is traversal order.
/// </param>
/// <param name="NodeKind">
/// What the node is, derived from its payloads at the moment of the change.
/// A list of names cannot tell a light from a wall, and a UI holding ids across
/// a thread boundary has no way to ask.
/// </param>
public readonly record struct SceneChange(
    SceneChangeKind Kind,
    Guid NodeId,
    Guid ParentId,
    string Name,
    int SiblingIndex,
    SceneNodeKind NodeKind = SceneNodeKind.Empty);
