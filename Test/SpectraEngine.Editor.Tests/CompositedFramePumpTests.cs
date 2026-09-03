using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Editor.Viewport;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The composited viewport's frame pump: which generation is on screen, when a
/// superseded one may be let go of, and what the renderer is told about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here fails silently in production.</b> A re-import that never
/// happens is a viewport frozen on the previous size; an acknowledgement that
/// never arrives pins a full-screen surface per step of a resize drag until a
/// cap forces it out; and an import disposed while the compositor is inside the
/// keyed-mutex bracket is a crash in a driver, with no managed stack and
/// nothing to catch. None of the three raises anything on the way past.
/// </para>
/// <para>
/// <b>The seam is what makes any of it reachable.</b> A real hand-over needs a
/// compositor, a GPU and a window; the pump's own decisions need none of those,
/// so the compositor half is an interface and the fake below completes its
/// tasks on the test's own thread - continuations run inline, which makes the
/// whole self-rescheduling loop deterministic instead of a race with the thread
/// pool.
/// </para>
/// </remarks>
public sealed class CompositedFramePumpTests
{
    private const nint ProducerHandle = 0x1000;
    private const int Width = 320;
    private const int Height = 240;

    /// <summary>One import, driven entirely by the test.</summary>
    private sealed class FakeImage : ICompositedImage
    {
        // The default, deliberately: without RunContinuationsAsynchronously a
        // continuation runs INLINE on whichever thread completes the source,
        // which here is the test's. That is what turns an async pump into
        // something a test can step through.
        private readonly TaskCompletionSource _import = new();

        // A QUEUE, because CompositedFramePump.HandOverDepth keeps more than
        // one hand-over outstanding. One slot silently ORPHANED the earlier
        // task - the pump then waited forever on a completion no test could
        // give it, while every assertion about counts still read plausibly.
        // Completed oldest-first, which is how the compositor's own server jobs
        // run.
        private readonly Queue<TaskCompletionSource> _updates = new();

        internal int Updates { get; private set; }

        /// <summary>
        /// Answers whether the pump is currently inside a UI-thread post.
        /// Set by <see cref="FakeSource"/> so an update can record where it was
        /// issued from.
        /// </summary>
        internal Func<bool>? InsideResume { get; set; }

        /// <summary>One entry per update: was it issued from inside a resume?</summary>
        internal List<bool> UpdateSites { get; } = [];

        internal bool Disposed { get; private set; }

        internal uint LastAcquireKey { get; private set; }

        internal uint LastReleaseKey { get; private set; }

        internal bool UpdateInFlight => _updates.Count > 0;

        /// <summary>How many hand-overs this image has been given and not finished.</summary>
        internal int UpdatesInFlight => _updates.Count;

        public Task ImportCompleted => _import.Task;

        public Task UpdateAsync(uint acquireKey, uint releaseKey)
        {
            Updates++;
            UpdateSites.Add(InsideResume?.Invoke() ?? true);
            LastAcquireKey = acquireKey;
            LastReleaseKey = releaseKey;
            var update = new TaskCompletionSource();
            _updates.Enqueue(update);
            return update.Task;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        internal void CompleteImport() => _import.TrySetResult();

        internal void FailImport() => _import.TrySetException(new InvalidOperationException("refused"));

        internal void CompleteUpdate()
        {
            if (_updates.TryDequeue(out TaskCompletionSource? update))
                update.TrySetResult();
        }

        /// <summary>Finishes every outstanding hand-over, oldest first.</summary>
        internal void CompleteAllUpdates()
        {
            // Bounded rather than while-non-empty: completing one re-issues
            // another inline, so draining to empty never terminates.
            for (int outstanding = _updates.Count; outstanding > 0; outstanding--)
                CompleteUpdate();
        }

        internal void FailUpdate()
        {
            if (_updates.TryDequeue(out TaskCompletionSource? update))
                update.TrySetException(new InvalidOperationException("hand-over refused"));
        }
    }

    private sealed class FakeSource : ICompositedImageSource
    {
        internal Func<bool>? InsideResume { get; set; }

        internal List<FakeImage> Images { get; } = [];

        internal List<nint> ImportedHandles { get; } = [];

        internal FakeImage Latest => Images[^1];

        /// <summary>
        /// The real one disposes the drawing surface every import snapshots
        /// into, which is why the pump may not do it under a live hand-over.
        /// </summary>
        internal bool Disposed { get; private set; }

