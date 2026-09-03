using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Maths;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editing.Hosting;
using SpectraEngine.Editing.Undo;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Wiring an entity's outputs to another entity's inputs: the command, the
/// editor verb over it, and the map bytes underneath.
/// </summary>
/// <remarks>
/// <para>
/// <b>Whole arrays, because a connection has no identity a delta could
/// name.</b> Two wires on one entity may be identical in every field, and an
/// insert shifts every index after it, so "remove the wire at 2" replayed
/// against a list an undo has already changed removes the wrong one. What the
/// tests below defend is that an undo puts the exact list back IN ORDER, that a
/// run of edits is one history entry, and that the file comes back byte for
/// byte afterwards - which is the claim the whole map format rests on.
/// </para>
/// </remarks>
public sealed class EntityWiringEditTests
{
    private static EntityConnection Wire(
        string output, string target, string input = "Trigger",
        string param = "", float delay = 0f, int times = EntityConnection.Infinite) =>
        new(output, target, input, param, delay, times);

    private static SceneNode Placed(string name, string className, params EntityConnection[] wires)
    {
        var data = new EntityData(className);
        data.Connections.AddRange(wires);
        return new SceneNode(name) { Entity = data };
    }

    private sealed class Rig
    {
        public Rig(params SceneNode[] nodes)
        {
            Scene = new Scene("entities");
            foreach (SceneNode node in nodes)
                Scene.Root.AddChild(node);

            Undo = new UndoStack(Scene);
        }

        public Scene Scene { get; }
        public UndoStack Undo { get; }
    }

    private static IReadOnlyList<EntityConnection> Wiring(SceneNode node) =>
        node.Entity!.Connections;

    // --- the command ---------------------------------------------------------

    [Fact]
    public void Undo_restores_the_exact_list_in_the_authored_order()
    {
        // ORDER, not merely membership. Connection order round-trips through
        // map.json, so an undo that put the same three wires back in a
        // different order would leave a file that is valid, different, and
        // bit-unequal to the one that was saved.
        SceneNode node = Placed(
            "door", "func_door",
            Wire("OnOpen", "light1", "TurnOn"),
            Wire("OnClose", "light2", "TurnOff", "x", 1.5f, 3),
            Wire("OnOpen", "sound1", "PlaySound"));

        var rig = new Rig(node);
        EntityConnection[] before = [.. Wiring(node)];

        // A reordering edit, which is the case a list-diffing implementation
        // gets wrong while every membership assertion stays green.
        rig.Undo.Execute(SetEntityConnectionsCommand.Capture(
            node, [before[2], before[0]]));

        Wiring(node).Count.ShouldBe(2);
        Wiring(node)[0].ShouldBe(before[2]);
        Wiring(node)[1].ShouldBe(before[0]);

        rig.Undo.Undo();

        Wiring(node).Count.ShouldBe(3);
        Wiring(node)[0].ShouldBe(before[0]);
        Wiring(node)[1].ShouldBe(before[1]);
        Wiring(node)[2].ShouldBe(before[2]);

        rig.Undo.Redo();
        Wiring(node)[0].ShouldBe(before[2]);
    }

    [Fact]
    public void The_captured_before_state_is_a_copy_of_the_live_list()
    {
        // EntityData owns the list and hands it out. A command that stored the
        // instance would have its own before-state rewritten by the edit it is
        // recording, so an undo would restore what it had just undone to.
        SceneNode node = Placed("door", "func_door", Wire("OnOpen", "a"));
        var rig = new Rig(node);

        var command = SetEntityConnectionsCommand.Capture(node, []);
        command.Before.Count.ShouldBe(1);

        rig.Undo.Execute(command);
        Wiring(node).ShouldBeEmpty();
        command.Before.Count.ShouldBe(1, "the before state is not the list it just cleared");

        rig.Undo.Undo();
        Wiring(node).Count.ShouldBe(1);
    }

