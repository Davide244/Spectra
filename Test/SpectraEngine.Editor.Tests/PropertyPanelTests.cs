using SpectraEngine.Core.Inspection;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editor.Shell;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The property panel's commit policy: when a typed value reaches the scene,
/// and when the scene is allowed to overwrite what somebody is typing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and worth testing precisely because the failures are invisible.</b>
/// A refresh that stole a focused field deletes characters as they are typed
/// and reads as a broken keyboard; a commit that fired per keystroke pushes an
/// undo entry per character and applies "1" on the way to "10". Neither throws.
/// </para>
/// </remarks>
public sealed class PropertyPanelTests
{
    private sealed class Rig
    {
        public List<PropertyEdit> Edits { get; } = [];
        public List<string> GesturesOpened { get; } = [];
        public List<bool> GesturesClosed { get; } = [];
        public PropertyPanelModel Panel { get; }

        public Rig() => Panel = new PropertyPanelModel(
            Edits.Add, GesturesOpened.Add, GesturesClosed.Add);

        public void Publish(params PropertyRow[] rows) => Panel.Apply(rows, 1);
        public void Publish(int selection, params PropertyRow[] rows) => Panel.Apply(rows, selection);

        public PropertyRowModel Row(PropertyId id) =>
            Panel.Groups.SelectMany(g => g.Rows).Single(r => r.Id == id);
    }

    private static PropertyRow Vector(PropertyId id, Vector3 value, PropertyAxes mixed = PropertyAxes.None) =>
        new()
        {
            Group = "Transform", Name = id.ToString(), Id = id, Kind = PropertyKind.Vector3,
            Vector = value, Choices = [], PresentCount = 1, SelectionCount = 1, MixedAxes = mixed,
        };

    private static PropertyRow Text(PropertyId id, string value) =>
        new()
        {
            Group = "Node", Name = "Name", Id = id, Kind = PropertyKind.Text,
            Text = value, Choices = [], PresentCount = 1, SelectionCount = 1,
        };

    private static PropertyRow Flag(PropertyId id, bool value) =>
        new()
        {
            Group = "Light", Name = "Enabled", Id = id, Kind = PropertyKind.Boolean,
            Flag = value, Choices = [], PresentCount = 1, SelectionCount = 1,
        };

    private static PropertyRow Choice(PropertyId id, string value, params string[] options) =>
        new()
        {
            Group = "Brush", Name = "Kind", Id = id, Kind = PropertyKind.Choice,
            Text = value, Choices = options, PresentCount = 1, SelectionCount = 1,
        };

    // --- the focus guard ----------------------------------------------------

