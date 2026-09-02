namespace SpectraEngine.Core.Entities;

/// <summary>
/// What a keyvalue MEANS, chosen from a closed set. The declared type is what an
/// editor renders, what a parser converts by, and what a schema exporter writes
/// as one byte.
/// </summary>
/// <remarks>
/// <para>
/// <b>Closed and APPEND-ONLY, and the numbering is frozen because it is a wire
/// byte.</b> A schema record stores this as a single <c>u8</c>, so inserting a
/// member renumbers every kind after it and every definition written before the
/// insert then describes a different type: a float field becomes a colour, an
/// asset path becomes a node reference, and nothing anywhere reports it. New
/// kinds go on the END, with the next free number, forever.
/// </para>
/// <para>
/// <b>These must map one-to-one onto the attribute-value union.</b> A keyvalue is
/// string-typed on the wire (see <see cref="KeyvalueWire"/>) and becomes a typed
/// value exactly once, when it is bound to a live entity; that union is the
/// runtime half of this enum and is not built yet. Every member here is
/// therefore a commitment that the union will carry a case for it, and a member
/// with no case is a keyvalue an entity can declare and never read. Adding a
/// kind here means adding a case there, in the same change.
/// </para>
/// <para>
/// <b>The names carry no behaviour.</b> Nothing resolves a string to one of
/// these by reflection or <c>Enum.Parse</c>; a reader that maps a written name
/// (<c>"asset:model"</c>) to a member does it through a hand-written table, the
/// same discipline every other closed vocabulary in the engine follows.
/// </para>
/// </remarks>
public enum KeyvalueType : byte
{
    /// <summary>A flag. Written <c>"0"</c> or <c>"1"</c>, never <c>"true"</c>.</summary>
    Bool = 0,

    /// <summary>A whole number.</summary>
    Int = 1,

    /// <summary>A finite single-precision number.</summary>
    Float = 2,

    /// <summary>Free text, carried verbatim.</summary>
    String = 3,

    /// <summary>Two floats, <c>"x y"</c>.</summary>
    Vec2 = 4,

    /// <summary>Three floats, <c>"x y z"</c>.</summary>
    Vec3 = 5,

    /// <summary>Four floats, <c>"x y z w"</c>.</summary>
    Vec4 = 6,

    /// <summary>
    /// Three LINEAR floats, <c>"r g b"</c>. Not a display colour and not a hex
    /// string; see <see cref="KeyvalueWire.FormatColor"/>.
    /// </summary>
    Color = 7,

    /// <summary>Three floats in degrees, <c>"pitch yaw roll"</c>.</summary>
    Angles = 8,

    /// <summary>
    /// The name of another entity, resolved at runtime rather than at load time
    /// because the target may be spawned later or not at all.
    /// </summary>
    TargetName = 9,

    /// <summary>
    /// A direct reference to one node, by <c>SceneNode.Id</c>. The precise form
    /// a targetname is not: it names exactly one node and survives a rename.
    /// </summary>
    NodeRef = 10,

    /// <summary>A content-root-relative path to a model.</summary>
    AssetModel = 11,

    /// <summary>A content-root-relative path to a material.</summary>
    AssetMaterial = 12,

    /// <summary>A content-root-relative path to a texture.</summary>
    AssetTexture = 13,

    /// <summary>A content-root-relative path to a sound.</summary>
    AssetSound = 14,

    /// <summary>
    /// One value out of a declared list. The wire form is the chosen value's own
    /// token, so a choice list can gain entries without rewriting any content.
    /// </summary>
    Choices = 15,

    /// <summary>
    /// A bit set, written as one non-negative integer. The meaning of each bit is
    /// declared by the descriptor, not by this enum.
    /// </summary>
    Flags = 16,
}
