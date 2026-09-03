using System;

namespace SpectraEngine.Core.Maps.Compiled;

/// <summary>
/// What a compiled node's payload IS, in a node record's <c>PayloadKind</c>.
/// </summary>
/// <remarks>
/// <para><b>Value 3 is retired and refused, although reuse would be free
/// today.</b> It named a fused, entity-local brush model whose mechanism was
/// overturned: an entity-owned brush is a part brush whose owner happens to be an
/// entity, and that distinction rides
/// <see cref="ScmapPayloadFlags.IsEntityOwned"/> instead. No <c>.scmap</c> has
/// ever shipped, so nothing would break by spending the value again, and it is
/// burned anyway, because an enum value in a shipped format is exactly the thing
/// that must never mean two things. A reader meeting it errors NAMING THE NODE
/// rather than guessing, because guessing is how a door becomes a wall.</para>
/// <para><b>1 versus 2 is the whole <c>BrushKind</c> route.</b> A node whose
/// brush was admitted to the carve writes <see cref="StaticWorldBrush"/>, which is
/// the same statement as "its geometry was baked into the chunks"; a node whose
/// brush stood apart writes <see cref="PartBrush"/>. Without both, a brush
/// converted between kinds does not survive save and load, which is data loss
/// rather than a gap.</para>
/// </remarks>
public enum ScmapPayloadKind : ushort
{
    /// <summary>No payload: a group, a folder, a transform anchor.</summary>
    None = 0,

    /// <summary>
    /// A brush that was admitted to the carve and whose geometry is already inside
    /// the chunk meshes. The cooked-record contract for such a node is
    /// <c>BakedIntoChunks</c>: it must never enter a live carve without first
    /// invalidating the chunks that contain it.
    /// </summary>
    StaticWorldBrush = 1,

    /// <summary>
    /// A standalone brush: never carved, never fused, drawing from its own
    /// brush-local mesh under the node's world matrix.
    /// </summary>
    PartBrush = 2,

    /// <summary>
    /// Retired. Never written, and refused on read. See the remarks on this enum.
    /// </summary>
    RetiredBrushModel = 3,

    /// <summary>A mesh instance: the node names a model and a submesh within it.</summary>
    MeshInstance = 4,

    /// <summary>The root of an instantiated prefab.</summary>
    PrefabRoot = 5,
}

/// <summary>
/// The declared realm of a compiled node, in <c>PayloadFlags</c> bits 3 and 4.
/// </summary>
/// <remarks>
/// <b>Declared, never effective.</b> An effective realm is an intersection down
/// the ancestor chain, so a runtime reparent has to be able to recompute it, and
/// it cannot recompute from a value that has already been folded. Storing the
/// folded value would be a silent correctness bug rather than a size saving, and
/// the forward pass that rebuilds the tree resolves it for free because a
/// parent's record always precedes its child's.
/// <para>The format owns this numbering: the file is where the bits live, so the
/// realm document cites these values rather than assigning its own, and the engine
/// enum that eventually lands must match them. There is no producer in the engine
/// yet, so every node this cook writes declares <see cref="Inherit"/>.</para>
/// </remarks>
public enum ScmapNodeRealm : byte
{
    /// <summary>Take the parent's answer.</summary>
    Inherit = 0,

    /// <summary>Present on the server and on every client.</summary>
    Shared = 1,

    /// <summary>Server only.</summary>
    Server = 2,

    /// <summary>Client only.</summary>
    Client = 3,
}

/// <summary>
/// The declared state of a compiled node, in <c>PayloadFlags</c> bits 5 and 6.
/// </summary>
/// <remarks>
/// The two-bit field has a fourth encoding and only three meanings, so <c>3</c> is
/// INVALID rather than a fourth state. A reader meeting it reports a per-node load
/// defect and carries on, because one bad node is not a reason to refuse a level;
/// the writer refuses it, because a cook is the loud gate and there is no such
/// value to write.
/// </remarks>
public enum ScmapNodeState : byte
{
    /// <summary>Take the parent's answer.</summary>
    Inherit = 0,

    /// <summary>Simulating and rendering.</summary>
    Active = 1,

    /// <summary>Present but not simulating.</summary>
    Dormant = 2,

    /// <summary>Not a state. The unused encoding of a two-bit field.</summary>
    Invalid = 3,
}

/// <summary>
/// The bit flags of a compiled node record's <c>PayloadFlags</c> half-word.
/// </summary>
/// <remarks>
/// <para><b>Bits 3 to 6 are the realm and state fields and are NOT flags</b>, so
/// they are absent from this enum and reached through
/// <see cref="ScmapNodeRecord.DeclaredRealm"/> and
/// <see cref="ScmapNodeRecord.DeclaredState"/>. A two-bit field spelled as two
/// independent flags is how a value of 2 gets written as bit 1 and read as
/// <c>Server | nothing</c>.</para>
/// <para><b>This is not <c>.sentdef</c>'s keyvalue flags word.</b> That is a
/// different record with its own allocation whose bit numbers deliberately do not
/// line up with these. Two records, two tables; the only thing they share is the
/// word realm.</para>
/// </remarks>
[Flags]
public enum ScmapPayloadFlags : ushort
{
    /// <summary>Nothing declared.</summary>
    None = 0,

    /// <summary>This node's authored source survives in <c>BRSH</c>.</summary>
    HasSource = 1 << 0,

    /// <summary>An entity owns this node's payload.</summary>
    IsEntityOwned = 1 << 1,

    /// <summary>The runtime may re-carve this brush, which requires <see cref="HasSource"/>.</summary>
    CanReCarve = 1 << 2,

    /// <summary>
    /// The brush subtracts rather than adds.
    /// </summary>
    /// <remarks>
    /// A flag rather than a <see cref="ScmapPayloadKind"/> value, because
    /// operation is orthogonal to admission: all four (kind, operation) pairs are
    /// legal, the inert one included, and it must round-trip or a brush-kind
    /// change becomes lossy. Meaningful only when the kind is
    /// <see cref="ScmapPayloadKind.StaticWorldBrush"/> or
    /// <see cref="ScmapPayloadKind.PartBrush"/>; a reader IGNORES it otherwise
    /// rather than erroring, so a future payload kind is free to leave it zero.
    /// </remarks>
    SubtractiveBrush = 1 << 7,
}