    [Fact]
    public void A_missing_target_is_a_no_op_rather_than_a_throw()
    {
        // History behind a still-undone delete legitimately names absent nodes.
        var scene = new Scene("entities");
        var command = new SetEntityConnectionsCommand(
            Guid.NewGuid(), [Wire("OnOpen", "a")], []);

        Should.NotThrow(() => command.Do(scene));
        Should.NotThrow(() => command.Undo(scene));
        Should.NotThrow(() => command.RollBack(scene));
    }

    [Fact]
    public void A_node_that_lost_its_entity_is_a_no_op_too()
    {
        SceneNode node = Placed("door", "func_door", Wire("OnOpen", "a"));
        var scene = new Scene("entities");
        scene.Root.AddChild(node);

        var command = SetEntityConnectionsCommand.Capture(node, []);
        node.Entity = null;

        Should.NotThrow(() => command.Do(scene));
    }

    [Fact]
    public void Capturing_from_a_node_with_no_entity_is_refused_at_the_call()
    {
        // A before-state of "no wires" read off a node that has no payload at
        // all would make an undo clear a list nothing ever stored.
        Should.Throw<InvalidOperationException>(
            () => SetEntityConnectionsCommand.Capture(new SceneNode("plain"), []));
    }

    [Fact]
    public void Sameness_is_order_sensitive_and_exact()
    {
        EntityConnection a = Wire("OnOpen", "light1");
        EntityConnection b = Wire("OnClose", "light2");

        SetEntityConnectionsCommand.SameWiring([a, b], [a, b]).ShouldBeTrue();
        SetEntityConnectionsCommand.SameWiring([a, b], [b, a]).ShouldBeFalse();
        SetEntityConnectionsCommand.SameWiring([a], [a, b]).ShouldBeFalse();

        // Exact, never tolerant: a delay differing in the last place really is
        // a different file, and a tolerance would report the two settled and
        // then write one over the other.
        SetEntityConnectionsCommand.SameWiring(
            [Wire("OnOpen", "x", delay: 1f)],
            [Wire("OnOpen", "x", delay: 1.0000001f)]).ShouldBeFalse();
    }

    // --- coalescing ----------------------------------------------------------

    [Fact]
    public void A_run_of_edits_to_one_nodes_wiring_is_one_history_entry()
    {
        SceneNode node = Placed("door", "func_door", Wire("OnOpen", "start"));
        var rig = new Rig(node);

        rig.Undo.BeginTransaction("Entity Wiring");
        rig.Undo.Execute(SetEntityConnectionsCommand.Capture(node, [Wire("OnOpen", "a")]));
        rig.Undo.Execute(SetEntityConnectionsCommand.Capture(node, [Wire("OnOpen", "ab")]));
        rig.Undo.Execute(SetEntityConnectionsCommand.Capture(
            node, [Wire("OnOpen", "abc"), Wire("OnClose", "z")]));
        rig.Undo.CommitTransaction();

        rig.Undo.UndoCount.ShouldBe(1);
        Wiring(node).Count.ShouldBe(2);

        rig.Undo.Undo();
        Wiring(node).Count.ShouldBe(1);
        Wiring(node)[0].TargetName.ShouldBe("start", "the entry spans the whole run");
    }

    [Fact]
    public void A_run_never_absorbs_an_edit_to_a_different_node()
    {
        var first = new SetEntityConnectionsCommand(
            Guid.NewGuid(), [], [Wire("OnOpen", "a")]);
        var other = new SetEntityConnectionsCommand(
            Guid.NewGuid(), [], [Wire("OnClose", "b")]);

        first.TryAbsorb(other).ShouldBeFalse();
        first.After[0].TargetName.ShouldBe("a");
    }

    // --- the editor verb -----------------------------------------------------

    private static SceneEditorHost NewHost(Scene scene)
    {
        var renderer = new CompilingRenderer();
        renderer.SetFramebufferSize(new Vector2D<int>(1280, 720));

        return new SceneEditorHost(
            NullLoggerFactory.Instance,
            scene,
            renderer,
            new InputManager(NullLogger<InputManager>.Instance));
    }

