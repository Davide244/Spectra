using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The tick's total order, the runaway containment, and the rule that the
/// runtime never writes back into what the author wrote.
/// </summary>
public sealed class EntityWorldTests
{
    private const float Tick = 1f / 60f;

    [Fact]
    public void Two_events_due_at_the_same_time_dispatch_in_the_order_they_were_scheduled()
    {
        // THE DETERMINISM PIN. Equal fire times are the common case, not the
        // corner: every wire on one output at zero delay is due at the same
        // instant. Without the monotonic sequence as the tiebreak, the heap's
        // own array layout decides, which changes with the insertion pattern -
        // so a level would behave differently for a reason nothing on screen
        // could ever explain. The targets are named to defeat an accidental
        // alphabetical order: authored order is beta then alpha.
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "recorder");
        EntityRuntime.Place(scene.Root, "alpha", "recorder");
        EntityRuntime.Place(scene.Root, "beta", "recorder");
        EntityRuntime.Wire(source, "OnGo", "beta", "Ping");
        EntityRuntime.Wire(source, "OnGo", "alpha", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        EntityRuntime.Live(world, source).FireOutput("OnGo");
        world.Tick(Tick);

        log.Count.ShouldBe(2);
        log[0].ShouldStartWith("beta:Ping");
        log[1].ShouldStartWith("alpha:Ping");
    }

    [Fact]
    public void A_later_authored_wire_with_no_delay_still_arrives_before_an_earlier_one_that_waits()
    {
        // Time beats sequence; sequence only breaks a tie in time. Stated as its
        // own test because the pair of rules is what makes the order total.
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "recorder");
        EntityRuntime.Place(scene.Root, "slow", "recorder");
        EntityRuntime.Place(scene.Root, "fast", "recorder");
        EntityRuntime.Wire(source, "OnGo", "slow", "Ping", delay: 0.5f);
        EntityRuntime.Wire(source, "OnGo", "fast", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        EntityRuntime.Live(world, source).FireOutput("OnGo");

        world.Tick(Tick);
        log.Count.ShouldBe(1);
        log[0].ShouldStartWith("fast:Ping");

        for (int i = 0; i < 60; i++)
            world.Tick(Tick);

        log.Count.ShouldBe(2);
        log[1].ShouldStartWith("slow:Ping");
    }

    [Fact]
    public void A_zero_delay_mutual_relay_trips_the_dispatch_budget_and_the_error_names_the_target()
    {
        // Two relays pointed at each other is three clicks to build and is an
        // infinite loop inside ONE tick: the render thread never returns and
        // there is nothing on screen to say why. The budget is what turns that
        // into a bug report.
        var log = new List<string>();
        var logger = new CapturingLogger();
        var scene = new Scene("Entities");
        SceneNode a = EntityRuntime.Place(scene.Root, "relay_a", "relay");
        SceneNode b = EntityRuntime.Place(scene.Root, "relay_b", "relay");
        EntityRuntime.Wire(a, "OnTrigger", "relay_b", "Trigger");
        EntityRuntime.Wire(b, "OnTrigger", "relay_a", "Trigger");

        var world = new EntityWorld(scene, logger, EntityRuntime.Catalog(log))
        {
            MaxDispatchesPerTick = 64,
        };
        world.Activate();
        EntityRuntime.Live(world, a).FireOutput("OnTrigger");

        world.Tick(Tick);

        world.DispatchBudgetTripCount.ShouldBe(1);
        world.LastTickDispatchCount.ShouldBe(64);

        string error = logger.MessagesAt(LogLevel.Error).ShouldHaveSingleItem();
        // The cascade alternates, so with a budget of 64 the event that could
        // not be dispatched is the 65th, which is aimed at relay_b.
        error.ShouldContain("relay_b");
        error.ShouldContain("OnTrigger");

        // The runaway is DROPPED rather than left queued: a level with one bad
        // relay keeps running, and the error is not repeated every tick forever.
        world.DiscardedEventCount.ShouldBeGreaterThan(0);
        world.Tick(Tick);
        world.DispatchBudgetTripCount.ShouldBe(1);
        logger.MessagesAt(LogLevel.Error).Count.ShouldBe(1);
    }

