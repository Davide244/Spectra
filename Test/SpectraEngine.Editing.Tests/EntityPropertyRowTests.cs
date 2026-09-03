using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Core.Scene;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// An entity payload becoming property rows: which rows exist, what type each
/// one is, and what a multi-selection merges them into.
/// </summary>
/// <remarks>
/// <b>The schema decides which rows exist and the payload decides what they
/// hold</b>, which is the whole reason a map naming a class this build has never
/// heard of is worth opening. Every failure in this area renders a panel rather
/// than throwing, so the assertions are about identity and order rather than
/// about anything blowing up.
/// </remarks>
public sealed class EntityPropertyRowTests
{
    // The catalogue can only be built from bytes, deliberately: going through
    // the .sentdef round trip is what stops an in-process editor and an
    // out-of-process one reading two different schemas.
    private static EntitySchemaCatalog Catalog(params EntitySchema[] schemas) =>
        EntitySchemaCatalog.LoadFromSentDef(SentDef.Write(schemas));

    private static KeyvalueDescriptor Kv(
        string name,
        KeyvalueType type,
        string value = "",
        uint flags = 0u,
        IReadOnlyList<(string Value, string Display)>? choices = null) =>
        new(name, "", "", value, type, KeyvalueWidget.Auto, float.NaN, float.NaN, flags,
            choices ?? KeyvalueDescriptor.NoChoices);

    private static SceneNode Placed(string className, params (string Key, string Value)[] keyvalues)
    {
        var data = new EntityData(className);
        foreach ((string key, string value) in keyvalues)
            data.SetValue(key, value);

        return new SceneNode(className) { Entity = data };
    }

    private static List<PropertyRow> Describe(SceneNode node, EntitySchemaCatalog? schemas = null)
    {
        var rows = new List<PropertyRow>();
        NodeInspector.Describe(node, rows, schemas);
        return rows;
    }

    private static PropertyRow Row(IReadOnlyList<PropertyRow> rows, string key)
    {
        foreach (PropertyRow row in rows)
        {
            if (row.Key == key)
                return row;
        }

        throw new Xunit.Sdk.XunitException($"No row carries the key '{key}'.");
    }

    // --- the shape regression ------------------------------------------------

    [Fact]
    public void Two_classes_with_equally_long_schemas_are_not_the_same_shape()
    {
        // Every keyvalue row wears one id, so comparing ids alone reports these
        // two entities as the same shape: the panel then keeps the controls it
        // already built and the next refresh pours speed's value into range's
        // editor box, with nothing thrown and nothing logged.
        EntitySchemaCatalog catalog = Catalog(
            new EntitySchema("a_thing", keyvalues:
            [
                Kv("speed", KeyvalueType.Float, "1"),
                Kv("loud", KeyvalueType.Bool, "0"),
            ]),
            new EntitySchema("b_thing", keyvalues:
            [
                Kv("range", KeyvalueType.Float, "1"),
                Kv("dark", KeyvalueType.Bool, "0"),
            ]));

        List<PropertyRow> first = Describe(Placed("a_thing"), catalog);
        List<PropertyRow> second = Describe(Placed("b_thing"), catalog);

        // The premise: the id sequences really are identical, so only the keys
        // can tell these apart.
        first.Count.ShouldBe(second.Count);
        for (int i = 0; i < first.Count; i++)
            first[i].Id.ShouldBe(second[i].Id);

        var shape = new PropertyRowShape();
        shape.CaptureFrom(first);

        shape.Matches(first).ShouldBeTrue();
        shape.Matches(second).ShouldBeFalse();
    }

    [Fact]
    public void One_class_keeps_its_shape_across_publishes_while_its_values_move()
    {
        // The mirror of the test above, and just as load-bearing: a shape that
        // moved when a NUMBER moved would rebuild the whole panel on every
        // frame of a drag, resetting scroll and dropping focus as it went.
        EntitySchemaCatalog catalog = Catalog(
            new EntitySchema("mover", keyvalues: [Kv("speed", KeyvalueType.Float, "1")]));

        var shape = new PropertyRowShape();
        shape.CaptureFrom(Describe(Placed("mover", ("speed", "1")), catalog));

        shape.Matches(Describe(Placed("mover", ("speed", "97.5")), catalog)).ShouldBeTrue();
    }

