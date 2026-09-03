using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Commands;

/// <summary>
/// Writes one keyvalue on a node's entity payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absolute strings on both sides, which is the whole reason the authoring
/// model is strings.</b> A keyvalue's wire form IS its value, so there is no
/// conversion to get wrong and nothing here can refuse a write: contrast
/// <see cref="SetLightCommand"/>, whose <c>Light</c> setters throw on a range of
/// zero and whose callers therefore have to clamp before they build a command.
/// That asymmetry is deliberate and worth keeping - it is the difference
/// between a payload that validates and a payload that carries text - and it
/// means nothing in this command can throw from <c>Do</c>, halfway through a
/// transaction, leaving the history open and the scene half-edited.
/// </para>
/// <para>
/// <b>Absent is not the empty string, and null is how this says so.</b> A key
/// the author never wrote is a member <c>map.json</c> does not have. An undo
/// that "restored" it as <c>""</c> would leave a member behind that the author
/// never wrote and that no further undo can take back, and the map format's
/// byte-identical save/load/save promise then carries it forever. So the
/// before-state is three-valued in one field: a string is the stored text, null
/// is "this key was not there", and undoing an edit that ADDED a key removes it
/// again.
/// </para>
/// <para>
/// <b>The payload is edited in place rather than replaced.</b>
/// <c>EntityData.SetValue</c> writes an existing entry where it stands, because
/// keyvalue order is the file's order and a remove-then-append would move an
/// edited member to the end of the object and rewrite a region of the file
/// nobody touched.
/// </para>
/// <para>
/// <b>Render thread only</b>, like every other scene mutation.
/// </para>
/// </remarks>
public sealed class SetEntityKeyvalueCommand : ICoalescingCommand
{
    private WeakReference<SceneNode>? _lastApplied;

    /// <summary>Creates a command from explicit before/after values.</summary>
    /// <param name="nodeId">The node to edit.</param>
    /// <param name="key">The keyvalue's wire name.</param>
    /// <param name="before">The stored value, or null when the key was absent.</param>
    /// <param name="after">The value to store, or null to remove the key.</param>
    public SetEntityKeyvalueCommand(Guid nodeId, string key, string? before, string? after)
    {
        // A keyvalue with no name cannot be written to a map or looked up out of
        // one. Refused where the command is built rather than stored under the
        // empty string, which would round-trip as a member nobody can address.
        ArgumentException.ThrowIfNullOrEmpty(key);

        NodeId = nodeId;
        Key = key;
        Before = before;
        After = after;
    }

    /// <summary>
    /// Captures the node's current value for <paramref name="key"/> as the
    /// before-state. Call this <em>before</em> applying the edit.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node carries no entity.</exception>
    public static SetEntityKeyvalueCommand Capture(SceneNode node, string key, string? after)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrEmpty(key);

        // A node with no entity payload has no keyvalues at all, and building a
        // command for one would record a before-state of "absent" that an undo
        // would then honour by removing a key nothing ever stored. Throwing here
        // is the same guard SetLightCommand.Capture makes, and for the same
        // reason: it is a caller error, not a missing target.
        if (node.Entity is not { } entity)
        {
            throw new InvalidOperationException(
                $"Node '{node.Name}' carries no entity to edit.");
        }

        string? before = entity.TryGetValue(key, out string stored) ? stored : null;
        return new SetEntityKeyvalueCommand(node.Id, key, before, after);
    }

    /// <summary>The id of the node this command edits.</summary>
    public Guid NodeId { get; }

    /// <summary>The keyvalue's wire name.</summary>
    public string Key { get; }

    /// <summary>What the key held before the edit, or null when it was absent.</summary>
    public string? Before { get; }

    /// <summary>What the key holds after the edit, or null when it is removed.</summary>
    /// <remarks>
    /// Private setter, retargeted through <see cref="SetAfter"/> and
    /// <see cref="TryAbsorb"/> while the command is still inside an open
    /// transaction - the drag path every other gesture command here has. A
    /// scrubbed number field emits one edit per pointer move, and without this
    /// a sixty-frame drag would be sixty history entries.
    /// </remarks>
    public string? After { get; private set; }

    /// <summary>Retargets the after-state, keeping the captured before-state.</summary>
    public void SetAfter(string? after) => After = after;

    /// <inheritdoc/>
    public bool TryAbsorb(IEditorCommand newer)
    {
        // Keyed on the PAIR. Absorbing on the node id alone would let a drag on
        // one field swallow an edit to a different keyvalue on the same entity:
        // the second value would be written and then silently replaced by the
        // drag's next frame, with one history entry claiming to be both.
        if (newer is not SetEntityKeyvalueCommand next
            || next.NodeId != NodeId
            || !string.Equals(next.Key, Key, StringComparison.Ordinal))
        {
            return false;
        }

        SetAfter(next.After);
        return true;
    }

    /// <inheritdoc/>
    public string Name { get; init; } = "Entity Property";

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

        // A node that left the scene mid-gesture still has to be restored, or it
        // is stranded at a half-dragged value with nothing left that could put
        // it back: a cancel discards its commands afterwards and gets no second
        // chance, unlike an undo behind a still-undone delete.
        if (_lastApplied is not null
            && _lastApplied.TryGetTarget(out SceneNode? detached)
            && detached.Entity is { } orphan)
        {
            Write(orphan, Before);
        }
    }

    private void Apply(Scene scene, string? value)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // Missing target = no-op, per the IEditorCommand contract. A node whose
        // entity payload was removed since is the same case: there is nothing to
        // write and nothing to report.
        if (!scene.TryFindById(NodeId, out SceneNode? node) || node.Entity is not { } entity)
            return;

        _lastApplied ??= new WeakReference<SceneNode>(node);
        Write(entity, value);
    }

    private void Write(EntityData entity, string? value)
    {
        if (value is not null)
        {
            entity.SetValue(Key, value);
            return;
        }

        // The first match, matching EntityData.TryGetValue and SetValue: a
        // hand-written file may carry a duplicate key, and the first one is the
        // value the entity binds, so it is the one this command has been
        // reading and writing all along.
        List<KeyValuePair<string, string>> keyvalues = entity.Keyvalues;
        for (int i = 0; i < keyvalues.Count; i++)
        {
            if (string.Equals(keyvalues[i].Key, Key, StringComparison.Ordinal))
            {
                keyvalues.RemoveAt(i);
                return;
            }
        }
    }
}
