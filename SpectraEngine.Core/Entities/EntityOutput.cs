using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// One of an entity's outputs, holding the RUNTIME copy of the wires the map
/// authored on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The copy is the whole point of this type.</b> A wire may be authored to
/// fire a limited number of times, and something has to count down. That counter
/// belongs here, on a per-session object, and never on the
/// <see cref="EntityConnection"/> stored in <see cref="EntityData"/>: writing it
/// back would edit the author's document as a side effect of playing the level,
/// the edited value would ride the next save out to disk, and nothing anywhere
/// would report it - a map that quietly stops working the third time it is
/// opened, with a diff as the only evidence.
/// </para>
/// <para>
/// <b>Order is the authored order.</b> Wires fire in the order the file lists
/// them, so two wires with the same delay are delivered in that order too (the
/// queue's sequence tiebreak carries it through), which is the only ordering
/// promise a person editing a map can act on.
/// </para>
/// </remarks>
public sealed class EntityOutput
{
    private readonly List<Wire> _wires = [];

    internal EntityOutput(string name) => Name = name;

    /// <summary>The output's name, as the map spells it.</summary>
    public string Name { get; }

    /// <summary>How many wires leave this output, exhausted ones included.</summary>
    public int WireCount => _wires.Count;

    /// <summary>How many wires can still fire.</summary>
    public int LiveWireCount
    {
        get
        {
            int live = 0;
            for (int i = 0; i < _wires.Count; i++)
            {
                if (_wires[i].FiresLeft != 0)
                    live++;
            }

            return live;
        }
    }

    /// <summary>
    /// The fires remaining on the wire at <paramref name="index"/>, negative for
    /// unlimited. This is the runtime counter; the authored value is unchanged
    /// in <see cref="EntityData.Connections"/>.
    /// </summary>
    public int FiresLeftAt(int index) => _wires[index].FiresLeft;

    /// <summary>The connection the wire at <paramref name="index"/> was built from.</summary>
    public EntityConnection ConnectionAt(int index) => _wires[index].Connection;

    internal void Add(in EntityConnection connection) =>
        _wires.Add(new Wire { Connection = connection, FiresLeft = connection.TimesToFire });

    internal void Fire(Entity caller, Entity? activator, string? parameterOverride)
    {
        EntityWorld world = caller.World;
        for (int i = 0; i < _wires.Count; i++)
        {
            Wire wire = _wires[i];
            // Zero is exhausted. Negative is infinite and is never decremented,
            // so a count cannot wrap out of infinity into a finite one.
            if (wire.FiresLeft == 0)
                continue;

            world.ScheduleOutput(caller, activator, Name, wire.Connection, parameterOverride);

            if (wire.FiresLeft > 0)
            {
                wire.FiresLeft--;
                // Write-back, because Wire is a struct held in a List: the local
                // above is a copy and decrementing it alone would make every
                // limited wire fire forever.
                _wires[i] = wire;
            }
        }
    }

    // A struct in a list rather than an object per wire: a level's wires are
    // counted in thousands and the only mutable state is one int.
    private struct Wire
    {
        public EntityConnection Connection;
        public int FiresLeft;
    }
}
