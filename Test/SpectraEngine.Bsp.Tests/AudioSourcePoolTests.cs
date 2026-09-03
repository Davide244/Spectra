using SpectraEngine.Core.Audio;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The source pool's reclaim policy: free first, then the OLDEST finished
/// source, and only then anything that can still be heard.
/// </summary>
/// <remarks>
/// The rule the tests exist to hold is "never steal a playing source ahead of a
/// finished one". A finished source is capacity nobody has claimed back yet, so
/// taking it costs nothing audible; a playing one is a sound somebody can hear.
/// Getting the order wrong does not fail, it just quietly cuts sounds off, and
/// the report that comes back is "audio drops out when it gets busy", which
/// points at nothing.
/// </remarks>
public sealed class AudioSourcePoolTests
{
    [Fact]
    public void A_pool_sizes_itself_to_what_the_driver_actually_granted()
    {
        // A driver with a hard limit refuses partway through, and the pool
        // honours that rather than assuming its request was met. Assuming would
        // hand out handle 0, which AL accepts and silently ignores.
        var backend = new FakeAudioBackend(maxSources: 5);
        var pool = new AudioSourcePool(backend, 32);

        pool.Capacity.ShouldBe(5);
        backend.LiveSourceCount.ShouldBe(5);
    }

    [Fact]
    public void An_exhausted_pool_reclaims_the_oldest_finished_source_and_not_a_playing_one()
    {
        var backend = new FakeAudioBackend(maxSources: 3);
        var pool = new AudioSourcePool(backend, 3);

        pool.TryAcquire(streaming: false, out uint first).ShouldBeTrue();
        pool.TryAcquire(streaming: false, out uint second).ShouldBeTrue();
        pool.TryAcquire(streaming: false, out uint third).ShouldBeTrue();
        backend.Play(first);
        backend.Play(second);
        backend.Play(third);

        // Two are over, in the opposite order to the one they were acquired in.
        // The pool must pick by ACQUIRE order, not by which finished first:
        // recycling the longest-held handle is what keeps them cycling instead
        // of thrashing one entry.
        backend.Finish(third);
        backend.Finish(second);

        pool.TryAcquire(streaming: false, out uint reclaimed).ShouldBeTrue();
        reclaimed.ShouldBe(second);
        pool.StolenCount.ShouldBe(0);

        // A voice plays the source the moment it gets it, so the reclaimed one
        // is audible again immediately. Modelling that is what makes the next
        // acquire a real question rather than a choice between two idle entries.
        backend.Play(reclaimed);

        pool.TryAcquire(streaming: false, out uint next).ShouldBeTrue();
        next.ShouldBe(third);
        pool.StolenCount.ShouldBe(0);
        backend.Play(next);

        // Only now, with nothing finished left, is a playing source taken, and
        // the pool says so rather than doing it silently.
        pool.TryAcquire(streaming: false, out uint stolen).ShouldBeTrue();
        stolen.ShouldBe(first);
        pool.StolenCount.ShouldBe(1);
    }

    [Fact]
    public void A_streaming_source_is_never_reclaimed_by_the_state_scan()
    {
        // The trap this closes: a streaming source that ran dry reports Stopped
        // exactly as a finished one does. A pool trusting the driver's state
        // would hand the music track's source to a footstep the first time a
        // frame hitched, and nothing anywhere would report it.
        var backend = new FakeAudioBackend(maxSources: 2);
        var pool = new AudioSourcePool(backend, 2);

        pool.TryAcquire(streaming: true, out uint music).ShouldBeTrue();
        pool.TryAcquire(streaming: false, out uint shot).ShouldBeTrue();
        backend.Play(music);
        backend.Play(shot);

        backend.Starve(music);
        backend.Finish(shot);

        // Both read as Stopped; only the one-shot may be taken.
        backend.StateOf(music).ShouldBe(AudioSourceState.Stopped);
        pool.TryAcquire(streaming: false, out uint reclaimed).ShouldBeTrue();
        reclaimed.ShouldBe(shot);
    }

    [Fact]
    public void A_pool_carrying_only_streams_drops_the_new_sound_rather_than_cutting_music()
    {
        var backend = new FakeAudioBackend(maxSources: 2);
        var pool = new AudioSourcePool(backend, 2);

        pool.TryAcquire(streaming: true, out _).ShouldBeTrue();
        pool.TryAcquire(streaming: true, out _).ShouldBeTrue();

        // Dropping a footstep is the right answer; the alternative is stopping
        // the music to play it.
        pool.TryAcquire(streaming: false, out uint source).ShouldBeFalse();
        source.ShouldBe(0u);
        pool.StarvedCount.ShouldBe(1);
        pool.StolenCount.ShouldBe(0);
    }

    [Fact]
    public void A_released_source_is_detached_before_it_is_handed_out_again()
    {
        // AL refuses a queue operation on a source that still holds a static
        // buffer, so a source that once played a one-shot would accept no
        // queued buffers as a streaming voice and play silence with no error.
        var backend = new FakeAudioBackend(maxSources: 1);
        var pool = new AudioSourcePool(backend, 1);

        pool.TryAcquire(streaming: false, out uint source).ShouldBeTrue();
        backend.SetSourceBuffer(source, 7);
        pool.Release(source);

        pool.TryAcquire(streaming: true, out uint reused).ShouldBeTrue();
        reused.ShouldBe(source);
        backend.QueueBuffer(reused, 9);
        backend.QueueDepth(reused).ShouldBe(1);
    }

    [Fact]
    public void Releasing_a_free_or_foreign_handle_is_a_no_op()
    {
        var backend = new FakeAudioBackend(maxSources: 2);
        var pool = new AudioSourcePool(backend, 2);

        pool.TryAcquire(streaming: false, out uint source).ShouldBeTrue();
        pool.Release(source);
        pool.InUse.ShouldBe(0);

        // Releasing the same free entry again, and a handle from no pool at
        // all, must both change nothing. The case this does NOT cover is a
        // STALE release of a reused handle, which no handle comparison can
        // detect: AudioManager buys that guarantee instead, by dropping a voice
        // in the same step it releases the source.
        pool.Release(source);
        pool.Release(9999);
        pool.InUse.ShouldBe(0);
    }
}
