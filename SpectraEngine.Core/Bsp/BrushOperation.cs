namespace SpectraEngine.Core.Bsp;

/// <summary>
/// Whether a brush <em>adds</em> solid to the compiled world or <em>removes</em>
/// it — Hammer's carve, Unreal's subtractive brush, Roblox's negate.
/// </summary>
/// <remarks>
/// <para>
/// <b>The compiled solid is <c>⋃{additive} \ ⋃{subtractive}</c>, regularized,
/// evaluated as an UNORDERED set expression.</b> Every subtractive brush beats
/// every additive one; subtractives compose with each other only by union.
/// There is no ordered CSG tree and no scoped negation.
/// </para>
/// <para>
/// <b>Why unordered, which is the whole design.</b> The only total order this
/// engine has over admitted brushes is scene-graph traversal order. Today that
/// order is consumed in exactly one place, to choose between two
/// <em>geometrically identical</em> coplanar faces — so a reparent is
/// invisible. Under an ordered subtraction the same order would choose between
/// "there is a doorway here" and "there is a wall here": dragging a node into a
/// folder would silently rewrite world topology. That is verbatim the failure
/// <see cref="Scene.BrushKind"/> exists to forbid in the admission dimension,
/// and it applies unchanged to a composition rule. An ordered rule would also
/// force a persistent, monotonic per-brush order key into the map format,
/// because a streamed open world cannot guarantee load order.
/// </para>
/// <para>
/// <b>Locally evaluable, which is what the open-world pillar actually needs.</b>
/// For any point, membership depends only on brushes whose volume contains it,
/// and every such brush is resident in that point's chunk cell — so the rule is
/// decidable from one cell's resident set. No global pass, no map extents.
/// </para>
/// <para>
/// <b>The one expressive loss, stated rather than hidden.</b> Under a set model
/// an additive brush placed inside a subtractive brush disappears entirely;
/// there is no "add it back afterwards". Hammer authors expect this. Roblox
/// authors expect <em>scoped</em> negation and will notice. The escape that
/// costs nothing today: a <see cref="Scene.BrushKind.Part"/> brush is not in
/// the placement list at all, so no negative can remove it — "put a Part brush
/// in the hole" is the fill-a-hole affordance.
/// </para>
/// </remarks>
public enum BrushOperation : byte
{
    /// <summary>
    /// The brush contributes its solid, merging with every additive brush it
    /// overlaps. The default, and what every brush authored before subtraction
    /// existed means.
    /// </summary>
    Additive = 0,

    /// <summary>
    /// The brush removes its solid from every additive brush it overlaps. It
    /// emits no outward skin of its own — instead it induces <em>cavity walls</em>,
    /// the inward-facing boundary of the removed region, seeded into each brush
    /// it cuts and wearing this brush's own per-face materials.
    /// </summary>
    Subtractive = 1,
}