    [Fact]
    public void An_unknown_classname_becomes_a_placeholder_that_keeps_every_keyvalue_and_wire()
    {
        // The whole point of string-typed keyvalues: a map authored against a
        // game this build does not have still loads, still shows in the tree and
        // still re-saves byte for byte.
        var scene = new Scene("Entities");
        SceneNode node = EntityRuntime.Place(scene.Root, "mystery", "func_nothing_here");
        node.Entity!.SetValue("speed", "100");
        node.Entity.SetValue("wait", "4");
        EntityRuntime.Wire(node, "OnFullyOpen", "hall_light", "TurnOn", delay: 0.5f);

        var logger = new CapturingLogger();
        var world = new EntityWorld(scene, logger, new EntityCatalog());
        world.Activate();

        world.Entities.ShouldHaveSingleItem().ShouldBeOfType<PlaceholderEntity>();
        node.Entity.ClassName.ShouldBe("func_nothing_here");
        node.Entity.Keyvalues.Count.ShouldBe(2);
        node.Entity.TryGetValue("speed", out string speed).ShouldBeTrue();
        speed.ShouldBe("100");
        node.Entity.Connections.Count.ShouldBe(1);
        node.Entity.Connections[0].Input.ShouldBe("TurnOn");
    }

    [Fact]
    public void A_placeholder_refuses_inputs_and_says_so_once_per_classname()
    {
        // Once per class, never once per attempt: a missing class is usually a
        // whole game's worth of entities, and a relay pointed at one fires on a
        // timer.
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "func_nothing_here");
        SceneNode target = EntityRuntime.Place(scene.Root, "target", "func_nothing_here");
        EntityRuntime.Wire(source, "OnGo", "target", "Open");

        var logger = new CapturingLogger();
        var world = new EntityWorld(scene, logger, new EntityCatalog());
        world.Activate();

        Entity live = EntityRuntime.Live(world, source);
        live.FireOutput("OnGo");
        world.Tick(Tick);
        live.FireOutput("OnGo");
        world.Tick(Tick);

