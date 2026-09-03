using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Audio;

/// <summary>
/// The fixed set of AL sources the engine ever owns, and the policy that
/// decides which one a new sound gets.
/// </summary>
/// <remarks>
/// <para><b>Why a pool at all.</b> An AL source is a hardware-ish resource with
/// a per-driver limit far below the number of sounds a game asks to play, and
/// creating one per sound fails somewhere between 32 and 256 depending on the
/// machine, which is the worst possible place to discover a limit. The pool is
/// created once, sized to what the driver actually granted, and never grows.
/// </para>
/// <para><b>The reclaim order is the whole design, and the order is: free,
/// then finished, then stolen.</b> A finished source is free capacity nobody
/// has noticed yet, so taking it costs nothing audible; a playing source is a
/// sound somebody can hear, and cutting one off to start another is a real
/// loss. Reclaiming the OLDEST finished one rather than any finished one keeps
/// handles cycling instead of thrashing one entry, which is what makes a
/// driver-side state bug reproduce in the same place twice.</para>
/// <para><b>A streaming voice is never stolen and never classified by AL
/// state.</b> Both halves of that matter. Streaming voices are music and
/// ambience, the two sounds a listener notices stopping; and a streaming source
/// that briefly ran dry reports <see cref="AudioSourceState.Stopped"/> exactly
/// as a finished one does, so a pool that trusted the driver's state would
/// hand a music track's source to a footstep the first time a frame hitched.
/// A streaming entry is released only when its voice says it is done.</para>
/// <para>Render thread only, like everything else that touches AL.</para>
/// </remarks>
public sealed class AudioSourcePool
{
    /// <summary>What a live entry is allowed to have done to it.</summary>
    private enum EntryKind
    {
        /// <summary>Nobody holds it.</summary>
        Free,

        /// <summary>A fire-and-forget sound. Finishes on its own and may be stolen as a last resort.</summary>
        OneShot,

        /// <summary>A queue-fed voice. Its finish is the voice's answer, never the driver's.</summary>
        Streaming,
    }

    private struct Entry
    {
        public uint Source;
        public EntryKind Kind;

        /// <summary>Monotonic acquire order, so "oldest" is a comparison and not a timestamp.</summary>
        public long Sequence;
    }

    private readonly IAudioBackend _backend;
    private readonly Entry[] _entries;
    private long _sequence;

    /// <summary>
    /// Creates up to <paramref name="requestedCount"/> sources, stopping at the
    /// first refusal. <see cref="Capacity"/> reports what was actually granted,
    /// which on a constrained driver is less than what was asked for.
    /// </summary>
    public AudioSourcePool(IAudioBackend backend, int requestedCount)
    {
        if (requestedCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedCount), requestedCount, "A pool needs at least one source.");

        _backend = backend;

        var created = new List<Entry>(requestedCount);
        for (int i = 0; i < requestedCount; i++)
        {
            if (!backend.TryCreateSource(out uint source))
                break;

            created.Add(new Entry { Source = source, Kind = EntryKind.Free, Sequence = 0 });
        }