        public ICompositedImage Import(nint ntHandle, int width, int height)
        {
            ImportedHandles.Add(ntHandle);
            var image = new FakeImage { InsideResume = InsideResume };
            Images.Add(image);
            return image;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Rig
    {
        /// <summary>
        /// True while the rig is running a posted UI-thread action, which is
        /// what stands in for "this is the UI thread" in a test with no
        /// dispatcher of its own.
        /// </summary>
        internal bool InsideResume { get; private set; }

        internal FakeSource Source { get; } = new();

        internal List<int> Acknowledged { get; } = [];

        internal List<nint> Closed { get; } = [];

        /// <summary>How many times the loop asked to be resumed on the UI thread.</summary>
        internal int Resumes { get; private set; }

        /// <summary>How many faults the pump raised.</summary>
        internal int Faults { get; private set; }

        /// <summary>
        /// Holds every resume instead of running it, so a test can stand where
        /// the compositor's render thread stands: the hand-over is finished and
        /// the loop has not been let back onto the UI thread yet.
        /// </summary>
        internal bool HoldResumes { get; init; }

        /// <summary>How many held resumes are waiting.</summary>
        internal int PendingResumes => _held.Count;

        /// <summary>Lets the loop back onto the UI thread.</summary>
        internal void ReleaseResumes()
        {
            // Draining rather than a foreach: a released resume runs the rest
            // of the loop, which issues the next hand-over and can queue
            // another one behind it.
            while (_held.Count > 0)
                _held.Dequeue()();
        }

        private readonly Queue<Action> _held = new();

        internal CompositedFramePump Pump { get; }

        internal Rig()
        {
            // The duplicate is a distinct value, because the whole point of
            // duplicating is that this side stops depending on the producer's
            // handle: a test that handed the same number back could not tell
            // the two apart.
            Pump = new CompositedFramePump(
                Source,
                Acknowledged.Add,
                NullLogger.Instance,
                handle => handle + 0x10000,
                Closed.Add,
                onFault: () => Faults++,

                // Inline, which is what keeps the whole self-rescheduling loop
                // steppable: the real one posts to Avalonia's dispatcher at the
                // top of its queue, and a test with no dispatcher running would
                // post into a queue nothing ever drains.
                resumeOnUiThread: action =>
                {
                    Resumes++;
                    if (HoldResumes)
                    {
                        _held.Enqueue(action);
                        return;
                    }

                    InsideResume = true;
                    try
                    {
                        action();
                    }
                    finally
                    {
                        InsideResume = false;
                    }
                });

            Source.InsideResume = () => InsideResume;
        }

        internal void Observe(int generation, nint handle = ProducerHandle) =>
            Pump.Observe(new Renderer.SharedTargetHandle(handle, Width, Height, generation));

        /// <summary>Imports a generation and gets its update loop running.</summary>
        internal FakeImage Adopt(int generation)
        {
            Observe(generation);
            FakeImage image = Source.Latest;
            image.CompleteImport();
            return image;
        }
    }

    [Fact]
    public void A_first_generation_is_imported_from_a_duplicate_of_the_producers_handle()
    {
        var rig = new Rig();
        rig.Observe(generation: 4);

        rig.Source.Images.Count.ShouldBe(1);
        rig.Source.ImportedHandles.ShouldBe([ProducerHandle + 0x10000]);
        rig.Pump.LiveGeneration.ShouldBe(4);
    }

    [Fact]
    public void The_duplicate_is_closed_once_the_import_has_completed_and_not_before()
    {
        var rig = new Rig();
        rig.Observe(generation: 1);

        // Closing sooner races the compositor's own open, which runs on its
        // render thread and has no diagnostic for a handle vanishing under it.
        rig.Closed.ShouldBeEmpty();

        rig.Source.Latest.CompleteImport();
        rig.Closed.ShouldBe([ProducerHandle + 0x10000]);
    }

    [Fact]
    public void The_same_generation_is_never_imported_twice()
    {
        var rig = new Rig();
        rig.Adopt(generation: 7);

        // Observe runs once per pass of the shell's pump, which is hundreds of
        // times a second: re-importing on each would build a fresh GPU image
        // per pass for a texture that never changed.
        rig.Observe(7);
        rig.Observe(7);

        rig.Source.Images.Count.ShouldBe(1);
    }

    [Fact]
    public void The_consumer_acquires_the_key_the_producer_released()
    {
        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 1);

        // The two sides invent nothing: the producer takes key 0 and releases
        // key 1, so this side takes 1 and hands 0 back. Releasing the key you
        // acquired instead deadlocks both sides on the following frame, with
        // nothing anywhere reporting a disagreement.
        image.LastAcquireKey.ShouldBe((uint)Renderer.SharedConsumerKey);
        image.LastReleaseKey.ShouldBe((uint)Renderer.SharedProducerKey);
    }