        IReadOnlyList<string> warnings = logger.MessagesAt(LogLevel.Warning);
        warnings.Count(w => w.Contains("cannot accept")).ShouldBe(1, logger.Describe());
    }

    [Fact]
    public void A_keyvalue_nothing_can_parse_warns_and_leaves_the_default_standing()
    {
        // A loaded map may legally carry a string nothing can read - authored
        // against another build, or hand-edited - and a throw here takes down
        // the load of an entire level over one field.
        var log = new List<string>();
        var logger = new CapturingLogger();
        var scene = new Scene("Entities");
        SceneNode node = EntityRuntime.Place(scene.Root, "mover", "speedster");
        node.Entity!.SetValue("speed", "as fast as it goes");

        var world = new EntityWorld(scene, logger, EntityRuntime.Catalog(log));
        Should.NotThrow(world.Activate);

        var entity = world.Entities.ShouldHaveSingleItem().ShouldBeOfType<SpeedEntity>();
        entity.Speed.ShouldBe(100f);

        string warning = logger.MessagesAt(LogLevel.Warning).ShouldHaveSingleItem();
        warning.ShouldContain("speed");
        warning.ShouldContain("as fast as it goes");
    }

    [Fact]
    public void Exhausting_a_wires_fire_count_never_touches_the_authored_data()
    {
        // Decrementing TimesToFire on EntityData is document corruption that
        // survives to the next save, and it is invisible until somebody diffs
        // the map.
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "recorder");
        EntityRuntime.Place(scene.Root, "target", "recorder");
        EntityRuntime.Wire(source, "OnGo", "target", "Ping", timesToFire: 1);

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        Entity live = EntityRuntime.Live(world, source);

        live.FireOutput("OnGo");
        world.Tick(Tick);
        live.FireOutput("OnGo");
        world.Tick(Tick);

        log.Count.ShouldBe(1);

        EntityOutput output = live.FindOutput("OnGo").ShouldNotBeNull();
        output.FiresLeftAt(0).ShouldBe(0);
        output.LiveWireCount.ShouldBe(0);

        // The authored value, untouched.
        source.Entity!.Connections[0].TimesToFire.ShouldBe(1);
    }

    [Fact]
    public void An_infinite_wire_is_never_decremented()
    {
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "recorder");
        EntityRuntime.Place(scene.Root, "target", "recorder");
        EntityRuntime.Wire(source, "OnGo", "target", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        Entity live = EntityRuntime.Live(world, source);

        for (int i = 0; i < 5; i++)
        {
            live.FireOutput("OnGo");
            world.Tick(Tick);
        }

        log.Count.ShouldBe(5);
        live.FindOutput("OnGo")!.FiresLeftAt(0).ShouldBe(EntityConnection.Infinite);
    }

    [Fact]
    public void Activation_wires_every_target_before_the_first_spawn_runs()
    {
        // The phases, asserted from the outside: an entity that fires an output
        // in OnSpawn reaches a target whose own node comes LATER in traversal
        // order, which a single-pass activate could not do.
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode source = EntityRuntime.Place(scene.Root, "source", "spawner");
        EntityRuntime.Place(scene.Root, "later", "recorder");
        EntityRuntime.Wire(source, "OnSpawned", "later", "Ping");

        EntityCatalog catalog = EntityRuntime.Catalog(log);
        catalog.Add(new EntitySchema("spawner"), () => new SpawnFiringEntity());

        var world = new EntityWorld(scene, new CapturingLogger(), catalog);
        world.Activate();
        world.Tick(Tick);

        log.ShouldHaveSingleItem().ShouldStartWith("later:Ping");
    }

    [Fact]
    public void A_think_runs_at_the_time_it_asked_for_and_a_reschedule_supersedes_the_old_one()
    {
        var scene = new Scene("Entities");
        SceneNode node = EntityRuntime.Place(scene.Root, "ticker", "ticker");

        var catalog = new EntityCatalog();
        catalog.Add(new EntitySchema("ticker"), () => new CountingThinkEntity());
        var world = new EntityWorld(scene, new CapturingLogger(), catalog);
        world.Activate();

        var entity = world.Entities.ShouldHaveSingleItem().ShouldBeOfType<CountingThinkEntity>();
        entity.SetNextThink(1f);
        // The reschedule leaves the first entry in the heap; the serial is what
        // stops it thinking twice.
        entity.SetNextThink(0.5f);

        for (int i = 0; i < 35; i++)
            world.Tick(Tick);

        entity.Thinks.ShouldBe(1);

        for (int i = 0; i < 60; i++)
            world.Tick(Tick);

        entity.Thinks.ShouldBe(1);
    }

    [Fact]
    public void Deactivating_removes_every_entity_and_lets_go_of_the_scene()
    {
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode node = EntityRuntime.Place(scene.Root, "one", "recorder");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();
        world.Index.ShouldNotBeNull();

        world.Deactivate();

        world.IsActive.ShouldBeFalse();
        world.Entities.ShouldBeEmpty();
        world.Index.ShouldBeNull();

        // Unsubscribed: a rename after deactivation must reach nothing.
        Should.NotThrow(() => node.Name = "renamed after");
    }

    private sealed class SpawnFiringEntity : Entity
    {
        protected internal override void OnSpawn() => FireOutput("OnSpawned");
    }

    private sealed class CountingThinkEntity : Entity
    {
        public int Thinks { get; private set; }

        protected internal override void Think() => Thinks++;
    }
}
