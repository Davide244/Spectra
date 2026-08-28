using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Hosting;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The contract an editor shell drives the engine through: queue work, ask it
/// to stop, and hear about finished frames.
/// </summary>
/// <remarks>
/// <b>Every claim here is about a thread boundary</b>, which is why the suite is
/// worth having at all. The render thread owns the scene; a UI thread owns none
/// of it and must never touch it. What is being pinned is that the queue
/// actually defers, that the snapshot a UI thread holds cannot change under it,
/// and that the structural log is complete or says it is not.
/// </remarks>
public sealed class EngineHostTests
{
    private static EngineHost NewHost() => new(NullLogger.Instance);

    // --- Commands ------------------------------------------------------------

    [Fact]
    public void A_queued_command_does_not_run_until_the_engine_drains_it()
    {
        var scene = new Scene("Host");
        EngineHost host = NewHost();
        bool ran = false;

        host.EnqueueCommand(_ => ran = true);

        // The whole point: enqueueing is what a UI thread does, and it must not
        // reach into the scene on the caller's thread.
        ran.ShouldBeFalse();
        host.PendingCommandCount.ShouldBe(1);

        host.DrainCommands(scene);

        ran.ShouldBeTrue();
        host.PendingCommandCount.ShouldBe(0);
    }

    [Fact]
    public void A_command_receives_the_scene_that_is_live_when_it_runs()
    {
        // Passed in rather than captured, so a command queued before a scene
        // swap edits the scene that is actually on screen when it executes.
        var first = new Scene("First");
        var second = new Scene("Second");
        EngineHost host = NewHost();

        Scene? seen = null;
        host.EnqueueCommand(s => seen = s);
        host.DrainCommands(second);

        seen.ShouldBeSameAs(second);
        seen.ShouldNotBeSameAs(first);
    }

    [Fact]
    public void Commands_queued_with_no_scene_are_held_rather_than_dropped()
    {
        // Work a user asked for must not evaporate because it arrived during a
        // load. Holding is the only answer that is not a silent loss.
        EngineHost host = NewHost();
        int ran = 0;
        host.EnqueueCommand(_ => ran++);

        host.DrainCommands(scene: null);

        ran.ShouldBe(0);
        host.PendingCommandCount.ShouldBe(1);

        host.DrainCommands(new Scene("Late"));
        ran.ShouldBe(1);
    }

    [Fact]
    public void A_command_that_throws_is_logged_and_the_frame_continues()
    {
        // One bad command from a shell must not take the render thread down and
        // the whole editor with it.
        var scene = new Scene("Host");
        EngineHost host = NewHost();
        bool afterRan = false;

        host.EnqueueCommand(_ => throw new InvalidOperationException("shell bug"));
        host.EnqueueCommand(_ => afterRan = true);

        Should.NotThrow(() => host.DrainCommands(scene));
        afterRan.ShouldBeTrue();
    }

    [Fact]
    public void A_flood_of_commands_cannot_starve_the_frame()
    {
        var scene = new Scene("Host");
        EngineHost host = NewHost();
        for (int i = 0; i < 500; i++)
            host.EnqueueCommand(_ => { });

        host.DrainCommands(scene, maxPerFrame: 100);

        host.PendingCommandCount.ShouldBe(400);
    }