    [Fact]
    public void The_update_loop_reschedules_itself()
    {
        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 1);

        // Adopting fills the queue rather than issuing one: see HandOverDepth.
        image.Updates.ShouldBe(CompositedFramePump.HandOverDepth);
        image.CompleteUpdate();
        image.Updates.ShouldBe(CompositedFramePump.HandOverDepth + 1);
        image.CompleteUpdate();
        image.Updates.ShouldBe(CompositedFramePump.HandOverDepth + 2);
    }

    /// <summary>
    /// A finished hand-over is replaced at once, so one is always already
    /// queued at the compositor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole of the composited viewport's frame rate.</b> An
    /// update is a server job the compositor picks up on its own tick; with
    /// only one in flight the next is not issued until the previous has
    /// completed and the loop is back on the UI thread, which about half the
    /// time is too late for the tick after and waits a whole refresh. Measured
    /// in a real session, d3d11 and d3d12 alike: 40.5 hand-overs a second at
    /// one deep, 60.5 at two, one constant apart.
    /// </para>
    /// <para>
    /// <b>The count is asserted as a NUMBER and not only against the constant
    /// itself</b>, which is the difference between this test biting and this
    /// test agreeing with whatever the constant currently says. Written the
    /// symbolic way it passed at a depth of one, where the thing it is named
    /// for is exactly what has stopped being true. Two tests beside it do
    /// notice a depth of one, but they notice it as a retirement that settled
    /// one hand-over early; nothing but this says the queue has to be deeper
    /// than the loop.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_queue_is_kept_full_so_the_compositor_never_idles()
    {
        CompositedFramePump.HandOverDepth.ShouldBeGreaterThan(1,
            "one hand-over in flight is one hand-over the compositor is waiting for, " +
            "and it idles a whole refresh about half the time");

        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 1);

        image.UpdatesInFlight.ShouldBe(CompositedFramePump.HandOverDepth);

        for (int lap = 0; lap < 4; lap++)
        {
            image.CompleteUpdate();
            image.UpdatesInFlight.ShouldBe(CompositedFramePump.HandOverDepth,
                "a completed hand-over is replaced inside the same resume");
        }
    }

    [Fact]
    public void A_new_generation_retires_the_old_import_and_acknowledges_it()
    {
        var rig = new Rig();
        FakeImage first = rig.Adopt(generation: 1);

        rig.Adopt(generation: 2);
        first.CompleteAllUpdates();

        first.Disposed.ShouldBeTrue();
        rig.Pump.LiveGeneration.ShouldBe(2);
        rig.Pump.RetiredCount.ShouldBe(0);

        // The RETIRED generation, never the live one. The renderer frees
        // everything at or below the number it is given, so acknowledging the
        // new one would free the resource the viewport is showing.
        rig.Acknowledged.ShouldBe([1]);
    }

    [Fact]
    public void A_retired_import_is_not_disposed_while_an_update_is_still_in_flight()
    {
        var rig = new Rig();
        FakeImage first = rig.Adopt(generation: 1);
        first.UpdateInFlight.ShouldBeTrue();

        rig.Observe(generation: 2);

        // The compositor is inside the keyed-mutex bracket on this image right
        // now. Disposing it here is the crash the whole retirement handshake
        // exists to avoid, and the renderer must not be told it may free the
        // resource either.
        first.Disposed.ShouldBeFalse();
        rig.Pump.RetiredCount.ShouldBe(1);
        rig.Acknowledged.ShouldBeEmpty();

        // One of the two, so the claim is tested at the boundary it is about:
        // a retired import owes a completion for every hand-over it was given,
        // and settling on the first would free it under the second.
        first.CompleteUpdate();
        first.Disposed.ShouldBeFalse();

        first.CompleteAllUpdates();

        first.Disposed.ShouldBeTrue();
        rig.Pump.RetiredCount.ShouldBe(0);
        rig.Acknowledged.ShouldBe([1]);
    }

