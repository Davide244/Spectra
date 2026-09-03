using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editor.Shell;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The Outputs section: the shell's half of entity wiring.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every edit posts the WHOLE list, so the thing worth pinning is what the
/// section GATHERS.</b> A row that reported only the field somebody touched
/// would send a list with five stale wires in it, and the command - which is
/// absolute by design - would write them.
/// </para>
/// <para>
/// <b>And the hold-off, which is invisible until it is missing.</b> A snapshot
/// published between an add and the engine's echo still describes the list the
/// add replaced; writing that back makes the new row appear and vanish. It is
/// bounded for the reason every optimistic value here is bounded: the value a
/// user asks for is not always the value they get.
/// </para>
/// </remarks>
public sealed class EntityWiringPanelTests
{
    private static readonly Guid NodeId = Guid.Parse("3f2a1c88-4b6d-4a19-9d0e-77c1f0a2b3e4");

    private static EntityConnection Wire(
        string output = "OnOpen", string target = "light1", string input = "TurnOn",
        string param = "", float delay = 0f, int times = EntityConnection.Infinite) =>
        new(output, target, input, param, delay, times);

    private static EntityPanelInfo Info(
        bool known = true,
        IReadOnlyList<string>? outputs = null,
        params EntityConnectionInfo[] wires) => new()
        {
            NodeId = NodeId,
            ClassName = "func_door",
            IsKnown = known,
            Outputs = outputs ?? ["OnOpen", "OnClose"],
            Connections = wires,
        };

    private static EntityConnectionInfo Resolved(EntityConnection wire) => new(wire, true);

    private static EntityConnectionInfo Unresolved(EntityConnection wire) => new(wire, false);

    private sealed class Rig
    {
        public List<(Guid NodeId, EntityConnection[] Wires)> Posts { get; } = [];

        public PropertyPanelModel Panel { get; }

        public Rig() => Panel = new PropertyPanelModel(
            _ => { }, _ => { }, _ => { },
            (id, wires) => Posts.Add((id, [.. wires])));

        public EntityWiringModel Wiring => Panel.Wiring;

        public void Publish(EntityPanelInfo? info) => Panel.Apply([], info is null ? 0 : 1, info);

        public EntityConnection[] LastPost => Posts[^1].Wires;
    }

    // --- what is shown -------------------------------------------------------

    [Fact]
    public void The_section_appears_only_for_an_entity()
    {
        var rig = new Rig();
        rig.Wiring.HasEntity.ShouldBeFalse();

        rig.Publish(Info(wires: Resolved(Wire())));
        rig.Wiring.HasEntity.ShouldBeTrue();
        rig.Wiring.Rows.Count.ShouldBe(1);

        rig.Publish(null);
        rig.Wiring.HasEntity.ShouldBeFalse();
        rig.Wiring.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void The_rows_keep_the_authored_order()
    {
        var rig = new Rig();
        rig.Publish(Info(
            wires:
            [
                Resolved(Wire("OnClose", "z")),
                Resolved(Wire("OnOpen", "a")),
                Resolved(Wire("OnClose", "m")),
            ]));

        rig.Wiring.Rows.Select(r => r.TargetField.Text).ShouldBe(["z", "a", "m"]);
    }

    [Fact]
    public void An_entity_with_no_wires_says_so()
    {
        var rig = new Rig();
        rig.Publish(Info());

        rig.Wiring.IsEmpty.ShouldBeTrue();

        rig.Publish(Info(wires: Resolved(Wire())));
        rig.Wiring.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Every_field_of_a_wire_is_shown()
    {
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire("OnClose", "light2", "TurnOff", "3", 1.5f, 2))));

