namespace Spectra.Kitchen.Rules;

/// <summary>
/// What a rule cooks. One value per rule, and it is part of the cache key.
/// </summary>
/// <remarks>
/// <para><b>The numbers are append-only, exactly as <c>PackEntryKind</c>'s
/// are.</b> The cook key hashes this as <c>RuleKindId</c>, so renumbering a
/// member does not merely rename a thing: every cached artifact produced by
/// every rule at or after the inserted value silently becomes a cache hit for a
/// different rule's output.</para>
/// <para><b>Members with no implementation yet are declared anyway.</b> That is
/// the same reservation the diagnostic bands make: an image rule and an audio
/// rule arriving in either order must not have to negotiate for a number.</para>
/// </remarks>
public enum RuleKind
{
    /// <summary>Bytes copied through unchanged, cooked for no format of their own.</summary>
    RawCopy = 1,

    /// <summary>Reserved: image and texture.</summary>
    Image = 2,

    /// <summary>Reserved: model.</summary>
    Model = 3,

    /// <summary>Reserved: audio.</summary>
    Audio = 4,

    /// <summary>Reserved: material.</summary>
    Material = 5,

    /// <summary>Reserved: shader.</summary>
    Shader = 6,

    /// <summary>Reserved: script.</summary>
    Script = 7,

    /// <summary>Reserved: map and geometry.</summary>
    Map = 8,
}
