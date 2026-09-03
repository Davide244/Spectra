using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SpectraEngine.Entities.Tests;

/// <summary>
/// The whole entity slice as one claim: a bundle on disk loads, its wiring runs,
/// its entities fire, and saving it afterwards writes the same bytes back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other entity test proves one link; this one proves the chain.</b> The
/// codec tests read and write documents nothing ever activates, the runtime tests
/// activate scenes nothing ever wrote, and the generator tests compile classes
/// nothing ever places. A slice can pass all three and still be broken at the
/// joins - a binder that drops a wire, a runtime that edits the data it was built
/// from - and neither half's own suite is looking at the join.
/// </para>
/// <para>
/// <b>The fixture is a little machine rather than a specimen.</b> A
/// <c>logic_timer</c> starts itself, drives a <c>logic_relay</c>, which counts
/// into a <c>math_counter</c>, which switches the relay off again when it reaches
/// its ceiling. That shape is chosen because it is the smallest one that runs on
/// its own (nothing here has a mouse or a trigger volume to press) and because
/// every link in it is observable from outside: a fire count, a trigger count, a
/// value, and an enabled bit.
/// </para>
/// <para>
/// <b>The no-writeback pin is the reason the counter has a ceiling and the
/// shutdown wire has <c>times: 1</c>.</b> Those two numbers are the ones the
/// runtime is most tempted to write back into the document - a clamp and a
/// decrementing fire budget - and both live on the runtime copy. If either
/// reached the authored data, the second play session would behave differently
/// from the first, which is asserted directly, and the saved bytes would differ,
/// which is asserted separately. A test that only compared bytes could pass
/// against a world where nothing fired at all.
/// </para>
/// </remarks>
public sealed class EntityMapEndToEndTests
{
    private const float Tick = 1f / 60f;

    /// <summary>Two seconds of ticks: long enough for the machine to run down.</summary>
    private const int TwoSeconds = 120;

    /// <summary>
    /// The fixture map, hand-authored so this file pins the shape rather than
    /// describing whatever the writer currently produces.
    /// </summary>
    /// <remarks>
    /// <b>It carries nothing the scene projection cannot rebuild.</b> There is no
    /// <c>editor</c> member, no <c>scene.spawn</c> and no preserved unknown,
    /// because <c>MapSceneBinder.FromScene</c> builds a fresh document out of the
    /// graph and those three live only on the document. Preserving them is the
    /// codec's claim and is tested where the codec is; mixing it in here would
    /// make a known limit of the scene projection look like a writeback.
    /// </remarks>
    private const string RelayFixture = """
        {
          "spectramap": 3,
          "minimumReadableVersion": 3,
          "engine": "1.0.0",
          "scene": {
            "name": "RelayFixture"
          },
          "nodes": [
            {
              "id": "7c0f4a21-9d38-4b52-8e61-0a5c2d7f3b90",
              "name": "Floor",
              "transform": {"p":[0,-1,0]},
              "brush": {
                "planes": [
                  [1,0,0,-8],
                  [-1,0,0,-8],
                  [0,1,0,-1],
                  [0,-1,0,-1],
                  [0,0,1,-8],
                  [0,0,-1,-8]
                ],
                "faces": [
                  {},
                  {},
                  {},
                  {},
                  {},
                  {}
                ]
              },
              "children": []
            },
            {
              "id": "1b8e6c04-52a7-4f13-9c80-6d3f1e7a20b5",
              "name": "Metronome",
              "transform": {"p":[0,0,0]},
              "entity": {
                "class": "logic_timer",
                "keys": {"refiretime":"0.25"},
                "outputs": [
                  {"output":"OnTimer","target":"Gate","input":"Trigger"}
                ]
              },
              "children": []
            },
            {
              "id": "d4a91f37-6b20-4e85-a1cd-38027f9b4e16",
              "name": "Gate",
              "transform": {"p":[0,0,0]},
              "entity": {
                "class": "logic_relay",
                "keys": {"startdisabled":"0"},
                "outputs": [
                  {"output":"OnTrigger","target":"Tally","input":"Add","param":"2"}
                ]
              },
              "children": []
            },
            {
              "id": "0e5d2a68-4c71-4930-bf24-91a6c80d5f73",
              "name": "Tally",
              "transform": {"p":[0,0,0]},
              "entity": {
                "class": "math_counter",
                "keys": {"startvalue":"0","min":"0","max":"6"},
                "outputs": [
                  {"output":"OnHitMax","target":"Gate","input":"Disable","delay":0.1,"times":1}
                ]
              },
              "children": []
            }
          ]
        }
        """;

    // -- the document --------------------------------------------------------