        ConnectionRowModel row = rig.Wiring.Rows[0];
        row.Output.ShouldBe("OnClose");
        row.TargetField.Text.ShouldBe("light2");
        row.InputField.Text.ShouldBe("TurnOff");
        row.ParameterField.Text.ShouldBe("3");
        row.DelayField.Text.ShouldBe("1.5");
        row.TimesField.Text.ShouldBe("2");
    }

    // --- the amber warning ---------------------------------------------------

    [Fact]
    public void An_unresolved_target_warns_and_the_wire_stays()
    {
        var rig = new Rig();
        rig.Publish(Info(wires: Unresolved(Wire(target: "nobody"))));

        ConnectionRowModel row = rig.Wiring.Rows[0];
        row.HasTargetWarning.ShouldBeTrue();
        row.TargetWarning.ShouldBe("Nothing here is named 'nobody'");
        row.TargetField.Text.ShouldBe("nobody", "warn and keep, never warn and drop");
    }

    [Fact]
    public void An_empty_target_says_it_is_empty_rather_than_naming_nothing()
    {
        var rig = new Rig();
        rig.Publish(Info(wires: Unresolved(Wire(target: ""))));

        rig.Wiring.Rows[0].TargetWarning.ShouldBe("No target set");
    }

    [Fact]
    public void A_resolved_target_shows_no_warning()
    {
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire())));

        rig.Wiring.Rows[0].HasTargetWarning.ShouldBeFalse();
        rig.Wiring.Rows[0].TargetWarning.ShouldBe("");
    }

    // --- the output dropdown -------------------------------------------------

    [Fact]
    public void The_dropdown_offers_what_the_class_declares()
    {
        var rig = new Rig();
        rig.Publish(Info(outputs: ["OnOpen", "OnClose"], wires: Resolved(Wire("OnOpen", "a"))));

        rig.Wiring.Rows[0].OutputChoices.ShouldBe(["OnOpen", "OnClose"]);
        rig.Wiring.Rows[0].HasOutputChoices.ShouldBeTrue();
    }

    [Fact]
    public void An_authored_output_nothing_declares_is_typed_rather_than_picked()
    {
        // A newer version of the class may have dropped it. A dropdown that
        // could not show the stored value would render blank and then write
        // that blank back the moment anybody touched the row beside it, so the
        // row falls back to a text box - which keeps the value visible AND
        // editable, and keeps the menu honest about what the class has.
        var rig = new Rig();
        rig.Publish(Info(outputs: ["OnOpen"], wires: Resolved(Wire("OnSomethingElse", "a"))));

        ConnectionRowModel row = rig.Wiring.Rows[0];
        row.HasOutputChoices.ShouldBeFalse();
        row.Output.ShouldBe("OnSomethingElse");
        row.OutputField.Text.ShouldBe("OnSomethingElse");
        row.OutputChoices.ShouldBe(["OnOpen"], "the menu still offers only what the class declares");
    }

    [Fact]
    public void Typing_a_declared_output_flips_the_row_back_to_the_dropdown()
    {
        var rig = new Rig();
        rig.Publish(Info(outputs: ["OnOpen"], wires: Resolved(Wire("OnSomethingElse", "a"))));

        ConnectionRowModel row = rig.Wiring.Rows[0];
        row.HasOutputChoices.ShouldBeFalse();

        row.OutputField.BeginEdit();
        row.OutputField.Text = "OnOpen";
        row.OutputField.Commit();

        row.HasOutputChoices.ShouldBeTrue();
        rig.LastPost[0].Output.ShouldBe("OnOpen");
    }

    [Fact]
    public void An_unchanged_publish_hands_the_dropdown_the_same_list_instance()
    {
        // A fresh array per publish makes the ComboBox drop its selection and
        // rebuild its popup thirty times a second. The identity of the list is
        // what keeps the control still, so the row is handed the schema's own
        // instance and never builds one.
        //
        // ONE outputs instance, which is what the engine publishes: the list
        // comes off the EntitySchema, and a schema's lists are built once and
        // documented as never mutated afterwards.
        string[] declared = ["OnOpen", "OnClose"];

        var rig = new Rig();
        rig.Publish(Info(outputs: declared, wires: Resolved(Wire("OnOpen", "a"))));

        rig.Wiring.Rows[0].OutputChoices.ShouldBeSameAs(declared);

        rig.Publish(Info(outputs: declared, wires: Resolved(Wire("OnClose", "b"))));

        rig.Wiring.Rows[0].OutputChoices.ShouldBeSameAs(declared);
    }

    [Fact]
    public void A_dropdown_clearing_its_own_selection_posts_nothing()
    {
        // A ComboBox clears SelectedItem when its ItemsSource is replaced, and
        // a two-way binding delivers that here looking exactly like a click.
        // Taken literally it would post a wire with no output at all - and
        // then write it, because the command is absolute.
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire("OnOpen", "a"))));

        rig.Wiring.Rows[0].Output = string.Empty;

        rig.Posts.ShouldBeEmpty();
        rig.Wiring.Rows[0].Output.ShouldBe("OnOpen");
    }

    [Fact]
    public void The_typed_output_commits_rather_than_writing_per_keystroke()
    {
        // The fallback for a class with no schema is a field model, not a
        // string bound two-way: typing "OnFoo" through a two-way Text binding
        // would post five wiring edits and put five entries in the history.
        var rig = new Rig();
        rig.Publish(Info(known: false, outputs: [], wires: Resolved(Wire("", "a"))));

        PropertyFieldModel output = rig.Wiring.Rows[0].OutputField;
        output.BeginEdit();
        output.Text = "OnFoo";
        rig.Posts.ShouldBeEmpty();

        output.Commit();
        rig.LastPost[0].Output.ShouldBe("OnFoo");
        rig.Wiring.Rows[0].Output.ShouldBe("OnFoo");
    }

    [Fact]
    public void Typing_an_output_never_changes_the_dropdowns_item_source()
    {
        // FOUND IN THE RUNNING SHELL, and the reason the choice list is never
        // widened. The first design put the authored value into the list, so
        // typing an output on a class with no schema replaced the item source -
        // the control discarded its selection, and a binding will not re-push a
        // value it has already pushed, so the box sat permanently blank over a
        // model that knew the answer. An item source that never changes cannot
        // fail that way at all.
        var rig = new Rig();
        rig.Publish(Info(known: false, outputs: [], wires: Resolved(Wire("", "a"))));

        IReadOnlyList<string> source = rig.Wiring.Rows[0].OutputChoices;

        PropertyFieldModel output = rig.Wiring.Rows[0].OutputField;
        output.BeginEdit();
        output.Text = "OnHandmade";
        output.Commit();

        rig.Publish(Info(known: false, outputs: [], wires: Resolved(Wire("OnHandmade", "a"))));

        rig.Wiring.Rows[0].OutputChoices.ShouldBeSameAs(source);
        rig.Wiring.Rows[0].HasOutputChoices.ShouldBeFalse("a class with no schema stays a text box");
        rig.Wiring.Rows[0].OutputField.Text.ShouldBe("OnHandmade");
    }

    [Fact]
    public void A_class_with_no_schema_gets_a_typed_field_instead_of_an_empty_dropdown()
    {
        var rig = new Rig();
        rig.Publish(Info(known: false, outputs: [], wires: Resolved(Wire("OnWhatever", "a"))));

        rig.Wiring.ShowsUnknownOutputs.ShouldBeTrue();
        rig.Wiring.Rows[0].HasOutputChoices.ShouldBeFalse();
        rig.Wiring.Rows[0].OutputField.Text.ShouldBe("OnWhatever");
    }

    // --- what a commit posts -------------------------------------------------

    [Fact]
    public void A_field_commit_posts_the_whole_list_with_the_edit_in_place()
    {
        var rig = new Rig();
        rig.Publish(Info(
            wires: [Resolved(Wire("OnOpen", "a")), Resolved(Wire("OnClose", "b"))]));

        PropertyFieldModel target = rig.Wiring.Rows[1].TargetField;
        target.BeginEdit();
        target.Text = "c";
        target.Commit();

        rig.Posts.Count.ShouldBe(1);
        rig.Posts[0].NodeId.ShouldBe(NodeId);
        rig.LastPost.Length.ShouldBe(2);
        rig.LastPost[0].TargetName.ShouldBe("a", "the untouched wire went along unchanged");
        rig.LastPost[1].TargetName.ShouldBe("c");
        rig.LastPost[1].Output.ShouldBe("OnClose");
    }

    [Fact]
    public void An_empty_parameter_is_a_value_and_commits()
    {
        // The one cell where clearing the box means "send no argument" rather
        // than "leave it alone". Every other field reverts an empty commit,
        // which for this one would make a parameter impossible to remove.
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire(param: "3"))));

        PropertyFieldModel parameter = rig.Wiring.Rows[0].ParameterField;
        parameter.BeginEdit();
        parameter.Text = "";
        parameter.Commit();

        rig.LastPost[0].Parameter.ShouldBe("");
    }

    [Fact]
    public void An_unparseable_delay_reverts_rather_than_sticking()
    {
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire(delay: 2f))));

        PropertyFieldModel delay = rig.Wiring.Rows[0].DelayField;
        delay.BeginEdit();
        delay.Text = "soon";
        delay.Commit();

        rig.Posts.ShouldBeEmpty();
        delay.Text.ShouldBe("2");

        // A negative delay is the same case rather than a smaller one: the
        // event queue keys on a fire time, so scheduling into the past is a
        // different bug, not a faster wire.
        delay.BeginEdit();
        delay.Text = "-1";
        delay.Commit();

        rig.Posts.ShouldBeEmpty();
        delay.Text.ShouldBe("2");
    }

    [Fact]
    public void Any_negative_times_normalises_to_the_infinite_sentinel()
    {
        // EntityConnection's own rule, applied where the value is typed so the
        // file gets the canonical -1 rather than whatever was entered.
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire(times: 4))));

        PropertyFieldModel times = rig.Wiring.Rows[0].TimesField;
        times.BeginEdit();
        times.Text = "-7";
        times.Commit();

        rig.LastPost[0].TimesToFire.ShouldBe(EntityConnection.Infinite);
    }

    [Fact]
    public void Picking_an_output_posts_it_and_a_refresh_does_not()
    {
        // The dropdown applies on the click, and guards its refresh: assigning
        // the published value back is indistinguishable from a user picking it,
        // and would post an edit per publish for as long as the entity stayed
        // selected.
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire("OnOpen", "a"))));
        rig.Posts.ShouldBeEmpty();

        rig.Wiring.Rows[0].Output = "OnClose";
        rig.LastPost[0].Output.ShouldBe("OnClose");

        int posts = rig.Posts.Count;
        rig.Publish(Info(wires: Resolved(Wire("OnClose", "a"))));
        rig.Posts.Count.ShouldBe(posts, "a refresh is not a click");
    }

    [Fact]
    public void Add_and_remove_post_the_new_list()
    {
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire("OnOpen", "a"))));

        rig.Wiring.Add();
        rig.Wiring.Rows.Count.ShouldBe(2);
        rig.LastPost.Length.ShouldBe(2);
        rig.LastPost[1].Output.ShouldBe("OnOpen", "a new wire starts on the first declared output");
        rig.LastPost[1].TargetName.ShouldBe("");
        rig.LastPost[1].TimesToFire.ShouldBe(EntityConnection.Infinite);

        rig.Wiring.Remove(rig.Wiring.Rows[0]);
        rig.LastPost.Length.ShouldBe(1);
        rig.LastPost[0].TargetName.ShouldBe("");
    }

    [Fact]
    public void Add_does_nothing_without_an_entity()
    {
        var rig = new Rig();
        rig.Wiring.Add();

        rig.Posts.ShouldBeEmpty();
        rig.Wiring.Rows.ShouldBeEmpty();
    }

    // --- the refresh guard and the hold-off ----------------------------------

    [Fact]
    public void A_focused_field_stops_taking_refreshes()
    {
        // The panel's standing contract, and it has to hold here too: a wiring
        // panel republishes at the snapshot rate, and a field that took each
        // one would delete characters as they were typed.
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire(target: "a"))));

        PropertyFieldModel target = rig.Wiring.Rows[0].TargetField;
        target.BeginEdit();
        target.Text = "half typed";

        rig.Publish(Info(wires: Resolved(Wire(target: "a"))));

        target.Text.ShouldBe("half typed");
    }

    [Fact]
    public void A_stale_snapshot_does_not_undo_an_add()
    {
        // The engine echoes an edit a publish or two later. Writing the older
        // list back in between makes the row appear and vanish, which reads as
        // the button being broken.
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire("OnOpen", "a"))));

        rig.Wiring.Add();
        rig.Wiring.Rows.Count.ShouldBe(2);

        rig.Publish(Info(wires: Resolved(Wire("OnOpen", "a"))));
        rig.Wiring.Rows.Count.ShouldBe(2, "the snapshot describes a frame from before the add");

        rig.Publish(Info(
            wires:
            [
                Resolved(Wire("OnOpen", "a")),
                Unresolved(new EntityConnection("OnOpen", "", "", "", 0f, EntityConnection.Infinite)),
            ]));

        rig.Wiring.Rows.Count.ShouldBe(2, "and now it agrees");
    }

    [Fact]
    public void The_engine_wins_once_the_hold_expires()
    {
        // The bound is the whole design: a wiring edit is refused outright
        // while play mode owns the scene, and a panel that held its own opinion
        // forever would show wiring the level does not have.
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire("OnOpen", "a"))));

        rig.Wiring.Add();
        rig.Wiring.Rows.Count.ShouldBe(2);

        for (int i = 0; i < EntityWiringModel.HoldSnapshots; i++)
            rig.Publish(Info(wires: Resolved(Wire("OnOpen", "a"))));

        rig.Wiring.Rows.Count.ShouldBe(1, "the engine refused, visibly");
    }

    [Fact]
    public void Selecting_a_different_entity_drops_a_pending_edit()
    {
        // The edit was aimed at a node this panel is no longer showing; holding
        // it would make the next entity open with the previous one's wiring on
        // screen.
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire("OnOpen", "a"))));
        rig.Wiring.Add();

        var other = new EntityPanelInfo
        {
            NodeId = Guid.NewGuid(),
            ClassName = "logic_relay",
            IsKnown = true,
            Outputs = ["OnTrigger"],
            Connections = [],
        };

        rig.Publish(other);

        rig.Wiring.Rows.ShouldBeEmpty();
    }

    // --- the section is patched, not replaced --------------------------------

    [Fact]
    public void An_unchanged_publish_keeps_the_controls_it_already_built()
    {
        // Assigning fresh rows per publish resets scroll and destroys a
        // half-typed value thirty times a second, which is the same reason the
        // property rows above are patched rather than rebuilt.
        var rig = new Rig();
        rig.Publish(Info(wires: Resolved(Wire(target: "a"))));

        ConnectionRowModel before = rig.Wiring.Rows[0];

        rig.Publish(Info(wires: Resolved(Wire(target: "b"))));

        rig.Wiring.Rows[0].ShouldBeSameAs(before);
        before.TargetField.Text.ShouldBe("b");
    }

    [Fact]
    public void A_wire_removed_by_the_engine_is_taken_off_the_panel()
    {
        var rig = new Rig();
        rig.Publish(Info(wires: [Resolved(Wire("OnOpen", "a")), Resolved(Wire("OnClose", "b"))]));
        rig.Wiring.Rows.Count.ShouldBe(2);

        rig.Publish(Info(wires: Resolved(Wire("OnOpen", "a"))));

        rig.Wiring.Rows.Count.ShouldBe(1);
        rig.Wiring.Rows[0].TargetField.Text.ShouldBe("a");
    }

    // --- the rest of the panel is unaffected ---------------------------------

    [Fact]
    public void A_panel_built_without_a_wiring_callback_still_works()
    {
        // The parameter is optional so a host with no wiring surface - a test
        // rig, a future read-only viewer - does not have to invent one, and a
        // click there must be a no-op rather than a null reference.
        var panel = new PropertyPanelModel(_ => { }, _ => { }, _ => { });

        panel.Apply([], 1, Info(wires: Resolved(Wire())));
        Should.NotThrow(() => panel.Wiring.Add());
    }
}
