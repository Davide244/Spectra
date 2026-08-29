namespace SpectraEngine.Core.Serialization;

/// <summary>Raw JSON carried through a round trip untouched.</summary>
public sealed class PreservedValue
{
    public PreservedValue(byte[] raw) => Raw = raw;

    /// <summary>The value's exact bytes as they appeared in the source document.</summary>
    public byte[] Raw { get; }
}

/// <summary>
/// An unrecognised member, and where in the canonical member order it sat.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Anchor"/> is what makes preservation byte-identical rather
/// than merely lossless.</b> The obvious implementation collects unknown
/// members into a list and replays them when the object closes, which loses
/// nothing and still produces different bytes from the document that was read,
/// for exactly the case preservation exists to serve: a newer engine wrote its
/// own members interleaved among the ones this one knows.
/// </para>
/// <para>
/// So each preserved member records the index, into the owning object's
/// canonical member order, of the last known member that preceded it
/// (<c>-1</c> for "before all of them"). The writer flushes anchored members
/// after emitting each canonical slot, which reproduces the original
/// interleaving exactly and degrades predictably: an unknown anchored to a
/// member that is now omitted lands at that member's slot, which is where it
/// was.
/// </para>
/// </remarks>
public sealed class PreservedMember
{
    public PreservedMember(string name, byte[] raw, int anchor)
    {
        Name = name;
        Raw = raw;
        Anchor = anchor;
    }

    public string Name { get; }

    public byte[] Raw { get; }

    public int Anchor { get; }
}
