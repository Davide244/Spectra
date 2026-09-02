using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// What a placed entity carries: the class it names, the keyvalues authored on
/// it, and the wires leaving its outputs. The fourth payload a
/// <c>SceneNode</c> can hold, beside a mesh renderer, a brush and a light.
/// </summary>
/// <remarks>
/// <para>
/// <b>It names a class; it does not reference a schema.</b> An
/// <see cref="EntitySchema"/> may not exist for
/// <see cref="ClassName"/> - the map may have been authored against a game this
/// build does not have - and that map must still load, round-trip and save. So
/// the class name is text, resolved by whatever catalogue is running, and an
/// unresolved one costs the entity its behaviour and nothing else.
/// </para>
/// <para>
/// <b>Mutable, like <c>Light</c> and unlike <c>Brush</c>.</b> Nothing derived is
/// keyed on its identity, so editing a keyvalue is an ordinary property write
/// and costs no recompile. That is also exactly why <see cref="Clone"/> exists:
/// a duplicate that shared this would have its keyvalues edited by every edit to
/// the original.
/// </para>
/// </remarks>
public sealed class EntityData
{
    /// <summary>An entity carrying no class yet.</summary>
    public EntityData()
    {
    }

    /// <summary>An entity of the named class.</summary>
    public EntityData(string className)
    {
        ArgumentNullException.ThrowIfNull(className);
        ClassName = className;
    }

    /// <summary>
    /// The entity class this instance is, as a map file spells it. Empty until
    /// one is assigned.
    /// </summary>
    public string ClassName { get; set; } = "";

    /// <summary>
    /// The authored keyvalues, name to value, in AUTHORED ORDER.
    /// </summary>
    /// <remarks>
    /// <b>An ordered list, deliberately not a dictionary.</b> Member order has to
    /// round-trip byte-identically through the map format - a person edits
    /// <c>map.json</c> in a text editor and the engine must not reshuffle their
    /// file on the next save - and a dictionary's iteration order is exactly the
    /// determinism sin this repo refuses everywhere else it appears. A level's
    /// entity carries a handful of keyvalues, so the linear scan
    /// <see cref="TryGetValue"/> does is cheaper than the hash anyway.
    /// <para>
    /// Names are matched ORDINALLY. A case-folding rule would need a culture to
    /// fold in, and the same file would then mean different things on different
    /// machines.
    /// </para>
    /// </remarks>
    public List<KeyValuePair<string, string>> Keyvalues { get; } = [];

    /// <summary>The wires leaving this entity's outputs, in authored order.</summary>
    public List<EntityConnection> Connections { get; } = [];

    /// <summary>
    /// Reads the value authored for <paramref name="name"/>. The first match
    /// wins, which is what a reader that preserves a hand-written duplicate has
    /// to do: the alternative is silently dropping one of the two.
    /// </summary>
    public bool TryGetValue(string name, out string value)
    {
        for (int i = 0; i < Keyvalues.Count; i++)
        {
            if (string.Equals(Keyvalues[i].Key, name, StringComparison.Ordinal))
            {
                value = Keyvalues[i].Value;
                return true;
            }
        }

        value = "";
        return false;
    }

    /// <summary>
    /// Writes <paramref name="name"/>, replacing the existing entry IN PLACE
    /// when there is one and appending otherwise.
    /// </summary>
    /// <remarks>
    /// In place, because the order is the file's order: removing and re-adding
    /// would move an edited keyvalue to the end of the object and rewrite a
    /// region of the file nobody touched.
    /// </remarks>
    public void SetValue(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        for (int i = 0; i < Keyvalues.Count; i++)
        {
            if (string.Equals(Keyvalues[i].Key, name, StringComparison.Ordinal))
            {
                Keyvalues[i] = new KeyValuePair<string, string>(name, value);
                return;
            }
        }

        Keyvalues.Add(new KeyValuePair<string, string>(name, value));
    }

    /// <summary>An independent copy carrying the same class, keyvalues and wires.</summary>
    /// <remarks>
    /// <b>This is why a duplicated node gets its own instance.</b> Everything
    /// inside is a string or a value type, so copying the two lists is a full
    /// copy; what would NOT be independent is sharing the lists themselves, which
    /// is precisely the failure a shared <c>Light</c> would have had - editing
    /// the duplicate edits the original, with nothing anywhere to say why.
    /// </remarks>
    public EntityData Clone()
    {
        var copy = new EntityData(ClassName);
        copy.Keyvalues.AddRange(Keyvalues);
        copy.Connections.AddRange(Connections);
        return copy;
    }
}
