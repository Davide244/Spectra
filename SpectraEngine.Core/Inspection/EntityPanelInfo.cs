using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Inspection;

/// <summary>
/// One authored wire, plus whether anything in the scene answers to its target
/// name.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wire is carried whole rather than flattened into six fields.</b>
/// <see cref="EntityConnection"/> is already a value of strings and numbers, so
/// it crosses the host boundary safely as it stands, and copying it apart here
/// would mean a second place that has to be updated when the record grows a
/// member - which for a wire is a member silently dropped from every panel.
/// </para>
/// <para>
/// <b><see cref="TargetResolves"/> is computed on the RENDER THREAD, at publish
/// time.</b> A shell's mirror of the scene tree is stale by up to a publish
/// interval, so a shell-side resolve would flag a target that exists as missing
/// for a third of a second after it was renamed into place, and would keep
/// showing one that has just been deleted as fine. The engine owns the graph
/// and is the only thing that can answer without racing it.
/// </para>
/// </remarks>
/// <param name="Wire">The authored connection, exactly as the node stores it.</param>
/// <param name="TargetResolves">
/// Whether at least one entity in the scene answers to
/// <see cref="EntityConnection.TargetName"/> - by exact name, by a trailing-*
/// prefix, or as one of the runtime <c>!</c> forms, which cannot be checked
/// statically and are therefore reported as resolving.
/// </param>
public readonly record struct EntityConnectionInfo(EntityConnection Wire, bool TargetResolves);

/// <summary>
/// What a wiring panel needs about the selected entity: the class it names,
/// the outputs that class declares, and the wires the node carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Copies, never the live lists.</b> Nothing crossing the host boundary may
/// be a live object: the render thread starts mutating that node the instant
/// the frame ends, and a panel holding <c>EntityData.Connections</c> itself
/// would be reading a <c>List&lt;T&gt;</c> from the wrong thread while an
/// undo rewrote it. That is the same rule <see cref="PropertyRow"/> follows and
/// the reason a published selection is ids rather than nodes.
/// </para>
/// <para>
/// <b>Published for a SINGLE-node entity selection only.</b> Merging the wiring
/// of several entities is a named deferral rather than an oversight: a keyvalue
/// merges per key and per axis, but a connection list has no key to merge on -
/// two entities' third wires are not the same wire - so the honest answers are
/// the union (which no edit could write back) or the intersection (which hides
/// wiring). Neither is worth shipping ahead of an entity picker, and a
/// panel that showed one entity's wires while the selection held five would be
/// the worst of the three.
/// </para>
/// </remarks>
public sealed class EntityPanelInfo
{
    /// <summary>
    /// The node the wiring belongs to.
    /// </summary>
    /// <remarks>
    /// <b>An edit addresses THIS id, not "the selection".</b> Every other
    /// property edit lets the editor resolve the selection on the render
    /// thread, because a property edit writes one named value and landing on a
    /// node the user has since also selected is harmless. A connection edit
    /// replaces a whole list, so landing on the wrong node would overwrite that
    /// node's entire wiring with another node's - so the id the panel was built
    /// from travels with it, and the command addresses that.
    /// </remarks>
    public required Guid NodeId { get; init; }

    /// <summary>The class the entity names, as the map spells it.</summary>
    public string ClassName { get; init; } = "";

    /// <summary>
    /// Whether a schema for <see cref="ClassName"/> was found.
    /// </summary>
    /// <remarks>
    /// False is not an error: <c>EntityData</c> is strings precisely so a map
    /// authored against a game this build does not have round-trips. What it
    /// costs is <see cref="Outputs"/>, so a panel can say why the dropdown is
    /// empty rather than looking broken.
    /// </remarks>
    public bool IsKnown { get; init; }

    /// <summary>The output names the class declares, in declaration order.</summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>
    /// The node's wires, in AUTHORED ORDER, each with its target's verdict.
    /// </summary>
    /// <remarks>
    /// <b>Order is authored data.</b> It round-trips through <c>map.json</c>
    /// and is never sorted here or anywhere else; a panel that listed wires
    /// alphabetically would rewrite that region of a person's file the first
    /// time they added one.
    /// </remarks>
    public IReadOnlyList<EntityConnectionInfo> Connections { get; init; } = [];