    [Fact]
    public void The_fixture_map_survives_a_read_and_a_write_byte_for_byte()
    {
        // Pins the fixture's own shape first, so that every failure below is a
        // failure of the thing that test is about rather than of the fixture.
        byte[] source = Utf8(RelayFixture);

        Same(source, MapWriter.Write(MapReader.Read(source)));
    }

    [Fact]
    public void The_fixture_map_binds_to_a_scene_and_projects_back_to_the_same_bytes()
    {
        // The lossy half of the round trip, asserted BEFORE anything plays. Its
        // job is to make the no-writeback pin below mean what it says: without
        // it, a projection that quietly dropped a wire would produce identical
        // bytes before and after play, and the pin would pass while proving
        // nothing at all.
        byte[] source = Utf8(RelayFixture);
        Scene scene = Load(source);

        Same(source, MapWriter.Write(MapSceneBinder.FromScene(scene)));
    }

    // -- the machine ---------------------------------------------------------

    [Fact]
    public void The_wired_map_runs_its_own_machine_when_it_is_played()
    {
        Scene scene = Load(Utf8(RelayFixture));

        Outcome outcome = Play(scene, TwoSeconds);

        // Every link named: the timer ran on its own, the relay passed exactly
        // the three triggers that fit before the shutdown wire landed, the
        // counter took the wire's "2" rather than the default 1 and stopped at
        // its ceiling, and the counter's own OnHitMax reached back and switched
        // the relay off.
        outcome.TimerFires.ShouldBeGreaterThanOrEqualTo(4,
            "a logic_timer starts itself in OnActivate and refires forever");
        outcome.RelayTriggers.ShouldBe(3,
            "the fourth timer fire lands after OnHitMax's 0.1 s wire has disabled the relay");
        outcome.Count.ShouldBe(6f,
            "three triggers at the wire's param of 2 reach the counter's max of 6 and clamp there");
        outcome.RelayEnabled.ShouldBeFalse("OnHitMax fires once, on arrival, and its wire sends Disable");
    }

    [Fact]
    public void An_unresolved_target_is_the_only_thing_that_stops_the_machine()
    {
        // The negative control for the test above. Everything it asserts is a
        // consequence of one wire resolving to one live entity, so this breaks
        // exactly that and nothing else: rename the counter, and the numbers all
        // move. Without it, a Play() that silently activated nothing would still
        // satisfy a "the count did not change" reading of the pin.
        Scene scene = Load(Utf8(RelayFixture));
        Find(scene, "Tally").Name = "TallyRenamed";

        Outcome outcome = Play(scene, TwoSeconds, counterName: "TallyRenamed");

        outcome.Count.ShouldBe(0f, "Add never arrives, because 'Tally' now names nothing");
        outcome.RelayEnabled.ShouldBeTrue("and OnHitMax therefore never fires either");
        outcome.RelayTriggers.ShouldBeGreaterThan(3, "so nothing ever switches the relay off");
    }

    // -- the pin -------------------------------------------------------------

    [Fact]
    public void Two_play_sessions_leave_the_authored_document_byte_identical()
    {
        // THE STRUCTURAL PROOF of the architecture's central claim: the runtime
        // builds instances FROM EntityData and never writes back, which is what
        // makes stopping a session need no state capture and no diff-restore.
        //
        // Twice, because once cannot tell "nothing was written" from "the first
        // session's writes happen to reproduce the input". The two outcomes are
        // compared to each other for the same reason: this fixture's counter
        // clamps and its shutdown wire has times: 1, so a runtime that decremented
        // the AUTHORED wire would leave the second session unable to switch its
        // relay off, and the bytes would still be identical because times: 1
        // would simply have become times: 0 - a value the writer still emits.
        byte[] source = Utf8(RelayFixture);
        Scene scene = Load(source);

        Outcome first = Play(scene, TwoSeconds);
        Outcome second = Play(scene, TwoSeconds);

        second.ShouldBe(first, "a second play session is a second run of the same document");
        first.Count.ShouldBe(6f, "and both of them really did run");

        Same(source, MapWriter.Write(MapSceneBinder.FromScene(scene)));
    }

