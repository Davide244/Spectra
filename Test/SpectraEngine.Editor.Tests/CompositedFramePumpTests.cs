using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Editor.Viewport;
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
        private TaskCompletionSource? _update;

        internal int Updates { get; private set; }

        internal bool Disposed { get; private set; }

        internal uint LastAcquireKey { get; private set; }

        internal uint LastReleaseKey { get; private set; }

        internal bool UpdateInFlight => _update is not null;

        public Task ImportCompleted => _import.Task;

        public Task UpdateAsync(uint acquireKey, uint releaseKey)
        {
            Updates++;
            LastAcquireKey = acquireKey;
            LastReleaseKey = releaseKey;
            _update = new TaskCompletionSource();
            return _update.Task;
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
            TaskCompletionSource? update = _update;
            _update = null;
            update?.TrySetResult();
        }
    }

    private sealed class FakeSource : ICompositedImageSource
    {
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
            var image = new FakeImage();
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
        internal FakeSource Source { get; } = new();

        internal List<int> Acknowledged { get; } = [];

        internal List<nint> Closed { get; } = [];

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
                Closed.Add);
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

        image.Updates.ShouldBe(1);
        image.CompleteUpdate();
        image.Updates.ShouldBe(2);
        image.CompleteUpdate();
        image.Updates.ShouldBe(3);
    }

    [Fact]
    public void A_new_generation_retires_the_old_import_and_acknowledges_it()
    {
        var rig = new Rig();
        FakeImage first = rig.Adopt(generation: 1);

        rig.Adopt(generation: 2);
        first.CompleteUpdate();

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

        first.CompleteUpdate();

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

        first.CompleteUpdate();

        second.Updates.ShouldBe(1);
        second.CompleteUpdate();
        second.Updates.ShouldBe(2);
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
        first.CompleteUpdate();

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

        // The last hand-over completes; nothing schedules another. A minimised
        // window would otherwise copy a full-screen texture per vsync for
        // something nobody can see.
        image.CompleteUpdate();
        image.Updates.ShouldBe(taken);
        rig.Pump.IsPumping.ShouldBeFalse();

        rig.Pump.SetVisible(true);
        image.Updates.ShouldBe(taken + 1);
    }

    [Fact]
    public void Stopping_retires_the_live_import_and_refuses_anything_further()
    {
        var rig = new Rig();
        FakeImage image = rig.Adopt(generation: 3);

        rig.Pump.Stop();
        image.CompleteUpdate();

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

        image.CompleteUpdate();
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

        image.CompleteUpdate();

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
        first.CompleteUpdate();

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

        first.CompleteUpdate();

        first.Disposed.ShouldBeTrue();
        rig.Source.Disposed.ShouldBeTrue();
        rig.Acknowledged.ShouldBe([2, 1]);
    }
}
