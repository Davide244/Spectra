using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;

namespace SpectraEngine.Entities.Tests;

/// <summary>
/// The already-built runtime, run against generated entities.
/// </summary>
/// <remarks>
/// <b>This is the test that proves the generator and the runtime agree, and it is
/// why the runtime was built first.</b> Every other test here reads generated
/// text or counts diagnostics; this one wires a relay to a counter through a real
/// <see cref="EntityWorld"/> and asserts on what a level would actually do. A
/// binder that assigned into the wrong member, a dispatch switch that fell
/// through to <c>base</c>, a schema naming an output nothing fires: none of them
/// is visible in a diff, and all of them fail here.
/// </remarks>
public sealed class BuiltinEntityBehaviourTests
{
    private const float Tick = 1f / 60f;

    [Fact]
    public void A_relay_wired_to_a_counter_fires_OnHitMax_once_when_the_count_arrives_at_its_ceiling()
    {
        // THE TRANSITION PIN. Three triggers into a counter whose ceiling is two:
        // the count arrives at the ceiling on the second and stays there on the
        // third, and OnHitMax fires exactly once. The other reading of "hit max" -
        // fire whenever a change lands at the bound - turns one door into three.
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode relay = EntityRuntime.Place(scene.Root, "relay", "logic_relay");
        SceneNode counter = EntityRuntime.Place(scene.Root, "counter", "math_counter");
        counter.Entity!.SetValue("max", "2");
        EntityRuntime.Place(scene.Root, "sink", "test_recorder");

        EntityRuntime.Wire(relay, LogicRelay.OnTrigger, "counter", "Add", "1");
        EntityRuntime.Wire(counter, MathCounter.OnHitMax, "sink", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        var live = EntityRuntime.Live<LogicRelay>(world, relay);
        var counted = EntityRuntime.Live<MathCounter>(world, counter);

        for (int i = 0; i < 3; i++)
        {
            EntityRuntime.Send(live, "Trigger").ShouldBeTrue();
            // Zero-delay wires are due the instant they are queued, so the whole
            // relay -> counter -> sink cascade drains inside one tick.
            world.Tick(Tick);
        }

        live.TriggerCount.ShouldBe(3);
        counted.Value.ShouldBe(2f);
        log.ShouldBe(["sink:Ping:"]);
    }

    [Fact]
    public void A_counter_that_leaves_its_ceiling_and_returns_announces_the_second_arrival()
    {
        // The other half of the transition rule: firing once per ARRIVAL is not
        // firing once ever. A counter that could only announce its ceiling one
        // time would be a counter no level could reuse.
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode counter = EntityRuntime.Place(scene.Root, "counter", "math_counter");
        counter.Entity!.SetValue("max", "2");
        EntityRuntime.Place(scene.Root, "sink", "test_recorder");
        EntityRuntime.Wire(counter, MathCounter.OnHitMax, "sink", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        var counted = EntityRuntime.Live<MathCounter>(world, counter);

        EntityRuntime.Send(counted, "Add", "2");
        world.Tick(Tick);
        EntityRuntime.Send(counted, "Subtract", "1");
        world.Tick(Tick);
        EntityRuntime.Send(counted, "Add", "1");
        world.Tick(Tick);

        log.Count.ShouldBe(2);
    }

    [Fact]
    public void An_unclamped_counter_announces_neither_bound()
    {
        // A pair of zeros is the unclamped counter, which is why both bounds
        // default to zero and there is no separate clamping switch.
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode counter = EntityRuntime.Place(scene.Root, "counter", "math_counter");
        EntityRuntime.Place(scene.Root, "sink", "test_recorder");
        EntityRuntime.Wire(counter, MathCounter.OnHitMax, "sink", "Max");
        EntityRuntime.Wire(counter, MathCounter.OnHitMin, "sink", "Min");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        var counted = EntityRuntime.Live<MathCounter>(world, counter);
        counted.IsClamped.ShouldBeFalse();

        EntityRuntime.Send(counted, "Add", "5");
        EntityRuntime.Send(counted, "Subtract", "9");
        world.Tick(Tick);

        counted.Value.ShouldBe(-4f);
        log.ShouldBeEmpty();
    }

    [Fact]
    public void A_counter_reports_its_value_on_the_wire_as_the_parameter()
    {
        // The generated fire helper's parameterOverride, and the one thing that
        // makes OutValue worth having: the value travels with it.
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode counter = EntityRuntime.Place(scene.Root, "counter", "math_counter");
        counter.Entity!.SetValue("startvalue", "7");
        EntityRuntime.Place(scene.Root, "sink", "test_recorder");
        EntityRuntime.Wire(counter, MathCounter.OutValue, "sink", "Show");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        var counted = EntityRuntime.Live<MathCounter>(world, counter);
        counted.Value.ShouldBe(7f);

        EntityRuntime.Send(counted, "GetValue");
        world.Tick(Tick);

        log.ShouldBe(["sink:Show:7"]);
    }

    [Fact]
    public void An_Add_with_no_argument_adds_one()
    {
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode counter = EntityRuntime.Place(scene.Root, "counter", "math_counter");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        var counted = EntityRuntime.Live<MathCounter>(world, counter);
        EntityRuntime.Send(counted, "Add");
        world.Tick(Tick);

        counted.Value.ShouldBe(1f);
        counted.RefusedInputCount.ShouldBe(0);
    }

    [Fact]
    public void A_relay_that_starts_disabled_passes_nothing_until_it_is_enabled()
    {
        // The generated keyvalue binder is the subject here: startdisabled is a
        // string on the wire and a bool on the class, and nothing but the emitted
        // switch converts between them.
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode relay = EntityRuntime.Place(scene.Root, "relay", "logic_relay");
        relay.Entity!.SetValue("startdisabled", "1");
        EntityRuntime.Place(scene.Root, "sink", "test_recorder");
        EntityRuntime.Wire(relay, LogicRelay.OnTrigger, "sink", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        var live = EntityRuntime.Live<LogicRelay>(world, relay);
        live.StartDisabled.ShouldBeTrue();
        live.IsEnabled.ShouldBeFalse();

        EntityRuntime.Send(live, "Trigger");
        world.Tick(Tick);
        log.ShouldBeEmpty();

        EntityRuntime.Send(live, "Enable");
        EntityRuntime.Send(live, "Trigger");
        world.Tick(Tick);
        log.ShouldBe(["sink:Ping:"]);
    }

    [Fact]
    public void A_relay_refires_while_an_earlier_trigger_is_still_pending()
    {
        // v1's declared answer, and the one the wire model can implement
        // honestly: the delay lives on the wire, so "pending" is not a state the
        // relay has. Two triggers inside one delay deliver two outputs.
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode relay = EntityRuntime.Place(scene.Root, "relay", "logic_relay");
        EntityRuntime.Place(scene.Root, "sink", "test_recorder");
        EntityRuntime.Wire(relay, LogicRelay.OnTrigger, "sink", "Ping", delay: 0.5f);

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        var live = EntityRuntime.Live<LogicRelay>(world, relay);
        EntityRuntime.Send(live, "Trigger");
        world.Tick(0.25f);
        EntityRuntime.Send(live, "Trigger");
        log.ShouldBeEmpty();

        world.Tick(0.25f);
        log.Count.ShouldBe(1);

        world.Tick(0.25f);
        log.Count.ShouldBe(2);
    }

    [Fact]
    public void A_timer_fires_on_its_interval_and_Enable_restarts_it()
    {
        // The declared semantics, measured: a timer re-enabled part way through an
        // interval waits a WHOLE interval, so the fire that the old schedule would
        // have produced at 1.0 does not happen.
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode timer = EntityRuntime.Place(scene.Root, "timer", "logic_timer");
        timer.Entity!.SetValue("refiretime", "0.5");
        EntityRuntime.Place(scene.Root, "sink", "test_recorder");
        EntityRuntime.Wire(timer, LogicTimer.OnTimer, "sink", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        var live = EntityRuntime.Live<LogicTimer>(world, timer);
        live.RefireInterval.ShouldBe(0.5f);
        live.IsEnabled.ShouldBeTrue();

        // Quarter-second steps, which are exact in binary: an interval test that
        // accumulated 1/60ths would be asserting on the rounding.
        world.Tick(0.25f);
        log.ShouldBeEmpty();

        world.Tick(0.25f);
        log.Count.ShouldBe(1);

        world.Tick(0.25f);
        EntityRuntime.Send(live, "Enable");

        world.Tick(0.25f);
        log.Count.ShouldBe(1, "Enable restarts the interval, so the fire the old schedule held is dropped.");

        world.Tick(0.25f);
        log.Count.ShouldBe(2);
    }

    [Fact]
    public void A_timer_that_starts_disabled_runs_only_once_something_enables_it()
    {
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode timer = EntityRuntime.Place(scene.Root, "timer", "logic_timer");
        timer.Entity!.SetValue("startdisabled", "1");
        timer.Entity!.SetValue("refiretime", "0.5");
        EntityRuntime.Place(scene.Root, "sink", "test_recorder");
        EntityRuntime.Wire(timer, LogicTimer.OnTimer, "sink", "Ping");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        var live = EntityRuntime.Live<LogicTimer>(world, timer);
        live.IsEnabled.ShouldBeFalse();

        world.Tick(0.5f);
        world.Tick(0.5f);
        log.ShouldBeEmpty();

        EntityRuntime.Send(live, "Enable");
        world.Tick(0.5f);
        log.Count.ShouldBe(1);
    }

    [Fact]
    public void A_timer_floors_a_refire_time_that_would_be_due_every_tick()
    {
        // A zero interval is a think that is always due, which the world would
        // dispatch until MaxDispatchesPerTick tripped and reported a runaway with
        // this timer's name on it. Refusing with a throw is not available: the
        // binder assigns straight into the property, and one unreadable field
        // must not take down the load of a whole level.
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode timer = EntityRuntime.Place(scene.Root, "timer", "logic_timer");
        timer.Entity!.SetValue("refiretime", "0");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        EntityRuntime.Live<LogicTimer>(world, timer).RefireInterval.ShouldBe(LogicTimer.MinimumInterval);
    }

    [Fact]
    public void A_keyvalue_the_binder_cannot_read_keeps_the_default_and_is_reported()
    {
        // The generated binder's refusal path: the key is recognised (so nothing
        // reports a missing property) and the value is not, so the default stands
        // and the load continues. A throw here would take down a level over one
        // hand-edited field.
        var log = new List<string>();
        var scene = new Scene("Entities");

        SceneNode timer = EntityRuntime.Place(scene.Root, "timer", "logic_timer");
        timer.Entity!.SetValue("refiretime", "half a second");

        var logger = new CapturingLogger();
        var world = new EntityWorld(scene, logger, EntityRuntime.Catalog(log));
        world.Activate();

        EntityRuntime.Live<LogicTimer>(world, timer).RefireInterval.ShouldBe(1f);
        logger.MessagesAt(LogLevel.Warning).ShouldContain(
            message => message.Contains("refiretime"),
            logger.Describe());
    }

    [Fact]
    public void An_input_no_built_in_class_declares_is_refused_rather_than_swallowed()
    {
        // The generated switch falls through to base, which returns false, which
        // is how EntityWorld knows to report it. A switch that returned true on
        // the default would make every typo in a map a silent no-op.
        var log = new List<string>();
        var scene = new Scene("Entities");
        SceneNode relay = EntityRuntime.Place(scene.Root, "relay", "logic_relay");

        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog(log));
        world.Activate();

        EntityRuntime.Send(EntityRuntime.Live<LogicRelay>(world, relay), "Detonate").ShouldBeFalse();
    }
}
