using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The entity runtime as the engine hosts it: the activation phases seen from
/// outside, the fixed step the tick takes, and the play-mode boundary the world
/// lives inside.
/// </summary>
/// <remarks>
/// <b>The engine loop itself is not reachable from here</b> - it owns a window,
/// a render thread and a graphics context - so what these pin is the seam it
/// drives: <see cref="SceneManager.StartEntityWorld"/> at play entry,
/// <see cref="SceneManager.StopEntityWorld"/> at play exit, and
/// <see cref="SceneManager.OnSceneReplaced"/> from the shell's map open, which
/// runs inside a queued command on that same thread.
/// </remarks>
public sealed class EntityHostingTests
{
    private const float Tick = 1f / 60f;

    [Fact]
    public void Every_entity_spawns_before_any_of_them_activates()
    {
        // THE PHASE ORDER, asserted directly rather than through an effect.
        // Collapse the two walks into one and the first node's activate runs
        // before the last node has spawned, so an entity that depends on
        // another's spawn work sees a half-built world - intermittently,
        // depending on where in the tree the two of them sit.
        var log = new List<string>();
        var scene = new Scene("Entities");
        EntityRuntime.Place(scene.Root, "a", "lifecycle");
        EntityRuntime.Place(scene.Root, "b", "lifecycle");
        EntityRuntime.Place(scene.Root, "c", "lifecycle");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        log.ShouldBe(new[]
        {
            "spawn:a", "spawn:b", "spawn:c",
            "activate:a", "activate:b", "activate:c",
        });

        log.Clear();
        world.Deactivate();
        log.ShouldBe(new[] { "remove:a", "remove:b", "remove:c" });
    }

    [Fact]
    public void A_tick_advances_world_time_by_exactly_the_step_it_is_given()
    {
        // The engine ticks this from inside the fixed loop with the FIXED delta,
        // never the frame delta: the heap is keyed on absolute fire times, so a
        // world advanced by whatever the last frame happened to cost makes when
        // a door opens a function of how fast the machine is.
        var scene = new Scene("Entities");
        var world = new EntityWorld(scene, new CapturingLogger(), new EntityCatalog());
        world.Activate();

        for (int i = 0; i < 60; i++)
            world.Tick(Tick);

        world.Time.ShouldBe(60f * Tick, 1e-4f);
    }

    [Fact]
    public void The_play_mode_boundary_builds_the_runtime_and_takes_it_away_again()
    {
        var log = new List<string>();
        SceneManager manager = Hosted(log);
        Scene scene = manager.ActiveScene.ShouldNotBeNull();
        EntityRuntime.Place(scene.Root, "door", "lifecycle");

        // Nothing runs in an editing session: the instances are a projection of
        // the authored data, and the data is what the editor edits.
        manager.EntityWorld.ShouldBeNull();

        manager.StartEntityWorld();

        EntityWorld world = manager.EntityWorld.ShouldNotBeNull();
        world.IsActive.ShouldBeTrue();
        world.Entities.ShouldHaveSingleItem().TargetName.ShouldBe("door");
        log.ShouldBe(new[] { "spawn:door", "activate:door" });

        manager.StopEntityWorld();

        manager.EntityWorld.ShouldBeNull();
        world.IsActive.ShouldBeFalse();
        log[^1].ShouldBe("remove:door");
    }

    [Fact]
    public void Opening_a_map_while_play_mode_is_running_tears_the_entity_world_down()
    {
        // THE LEAK THIS EXISTS FOR. The shell's map open runs inside a queued
        // command on the render thread and replaces the live scene's graph in
        // place; a world left standing across that holds entities bound to
        // nodes that are no longer in any scene, and the next test shows what
        // the index then does with them.
        var log = new List<string>();
        SceneManager manager = Hosted(log);
        Scene scene = manager.ActiveScene.ShouldNotBeNull();
        SceneNode authored = EntityRuntime.Place(scene.Root, "door", "lifecycle");

        manager.StartEntityWorld();
        EntityWorld world = manager.EntityWorld.ShouldNotBeNull();

        // What EditorSession.OpenMap does, in its order: the runtime lets go
        // first, then the graph is replaced.
        manager.OnSceneReplaced();
        MapSceneBinder.ApplyTo(MapSceneBinder.FromScene(scene), scene);

        manager.EntityWorld.ShouldBeNull();
        world.IsActive.ShouldBeFalse();
        world.Entities.ShouldBeEmpty();
        world.Index.ShouldBeNull();
        log[^1].ShouldBe("remove:door");

        // The reloaded node carries the SAME id and is a different object. A
        // world that was still listening would have been handed it.
        SceneNode reloaded = scene.Root.Children[^1];
        reloaded.Id.ShouldBe(authored.Id);
        ReferenceEquals(reloaded, authored).ShouldBeFalse();
    }

    [Fact]
    public void A_world_kept_across_a_map_load_is_rebound_onto_the_new_nodes()
    {
        // The hazard itself, pinned so that the teardown above cannot be
        // deleted as housekeeping. Node ids survive a map load - that is what
        // makes commands and undo work across one - so the target-name index's
        // own NodeAdded handler recognises every stale entity and repoints it
        // at the fresh node: last map's think times, fire counts and wiring,
        // running over this map's scene, with nothing reporting it.
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode authored = EntityRuntime.Place(scene.Root, "door", "lifecycle");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        Entity entity = world.Entities.ShouldHaveSingleItem();

        MapSceneBinder.ApplyTo(MapSceneBinder.FromScene(scene), scene);

        SceneNode reloaded = scene.Root.Children.ShouldHaveSingleItem();
        ReferenceEquals(reloaded, authored).ShouldBeFalse();
        ReferenceEquals(entity.Node, reloaded).ShouldBeTrue();

        // And it never spawned into the new scene, which is the part that makes
        // this a silent fault rather than a visible one.
        log.ShouldBe(new[] { "spawn:door", "activate:door" });
    }

    // A scene manager with a real active scene, built the way the editor shell
    // boots one. The catalogue is scoped rather than left to
    // EntityCatalog.Shared, which freezes on its first read and would make test
    // order load-bearing.
    private static SceneManager Hosted(List<string> log)
    {
        var manager = new SceneManager(NullLogger<SceneManager>.Instance)
        {
            Startup = StartupSceneKind.Baseplate,
            EntityCatalog = EntityRuntime.Catalog(log),
        };

        manager.LoadStartupScene(new FakeRenderer(), new AssetManager(NullLogger<AssetManager>.Instance));
        return manager;
    }
}