    [Fact]
    public void A_bundle_that_has_been_played_twice_is_not_rewritten_when_it_is_saved()
    {
        // The same claim as a person experiences it: open the folder, press play,
        // press stop, press play, press stop, press Ctrl+S, and git reports
        // nothing. MapBundle.Save skips a file whose bytes have not changed, so a
        // false return IS the observable form of the pin - and the timestamp not
        // moving is what keeps a watcher, a cook and a diff quiet.
        string bundle = Path.Combine(
            Path.GetTempPath(), $"spectra_entity_map_{Guid.NewGuid():N}{MapFormat.BundleExtension}");
        try
        {
            Directory.CreateDirectory(bundle);
            File.WriteAllBytes(MapBundle.DocumentPath(bundle), Utf8(RelayFixture));
            DateTime written = File.GetLastWriteTimeUtc(MapBundle.DocumentPath(bundle));

            var scene = new Scene("Empty");
            MapSceneBinder.ApplyTo(MapBundle.Load(bundle), scene);
            Play(scene, TwoSeconds);
            Play(scene, TwoSeconds);

            MapBundle.Save(bundle, MapSceneBinder.FromScene(scene))
                .ShouldBeFalse("a save with no edit in it must not touch the file");
            File.GetLastWriteTimeUtc(MapBundle.DocumentPath(bundle)).ShouldBe(written);
            Same(Utf8(RelayFixture), File.ReadAllBytes(MapBundle.DocumentPath(bundle)));
        }
        finally
        {
            if (Directory.Exists(bundle))
                Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public void Playing_edits_the_runtime_wires_and_never_the_authored_ones()
    {
        // The mechanism the byte comparison catches, named directly, because the
        // byte comparison alone would not say WHICH member had been rewritten.
        // times-to-fire is the one authored value a running world decrements, and
        // decrementing the stored copy is corruption that survives to the next
        // save and stays invisible until somebody diffs a map.
        Scene scene = Load(Utf8(RelayFixture));
        EntityData tally = Find(scene, "Tally").Entity.ShouldNotBeNull();

        Play(scene, TwoSeconds);

        tally.Connections.Count.ShouldBe(1);
        tally.Connections[0].TimesToFire.ShouldBe(1, "the wire fired, and the authored budget is untouched");

        // The keyvalues are the other half: the counter clamped its value to 6
        // and the relay switched itself off, and neither is a fact about the
        // document.
        tally.TryGetValue("startvalue", out string startValue).ShouldBeTrue();
        startValue.ShouldBe("0");
        Find(scene, "Gate").Entity!.TryGetValue("startdisabled", out string startDisabled).ShouldBeTrue();
        startDisabled.ShouldBe("0");
    }

    // -- scaffolding ---------------------------------------------------------

    /// <summary>What one play session did, as four numbers taken at Stop.</summary>
    /// <remarks>
    /// A record so two sessions can be compared with one assertion. Every member
    /// is read off a live entity before <c>Deactivate</c>, because after it there
    /// are no instances left to read - which is the whole point of the design
    /// being pinned here.
    /// </remarks>
    private readonly record struct Outcome(int TimerFires, int RelayTriggers, float Count, bool RelayEnabled);

    private static Scene Load(byte[] document)
    {
        var scene = new Scene("Empty");
        MapSceneBinder.ApplyTo(MapReader.Read(document), scene);
        return scene;
    }

    /// <summary>Enters play mode, runs <paramref name="ticks"/> fixed steps, and leaves.</summary>
    /// <remarks>
    /// A private catalogue rather than <see cref="EntityCatalog.Shared"/>, for the
    /// reason <c>EntityRuntime</c> states: the shared one freezes on its first
    /// read, so the first test to run would freeze it for every test after it.
    /// The classes go in through their own GENERATED schemas, so what runs here is
    /// exactly what the generator produced.
    /// </remarks>
    private static Outcome Play(Scene scene, int ticks, string counterName = "Tally")
    {
        var world = new EntityWorld(scene, new CapturingLogger(), EntityRuntime.Catalog([]));
        world.Activate();

        for (int i = 0; i < ticks; i++)
            world.Tick(Tick);

        var outcome = new Outcome(
            EntityRuntime.Live<LogicTimer>(world, Find(scene, "Metronome")).FireCount,
            EntityRuntime.Live<LogicRelay>(world, Find(scene, "Gate")).TriggerCount,
            EntityRuntime.Live<MathCounter>(world, Find(scene, counterName)).Value,
            EntityRuntime.Live<LogicRelay>(world, Find(scene, "Gate")).IsEnabled);

        world.Deactivate();
        return outcome;
    }

    private static SceneNode Find(Scene scene, string name)
    {
        foreach (SceneNode node in scene.Root.Traverse())
        {
            if (node.Name == name)
                return node;
        }

        throw new Xunit.Sdk.XunitException($"The fixture has no node named '{name}'.");
    }

    private static byte[] Utf8(string text) =>
        Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n") + "\n");

    private static void Same(byte[] expected, byte[] actual)
    {
        if (expected.AsSpan().SequenceEqual(actual)) return;

        string want = Encoding.UTF8.GetString(expected);
        string got = Encoding.UTF8.GetString(actual);
        int at = 0;
        while (at < want.Length && at < got.Length && want[at] == got[at]) at++;

        throw new Xunit.Sdk.XunitException(
            $"The document changed on the way through, first at character {at}.\n"
            + $"--- expected ---\n{want}\n--- actual ---\n{got}");
    }
}