    // --- which rows exist ----------------------------------------------------

    [Fact]
    public void The_classname_is_shown_and_cannot_be_edited()
    {
        // A classname is not a property, it is which set of properties there
        // ARE. Retyping it would rewrite the section under the reader's cursor.
        PropertyRow row = Describe(Placed("light_omni"))
            .Find(r => r.Id == PropertyId.EntityClassname);
        row.Group.ShouldBe(NodeInspector.EntityGroup);
        row.Kind.ShouldBe(PropertyKind.ReadOnlyText);
        row.Text.ShouldBe("light_omni");
        row.IsEditable.ShouldBeFalse();
    }

    [Fact]
    public void A_node_with_no_entity_grows_no_entity_rows()
    {
        Describe(new SceneNode("plain")).ShouldNotContain(r => r.Group == NodeInspector.EntityGroup);
    }

    [Theory]
    [InlineData(KeyvalueType.Bool, "1", PropertyKind.Boolean)]
    [InlineData(KeyvalueType.Int, "7", PropertyKind.Number)]
    [InlineData(KeyvalueType.Float, "2.5", PropertyKind.Number)]
    [InlineData(KeyvalueType.Vec3, "1 2 3", PropertyKind.Vector3)]
    [InlineData(KeyvalueType.Angles, "0 90 0", PropertyKind.Vector3)]
    [InlineData(KeyvalueType.Color, "1 0.5 0", PropertyKind.Color)]
    [InlineData(KeyvalueType.String, "hello", PropertyKind.Text)]
    [InlineData(KeyvalueType.TargetName, "door", PropertyKind.Text)]
    [InlineData(KeyvalueType.AssetModel, "Models/x.obj", PropertyKind.Text)]
    [InlineData(KeyvalueType.Flags, "3", PropertyKind.Text)]
    [InlineData(KeyvalueType.Vec2, "1 2", PropertyKind.Text)]
    public void A_declared_type_picks_the_editor(KeyvalueType type, string value, PropertyKind kind)
    {
        EntitySchemaCatalog catalog = Catalog(
            new EntitySchema("thing", keyvalues: [Kv("p", type)]));

        Row(Describe(Placed("thing", ("p", value)), catalog), "p").Kind.ShouldBe(kind);
    }

    [Fact]
    public void A_choices_row_offers_the_wire_tokens_rather_than_the_display_names()
    {
        // The row's value is the wire string and a dropdown is matched by text,
        // so display names here would leave every choice unselected and the
        // first edit would write a display name into the map.
        EntitySchemaCatalog catalog = Catalog(new EntitySchema("door", keyvalues:
        [
            Kv("movedir", KeyvalueType.Choices, "up", choices: [("up", "Up"), ("down", "Down")]),
        ]));

        PropertyRow row = Row(Describe(Placed("door"), catalog), "movedir");
        row.Kind.ShouldBe(PropertyKind.Choice);
        row.Text.ShouldBe("up");
        row.Choices.ShouldBe(["up", "down"]);
    }

    [Fact]
    public void An_angles_row_says_it_is_degrees()
    {
        EntitySchemaCatalog catalog = Catalog(
            new EntitySchema("thing", keyvalues: [Kv("angles", KeyvalueType.Angles, "0 0 0")]));

        Row(Describe(Placed("thing"), catalog), "angles").Unit.ShouldBe("deg");
    }

    [Fact]
    public void A_value_the_declared_type_cannot_carry_degrades_to_text()
    {
        // A typed row would parse it to zero and then write that zero back on
        // the next commit, destroying what the author actually wrote.
        EntitySchemaCatalog catalog = Catalog(
            new EntitySchema("thing", keyvalues: [Kv("size", KeyvalueType.Vec3, "1 1 1")]));

        PropertyRow row = Row(Describe(Placed("thing", ("size", "wide")), catalog), "size");
        row.Kind.ShouldBe(PropertyKind.Text);
        row.Text.ShouldBe("wide");
    }

    [Fact]
    public void A_read_only_descriptor_is_shown_and_not_edited()
    {
        EntitySchemaCatalog catalog = Catalog(new EntitySchema("thing", keyvalues:
        [
            Kv("build", KeyvalueType.String, "42", KeyvalueFlags.ReadOnly),
        ]));

        Row(Describe(Placed("thing"), catalog), "build").IsEditable.ShouldBeFalse();
    }

