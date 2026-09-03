using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Which generation of a shared colour target is live, which retired ones are
/// still being held for a consumer that has not let go, and when each may
/// finally be freed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A shared target is never resized in place</b>, because the consumer
/// imported the handle and a handle cannot be swapped inside a wrapper the way
/// <see cref="RenderTarget.ColorTexture"/>'s GPU resource can. So a resize
/// destroys the target and builds a new one under a fresh generation - and
/// "destroys" is exactly the word that cannot be taken literally. The consumer
/// may still be sampling the old resource this instant, in its own process or
/// at least on its own device, and freeing it underneath produces no exception
/// on either side: a black frame if you are lucky, a device removal some frames
/// later if you are not.
/// </para>
/// <para>
/// <b>So a retired generation is held until the consumer says it is done with
/// it</b>, and the counter is what makes that sayable: the consumer names the
/// generation it has released, and everything at or below that number is free.
/// Nothing here knows what a generation's resources ARE - the release is a
/// callback the backend supplies - which is what keeps this backend-neutral and
/// testable with no device at all.
/// </para>
/// <para>
/// <b><see cref="Cap"/> exists because the acknowledgement may never come.</b> A
/// consumer that has crashed, been detached, or simply never wired the
/// acknowledgement up leaves every retired generation pinned, and a live window
/// drag retires one per resize step: an unbounded retire list is a GPU memory
/// leak whose only symptom is memory. Past the cap the oldest is released
/// anyway and said so out loud, because a leak that reports nothing is worse
/// than a frame that flickers.
/// </para>
/// <para>
/// Render thread only, like everything else that owns a GPU resource's
/// lifetime.
/// </para>
/// </remarks>
internal sealed class SharedTargetRetirement
{
    /// <summary>
    /// How many retired generations may be held at once before the oldest is
    /// released without its acknowledgement.
    /// </summary>
    /// <remarks>
    /// Eight full-screen RGBA8 surfaces is about 66 MB at 1080p, which is enough
    /// slack for a consumer that is a few frames behind and small enough that
    /// the leak is caught while it is still a nuisance.
    /// </remarks>
    internal const int Cap = 8;

    private readonly record struct Retired(int Generation, Action Release);

    private readonly ILogger _logger;
    private readonly List<Retired> _retired = [];
    private int _generation;

    internal SharedTargetRetirement(ILogger logger) => _logger = logger;

    /// <summary>The generation most recently handed out by <see cref="Next"/>; zero before the first.</summary>
    internal int CurrentGeneration => _generation;

    /// <summary>How many retired generations are still waiting to be acknowledged.</summary>
    internal int PendingCount => _retired.Count;

    /// <summary>
    /// How many generations have been released without an acknowledgement
    /// because <see cref="Cap"/> was reached.
    /// </summary>
    /// <remarks>
    /// Counted as well as logged so a gate can fail on it. A log line is read by
    /// somebody who is already looking.
    /// </remarks>
    internal int ForcedReleaseCount { get; private set; }

    /// <summary>
    /// Takes the next generation number. Monotonic and never reused, which is
    /// what lets a consumer tell "re-import" from "the handle you have is still
    /// the current one".
    /// </summary>
    internal int Next() => ++_generation;

    /// <summary>
    /// Holds <paramref name="release"/> until the consumer acknowledges
    /// <paramref name="generation"/>, or until <see cref="Cap"/> forces it.
    /// </summary>
    internal void Retire(int generation, Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        _retired.Add(new Retired(generation, release));

        while (_retired.Count > Cap)
        {
            Retired oldest = _retired[0];
            _retired.RemoveAt(0);
            ForcedReleaseCount++;

            // The FIRST one is the diagnosis and is worth a warning; repeating
            // it at warning level for every step of a live resize drag would
            // bury the line that says what happened. The running total travels
            // in both, so either line stands alone.
            if (ForcedReleaseCount == 1)
            {
                _logger.LogWarning(
                    "Shared target generation {Generation} was released without the consumer acknowledging it: " +
                    "{Cap} retired generations were already held. Either nothing is consuming the shared target " +
                    "or it never calls back, and holding them all would leak a full-screen surface per resize.",
                    oldest.Generation, Cap);
            }
            else
            {
                _logger.LogDebug(
                    "Shared target generation {Generation} force-released ({Count} so far).",
                    oldest.Generation, ForcedReleaseCount);
            }

            oldest.Release();
        }
    }

    /// <summary>
    /// The consumer has finished with <paramref name="generation"/>. Releases it
    /// and every older retired generation, and returns how many that was.
    /// </summary>
    /// <remarks>
    /// <b>At or below, not equal to.</b> A consumer that skipped a generation -
    /// two resizes inside one of its frames - would otherwise pin the one it
    /// never saw forever, and it genuinely is done with it: it never imported
    /// it.
    /// </remarks>
    internal int ConsumerReleased(int generation)
    {
        int released = 0;

        // Oldest first, and re-reading index 0 each time rather than iterating:
        // the list is ordered by generation because Retire only ever appends.
        while (_retired.Count > 0 && _retired[0].Generation <= generation)
        {
            Retired oldest = _retired[0];
            _retired.RemoveAt(0);
            released++;
            oldest.Release();
        }

        return released;
    }

    /// <summary>
    /// Releases every retired generation regardless of acknowledgement. What
    /// shutdown does: the device is going, so there is nothing left for a
    /// consumer to hold on to.
    /// </summary>
    internal void ReleaseAll()
    {
        // Copied out first: a release callback runs arbitrary teardown, and this
        // must not be re-entered halfway through with a half-emptied list.
        Retired[] pending = [.. _retired];
        _retired.Clear();
        foreach (Retired entry in pending)
            entry.Release();
    }
}