    [Fact]
    public async Task Commands_may_be_queued_from_another_thread_while_the_engine_drains()
    {
        // The real shape: a UI thread posting while the render thread drains.
        var scene = new Scene("Host");
        EngineHost host = NewHost();
        int ran = 0;
        const int Total = 2000;

        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < Total; i++)
                host.EnqueueCommand(_ => Interlocked.Increment(ref ran));
        });

        while (!producer.IsCompleted || host.PendingCommandCount > 0)
            host.DrainCommands(scene);

        await producer;
        host.DrainCommands(scene);

        ran.ShouldBe(Total);
    }

    // --- Shutdown ------------------------------------------------------------

    [Fact]
    public void Requesting_shutdown_is_visible_and_idempotent()
    {
        EngineHost host = NewHost();
        host.ShutdownRequested.ShouldBeFalse();

        host.RequestShutdown();
        host.RequestShutdown();

        host.ShutdownRequested.ShouldBeTrue();
    }

    // --- Snapshots -----------------------------------------------------------

    [Fact]
    public void A_snapshot_is_published_on_an_interval_not_every_frame()
    {
        EngineHost host = NewHost();
        host.SnapshotInterval = TimeSpan.FromMilliseconds(100);

        var published = new List<FrameSnapshot>();
        host.FrameCompleted += published.Add;

        // Ten frames inside one interval: the engine runs at hundreds of frames
        // a second and no panel refreshes that fast, so publishing per frame
        // would put real garbage on the render thread for a UI that discards it.
        for (int i = 0; i < 10; i++)
            host.PublishFrame(TimeSpan.FromMilliseconds(i), Build);

        published.Count.ShouldBe(1);

        host.PublishFrame(TimeSpan.FromMilliseconds(500), Build);
        published.Count.ShouldBe(2);
    }

    [Fact]
    public void Structural_news_does_not_wait_for_the_interval()
    {
        // A tree view a third of a second behind a delete reads as a broken
        // editor, so a frame with changes publishes regardless of the clock.
        var scene = new Scene("Host");
        EngineHost host = NewHost();
        host.SnapshotInterval = TimeSpan.FromSeconds(10);
        host.ObserveScene(scene);
        host.PublishFrame(TimeSpan.Zero, Build); // clears the scene-swap overflow

        var published = new List<FrameSnapshot>();
        host.FrameCompleted += published.Add;

        host.PublishFrame(TimeSpan.FromMilliseconds(1), Build).ShouldBeNull();
        published.ShouldBeEmpty();

        scene.Root.CreateChild("New");
        host.PublishFrame(TimeSpan.FromMilliseconds(2), Build).ShouldNotBeNull();
        published.Count.ShouldBe(1);
        published[0].Changes.Count.ShouldBe(1);
        published[0].Changes[0].Kind.ShouldBe(SceneChangeKind.Added);
    }

    [Fact]
    public void The_last_snapshot_is_available_to_a_shell_that_attaches_late()
    {
        EngineHost host = NewHost();
        host.SnapshotInterval = TimeSpan.Zero;
        host.LastSnapshot.ShouldBeSameAs(FrameSnapshot.Empty);

        host.PublishFrame(TimeSpan.FromMilliseconds(1), Build);

        host.LastSnapshot.ShouldNotBeSameAs(FrameSnapshot.Empty);
        host.LastSnapshot.FrameNumber.ShouldBe(1);
    }

    [Fact]
    public void A_published_snapshot_never_changes_afterwards()
    {
        // The property a UI thread depends on: it may hold a snapshot for as
        // long as it likes and read it from its own thread.
        var scene = new Scene("Host");
        EngineHost host = NewHost();
        host.SnapshotInterval = TimeSpan.Zero;
        host.ObserveScene(scene);

        scene.Root.CreateChild("A");
        FrameSnapshot first = host.PublishFrame(TimeSpan.FromMilliseconds(1), Build)!;
        int countAtPublish = first.Changes.Count;

        // Everything the engine does afterwards, including filling the next
        // batch, must leave the handed-over one alone.
        scene.Root.CreateChild("B");
        scene.Root.CreateChild("C");
        host.PublishFrame(TimeSpan.FromMilliseconds(2), Build);

        first.Changes.Count.ShouldBe(countAtPublish);
    }

    // --- The change log ------------------------------------------------------

    [Fact]
    public void A_reparent_is_reported_even_though_it_raises_no_membership_event()
    {
        // The one structural change the membership events cannot see: nothing
        // entered or left the graph, so a tree view fed only Added/Removed
        // desynchronises the first time somebody drags a node in it.
        var scene = new Scene("Host");
        SceneNode a = scene.Root.CreateChild("A");
        SceneNode b = scene.Root.CreateChild("B");

        EngineHost host = NewHost();
        host.SnapshotInterval = TimeSpan.Zero;
        host.ObserveScene(scene);
        host.PublishFrame(TimeSpan.Zero, Build);

        a.AddChild(b);

        FrameSnapshot snapshot = host.PublishFrame(TimeSpan.FromMilliseconds(1), Build)!;
        snapshot.Changes.Count.ShouldBe(1);

        SceneChange change = snapshot.Changes[0];
        change.Kind.ShouldBe(SceneChangeKind.Reparented);
        change.NodeId.ShouldBe(b.Id);
        change.ParentId.ShouldBe(a.Id);
        change.SiblingIndex.ShouldBe(0);
    }

    [Fact]
    public void A_reorder_under_the_same_parent_is_reported_too()
    {
        // Sibling index is traversal order, which is the static world's
        // placement-slot order, so a reorder is a real structural change.
        var scene = new Scene("Host");
        scene.Root.CreateChild("A");
        SceneNode b = scene.Root.CreateChild("B");

        EngineHost host = NewHost();
        host.SnapshotInterval = TimeSpan.Zero;
        host.ObserveScene(scene);
        host.PublishFrame(TimeSpan.Zero, Build);

        scene.Root.InsertChild(0, b);

        FrameSnapshot snapshot = host.PublishFrame(TimeSpan.FromMilliseconds(1), Build)!;
        snapshot.Changes.Count.ShouldBe(1);
        snapshot.Changes[0].Kind.ShouldBe(SceneChangeKind.Reparented);
        snapshot.Changes[0].SiblingIndex.ShouldBe(0);
    }

    [Fact]
    public void An_attached_subtree_is_reported_once_per_node_parents_first()
    {
        var scene = new Scene("Host");
        EngineHost host = NewHost();
        host.SnapshotInterval = TimeSpan.Zero;
        host.ObserveScene(scene);
        host.PublishFrame(TimeSpan.Zero, Build);

        var group = new SceneNode("Group");
        SceneNode child = group.CreateChild("Child");
        child.CreateChild("Grandchild");
        scene.Root.AddChild(group);

        FrameSnapshot snapshot = host.PublishFrame(TimeSpan.FromMilliseconds(1), Build)!;
        snapshot.Changes.Count.ShouldBe(3);
        snapshot.Changes[0].Name.ShouldBe("Group");
        snapshot.Changes[1].Name.ShouldBe("Child");
        snapshot.Changes[2].Name.ShouldBe("Grandchild");
        snapshot.ChangesOverflowed.ShouldBeFalse();
    }

    [Fact]
    public void Transform_changes_are_not_logged()
    {
        // They fire for every moved node every frame an animation runs, and a
        // tree view does not show positions. An inspector reads the value, not
        // the history.
        var scene = new Scene("Host");
        SceneNode node = scene.Root.CreateChild("Mover");

        EngineHost host = NewHost();
        host.SnapshotInterval = TimeSpan.Zero;
        host.ObserveScene(scene);
        host.PublishFrame(TimeSpan.Zero, Build);

        for (int i = 1; i <= 100; i++)
            node.LocalPosition = new Vector3(i, 0f, 0f);

        host.PublishFrame(TimeSpan.FromMilliseconds(1), Build)!.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void An_overflowing_log_says_so_instead_of_truncating_silently()
    {
        // A tree view fed a partial log looks correct and is wrong, which is
        // worse than one told to start over.
        var scene = new Scene("Host");
        var log = new SceneChangeLog(capacity: 8);
        log.Observe(scene);
        log.Drain(); // clear the scene-swap overflow

        for (int i = 0; i < 20; i++)
            scene.Root.CreateChild($"N{i}");

        (IReadOnlyList<SceneChange> changes, bool overflowed) = log.Drain();
        changes.Count.ShouldBe(8);
        overflowed.ShouldBeTrue();

        // ...and the flag clears with the batch, so the next one is trusted again.
        log.Drain().Overflowed.ShouldBeFalse();
    }

    [Fact]
    public void Swapping_the_observed_scene_reports_an_overflow_rather_than_a_fake_diff()
    {
        // The shell's view of the old graph is worthless either way, and
        // enumerating a whole scene to say so would be the rebuild it is trying
        // to avoid, done twice.
        var first = new Scene("First");
        var log = new SceneChangeLog();
        log.Observe(first);
        log.Drain();

        first.Root.CreateChild("Doomed");
        log.Observe(new Scene("Second"));

        (IReadOnlyList<SceneChange> changes, bool overflowed) = log.Drain();
        changes.ShouldBeEmpty();
        overflowed.ShouldBeTrue();
    }

    [Fact]
    public void A_detached_scene_stops_being_reported()
    {
        var scene = new Scene("Host");
        var log = new SceneChangeLog();
        log.Observe(scene);
        log.Observe(null);
        log.Drain();

        scene.Root.CreateChild("Ignored");

        log.Drain().Changes.ShouldBeEmpty();
    }

    private static FrameSnapshot Build(FrameSnapshotBuilder builder) => new()
    {
        FrameNumber = builder.FrameNumber,
        Changes = builder.Changes,
        ChangesOverflowed = builder.ChangesOverflowed,
    };
}