    /// <summary>
    /// Describes <paramref name="node"/>'s entity payload, or null when it
    /// carries none.
    /// </summary>
    /// <remarks>
    /// <b>Render thread only</b>, like <see cref="NodeInspector"/>: it reads a
    /// live node and the graph around it, and hands back values that reference
    /// neither.
    /// </remarks>
    /// <param name="node">The selected node.</param>
    /// <param name="schemas">What the scene's classes declare, or null.</param>
    /// <param name="scene">
    /// The scene to resolve target names against, or null to skip the check
    /// (every target then reports as unresolved, which is the honest answer
    /// when there is nothing to resolve against).
    /// </param>
    /// <param name="nameScratch">
    /// A list the caller owns, reused across publishes. The walk that fills it
    /// is the one part of this that is proportional to the scene rather than to
    /// the entity, so it runs only when there is a wire to check and allocates
    /// nothing when the caller brings its own buffer.
    /// </param>
    public static EntityPanelInfo? Capture(
        SceneNode node,
        EntitySchemaCatalog? schemas,
        Scene.Scene? scene,
        List<string>? nameScratch = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Entity is not { } entity)
            return null;

        EntitySchema? schema = null;
        bool known = schemas is not null && schemas.TryGetSchema(entity.ClassName, out schema);
        IReadOnlyList<string> outputs = known && schema is not null ? schema.Outputs : [];

        List<EntityConnection> wires = entity.Connections;
        if (wires.Count == 0)
        {
            return new EntityPanelInfo
            {
                NodeId = node.Id,
                ClassName = entity.ClassName,
                IsKnown = known,
                Outputs = outputs,
                Connections = [],
            };
        }

        // The scan is gated on there being a wire at all, because it is the
        // only part of a publish that walks the whole graph. A selection of one
        // entity carrying no wiring - which is most of them - costs nothing.
        List<string> names = nameScratch ?? [];
        names.Clear();
        if (scene is not null)
            CollectEntityNames(scene.Root, names);

        var described = new EntityConnectionInfo[wires.Count];
        for (int i = 0; i < wires.Count; i++)
            described[i] = new EntityConnectionInfo(wires[i], Resolves(wires[i].TargetName, names));

        return new EntityPanelInfo
        {
            NodeId = node.Id,
            ClassName = entity.ClassName,
            IsKnown = known,
            Outputs = outputs,
            Connections = described,
        };
    }

    /// <summary>
    /// Whether anything in <paramref name="names"/> answers to
    /// <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// <b>The same three forms <c>TargetNameIndex.Resolve</c> honours</b>, and
    /// deliberately no more: a check that recognised a form the runtime does
    /// not would report a dead wire as live, which is worse than no check.
    /// A <c>!</c> form names an entity chosen while the level runs, so there is
    /// nothing here that could disprove it and it reports as resolving.
    /// </remarks>
    private static bool Resolves(string? target, List<string> names)
    {
        if (string.IsNullOrEmpty(target))
            return false;

        if (target[0] == '!')
        {
            return target is TargetNameIndex.SelfToken
                or TargetNameIndex.ActivatorToken
                or TargetNameIndex.CallerToken;
        }

        if (target[^1] == '*')
        {
            ReadOnlySpan<char> prefix = target.AsSpan(0, target.Length - 1);
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].AsSpan().StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], target, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Appends the name of every node in the subtree that carries an entity.
    /// </summary>
    /// <remarks>
    /// <b>Entity-carrying nodes only, because that is what the runtime
    /// resolves.</b> <c>TargetNameIndex</c> lists entities and nothing else, so
    /// a wire aimed at a plain brush node named "door" delivers to nothing -
    /// and a check that counted every node would call that wire healthy and
    /// then let it fail silently at run time, which is the exact failure this
    /// flag exists to catch.
    /// </remarks>
    private static void CollectEntityNames(SceneNode node, List<string> into)
    {
        if (node.Entity is not null)
            into.Add(node.Name);

        IReadOnlyList<SceneNode> children = node.Children;
        for (int i = 0; i < children.Count; i++)
            CollectEntityNames(children[i], into);
    }
}