    [Fact]
    public void The_replacement_keeps_pumping_after_the_old_loop_unwinds()
    {
        var rig = new Rig();
        FakeImage first = rig.Adopt(generation: 1);

        // The new import adopts while the previous loop is still awaiting its
        // last hand-over, so it finds one running and stands down. Nothing
        // would ever start it again if the unwinding loop did not.
        FakeImage second = rig.Adopt(generation: 2);
        second.Updates.ShouldBe(0);

        first.CompleteAllUpdates();

        second.Updates.ShouldBe(CompositedFramePump.HandOverDepth);
        second.CompleteUpdate();
        second.Updates.ShouldBe(CompositedFramePump.HandOverDepth + 1);
    }

    [Fact]
    public void A_generation_the_shell_never_saw_is_covered_by_the_next_acknowledgement()
    {
        var rig = new Rig();
        FakeImage first = rig.Adopt(generation: 1);

        // Two resizes inside one pass of the pump: generation 2 is never
        // observed at all. The renderer releases at-or-below, so saying "done
        // with 1" and then, on the next retirement, "done with 3" frees it -
        // which is correct, because it was never imported.
        rig.Adopt(generation: 3);
        first.CompleteAllUpdates();

        rig.Acknowledged.ShouldBe([1]);
        rig.Source.Images.Count.ShouldBe(2);
        rig.Pump.LiveGeneration.ShouldBe(3);
    }

    [Fact]
    public void An_import_the_compositor_refuses_is_released_rather_than_left_live()
    {
        var rig = new Rig();
        rig.Observe(generation: 5);

        rig.Source.Latest.FailImport();

        rig.Pump.LiveGeneration.ShouldBe(0);
        rig.Source.Latest.Disposed.ShouldBeTrue();
        rig.Closed.ShouldBe([ProducerHandle + 0x10000]);
        rig.Acknowledged.ShouldBe([5]);
    }

    [Fact]
    public void A_handle_that_cannot_be_duplicated_is_skipped_rather_than_imported()
    {
        var source = new FakeSource();
        var pump = new CompositedFramePump(
            source, _ => { }, NullLogger.Instance, _ => 0, _ => { });

        // The producer retired its handle between the publish and here, which
        // is a resize that outran the shell. There is nothing to import and
        // nothing to fix; the next generation is already on its way.
        pump.Observe(new Renderer.SharedTargetHandle(ProducerHandle, Width, Height, 2));

        source.Images.ShouldBeEmpty();
        pump.LiveGeneration.ShouldBe(0);
    }

    [Fact]
    public void Nothing_is_imported_for_a_target_that_does_not_exist_yet()
    {
        var rig = new Rig();

        rig.Pump.Observe(new Renderer.SharedTargetHandle(0, Width, Height, 1));
        rig.Pump.Observe(new Renderer.SharedTargetHandle(ProducerHandle, 0, Height, 1));

        rig.Source.Images.ShouldBeEmpty();
    }

    [Fact]
    public void A_hidden_viewport_stops_pumping_and_starts_again_when_it_comes_back()
    {
        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 1);
        image.CompleteUpdate();

        int taken = image.Updates;
        rig.Pump.SetVisible(false);

        // Every outstanding hand-over completes; nothing schedules another. A
        // minimised window would otherwise copy a full-screen texture per
        // vsync for something nobody can see. All of them, because the loop
        // ends when the last one lands rather than when the first does.
        image.CompleteAllUpdates();
        image.Updates.ShouldBe(taken);
        rig.Pump.IsPumping.ShouldBeFalse();

