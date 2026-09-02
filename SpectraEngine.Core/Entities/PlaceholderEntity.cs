namespace SpectraEngine.Core.Entities;

/// <summary>
/// The entity a class name nothing is registered for becomes: it keeps
/// everything, parses nothing, accepts no input and fires nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is lossless for free, and that is the design paying off rather than a
/// feature it implements.</b> The authored data is the store
/// (<see cref="EntityData"/> on the node) and a runtime instance is built from
/// it and never writes back, so an entity with no behaviour keeps its class
/// name, every keyvalue and every wire simply by not touching them - and the map
/// it came from re-saves byte for byte. There is nothing here to get that wrong.
/// </para>
/// <para>
/// <b>Refusing an input warns once per CLASS NAME, not once per attempt.</b> A
/// missing class is usually a whole game's worth of entities missing at once,
/// and a relay pointed at one fires on a timer; per-attempt reporting turns one
/// fact into a log nobody can read, and per-instance reporting turns it into a
/// hundred copies of one line.
/// </para>
/// </remarks>
public sealed class PlaceholderEntity : Entity
{
    /// <inheritdoc/>
    /// <remarks>
    /// Always false: this class recognises no key, which is precisely what makes
    /// it lossless. The world logs the unknown key at debug level and the value
    /// stays exactly where it was authored.
    /// </remarks>
    public override bool ParseKeyValue(string key, string value) => false;

    /// <inheritdoc/>
    public override bool AcceptInput(string input, ref EntityInputContext context)
    {
        World.ReportPlaceholderInput(this, input);
        return false;
    }
}