    [Fact]
    public void The_verb_addresses_a_node_id_rather_than_the_selection()
    {
        // A property edit lets the editor resolve the selection on the render
        // thread; a wiring edit replaces a whole LIST, so landing on a node the
        // user has also selected would overwrite that node's entire wiring.
        SceneNode wired = Placed("door", "func_door", Wire("OnOpen", "a"));
        SceneNode other = Placed("relay", "logic_relay", Wire("OnTrigger", "b"));

        var scene = new Scene("Editor");
        scene.Root.AddChild(wired);
        scene.Root.AddChild(other);
        SceneEditorHost host = NewHost(scene);

        scene.Selection.Select(other);

        host.ApplyEntityConnections(wired.Id, [Wire("OnOpen", "z")]).ShouldBeTrue();

        Wiring(wired)[0].TargetName.ShouldBe("z");
        Wiring(other)[0].TargetName.ShouldBe("b", "the selection is not the subject");
    }

    [Fact]
    public void Writing_the_list_the_node_already_has_records_nothing()
    {
        // The panel commits on Enter and on losing focus, so tabbing through a
        // wire's fields is ordinary and must not fill the history with entries
        // that undo to themselves.
        SceneNode node = Placed("door", "func_door", Wire("OnOpen", "a", "TurnOn", "p", 2f, 4));
        var scene = new Scene("Editor");
        scene.Root.AddChild(node);
        SceneEditorHost host = NewHost(scene);

        host.ApplyEntityConnections(node.Id, [.. Wiring(node)]).ShouldBeFalse();
        host.UndoDepth.ShouldBe(0);

        host.ApplyEntityConnections(node.Id, [Wire("OnOpen", "b")]).ShouldBeTrue();
        host.UndoDepth.ShouldBe(1);
    }

    [Fact]
    public void A_node_with_no_entity_is_refused_rather_than_given_one()
    {
        var plain = new SceneNode("plain");
        var scene = new Scene("Editor");
        scene.Root.AddChild(plain);
        SceneEditorHost host = NewHost(scene);

        host.ApplyEntityConnections(plain.Id, [Wire("OnOpen", "a")]).ShouldBeFalse();
        plain.Entity.ShouldBeNull();
        host.UndoDepth.ShouldBe(0);
    }

    [Fact]
    public void A_suspended_editor_refuses_a_wiring_edit()
    {
        // A panel's view of play mode is stale by up to a publish interval, so
        // a click landing in that window must do nothing rather than edit a
        // scene somebody is walking around in.
        SceneNode node = Placed("door", "func_door", Wire("OnOpen", "a"));
        var scene = new Scene("Editor");
        scene.Root.AddChild(node);
        SceneEditorHost host = NewHost(scene);

        host.Suspend();
        host.ApplyEntityConnections(node.Id, [Wire("OnOpen", "z")]).ShouldBeFalse();
        Wiring(node)[0].TargetName.ShouldBe("a");

        host.Resume();
        host.ApplyEntityConnections(node.Id, [Wire("OnOpen", "z")]).ShouldBeTrue();
    }

    [Fact]
    public void A_wire_whose_target_does_not_exist_is_kept_rather_than_dropped()
    {
        // The map loader keeps an unresolved wire - the target may be spawned
        // at run time, or belong to a level that is not open - and the editor
        // must not be the place that quietly disagrees.
        SceneNode node = Placed("door", "func_door");
        var scene = new Scene("Editor");
        scene.Root.AddChild(node);
        SceneEditorHost host = NewHost(scene);

        host.ApplyEntityConnections(
            node.Id, [Wire("OnOpen", "nothing_is_called_this")]).ShouldBeTrue();

        Wiring(node).Count.ShouldBe(1);
        Wiring(node)[0].TargetName.ShouldBe("nothing_is_called_this");
    }

    // --- the bytes -----------------------------------------------------------

