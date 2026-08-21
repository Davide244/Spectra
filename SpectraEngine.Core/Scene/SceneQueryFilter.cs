using System.Collections.Generic;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// What a spatial query is allowed to see: the per-node flag rules, an
/// exclusion list, and a collision-group filter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The default sees everything queryable.</b> <c>default(SceneQueryFilter)</c>
/// is the zero value and means "no exclusions, no group filter, honour
/// <see cref="PhysicsFlags.CanQuery"/>" — so every existing caller that passes
/// nothing keeps the behaviour it had, and a caller that wants Roblox's
/// coupling opts into it rather than inheriting it.
/// </para>
/// <para>
/// <b>A struct, passed by <c>in</c>.</b> Queries run per frame and per tick;
/// a class here would allocate on a path whose whole purpose is to be cheap.
/// </para>
/// </remarks>
public readonly record struct SceneQueryFilter
{
    /// <summary>
    /// Nodes this query must not report, or null for none. Compared by
    /// reference; a small list is the right shape because the overwhelmingly
    /// common case is "ignore the caster" — one entry.
    /// </summary>
    public IReadOnlyList<SceneNode>? Ignore { get; init; }

    /// <summary>
    /// When set with <see cref="CollisionGroup"/>, only nodes whose group
    /// interacts with that group are reported.
    /// </summary>
    public CollisionGroups? Groups { get; init; }

    /// <summary>The group this query acts on behalf of. Ignored unless <see cref="Groups"/> is set.</summary>
    public int CollisionGroup { get; init; }

    /// <summary>
    /// Roblox parity: also require <see cref="PhysicsFlags.CanCollide"/> before
    /// a node is reported.
    /// </summary>
    /// <remarks>
    /// Off by default, which is the deliberate divergence. Roblox states that
    /// <c>CanCollide</c> must be disabled for <c>CanQuery</c> to take effect,
    /// so "solid, but a targeting ray passes through it" is inexpressible
    /// there. Here the two flags are independent and this switch exists only so
    /// ported code can ask for the old coupling by name.
    /// </remarks>
    public bool RespectCanCollide { get; init; }

    /// <summary>
    /// Ignore <see cref="PhysicsFlags.CanQuery"/> entirely and report every
    /// spatial node. This is what editor picking wants: a designer must be able
    /// to select the thing they can see, whatever its gameplay flags say, or
    /// clearing <c>CanQuery</c> would make a brush unselectable and the only
    /// way back would be the Explorer.
    /// </summary>
    public bool IgnoreQueryFlags { get; init; }

    /// <summary>
    /// Report subtractive brushes, which every other query deliberately skips
    /// because a hole contributes no solid. Editor tooling needs this — an
    /// author must be able to select the negative brush that cut their
    /// doorway — and gameplay never does.
    /// </summary>
    public bool IncludeSubtractiveBrushes { get; init; }

    /// <summary>The filter editor tooling uses: everything selectable, flags disregarded.</summary>
    public static SceneQueryFilter EditorPicking =>
        new() { IgnoreQueryFlags = true, IncludeSubtractiveBrushes = true };

    /// <summary>Whether <paramref name="node"/> may be reported by this query.</summary>
    public bool Accepts(SceneNode node)
    {
        if (node is null)
            return false;

        if (Ignore is { } ignore)
        {
            for (int i = 0; i < ignore.Count; i++)
            {
                if (ReferenceEquals(ignore[i], node))
                    return false;
            }
        }

        if (!IgnoreQueryFlags)
        {
            PhysicsFlags flags = node.PhysicsFlags;
            if ((flags & PhysicsFlags.CanQuery) == 0)
                return false;
            if (RespectCanCollide && (flags & PhysicsFlags.CanCollide) == 0)
                return false;
        }

        // Interacts, not AreCollidable: the node's id is node-supplied and may
        // legally name a group this registry has not registered, and a
        // broad-phase walk must never be where that is diagnosed. The query
        // group is validated eagerly at the Scene entry point instead, where a
        // caller's mistake is reported deterministically.
        if (Groups is { } groups && !groups.Interacts(CollisionGroup, node.CollisionGroup))
            return false;

        // A subtractive brush is a HOLE — it contributes no solid anywhere, so
        // reporting a hit on its own box is wrong under every reading: a shot
        // fired at a doorway would stop in mid-air, on geometry that is not
        // drawn. Editor tooling still needs to select one, which is what
        // IncludeSubtractiveBrushes is for.
        if (!IncludeSubtractiveBrushes &&
            node.Brush is { Operation: Bsp.BrushOperation.Subtractive })
        {
            return false;
        }

        return true;
    }
}
