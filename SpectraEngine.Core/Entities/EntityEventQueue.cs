using System;

namespace SpectraEngine.Core.Entities;

/// <summary>What a queued event does when its time comes.</summary>
internal enum EntityEventKind : byte
{
    /// <summary>Wake one entity's <c>Think</c>.</summary>
    Think = 0,

    /// <summary>Deliver one input to every entity a target name resolves to.</summary>
    Input = 1,
}

/// <summary>
/// One thing the world will do at a stated time.
/// </summary>
/// <remarks>
/// <b>Think wakeups and input deliveries are ONE record in ONE queue.</b> Two
/// queues drained one after the other would order every think against every
/// input by which queue was drained first rather than by time, so a think
/// scheduled before an input and due at the same moment could run after it - a
/// total order that is only total within each half.
/// </remarks>
internal struct EntityEvent
{
    public float Time;

    // The tiebreak, and the whole of the determinism promise: equal times
    // dispatch in the order they were scheduled. A long rather than an int
    // because it is never reset within a session and a busy level schedules
    // millions of events an hour.
    public long Sequence;

    public EntityEventKind Kind;

    /// <summary>The thinking entity, or the entity whose output fired.</summary>
    public Entity? Entity;

    /// <summary>
    /// The <see cref="Entities.Entity.ThinkSerial"/> this think was scheduled
    /// with. A heap cannot remove an entry, so a superseded think is recognised
    /// here and dropped rather than run twice.
    /// </summary>
    public int ThinkSerial;

    public Entity? Activator;
    public string TargetName;
    public string Input;
    public string Parameter;

    /// <summary>The output this came from. Carried for the budget message only.</summary>
    public string Output;
}

/// <summary>
/// A binary min-heap over <see cref="EntityEvent"/>, ordered by
/// <c>(Time, Sequence)</c>.
/// </summary>
/// <remarks>
/// Hand-written rather than <c>PriorityQueue</c> because the ordering is a pair
/// and the tiebreak is load-bearing: a comparer that only saw the time would let
/// the heap's own array layout decide the order of equal-time events, which
/// changes with the insertion pattern and is exactly the non-determinism this
/// queue exists to remove.
/// </remarks>
internal sealed class EntityEventQueue
{
    private EntityEvent[] _heap = new EntityEvent[16];
    private int _count;

    public int Count => _count;

    public void Clear()
    {
        // Cleared rather than truncated: the entries hold entity references and
        // a deactivated world must not keep its instances alive through them.
        Array.Clear(_heap, 0, _count);
        _count = 0;
    }

    public void Push(in EntityEvent item)
    {
        if (_count == _heap.Length)
            Array.Resize(ref _heap, _heap.Length * 2);

        int index = _count++;
        _heap[index] = item;

        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (Precedes(_heap[parent], _heap[index]))
                break;

            (_heap[parent], _heap[index]) = (_heap[index], _heap[parent]);
            index = parent;
        }
    }

    public bool TryPeek(out EntityEvent item)
    {
        if (_count == 0)
        {
            item = default;
            return false;
        }

        item = _heap[0];
        return true;
    }

    public bool TryPop(out EntityEvent item)
    {
        if (!TryPeek(out item))
            return false;

        _count--;
        _heap[0] = _heap[_count];
        _heap[_count] = default;

        int index = 0;
        while (true)
        {
            int left = (index * 2) + 1;
            if (left >= _count)
                break;

            int smallest = left;
            int right = left + 1;
            if (right < _count && Precedes(_heap[right], _heap[left]))
                smallest = right;

            if (Precedes(_heap[index], _heap[smallest]))
                break;

            (_heap[index], _heap[smallest]) = (_heap[smallest], _heap[index]);
            index = smallest;
        }

        return true;
    }

    private static bool Precedes(in EntityEvent a, in EntityEvent b) =>
        a.Time != b.Time ? a.Time < b.Time : a.Sequence < b.Sequence;
}