        _entries = created.ToArray();
    }

    /// <summary>Sources the driver actually granted.</summary>
    public int Capacity => _entries.Length;

    /// <summary>Entries currently held by a voice.</summary>
    public int InUse
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _entries.Length; i++)
                if (_entries[i].Kind != EntryKind.Free) count++;
            return count;
        }
    }

    /// <summary>
    /// Sources taken from a sound that was still audible because nothing else
    /// was available. A non-zero value is the signal that the pool is too small
    /// for the scene, and it is counted rather than logged per event because
    /// the case that produces it produces many.
    /// </summary>
    public int StolenCount { get; private set; }

    /// <summary>
    /// Sounds refused outright because every source was carrying a streaming
    /// voice. Distinct from <see cref="StolenCount"/>: this one dropped a sound
    /// rather than cutting one off.
    /// </summary>
    public int StarvedCount { get; private set; }

    /// <summary>
    /// Hands out a source, reclaiming the oldest finished one when nothing is
    /// free and, only if there is nothing finished either, stealing the oldest
    /// one-shot.
    /// </summary>
    /// <param name="streaming">
    /// True to mark the entry as a streaming voice, which exempts it from both
    /// the finished scan and the steal.
    /// </param>
    public bool TryAcquire(bool streaming, out uint source)
    {
        int index = FindFree();

        // A finished one-shot is capacity nobody has claimed back yet. Scanned
        // before any steal, never after: the driver already told us this sound
        // is over, so taking it is free and taking a playing one is not.
        if (index < 0) index = FindOldest(onlyFinished: true);

        if (index < 0)
        {
            index = FindOldest(onlyFinished: false);
            if (index >= 0) StolenCount++;
        }

        if (index < 0)
        {
            // Every source is carrying a streaming voice. Dropping the new
            // sound is the right answer: the alternative is cutting off the
            // music to play a footstep.
            StarvedCount++;
            source = 0;
            return false;
        }

        ref Entry entry = ref _entries[index];
        if (entry.Kind != EntryKind.Free)
        {
            _backend.Stop(entry.Source);

            // Detaching the static buffer is not tidiness: AL refuses a queue
            // operation on a source that still holds one, so a reused source
            // that once played a one-shot would silently accept no queued
            // buffers as a streaming voice.
            _backend.SetSourceBuffer(entry.Source, 0);
        }

        entry.Kind = streaming ? EntryKind.Streaming : EntryKind.OneShot;
        entry.Sequence = ++_sequence;
        source = entry.Source;
        return true;
    }

    /// <summary>
    /// Returns a source to the pool. A handle this pool does not own, or one
    /// already free, is ignored.
    /// </summary>
    /// <remarks>
    /// It is NOT safe against a stale release: a handle is reused, so releasing
    /// one the caller no longer holds frees whatever sound has it now. There is
    /// no way to tell the two apart from a handle alone, so the guarantee is
    /// bought on the other side instead, by <see cref="AudioManager"/> removing
    /// a voice from its list in the same step it releases the source.
    /// </remarks>
    public void Release(uint source)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Source != source || _entries[i].Kind == EntryKind.Free)
                continue;

            _backend.Stop(source);
            _backend.SetSourceBuffer(source, 0);
            _entries[i].Kind = EntryKind.Free;
            _entries[i].Sequence = 0;
            return;
        }
    }

    /// <summary>Stops everything and hands every source back. Used when the manager shuts down.</summary>
    public void ReleaseAll()
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].Kind == EntryKind.Free) continue;
            _backend.Stop(_entries[i].Source);
            _entries[i].Kind = EntryKind.Free;
            _entries[i].Sequence = 0;
        }
    }

    /// <summary>Destroys every source. The pool is unusable afterwards.</summary>
    public void Dispose()
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            _backend.Stop(_entries[i].Source);
            _backend.DestroySource(_entries[i].Source);
            _entries[i].Kind = EntryKind.Free;
        }
    }

    private int FindFree()
    {
        for (int i = 0; i < _entries.Length; i++)
            if (_entries[i].Kind == EntryKind.Free) return i;
        return -1;
    }

    private int FindOldest(bool onlyFinished)
    {
        int best = -1;
        long bestSequence = long.MaxValue;

        for (int i = 0; i < _entries.Length; i++)
        {
            // Streaming entries are exempt from both passes. Their AL state
            // lies during an underrun, and they are the sounds least acceptable
            // to cut off.
            if (_entries[i].Kind != EntryKind.OneShot) continue;

            if (onlyFinished)
            {
                AudioSourceState state = _backend.GetSourceState(_entries[i].Source);
                if (state is AudioSourceState.Playing or AudioSourceState.Paused) continue;
            }

            if (_entries[i].Sequence >= bestSequence) continue;
            bestSequence = _entries[i].Sequence;
            best = i;
        }

        return best;
    }
}
