namespace SpectraEngine.Core.Scene;

/// <summary>
/// Whether a node's <see cref="SceneNode.Brush"/> is admitted to the fused
/// static world, or stands alone as a movable object.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The CSG carve is union-skin extraction, not
/// subtraction (see <c>Bsp/Csg.cs</c>): two overlapping brushes <em>merge</em>,
/// so a crate resting on the floor does not punch a hole in it, and nothing
/// about a brush merely <em>sitting</em> in the world damages anything. The
/// damage starts when a brush <b>moves</b> under simulation: every tick changes
/// the overlap <em>set</em> rather than just a placement, which is precisely
/// the structural change the incremental compiler cannot carry — so a simulated
/// brush would bail to the fully-validated O(world) compile every tick, forever,
/// while everything still rendered correctly. That is the open-world pillar
/// dying silently.
/// </para>
/// <para>
/// So this bit's job is exact: make "participates in the fused world" a
/// property that a <b>simulation can never change, and a human changes only by
/// asking</b>.
/// </para>
/// <para>
/// <b>It is declared and stamped, never derived, and never inherited.</b> There
/// is deliberately no <c>Inherit</c> value: because the kind is per-node,
/// <see cref="SceneNode.AddChild"/> is not a refusal site for it and no reparent
/// can rewrite world topology — dragging a brush into a folder can never
/// silently add it to or remove it from the carve. An admission predicate
/// computed from mutable ancestry is how silent corruption happens.
/// </para>
/// </remarks>
public enum BrushKind : byte
{
    /// <summary>
    /// World geometry: the brush enters the static-world placement list, carves
    /// against its neighbours and is fused into the compiled chunk meshes. The
    /// default, because it is what every brush authored so far means, and
    /// because a part face left coplanar with a world face z-fights — which
    /// would otherwise be a beginner's first experience.
    /// </summary>
    World = 0,

    /// <summary>
    /// A standalone object: the brush leaves the placement list entirely, is
    /// never carved and never fused, and carries its own mesh (and later its own
    /// physics hull). Moving one costs no static-world recompile at all. This is
    /// what a Roblox part is, and what anything simulated must be.
    /// </summary>
    Part = 1,
}