    [Fact]
    public void A_refresh_does_not_touch_a_field_that_is_being_edited()
    {
        // A gizmo drag republishes the position about thirty times a second. A
        // field that took each refresh would delete characters out from under
        // somebody halfway through typing a number.
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, new Vector3(1f, 2f, 3f)));

        PropertyFieldModel x = rig.Row(PropertyId.Position).Fields[0];
        x.BeginEdit();
        x.Text = "12";

        rig.Publish(Vector(PropertyId.Position, new Vector3(99f, 2f, 3f)));

        x.Text.ShouldBe("12", "the field belongs to the person typing in it");
    }

    [Fact]
    public void A_field_that_is_not_being_edited_follows_the_scene()
    {
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, new Vector3(1f, 2f, 3f)));

        rig.Publish(Vector(PropertyId.Position, new Vector3(7f, 2f, 3f)));

        rig.Row(PropertyId.Position).Fields[0].Text.ShouldBe("7");
    }

    [Fact]
    public void A_committed_field_starts_following_the_scene_again()
    {
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, new Vector3(1f, 2f, 3f)));

        PropertyFieldModel x = rig.Row(PropertyId.Position).Fields[0];
        x.BeginEdit();
        x.Text = "12";
        x.Commit();

        rig.Publish(Vector(PropertyId.Position, new Vector3(50f, 2f, 3f)));

        x.Text.ShouldBe("50");
    }

    // --- when a value is applied --------------------------------------------

    [Fact]
    public void Committing_a_vector_cell_writes_only_that_axis()
    {
        // The whole reason a vector row is three cells: typing into y is a bulk
        // edit that leaves every node's own x and z alone.
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, new Vector3(1f, 2f, 3f)));

        PropertyFieldModel y = rig.Row(PropertyId.Position).Fields[1];
        y.BeginEdit();
        y.Text = "40";
        y.Commit();

        rig.Edits.Count.ShouldBe(1);
        rig.Edits[0].Id.ShouldBe(PropertyId.Position);
        rig.Edits[0].Axes.ShouldBe(PropertyAxes.Y);
        rig.Edits[0].Vector.Y.ShouldBe(40f);
    }

    [Fact]
    public void Committing_an_unchanged_field_applies_nothing()
    {
        // Tabbing through fields without changing anything is an ordinary thing
        // to do and must not reach the undo stack.
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, new Vector3(1f, 2f, 3f)));

        PropertyFieldModel x = rig.Row(PropertyId.Position).Fields[0];
        x.BeginEdit();
        x.Commit();

        rig.Edits.ShouldBeEmpty();
    }

    [Fact]
    public void A_field_that_was_never_focused_cannot_commit()
    {
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, new Vector3(1f, 2f, 3f)));

        rig.Row(PropertyId.Position).Fields[0].Commit();

        rig.Edits.ShouldBeEmpty();
    }

    [Fact]
    public void Text_that_will_not_parse_reverts_rather_than_sticking()
    {
        // A field left holding something the scene does not contain disagrees
        // with the viewport until somebody notices.
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, new Vector3(1f, 2f, 3f)));

        PropertyFieldModel x = rig.Row(PropertyId.Position).Fields[0];
        x.BeginEdit();
        x.Text = "not a number";
        x.Commit();

        rig.Edits.ShouldBeEmpty();
        x.Text.ShouldBe("1");
    }

    [Fact]
    public void Escape_puts_the_live_value_back_and_applies_nothing()
    {
        // Escape has to exist precisely because blur commits: without it there
        // is no way to abandon a half-typed value once a field is holding it.
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, new Vector3(1f, 2f, 3f)));

        PropertyFieldModel z = rig.Row(PropertyId.Position).Fields[2];
        z.BeginEdit();
        z.Text = "999";
        z.Revert();

        rig.Edits.ShouldBeEmpty();
        z.Text.ShouldBe("3");
    }

    [Fact]
    public void A_name_is_applied_as_text_rather_than_parsed()
    {
        var rig = new Rig();
        rig.Publish(Text(PropertyId.NodeName, "Wall"));

        // Through the header's own field, which is where the name lives now.
        PropertyFieldModel field = rig.Panel.NameField;
        field.BeginEdit();
        field.Text = "Doorway";
        field.Commit();

        rig.Edits.Single().Text.ShouldBe("Doorway");
    }

    // --- mixed --------------------------------------------------------------

    [Fact]
    public void A_mixed_cell_shows_nothing_rather_than_one_nodes_value()
    {
        // A number sitting in a mixed field is a number somebody will read as
        // the answer.
        var rig = new Rig();
        rig.Publish(2, Vector(PropertyId.Position, new Vector3(1f, 2f, 3f), PropertyAxes.Y));

        IReadOnlyList<PropertyFieldModel> fields = rig.Row(PropertyId.Position).Fields;

        fields[1].Text.ShouldBeEmpty();
        fields[1].IsMixed.ShouldBeTrue();
        fields[1].Placeholder.ShouldBe("mixed");

        fields[0].Text.ShouldBe("1", "the settled axes still show their value");
        fields[2].Text.ShouldBe("3");
    }

    [Fact]
    public void Leaving_a_mixed_cell_empty_applies_nothing()
    {
        // An empty mixed box already means "leave them all alone", so blurring
        // out of one must not write a value.
        var rig = new Rig();
        rig.Publish(2, Vector(PropertyId.Position, new Vector3(1f, 2f, 3f), PropertyAxes.Y));

        PropertyFieldModel y = rig.Row(PropertyId.Position).Fields[1];
        y.BeginEdit();
        y.Commit();

        rig.Edits.ShouldBeEmpty();
    }

    [Fact]
    public void Typing_into_a_mixed_cell_applies_to_the_whole_selection()
    {
        var rig = new Rig();
        rig.Publish(2, Vector(PropertyId.Position, new Vector3(1f, 2f, 3f), PropertyAxes.Y));

        PropertyFieldModel y = rig.Row(PropertyId.Position).Fields[1];
        y.BeginEdit();
        y.Text = "0";
        y.Commit();

        rig.Edits.Single().Axes.ShouldBe(PropertyAxes.Y);
        rig.Edits.Single().Vector.Y.ShouldBe(0f);
    }

    // --- widgets with no typing in them -------------------------------------

    [Fact]
    public void A_checkbox_applies_on_the_click_because_there_is_nothing_to_finish()
    {
        var rig = new Rig();
        rig.Publish(Flag(PropertyId.LightEnabled, true));

        rig.Row(PropertyId.LightEnabled).Flag = false;

        rig.Edits.Single().Id.ShouldBe(PropertyId.LightEnabled);
        rig.Edits.Single().Flag.ShouldBeFalse();
    }

    [Fact]
    public void A_refresh_of_a_checkbox_does_not_apply_itself_back_to_the_scene()
    {
        // Assigning the refreshed value would otherwise look exactly like a
        // click, and the panel would write the scene's own value back to it
        // thirty times a second.
        var rig = new Rig();
        rig.Publish(Flag(PropertyId.LightEnabled, true));
        rig.Edits.Clear();

        rig.Publish(Flag(PropertyId.LightEnabled, false));

        rig.Edits.ShouldBeEmpty();
        rig.Row(PropertyId.LightEnabled).Flag.ShouldBeFalse();
    }

    [Fact]
    public void A_choice_applies_on_selection_and_not_on_refresh()
    {
        var rig = new Rig();
        rig.Publish(Choice(PropertyId.BrushKind, "World", "World", "Part"));
        rig.Edits.Clear();

        rig.Publish(Choice(PropertyId.BrushKind, "Part", "World", "Part"));
        rig.Edits.ShouldBeEmpty();

        rig.Row(PropertyId.BrushKind).Choice = "World";
        rig.Edits.Single().Text.ShouldBe("World");
    }

    // --- the collection -----------------------------------------------------

    [Fact]
    public void Rows_are_patched_rather_than_replaced_between_refreshes()
    {
        // Assigning a fresh collection every snapshot would reset scroll, drop
        // focus and destroy a half-typed value thirty times a second.
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, Vector3.Zero));
        PropertyRowModel before = rig.Row(PropertyId.Position);

        rig.Publish(Vector(PropertyId.Position, Vector3.One));

        rig.Row(PropertyId.Position).ShouldBeSameAs(before);
    }

    [Fact]
    public void A_change_in_which_properties_exist_rebuilds_the_rows()
    {
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, Vector3.Zero));

        rig.Publish(Vector(PropertyId.Position, Vector3.Zero), Flag(PropertyId.LightEnabled, true));

        rig.Panel.Groups.SelectMany(g => g.Rows).Select(r => r.Id)
            .ShouldBe([PropertyId.Position, PropertyId.LightEnabled]);
    }

    [Fact]
    public void Sections_come_from_runs_of_equal_groups()
    {
        var rig = new Rig();
        rig.Publish(
            Text(PropertyId.NodeName, "Wall"),
            Vector(PropertyId.Position, Vector3.Zero),
            Vector(PropertyId.Scale, Vector3.One),
            Choice(PropertyId.BrushKind, "World", "World", "Part"));

        // The name is the panel's HEADER now, not the first row of a "Node"
        // section, so the sections start at the first real payload.
        rig.Panel.Groups.Select(g => g.Name).ShouldBe(["Transform", "Brush"]);
        rig.Panel.Groups[0].Rows.Count.ShouldBe(2);
        rig.Panel.NameField.Text.ShouldBe("Wall");
    }

    [Fact]
    public void The_name_becomes_the_header_and_the_id_is_not_shown()
    {
        var rig = new Rig();
        rig.Publish(
            Text(PropertyId.NodeName, "WallNorth"),
            new PropertyRow
            {
                Group = "Node", Name = "Id", Id = PropertyId.NodeId,
                Kind = PropertyKind.ReadOnlyText, Text = "1ef302b1-1aa4-42ed-8aa1-7490bbbb0000",
                Choices = [], PresentCount = 1, SelectionCount = 1,
            },
            Vector(PropertyId.Position, Vector3.Zero));

        rig.Panel.NameField.Text.ShouldBe("WallNorth");
        rig.Panel.Groups.SelectMany(g => g.Rows).Select(r => r.Id).ShouldBe([PropertyId.Position]);
    }

    [Fact]
    public void Rows_still_refresh_when_a_published_row_is_not_rendered()
    {
        // The panel skips two of the published rows, so its rows and the
        // snapshot's are no longer index-for-index. Getting that wrong reads
        // one row's value into another's box, which is silent and wrong rather
        // than a crash.
        var rig = new Rig();
        rig.Publish(
            Text(PropertyId.NodeName, "Wall"),
            new PropertyRow
            {
                Group = "Node", Name = "Id", Id = PropertyId.NodeId,
                Kind = PropertyKind.ReadOnlyText, Text = "id", Choices = [],
                PresentCount = 1, SelectionCount = 1,
            },
            Vector(PropertyId.Position, new Vector3(1f, 2f, 3f)),
            Vector(PropertyId.Scale, new Vector3(4f, 5f, 6f)));

        rig.Row(PropertyId.Position).Fields[1].Text.ShouldBe("2");
        rig.Row(PropertyId.Scale).Fields[2].Text.ShouldBe("6");
    }

    [Fact]
    public void A_kind_is_derived_from_the_sections_the_selection_grew()
    {
        var rig = new Rig();
        rig.Publish(
            Text(PropertyId.NodeName, "DoorwayCut"),
            Choice(PropertyId.BrushKind, "World", "World", "Part"),
            Choice(PropertyId.BrushOperation, "Subtractive", "Additive", "Subtractive"));

        // Subtractive outranks the kind, exactly as it does in the tree: a cut
        // renders nothing at all, so it is the fact worth leading with.
        rig.Panel.HeaderKind.ShouldBe("Cut");
    }

    [Fact]
    public void A_colour_is_shown_in_sRGB_and_read_back_as_linear()
    {
        var rig = new Rig();
        rig.Publish(new PropertyRow
        {
            Group = "Light", Name = "Color", Id = PropertyId.LightColor, Kind = PropertyKind.Color,
            Vector = Vector3.One, Choices = [], PresentCount = 1, SelectionCount = 1,
        });

        PropertyRowModel row = rig.Row(PropertyId.LightColor);
        row.Hex.ShouldBe("#FFFFFF");
        row.Fields[0].Text.ShouldBe("#FFFFFF");

        // Through the cell, on the same commit contract as every other field.
        PropertyFieldModel cell = row.Fields[0];
        cell.BeginEdit();
        cell.Text = "#808080";
        cell.Commit();

        // Mid grey in sRGB is about 0.216 in linear light, which is the whole
        // reason the panel converts rather than showing the stored numbers.
        rig.Edits.Count.ShouldBe(1);
        rig.Edits[0].Id.ShouldBe(PropertyId.LightColor);
        rig.Edits[0].Vector.X.ShouldBeInRange(0.20f, 0.23f);
    }

    [Fact]
    public void An_unreadable_colour_puts_the_last_good_one_back()
    {
        var rig = new Rig();
        rig.Publish(new PropertyRow
        {
            Group = "Light", Name = "Color", Id = PropertyId.LightColor, Kind = PropertyKind.Color,
            Vector = Vector3.One, Choices = [], PresentCount = 1, SelectionCount = 1,
        });

        PropertyRowModel row = rig.Row(PropertyId.LightColor);
        PropertyFieldModel cell = row.Fields[0];

        // A partial value is exactly what typing produces, and it must NOT be
        // parsed: "#8" and "#80" are both unreadable, and a box that reverted
        // on each one could never be typed into at all.
        cell.BeginEdit();
        cell.Text = "#80";
        rig.Publish(new PropertyRow
        {
            Group = "Light", Name = "Color", Id = PropertyId.LightColor, Kind = PropertyKind.Color,
            Vector = Vector3.One, Choices = [], PresentCount = 1, SelectionCount = 1,
        });

        rig.Edits.ShouldBeEmpty();
        cell.Text.ShouldBe("#80");

        // It is only on the commit that an unreadable value puts the last good
        // one back.
        cell.Commit();
        rig.Edits.ShouldBeEmpty();
        cell.Text.ShouldBe("#FFFFFF");
    }

    [Fact]
    public void A_committed_field_takes_the_next_refresh()
    {
        // The regression this exists for: Enter used to commit and then re-open
        // the edit, so the field stopped taking refreshes for as long as it kept
        // focus. Type a position, press Enter, drag the object in the viewport,
        // and the box went on showing the number you typed while the object was
        // somewhere else.
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, Vector3.Zero));

        PropertyFieldModel field = rig.Row(PropertyId.Position).Fields[0];
        field.BeginEdit();
        field.Text = "5";
        field.Commit();

        field.IsEditing.ShouldBeFalse();

        rig.Publish(Vector(PropertyId.Position, new Vector3(9f, 0f, 0f)));
        field.Text.ShouldBe("9");
    }

    [Fact]
    public void A_drag_on_one_axis_leaves_a_sibling_being_typed_into_alone()
    {
        // The scrub guard and the typing guard were once the same bool, and a
        // drag clears its guard on EVERY cell of the row (a vector drag writes
        // all three). So typing into x without committing, then dragging y,
        // handed x back to the refresh, and the next publish silently replaced
        // what had been typed. A pointer capture does not move keyboard focus,
        // so the two states are genuinely independent.
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, Vector3.Zero));

        PropertyRowModel row = rig.Row(PropertyId.Position);
        PropertyFieldModel x = row.Fields[0];
        PropertyFieldModel y = row.Fields[1];

        x.BeginEdit();
        x.Text = "12.5";

        foreach (PropertyFieldModel cell in row.Fields)
            cell.BeginScrub();
        row.ScrubTo(y, 3f);
        foreach (PropertyFieldModel cell in row.Fields)
            cell.EndScrub();

        rig.Publish(Vector(PropertyId.Position, new Vector3(0f, 3f, 0f)));

        x.Text.ShouldBe("12.5");
        x.IsEditing.ShouldBeTrue();
    }

    [Fact]
    public void A_scrub_is_one_gesture_and_the_field_ignores_refreshes_inside_it()
    {
        var rig = new Rig();
        rig.Publish(Vector(PropertyId.Position, Vector3.Zero));

        PropertyRowModel row = rig.Row(PropertyId.Position);
        PropertyFieldModel field = row.Fields[1];

        rig.Panel.BeginGesture(row.Name);
        foreach (PropertyFieldModel cell in row.Fields)
            cell.BeginScrub();

        row.ScrubTo(field, 1.5f);
        row.ScrubTo(field, 2.5f);

        // A stale publish arriving mid-drag must not pull the number back.
        rig.Publish(Vector(PropertyId.Position, Vector3.Zero));
        field.Text.ShouldBe("2.5");

        foreach (PropertyFieldModel cell in row.Fields)
            cell.EndScrub();
        rig.Panel.EndGesture(commit: true);

        rig.GesturesOpened.ShouldBe(["Position"]);
        rig.GesturesClosed.ShouldBe([true]);
        rig.Edits.Count.ShouldBe(2);
        rig.Edits.ShouldAllBe(e => e.Axes == PropertyAxes.Y);

        // And it takes refreshes again the moment the drag is over.
        rig.Publish(Vector(PropertyId.Position, new Vector3(0f, 7f, 0f)));
        field.Text.ShouldBe("7");
    }

    [Fact]
    public void An_empty_selection_reports_that_there_is_nothing_to_show()
    {
        var rig = new Rig();
        rig.Publish(0);

        rig.Panel.HasSelection.ShouldBeFalse();
        rig.Panel.Groups.ShouldBeEmpty();
    }

    [Fact]
    public void The_partial_label_says_how_far_a_bulk_edit_will_reach()
    {
        var rig = new Rig();
        rig.Publish(5, new PropertyRow
        {
            Group = "Brush", Name = "Kind", Id = PropertyId.BrushKind, Kind = PropertyKind.Choice,
            Text = "World", Choices = ["World", "Part"], PresentCount = 3, SelectionCount = 5,
        });

        PropertyRowModel row = rig.Row(PropertyId.BrushKind);
        row.IsPartial.ShouldBeTrue();
        row.PartialLabel.ShouldBe("3 of 5");
    }
}
