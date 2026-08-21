using System;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// The per-node physics and query bits, packed into one byte on
/// <see cref="SceneNode"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bit, not a payload.</b> <see cref="SceneNode"/> carries two payload
/// reference fields today (<see cref="SceneNode.Brush"/> and
/// <see cref="SceneNode.MeshRenderer"/>) and four more are designed. Adding a
/// body reference for something the overwhelming majority of nodes never carry
/// is exactly the drift the data model warns about, so the body handle lives in
/// a side table keyed by <see cref="SceneNode.Id"/> and only
/// <see cref="HasBody"/> — one bit test on a cache line already read — decides
/// whether that table is probed at all.
/// </para>
/// <para>
/// <b><see cref="CanQuery"/> is deliberately decoupled from
/// <see cref="CanCollide"/>.</b> Roblox requires <c>CanCollide</c> to be off
/// before <c>CanQuery</c> takes effect, which is why its raycast params carry a
/// <c>RespectCanCollide</c> switch at all — so "collidable but not raycastable",
/// a solid wall a targeting ray should pass through, is a routine requirement
/// Roblox cannot express. Decoupling costs nothing here because the flag is
/// enforced in this engine's own BVH traversal, and
/// <see cref="SceneQueryFilter.RespectCanCollide"/> keeps the parity escape
/// hatch for ported code.
/// </para>
/// </remarks>
[Flags]
public enum PhysicsFlags : byte
{
    /// <summary>Nothing set: invisible to collision, to queries and to touch.</summary>
    None = 0,

    /// <summary>The node's geometry participates in collision.</summary>
    CanCollide = 1 << 0,

    /// <summary>
    /// The node's geometry is visible to spatial queries — raycasts, overlaps,
    /// shape casts. Independent of <see cref="CanCollide"/>.
    /// </summary>
    CanQuery = 1 << 1,

    /// <summary>The node generates touch/trigger events.</summary>
    CanTouch = 1 << 2,

    /// <summary>
    /// The node is not moved by simulation. Default ON, which is a deliberate
    /// divergence from Roblox's unanchored-by-default part: in this engine a
    /// brush is world geometry until somebody says otherwise, and a default
    /// that quietly enrolled every authored brush in the simulation would make
    /// loading a map a physics event. The part-creation route stamps it off.
    /// </summary>
    Anchored = 1 << 3,

    /// <summary>The node contributes no mass to a body it is attached to.</summary>
    Massless = 1 << 4,

    /// <summary>
    /// A physics body exists for this node in the side table. <b>Owned by the
    /// physics layer, never written by user code</b> — it is the gate that keeps
    /// the body lookup off the hot path for the 99% of nodes that have none.
    /// </summary>
    HasBody = 1 << 5,

    /// <summary>
    /// What a node carries unless it says otherwise: solid, queryable,
    /// touchable, and not simulated.
    /// </summary>
    Default = CanCollide | CanQuery | CanTouch | Anchored,
}
