using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// What a wire's target name means: the runtime forms, the trailing-star match,
/// duplicates, and the three scene events that keep the answer honest while the
/// graph changes underneath it.
/// </summary>
public sealed class TargetNameIndexTests
{
    private const float Tick = 1f / 60f;

    [Fact]
    public void The_runtime_forms_resolve_to_self_the_activator_and_the_caller()
    {
        // !self and !caller are the same entity IN A CONNECTION, because the
        // entity a wire leaves is the entity firing it. !activator is the one
        // that differs, and it is the reason a door can tell which player opened
        // it once a relay stands between them.
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "recorder");
        SceneNode player = EntityRuntime.Place(scene.Root, "player", "recorder");
        EntityRuntime.Wire(source, "OnGo", "!self", "Ping");
        EntityRuntime.Wire(source, "OnGo", "!caller", "Ping");
        EntityRuntime.Wire(source, "OnGo", "!activator", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        EntityRuntime.Live(world, source).FireOutput("OnGo", EntityRuntime.Live(world, player));
        world.Tick(Tick);

        log.Count.ShouldBe(3);
        log[0].ShouldStartWith("source:Ping");
        log[1].ShouldStartWith("source:Ping");
        log[2].ShouldStartWith("player:Ping");
        // Every delivery carries the same activator, whichever entity it reached.
        log.ShouldAllBe(entry => entry.EndsWith(":player:source", StringComparison.Ordinal));
    }

    [Fact]
    public void A_trailing_star_matches_by_prefix_and_leaves_everything_else_alone()
    {
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "recorder");
        EntityRuntime.Place(scene.Root, "door_north", "recorder");
        EntityRuntime.Place(scene.Root, "door_south", "recorder");
        EntityRuntime.Place(scene.Root, "hall", "recorder");
        EntityRuntime.Wire(source, "OnGo", "door*", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        EntityRuntime.Live(world, source).FireOutput("OnGo");
        world.Tick(Tick);

        log.Count.ShouldBe(2);
        log[0].ShouldStartWith("door_north:Ping");
        log[1].ShouldStartWith("door_south:Ping");
    }

    [Fact]
    public void A_prefix_match_comes_back_in_traversal_order_whatever_order_the_names_appeared_in()
    {
        // Buckets are made in the order names are first seen, which a rename
        // reshuffles: renaming the FIRST door creates its bucket last. Traversal
        // order is not that order, and it is the only total order this engine
        // recognises.
        var scene = new Scene("Entities");
        SceneNode first = EntityRuntime.Place(scene.Root, "door_north", "recorder");
        EntityRuntime.Place(scene.Root, "door_south", "recorder");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog([]));
        world.Activate();

        first.Name = "door_west";

        var results = new List<Entity>();
        world.Index!.Resolve("door*", null, null, null, results);

