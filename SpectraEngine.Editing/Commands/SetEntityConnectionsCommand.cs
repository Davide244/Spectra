using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Replaces the whole list of wires leaving a node's entity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Whole ARRAYS on both sides, and one command for add, remove and edit.</b>
/// Every command in this assembly captures absolute before/after values,
/// because that is what makes an undo an inverse rather than a re-derivation -
/// and a connection list has no per-item identity to address a delta by. Two
/// wires on one entity may be identical in every field, an insert shifts every
/// index after it, and a "remove the wire at 2" replayed against a list an undo
/// has already changed removes the wrong one. An absolute list is idempotent
/// under replay and costs a handful of strings per edit, which is nothing
/// beside a level.
/// </para>
/// <para>
/// <b>ORDER is authored data and is written back exactly as given.</b> The list
/// round-trips through <c>map.json</c>, so a command that sorted or
/// de-duplicated on the way in would rewrite a region of a person's file that
/// nobody touched, and an undo would not restore the bytes that were there.
/// </para>
/// <para>
/// <b>Coalesced on the node id alone</b>, unlike
/// <see cref="SetEntityKeyvalueCommand"/>, which keys on the (node, key) pair.
/// There is no key here: every edit to this node's wiring carries the whole
/// list, so a later one already contains everything an earlier one did and
/// absorbing it is exactly right. A run of edits inside one transaction is
/// therefore one history entry whose before-state is where the run started.
/// </para>
/// <para>
/// <b>Render thread only</b>, like every other scene mutation.
/// </para>
/// </remarks>
public sealed class SetEntityConnectionsCommand : ICoalescingCommand
{
    private WeakReference<SceneNode>? _lastApplied;

    /// <summary>Creates a command from explicit before/after lists.</summary>
    /// <param name="nodeId">The node to edit.</param>
    /// <param name="before">The wires the node carried, in authored order.</param>
    /// <param name="after">The wires it should carry, in authored order.</param>
    public SetEntityConnectionsCommand(
        Guid nodeId, IReadOnlyList<EntityConnection> before, IReadOnlyList<EntityConnection> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        NodeId = nodeId;
        Before = Copy(before);
        After = Copy(after);
    }

    /// <summary>
    /// Captures the node's current wiring as the before-state. Call this
    /// <em>before</em> applying the edit.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node carries no entity.</exception>
    public static SetEntityConnectionsCommand Capture(
        SceneNode node, IReadOnlyList<EntityConnection> after)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(after);

        // A node with no entity payload has no wiring at all, and a command
        // built for one would record an empty before-state that an undo would
        // then honour by clearing a list nothing ever stored. The same guard
        // SetEntityKeyvalueCommand.Capture makes, for the same reason: it is a
        // caller error, not a missing target.
        if (node.Entity is not { } entity)
        {
            throw new InvalidOperationException(
                $"Node '{node.Name}' carries no entity to wire.");
        }

        return new SetEntityConnectionsCommand(node.Id, entity.Connections, after);
    }

    /// <summary>The id of the node this command edits.</summary>
    public Guid NodeId { get; }

    /// <summary>The wires the node carried before the edit.</summary>
    public IReadOnlyList<EntityConnection> Before { get; }

    /// <summary>The wires it carries after it.</summary>
    /// <remarks>
    /// Private setter, retargeted through <see cref="SetAfter"/> and
    /// <see cref="TryAbsorb"/> while the command is still inside an open
    /// transaction - the same drag path every other gesture command here has.
    /// </remarks>
    public IReadOnlyList<EntityConnection> After { get; private set; }

    /// <summary>Retargets the after-state, keeping the captured before-state.</summary>
    public void SetAfter(IReadOnlyList<EntityConnection> after)
    {
        ArgumentNullException.ThrowIfNull(after);
        After = Copy(after);
    }

    /// <inheritdoc/>
    public bool TryAbsorb(IEditorCommand newer)
    {
        if (newer is not SetEntityConnectionsCommand next || next.NodeId != NodeId)
            return false;

        After = next.After;
        return true;
    }

    /// <inheritdoc/>
    public string Name { get; init; } = "Entity Wiring";

    /// <inheritdoc/>
    public void Do(Scene scene) => Apply(scene, After);

    /// <inheritdoc/>
    public void Undo(Scene scene) => Apply(scene, Before);

    /// <inheritdoc/>
    public void RollBack(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (scene.TryFindById(NodeId, out SceneNode? node) && node.Entity is { } live)
        {
            Write(live, Before);
            return;
        }

        // A node that left the scene mid-gesture still has to be restored, or
        // it is stranded holding a half-built list with nothing left that could
        // put it back: a cancel discards its commands afterwards and gets no
        // second chance, unlike an undo behind a still-undone delete.
        if (_lastApplied is not null
            && _lastApplied.TryGetTarget(out SceneNode? detached)
            && detached.Entity is { } orphan)
        {
            Write(orphan, Before);
        }
    }

    private void Apply(Scene scene, IReadOnlyList<EntityConnection> value)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // Missing target = no-op, per the IEditorCommand contract. A node whose
        // entity payload was removed since is the same case: there is nothing
        // to write and nothing to report.
        if (!scene.TryFindById(NodeId, out SceneNode? node) || node.Entity is not { } entity)
            return;

        _lastApplied ??= new WeakReference<SceneNode>(node);
        Write(entity, value);
    }

    // Cleared and refilled rather than replaced, because EntityData owns the
    // list and hands it out: a fresh instance assigned here would leave anything
    // already holding a reference reading the wiring this edit replaced.
    private static void Write(EntityData entity, IReadOnlyList<EntityConnection> value)
    {
        entity.Connections.Clear();
        for (int i = 0; i < value.Count; i++)
            entity.Connections.Add(value[i]);
    }

    // A snapshot of the caller's list, in order. Without it a command built
    // from EntityData.Connections would hold the live list, so its own before
    // state would be rewritten by the edit it is recording - an undo that
    // restores what it just undid to.
    private static EntityConnection[] Copy(IReadOnlyList<EntityConnection> from)
    {
        if (from.Count == 0)
            return [];

        var copy = new EntityConnection[from.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = from[i];

        return copy;
    }

    /// <summary>
    /// Whether two wire lists are the same wires in the same order.
    /// </summary>
    /// <remarks>
    /// <b>Exact, and order-sensitive.</b> <see cref="EntityConnection"/> is a
    /// record struct, so its equality is field-by-field with ordinal string
    /// comparison - which is what the map format writes and reads. A commit
    /// that produces the list the node already has records nothing, exactly as
    /// a rename does for an unchanged name.
    /// </remarks>
    public static bool SameWiring(
        IReadOnlyList<EntityConnection> a, IReadOnlyList<EntityConnection> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Count != b.Count)
            return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }
}
