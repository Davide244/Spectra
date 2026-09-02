namespace SpectraEngine.Core.Assets.Packs;

/// <summary>
/// What a pack entry's payload is, as one byte in <see cref="PackEntry.Kind"/>.
/// </summary>
/// <remarks>
/// <para><b>The numbers are the format and are append-only.</b> Inserting a value
/// renumbers every kind after it, and a pack cooked before the insertion then
/// resolves each entry to the wrong kind with no error anywhere: a model handed
/// to the image path is a decode failure at best and a misread header at worst.
/// New kinds go on the end.</para>
/// <para><b>The kind is a routing hint, never the authority on the bytes.</b>
/// Every cooked format carries its own magic and version, so a reader validates
/// what it opened rather than trusting what the table said it would be.</para>
/// </remarks>
public enum PackEntryKind : byte
{
    /// <summary>Bytes with no cooked format of their own, served verbatim.</summary>
    Raw = 0,

    /// <summary>A cooked image: a restricted-profile KTX2 payload.</summary>
    Image = 1,

    /// <summary>A cooked mesh.</summary>
    Model = 2,

    /// <summary>Cooked audio.</summary>
    Audio = 3,

    /// <summary>A cooked material.</summary>
    Material = 4,

    /// <summary>A compiled shader blob, one per target backend.</summary>
    Shader = 5,

    /// <summary>Luau bytecode.</summary>
    Script = 6,

    /// <summary>A compiled map.</summary>
    Map = 7,

    /// <summary>Entity type definitions.</summary>
    EntityDefs = 8,

    /// <summary>
    /// Reserved: several small entries compressed as one solid block, which is
    /// how per-entry compression's forfeit of cross-entry redundancy is bought
    /// back if measurement ever says it is worth buying.
    /// </summary>
    Bundle = 9,

    /// <summary>Reserved: cooked video, named but deliberately unspecified.</summary>
    Video = 10,

    /// <summary>
    /// A deletion. Carries a zero-length payload and means "the logical path this
    /// entry names does not exist", so a higher-priority pack can remove content a
    /// lower-priority one shipped.
    /// </summary>
    /// <remarks>
    /// A game cooks to ONE pack today, where a tombstone can never win anything,
    /// and it is in the format anyway: saying yes to mods later must not require a
    /// format change, and a value at the top of the byte's range costs nothing to
    /// reserve now and cannot be reserved later.
    /// </remarks>
    Tombstone = 0xFF,
}