        results.Count.ShouldBe(2);
        results[0].TargetName.ShouldBe("door_west");
        results[1].TargetName.ShouldBe("door_south");
    }

    [Fact]
    public void Duplicate_names_are_legal_and_firing_at_one_fires_every_match_in_traversal_order()
    {
        // Two nodes may share a name in the tree, so they may share a targetname
        // too: that IS how a level says "all the lights in this room". The
        // deeper lamp is created FIRST and ends up SECOND in traversal order,
        // so creation order cannot be what the answer is coming from.
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "recorder");
        SceneNode group = scene.Root.CreateChild("GroupA");
        SceneNode nested = EntityRuntime.Place(group, "lamp", "recorder");
        nested.Entity!.SetValue("tag", "nested");
        SceneNode top = EntityRuntime.Place(scene.Root, "lamp", "recorder");
        top.Entity!.SetValue("tag", "top");
        EntityRuntime.Wire(source, "OnGo", "lamp", "Ping");

        // Root children become [source, lamp(top), GroupA], so pre-order visits
        // the top lamp before the nested one.
        scene.Root.InsertChild(1, top);

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        EntityRuntime.Live(world, source).FireOutput("OnGo");
        world.Tick(Tick);

        log.Count.ShouldBe(2);
        log[0].ShouldStartWith("top:Ping");
        log[1].ShouldStartWith("nested:Ping");
    }

    [Fact]
    public void A_rename_retargets_the_entity_through_the_scenes_own_event()
    {
        // Without NodeRenamed the index would answer to the old name forever: a
        // rename is neither a membership change nor a reparent, so nothing else
        // reports it.
        var scene = new Scene("Entities");
        SceneNode node = EntityRuntime.Place(scene.Root, "door", "recorder");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog([]));
        world.Activate();

        Resolve(world, "door").Count.ShouldBe(1);

        node.Name = "gate";

        Resolve(world, "door").ShouldBeEmpty();
        Resolve(world, "gate").ShouldHaveSingleItem().Node.ShouldBeSameAs(node);
    }

    [Fact]
    public void A_node_removed_mid_session_leaves_the_index()
    {
        var scene = new Scene("Entities");
        SceneNode node = EntityRuntime.Place(scene.Root, "door", "recorder");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog([]));
        world.Activate();
        Resolve(world, "door").Count.ShouldBe(1);

        scene.Root.RemoveChild(node);

        Resolve(world, "door").ShouldBeEmpty();
        // The id mapping SURVIVES, which is what an undo of the delete needs.
        world.Index!.EntityCount.ShouldBe(1);
    }

    [Fact]
    public void A_node_re_added_under_the_same_id_rejoins_the_index()
    {
        // Undo of a delete rebuilds the node as a NEW object carrying the OLD
        // id. A handler that dropped the mapping on removal and added blindly on
        // arrival would lose this entity permanently, with nothing thrown and
        // nothing logged.
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "recorder");
        SceneNode door = EntityRuntime.Place(scene.Root, "door", "recorder");
        EntityData authored = door.Entity!;
        EntityRuntime.Wire(source, "OnGo", "door", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        Entity live = EntityRuntime.Live(world, door);

        scene.Root.RemoveChild(door);
        Resolve(world, "door").ShouldBeEmpty();

        var restored = new SceneNode("door", door.Id) { Entity = authored };
        scene.Root.AddChild(restored);

        Resolve(world, "door").ShouldHaveSingleItem().ShouldBeSameAs(live);
        // The back-reference followed the node, so the entity reads its name off
        // the object that is actually in the scene.
        live.Node.ShouldBeSameAs(restored);

        EntityRuntime.Live(world, source).FireOutput("OnGo");
        world.Tick(Tick);
        log.ShouldHaveSingleItem().ShouldStartWith("door:Ping");
    }

    [Fact]
    public void An_entity_re_added_under_a_different_name_is_listed_under_that_name_only()
    {
        // The restored node is the author's, not the index's: whatever it is
        // called on arrival is what it answers to.
        var scene = new Scene("Entities");
        SceneNode door = EntityRuntime.Place(scene.Root, "door", "recorder");
        EntityData authored = door.Entity!;

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog([]));
        world.Activate();

        scene.Root.RemoveChild(door);
        scene.Root.AddChild(new SceneNode("gate", door.Id) { Entity = authored });

        Resolve(world, "door").ShouldBeEmpty();
        Resolve(world, "gate").Count.ShouldBe(1);
    }

    [Fact]
    public void An_unknown_target_resolves_to_nothing_rather_than_to_everything()
    {
        var scene = new Scene("Entities");
        EntityRuntime.Place(scene.Root, "door", "recorder");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog([]));
        world.Activate();

        Resolve(world, "hall").ShouldBeEmpty();
        Resolve(world, "").ShouldBeEmpty();
        Resolve(world, "!nonsense").ShouldBeEmpty();
        // A bare star is a legal prefix of length zero.
        Resolve(world, "*").Count.ShouldBe(1);
    }

    private static List<Entity> Resolve(EntityWorld world, string target)
    {
        var results = new List<Entity>();
        world.Index!.Resolve(target, null, null, null, results);
        return results;
    }
}