    [Fact]
    public void A_hidden_descriptor_gets_no_row_even_when_the_node_stores_it()
    {
        // "Bound and carried, never shown" is what the flag says, and treating
        // the key as unnamed would bring it straight back as an unknown row.
        EntitySchemaCatalog catalog = Catalog(new EntitySchema("thing", keyvalues:
        [
            Kv("secret", KeyvalueType.String, "", KeyvalueFlags.HideInEditor),
            Kv("shown", KeyvalueType.String),
        ]));

        List<PropertyRow> rows = Describe(Placed("thing", ("secret", "x"), ("shown", "y")), catalog);
        rows.ShouldNotContain(r => r.Key == "secret");
        rows.ShouldContain(r => r.Key == "shown");
    }

    // --- values and order ----------------------------------------------------

    [Fact]
    public void An_unauthored_key_shows_the_declared_default()
    {
        // Showing an empty field would be a lie about what the level does: the
        // default is the value the entity will actually run with.
        EntitySchemaCatalog catalog = Catalog(
            new EntitySchema("thing", keyvalues: [Kv("speed", KeyvalueType.Float, "100")]));

        Row(Describe(Placed("thing"), catalog), "speed").Number.ShouldBe(100f);
        Row(Describe(Placed("thing", ("speed", "3")), catalog), "speed").Number.ShouldBe(3f);
    }

    [Fact]
    public void Declared_rows_follow_the_schemas_order_not_the_authored_one()
    {
        // Declaration order is the order the schema author meant, and it is not
        // this panel's to reshuffle - alphabetical would scramble it.
        EntitySchemaCatalog catalog = Catalog(new EntitySchema("thing", keyvalues:
        [
            Kv("zulu", KeyvalueType.String),
            Kv("alpha", KeyvalueType.String),
            Kv("mike", KeyvalueType.String),
        ]));

        List<PropertyRow> rows = Describe(
            Placed("thing", ("mike", "3"), ("alpha", "2"), ("zulu", "1")), catalog);

        KeysOf(rows).ShouldBe(["zulu", "alpha", "mike"]);
    }

    [Fact]
    public void A_placeholder_entity_shows_every_key_it_carries()
    {
        // The whole reason a map naming an unknown class is worth opening: the
        // data is still in the file, so a panel that showed nothing would make
        // it invisible while it was still there. Authored order, as written.
        List<PropertyRow> rows = Describe(
            Placed("game_from_another_engine", ("wait", "3"), ("target", "door"), ("aa", "1")));

        KeysOf(rows).ShouldBe(["wait", "target", "aa"]);
        rows.ShouldAllBe(r => r.Key == "" || r.Kind == PropertyKind.Text);
    }

    [Fact]
    public void Keys_the_schema_does_not_name_are_kept_after_the_ones_it_does()
    {
        EntitySchemaCatalog catalog = Catalog(
            new EntitySchema("thing", keyvalues: [Kv("speed", KeyvalueType.Float, "1")]));

        List<PropertyRow> rows = Describe(
            Placed("thing", ("leftover", "7"), ("speed", "2")), catalog);

        KeysOf(rows).ShouldBe(["speed", "leftover"]);
        Row(rows, "leftover").Kind.ShouldBe(PropertyKind.Text);
    }

    [Fact]
    public void A_duplicated_key_produces_one_row()
    {
        // A hand-written file may legally carry the same key twice, and two rows
        // sharing an identity would collide in the merge and in the shape. The
        // first wins, matching the value the entity actually binds.
        var data = new EntityData("thing");
        data.Keyvalues.Add(new KeyValuePair<string, string>("wait", "1"));
        data.Keyvalues.Add(new KeyValuePair<string, string>("wait", "2"));
        var node = new SceneNode("thing") { Entity = data };

        List<PropertyRow> rows = Describe(node);
        KeysOf(rows).ShouldBe(["wait"]);
        Row(rows, "wait").Text.ShouldBe("1");
    }

    // --- merging a multi-selection -------------------------------------------