        rig.Pump.SetVisible(true);
        image.Updates.ShouldBe(taken + CompositedFramePump.HandOverDepth);
    }

    [Fact]
    public void Stopping_retires_the_live_import_and_refuses_anything_further()
    {
        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 3);

        rig.Pump.Stop();
        image.CompleteAllUpdates();

        image.Disposed.ShouldBeTrue();
        rig.Acknowledged.ShouldBe([3]);
        rig.Pump.LiveGeneration.ShouldBe(0);

        rig.Observe(generation: 4);
        rig.Source.Images.Count.ShouldBe(1);
    }

    [Fact]
    public void A_stop_during_a_hand_over_still_waits_for_it()
    {
        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 1);
        image.UpdateInFlight.ShouldBeTrue();

        rig.Pump.Stop();

        // The shell clears the viewport's host before it stops the session, so
        // the producer is still there to release the key this hand-over is
        // waiting on. Disposing under it would not be waiting, it would be the
        // crash.
        image.Disposed.ShouldBeFalse();

        // Every one of them: a stop that waited for the first hand-over and
        // freed the image under the second would be the same crash, one queue
        // slot along.
        image.CompleteUpdate();
        image.Disposed.ShouldBeFalse();

        image.CompleteAllUpdates();
        image.Disposed.ShouldBeTrue();
        rig.Acknowledged.ShouldBe([1]);
    }

    // --- The compositor half, which a re-parent takes with it ----------------

    /// <summary>
    /// The drawing surface goes with the pump, and not one instant sooner.
    /// </summary>
    /// <remarks>
    /// <b>This is the defect a dockable viewport is most likely to ship
    /// with.</b> The pane is detached and re-attached by every dock drag, and
    /// the viewport used to dispose its drawing surface at the moment of
    /// detach - which was safe while a detach only ever meant a session ending,
    /// and is a disposal under a live keyed-mutex bracket once it can also mean
    /// a re-dock. The pending hand-over then faults and the fault is reported as
    /// the composited viewport having failed, on every re-dock, on a viewport
    /// that is working perfectly.
    /// </remarks>
    [Fact]
    public void The_source_is_released_only_after_the_last_hand_over_has_finished()
    {
        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 2);
        image.UpdateInFlight.ShouldBeTrue();

        rig.Pump.Stop();
        rig.Source.Disposed.ShouldBeFalse();
        rig.Pump.SourceReleased.ShouldBeFalse();

        image.CompleteAllUpdates();

        rig.Source.Disposed.ShouldBeTrue();
        rig.Pump.SourceReleased.ShouldBeTrue();
    }

    [Fact]
    public void A_pump_that_never_imported_anything_releases_its_source_at_once()
    {
        // A viewport detached before the first frame arrived: there is nothing
        // to wait for, and leaving the surface alive would leak one per launch
        // of a session that was closed immediately.
        var rig = new Rig();

        rig.Pump.Stop();

        rig.Source.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void A_superseded_generation_does_not_release_the_source_under_the_live_one()
    {
        // A resize retires an import while the pump keeps running on the next
        // one. The source outlives every import by construction, so settling a
        // retired one must not take it: the live import is still snapshotting
        // into that very surface.
        var rig = new Rig();
        FakeImage first = rig.Adopt(generation: 1);

        rig.Observe(generation: 2);
        first.CompleteAllUpdates();

        first.Disposed.ShouldBeTrue();
        rig.Source.Disposed.ShouldBeFalse();
        rig.Pump.LiveGeneration.ShouldBe(2);
    }

    /// <summary>
    /// The source waits for EVERY import, not merely for the live one.
    /// </summary>
    /// <remarks>
    /// A detach that lands mid-resize has a retired generation still inside its
    /// hand-over and a fresh one already imported. Releasing the surface when
    /// the live import settles would free it under the retired one, which is
    /// the same crash one level down and is invisible from either side.
    /// </remarks>
    [Fact]
    public void The_source_waits_for_a_retired_import_as_well_as_the_live_one()
    {
        var rig = new Rig();
        FakeImage first = rig.Adopt(generation: 1);

        // A resize: the second import is adopted while the first is still
        // inside the hand-over it started.
        rig.Observe(generation: 2);
        FakeImage second = rig.Source.Latest;
        second.CompleteImport();
        first.UpdateInFlight.ShouldBeTrue();

        // The live import settles immediately - it never got a loop of its own,
        // because the first one's is still running - and the retired one does
        // not.
        rig.Pump.Stop();
        second.Disposed.ShouldBeTrue();
        rig.Source.Disposed.ShouldBeFalse();

        first.CompleteAllUpdates();

        first.Disposed.ShouldBeTrue();
        rig.Source.Disposed.ShouldBeTrue();
        rig.Acknowledged.ShouldBe([2, 1]);
    }

    // --- Where the loop resumes ----------------------------------------------
    //
    // A hand-over's task is completed by the compositor on ITS OWN render
    // thread, so everything after the await is on the wrong thread until the
    // loop is posted back. Three things depend on that post: the next
    // UpdateAsync (Avalonia verifies UI-thread access and throws otherwise),
    // the retirement bookkeeping (UI-thread state), and the fault report (a
    // shell handler). None of the three announces the mistake - a compositor
    // call from the wrong thread is an exception in a task nobody awaits, and
    // the other two are a data race - so each has a test.

    [Fact]
    public void The_next_hand_over_waits_for_the_loop_to_be_resumed_on_the_ui_thread()
    {
        var rig = new Rig { HoldResumes = true };
        FakeImage image = rig.Adopt(generation: 1);
        image.Updates.ShouldBe(CompositedFramePump.HandOverDepth);

        // Completed on the compositor's render thread. Issuing the next one
        // from there reaches Compositor.PostServerJob, which verifies UI-thread
        // access.
        image.CompleteUpdate();
        image.Updates.ShouldBe(CompositedFramePump.HandOverDepth);
        rig.PendingResumes.ShouldBe(1);

        rig.ReleaseResumes();
        image.Updates.ShouldBe(CompositedFramePump.HandOverDepth + 1);
    }

    [Fact]
    public void A_retired_import_is_not_disposed_until_the_loop_is_back_on_the_ui_thread()
    {
        var rig = new Rig { HoldResumes = true };
        FakeImage first = rig.Adopt(generation: 1);
        rig.Observe(generation: 2);

        // The bracket is over, so the dispose is safe - and it is UI-thread
        // work, and so is the acknowledgement that frees the producer's
        // resource. Neither may happen from the compositor's thread.
        first.CompleteAllUpdates();
        first.Disposed.ShouldBeFalse();
        rig.Acknowledged.ShouldBeEmpty();

        rig.ReleaseResumes();
        first.Disposed.ShouldBeTrue();
        rig.Acknowledged.ShouldBe([1]);
    }

    [Fact]
    public void A_hand_over_that_throws_reports_its_fault_from_the_ui_thread()
    {
        var rig = new Rig { HoldResumes = true };
        FakeImage image = rig.Adopt(generation: 1);

        image.FailUpdate();
        rig.Faults.ShouldBe(0);
        rig.Pump.IsStalled.ShouldBeFalse();

        rig.ReleaseResumes();
        rig.Faults.ShouldBe(1);
        rig.Pump.IsStalled.ShouldBeTrue();
    }

    [Fact]
    public void Every_hand_over_costs_exactly_one_resume()
    {
        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 1);

        // One per completed hand-over and not one per frame: the post is the
        // loop's own, so a second one per turn would be a second job in the
        // dispatcher for nothing.
        rig.Resumes.ShouldBe(0);
        image.CompleteUpdate();
        rig.Resumes.ShouldBe(1);
        image.CompleteUpdate();
        rig.Resumes.ShouldBe(2);
    }

    /// <summary>
    /// Every hand-over after the first is issued from INSIDE the UI-thread
    /// post, never after it has returned.
    /// </summary>
    /// <remarks>
    /// <b>This is the test the four ordering tests beside it did not amount
    /// to, and the defect it catches shipped.</b> The pump used to await a
    /// TaskCompletionSource that the posted action completed, on the reasoning
    /// that completing it inline would resume the loop inline on the UI thread.
    /// That holds only when the awaiter has already attached: a resume posted
    /// at the highest dispatcher priority usually runs FIRST, so the await saw
    /// an already-completed task and continued synchronously on the thread it
    /// was trying to leave. The next <c>UpdateAsync</c> then called
    /// <c>Dispatcher.VerifyAccess</c> from the compositor's render thread and
    /// the pump reported a fault on a viewport that was working perfectly.
    /// <para>
    /// The four tests beside this one all pass against that code, because they
    /// assert that a resume HAPPENS and in what order, never where the work
    /// after it runs. This asserts the latter, which is the actual invariant.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_hand_over_after_the_first_is_issued_from_inside_the_UI_thread_post()
    {
        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 1);

        for (int i = 0; i < 4; i++)
            image.CompleteUpdate();

        image.Updates.ShouldBeGreaterThan(1, "the loop must have re-issued");

        // The first HandOverDepth are issued by StartLoop, which the shell only
        // ever calls on the UI thread; every later one is the loop re-issuing
        // itself.
        image.UpdateSites.Count.ShouldBe(image.Updates);
        image.UpdateSites.Skip(CompositedFramePump.HandOverDepth).ShouldAllBe(inside => inside,
            "a hand-over issued after the post returned is issued off the UI thread, " +
            "where UpdateAsync verifies access and throws");
    }
}
