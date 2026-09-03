using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The generation bookkeeping behind a shared target that is rebuilt rather than
/// resized.
/// </summary>
/// <remarks>
/// Pure: no device, no driver, no handle. That is the whole reason the
/// bookkeeping is a separate type from the backend that owns the resources - the
/// rule being enforced ("do not free this until the consumer says it is done")
/// is about ordering, and ordering is exactly what a GPU test cannot observe. A
/// release here is a lambda that appends to a list; in the renderer it is a
/// <c>DestroyRenderTarget</c>, and neither knows about the other.
/// </remarks>
public sealed class SharedTargetRetirementTests
{
    private static SharedTargetRetirement New() => new(NullLogger.Instance);

    [Fact]
    public void Generations_start_at_one_and_never_repeat()
    {
        // A consumer re-imports when the number changes and never otherwise, so
        // a reused number is a consumer sampling a destroyed resource while
        // being told nothing happened. Zero is reserved for "no target yet".
        var retirement = New();

        retirement.CurrentGeneration.ShouldBe(0);
        retirement.Next().ShouldBe(1);
        retirement.Next().ShouldBe(2);
        retirement.Next().ShouldBe(3);
        retirement.CurrentGeneration.ShouldBe(3);
    }

    [Fact]
    public void A_retired_generation_is_not_released_until_the_consumer_says_so()
    {
        // The single assertion this type exists for. The consumer may be
        // sampling the old resource this instant, and freeing it underneath
        // raises nothing on either side.
        var released = new List<int>();
        var retirement = New();

        int first = retirement.Next();
        retirement.Retire(first, () => released.Add(first));

        released.ShouldBeEmpty();
        retirement.PendingCount.ShouldBe(1);

        retirement.ConsumerReleased(first).ShouldBe(1);

        released.ShouldBe([first]);
        retirement.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void An_acknowledgement_releases_every_older_generation_too()
    {
        // At or below, not equal to. Two resizes inside one of the consumer's
        // frames leave a generation it never imported, and it genuinely is done
        // with that one: pinning it on an acknowledgement that can never arrive
        // is the leak this avoids.
        var released = new List<int>();
        var retirement = New();

        for (int i = 0; i < 3; i++)
        {
            int generation = retirement.Next();
            retirement.Retire(generation, () => released.Add(generation));
        }

        retirement.ConsumerReleased(3).ShouldBe(3);
        released.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Generations_are_released_oldest_first()
    {
        // Not cosmetic: the release runs arbitrary GPU teardown, and a consumer
        // that acknowledged a later generation has by construction finished with
        // every earlier one first.
        var released = new List<int>();
        var retirement = New();

        for (int i = 0; i < 4; i++)
        {
            int generation = retirement.Next();
            retirement.Retire(generation, () => released.Add(generation));
        }

        retirement.ConsumerReleased(2);
        released.ShouldBe([1, 2]);

        retirement.ConsumerReleased(4);
        released.ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public void An_acknowledgement_for_a_generation_still_live_releases_nothing()
    {
        // The live generation is not on the list at all, so this must be a
        // no-op rather than a release of the target currently being drawn into.
        var released = new List<int>();
        var retirement = New();

        int retired = retirement.Next();
        retirement.Retire(retired, () => released.Add(retired));
        int live = retirement.Next();

        retirement.ConsumerReleased(retired).ShouldBe(1);
        retirement.ConsumerReleased(live).ShouldBe(0);
        released.ShouldBe([retired]);
    }

    [Fact]
    public void Past_the_cap_the_oldest_is_released_without_its_acknowledgement()
    {
        // A consumer that crashed, was detached, or never wired the
        // acknowledgement up would otherwise pin one full-screen surface per
        // resize step of a drag, and the only symptom of that is memory.
        var released = new List<int>();
        var retirement = New();

        for (int i = 0; i < SharedTargetRetirement.Cap + 3; i++)
        {
            int generation = retirement.Next();
            retirement.Retire(generation, () => released.Add(generation));
        }

        retirement.PendingCount.ShouldBe(SharedTargetRetirement.Cap);
        retirement.ForcedReleaseCount.ShouldBe(3);
        released.ShouldBe([1, 2, 3], "the cap releases the OLDEST, which is the one the consumer is least likely to still hold");
    }

    [Fact]
    public void The_forced_release_count_is_readable_and_not_only_logged()
    {
        // Counted as well as logged so a gate can fail on it: a log line is read
        // by somebody who is already looking, and this failure's whole character
        // is that nobody is.
        var retirement = New();

        retirement.ForcedReleaseCount.ShouldBe(0);

        for (int i = 0; i < SharedTargetRetirement.Cap + 1; i++)
            retirement.Retire(retirement.Next(), () => { });

        retirement.ForcedReleaseCount.ShouldBe(1);
    }

    [Fact]
    public void Shutdown_releases_everything_regardless_of_acknowledgement()
    {
        // The device is going with them, so there is nothing left for a consumer
        // to hold on to and holding out for a call that will never come would
        // leak the lot.
        var released = new List<int>();
        var retirement = New();

        for (int i = 0; i < 3; i++)
        {
            int generation = retirement.Next();
            retirement.Retire(generation, () => released.Add(generation));
        }

        retirement.ReleaseAll();

        released.ShouldBe([1, 2, 3]);
        retirement.PendingCount.ShouldBe(0);
        Should.NotThrow(retirement.ReleaseAll);
    }

    [Fact]
    public void A_release_that_retires_again_does_not_corrupt_the_list()
    {
        // ReleaseAll copies the pending entries out before running any of them,
        // because a release callback is arbitrary teardown and re-entering a
        // half-emptied list is the kind of fault that shows up as a missed
        // resource months later.
        var retirement = New();
        var released = new List<int>();

        int first = retirement.Next();
        retirement.Retire(first, () =>
        {
            released.Add(first);
            retirement.Retire(retirement.Next(), () => released.Add(99));
        });

        retirement.ReleaseAll();

        released.ShouldBe([first]);
        retirement.PendingCount.ShouldBe(1, "what the callback added stays pending rather than being swallowed");
    }

    [Fact]
    public void A_null_release_is_refused_where_it_is_written()
    {
        // A retired generation with nothing to run is a resource that is never
        // freed and never reported; refused at the call that wrote it.
        var retirement = New();

        Should.Throw<ArgumentNullException>(() => retirement.Retire(retirement.Next(), null!));
    }
}