    [Fact]
    public void A_selection_shows_the_union_of_its_entities_keys()
    {
        // Hiding a row because only part of the selection carries it would mean
        // selecting one extra object silently removes the field somebody was
        // about to type into.
        var rows = new List<PropertyRow>();
        NodeInspector.Describe(
            [
                Placed("a", ("shared", "1"), ("only_a", "x")),
                Placed("b", ("shared", "1"), ("only_b", "y")),
            ],
            rows);

        Row(rows, "shared").PresentCount.ShouldBe(2);
        Row(rows, "only_a").PresentCount.ShouldBe(1);
        Row(rows, "only_a").IsPartial.ShouldBeTrue();
        Row(rows, "only_b").PresentCount.ShouldBe(1);
    }

    [Fact]
    public void A_merged_key_reports_mixing_per_axis()
    {
        // "Put all of these on the floor" sets y and must leave x and z alone,
        // which is only expressible if the merge tracks the axes separately.
        EntitySchemaCatalog catalog = Catalog(
            new EntitySchema("thing", keyvalues: [Kv("offset", KeyvalueType.Vec3, "0 0 0")]));

        var rows = new List<PropertyRow>();
        NodeInspector.Describe(
            [
                Placed("thing", ("offset", "1 2 3")),
                Placed("thing", ("offset", "1 9 3")),
            ],
            rows,
            catalog);

        Row(rows, "offset").MixedAxes.ShouldBe(PropertyAxes.Y);
    }

    [Fact]
    public void Merged_keys_are_compared_as_exact_strings()
    {
        // A tolerance would report two different spellings as settled and then
        // write one over the other on the next bulk edit.
        var rows = new List<PropertyRow>();
        NodeInspector.Describe(
            [Placed("thing", ("wait", "1")), Placed("thing", ("wait", "1.0"))],
            rows);

        Row(rows, "wait").IsMixed.ShouldBeTrue();
    }

    [Fact]
    public void A_merged_selection_keeps_one_row_per_key()
    {
        // Merging on the id alone would fold every keyvalue into one row, since
        // they all wear PropertyId.EntityKeyvalue.
        var rows = new List<PropertyRow>();
        NodeInspector.Describe(
            [Placed("thing", ("a", "1"), ("b", "2")), Placed("thing", ("a", "1"), ("b", "2"))],
            rows);

        KeysOf(rows).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void A_merged_selection_keeps_schema_order_within_the_entity_section()
    {
        EntitySchemaCatalog catalog = Catalog(new EntitySchema("thing", keyvalues:
        [
            Kv("zulu", KeyvalueType.String),
            Kv("alpha", KeyvalueType.String),
        ]));

        var rows = new List<PropertyRow>();
        NodeInspector.Describe([Placed("thing"), Placed("thing")], rows, catalog);

        KeysOf(rows).ShouldBe(["zulu", "alpha"]);
    }

    [Fact]
    public void The_entity_section_stays_one_contiguous_run()
    {
        // The panel groups by a run of equal group names, so a section split in
        // two would render as two sections with the same heading.
        var light = new SceneNode("lamp")
        {
            Light = new Light { Kind = LightKind.Point },
            Entity = new EntityData("light_omni"),
        };
        light.Entity!.SetValue("brightness", "2");

        List<PropertyRow> rows = Describe(light);

        int first = rows.FindIndex(r => r.Group == NodeInspector.EntityGroup);
        int last = rows.FindLastIndex(r => r.Group == NodeInspector.EntityGroup);
        int count = 0;
        foreach (PropertyRow row in rows)
        {
            if (row.Group == NodeInspector.EntityGroup)
                count++;
        }

        first.ShouldBeGreaterThanOrEqualTo(0);
        (last - first + 1).ShouldBe(count);
    }

    [Fact]
    public void Every_row_that_is_not_an_entity_keyvalue_carries_an_empty_key()
    {
        // The key is the second half of a row's identity, so a stray key on an
        // ordinary row would make two of them fail to match themselves.
        var node = new SceneNode("lamp")
        {
            Light = new Light { Kind = LightKind.Point },
            LocalPosition = new Vector3(1f, 2f, 3f),
        };

        foreach (PropertyRow row in Describe(node))
            row.Key.ShouldBe("");
    }

    private static List<string> KeysOf(IReadOnlyList<PropertyRow> rows)
    {
        var keys = new List<string>();
        foreach (PropertyRow row in rows)
        {
            if (row.Id == PropertyId.EntityKeyvalue)
                keys.Add(row.Key);
        }

        return keys;
    }
}