    // A hand-written bundle, so the byte-identity claim is about a file a person
    // could have typed rather than about whatever the writer happens to emit.
    // No geometry: Brush's constructor re-normalises every plane, which is a
    // canonicalisation rather than a defect and would make this test about
    // something else entirely.
    private static readonly byte[] WiredMap = Encoding.UTF8.GetBytes("""
        {
          "spectramap": 3,
          "minimumReadableVersion": 3,
          "engine": "1.0.0",
          "scene": {
            "name": "Wired"
          },
          "nodes": [
            {
              "id": "3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4",
              "name": "door",
              "transform": {"p":[0,0,0]},
              "entity": {
                "class": "func_door",
                "keys": {"speed":"100"},
                "outputs": [
                  {"output":"OnFullyOpen","target":"light1","input":"TurnOn"},
                  {"output":"OnFullyClosed","target":"light1","input":"TurnOff","param":"2","delay":1.5,"times":3},
                  {"output":"OnFullyOpen","target":"missing_entity","input":"Kill"}
                ]
              },
              "children": []
            },
            {
              "id": "7d1b9e40-2c55-4f13-8a6c-1e9d5b04a7f2",
              "name": "light1",
              "transform": {"p":[0,0,0]},
              "entity": {
                "class": "logic_relay"
              },
              "children": []
            }
          ]
        }
        """.ReplaceLineEndings("\n") + "\n");

    [Fact]
    public void A_wired_map_saves_byte_for_byte_after_a_load()
    {
        var loaded = new Scene("Testmap");
        MapSceneBinder.ApplyTo(MapReader.Read(WiredMap), loaded);

        MapWriter.Write(MapSceneBinder.FromScene(loaded)).ShouldBe(WiredMap);
    }

    [Fact]
    public void A_wiring_edit_undone_saves_byte_for_byte_again()
    {
        // The whole point of an absolute-array command: the undo has to restore
        // the bytes, not merely a list that means the same thing. A reordering
        // edit is used deliberately, since a re-sorted list is the failure this
        // catches and the only one that survives a membership assertion.
        var loaded = new Scene("Testmap");
        MapSceneBinder.ApplyTo(MapReader.Read(WiredMap), loaded);

        SceneNode door = loaded.Root.Children[0];
        door.Name.ShouldBe("door");

        var undo = new UndoStack(loaded);
        EntityConnection[] original = [.. Wiring(door)];

        undo.Execute(SetEntityConnectionsCommand.Capture(
            door,
            [
                original[2],
                original[0],
                new EntityConnection("OnFullyOpen", "light1", "Toggle", "", 0.25f, 1),
            ]));

        MapWriter.Write(MapSceneBinder.FromScene(loaded)).ShouldNotBe(WiredMap);

        undo.Undo();

        MapWriter.Write(MapSceneBinder.FromScene(loaded)).ShouldBe(WiredMap);
    }

    [Fact]
    public void An_added_wire_survives_a_save_and_a_load_in_its_authored_place()
    {
        var loaded = new Scene("Testmap");
        MapSceneBinder.ApplyTo(MapReader.Read(WiredMap), loaded);

        SceneNode door = loaded.Root.Children[0];
        var undo = new UndoStack(loaded);

        List<EntityConnection> wires = [.. Wiring(door)];
        wires.Insert(1, new EntityConnection("OnOpen", "light1", "Blink", "", 0f, 2));
        undo.Execute(SetEntityConnectionsCommand.Capture(door, wires));

        var reloaded = new Scene("Testmap");
        MapSceneBinder.ApplyTo(
            MapReader.Read(MapWriter.Write(MapSceneBinder.FromScene(loaded))), reloaded);

        IReadOnlyList<EntityConnection> back = Wiring(reloaded.Root.Children[0]);
        back.Count.ShouldBe(4);
        back[1].Output.ShouldBe("OnOpen");
        back[1].Input.ShouldBe("Blink");
        back[1].TimesToFire.ShouldBe(2);
        back[2].Input.ShouldBe("TurnOff", "the wires after the insert kept their order");
    }

    [Fact]
    public void A_transform_is_untouched_by_a_wiring_edit()
    {
        // The command writes one payload and nothing else. Stated as a test
        // because the node it edits is resolved by id and a wrong lookup would
        // be invisible in every wiring assertion above.
        SceneNode node = Placed("door", "func_door", Wire("OnOpen", "a"));
        node.LocalPosition = new Vector3(3f, 4f, 5f);
        var rig = new Rig(node);

        rig.Undo.Execute(SetEntityConnectionsCommand.Capture(node, []));

        node.LocalPosition.ShouldBe(new Vector3(3f, 4f, 5f));
    }
}
