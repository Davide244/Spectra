using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Inspection;
using SpectraEngine.Editing.Commands;
using SpectraEngine.Editor.Shell;
using System.Collections.Generic;
using System.Linq;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// The shell's half of entities: the panel that renders their rows, and the
/// menu that places them.
/// </summary>
/// <remarks>
/// <b>Both halves fail silently, in the same way, for the same reason.</b> An
/// entity's properties are named by the class it declares rather than by this
/// engine, so every keyvalue row wears one <see cref="PropertyId"/> and is told
/// apart only by its key - and a panel that compares rows on the id alone keeps
/// the controls it already built and then pours one key's value into another
/// key's box. Nothing throws. The menu's failure is the mirror: built from the
/// in-process registry instead of the parsed catalogue it looks identical
/// today and offers the wrong classes the day a project ships definitions this
/// build has no C# for.
/// </remarks>
public sealed class EntityShellTests
{
    // --- fixtures -----------------------------------------------------------

    private static PropertyRow ClassRow(string className) => new()
    {
        Group = NodeInspector.EntityGroup, Name = "Class", Id = PropertyId.EntityClassname,
        Key = "", Kind = PropertyKind.ReadOnlyText, Text = className, Choices = [],
        PresentCount = 1, SelectionCount = 1,
    };

    private static PropertyRow KeyvalueText(string key, string value) => new()
    {
        Group = NodeInspector.EntityGroup, Name = key, Id = PropertyId.EntityKeyvalue,
        Key = key, Kind = PropertyKind.Text, Text = value, Choices = [],
        PresentCount = 1, SelectionCount = 1,
    };

    private static PropertyRow KeyvalueNumber(string key, float value) => new()
    {
        Group = NodeInspector.EntityGroup, Name = key, Id = PropertyId.EntityKeyvalue,
        Key = key, Kind = PropertyKind.Number, Number = value, Choices = [],
        PresentCount = 1, SelectionCount = 1,
    };

    private static PropertyRow KeyvalueFlag(string key, bool value) => new()
    {
        Group = NodeInspector.EntityGroup, Name = key, Id = PropertyId.EntityKeyvalue,
        Key = key, Kind = PropertyKind.Boolean, Flag = value, Choices = [],
        PresentCount = 1, SelectionCount = 1,
    };

    private static PropertyRow KeyvalueVector(string key, System.Numerics.Vector3 value) => new()
    {
        Group = NodeInspector.EntityGroup, Name = key, Id = PropertyId.EntityKeyvalue,
        Key = key, Kind = PropertyKind.Vector3, Vector = value, Choices = [],
        PresentCount = 1, SelectionCount = 1,
    };

    private sealed class Rig
    {
        public List<PropertyEdit> Edits { get; } = [];
        public PropertyPanelModel Panel { get; }

        public Rig() => Panel = new PropertyPanelModel(Edits.Add, _ => { }, _ => { });

        public void Publish(params PropertyRow[] rows) => Panel.Apply(rows, 1);

        public IEnumerable<PropertyRowModel> Rows => Panel.Groups.SelectMany(g => g.Rows);

        public PropertyRowModel Row(string key) =>
            Rows.Single(r => r.Id == PropertyId.EntityKeyvalue && r.Key == key);
    }

    /// <summary>A catalogue built the way a session builds one: through the bytes.</summary>
    private static EntitySchemaCatalog Catalog(params EntitySchema[] schemas) =>
        EntitySchemaCatalog.LoadFromSentDef(SentDef.Write(schemas));

    private static EntitySchema Schema(
        string className, string display = "", EntityPlacement placement = EntityPlacement.Point) =>
        new(className, display, "Logic", placement);

    // --- the shape trap -----------------------------------------------------

    [Fact]
    public void Two_classes_of_the_same_LENGTH_rebuild_the_panel_rather_than_refreshing_it()
    {
        // THE SHELL HALF OF THE TRAP. Every keyvalue row wears
        // PropertyId.EntityKeyvalue, so two classes that happen to declare the
        // same NUMBER of properties compare "same shape" on ids alone. The
        // panel would keep the controls it built for (p, q) and refresh them
        // from the rows for (r, s): the box labelled p would show r's value,
        // and committing it would write r. Nothing throws and nothing logs.
        var rig = new Rig();
        rig.Publish(ClassRow("first_class"), KeyvalueText("p", "1"), KeyvalueText("q", "2"));

        rig.Rows.Select(r => r.Key).ShouldBe(["", "p", "q"]);

        rig.Publish(ClassRow("second_class"), KeyvalueText("r", "3"), KeyvalueText("s", "4"));

        rig.Rows.Select(r => r.Key).ShouldBe(["", "r", "s"]);
        rig.Row("r").Fields[0].Text.ShouldBe("3");
        rig.Row("s").Fields[0].Text.ShouldBe("4");
    }

    [Fact]
    public void The_same_class_twice_keeps_the_controls_it_already_built()
    {
        // The other half, and the reason the comparison cannot simply be "did
        // anything change": assigning fresh rows per publish resets scroll,
        // drops focus and destroys a half-typed value thirty times a second.
        var rig = new Rig();
        rig.Publish(ClassRow("logic_relay"), KeyvalueText("StartDisabled", "0"));

        PropertyRowModel before = rig.Row("StartDisabled");

        rig.Publish(ClassRow("logic_relay"), KeyvalueText("StartDisabled", "1"));

        rig.Row("StartDisabled").ShouldBeSameAs(before);
        before.Fields[0].Text.ShouldBe("1");
    }

    // --- a commit names its key, and crosses as wire text --------------------

    [Fact]
    public void An_entity_edit_carries_the_key_and_the_wire_text()
    {
        // A keyvalue's storage IS its wire string, and PropertyEditor's entity
        // arm reads Text alone: an edit that arrived carrying only Number would
        // be refused for an empty key and, with a key, would write the empty
        // string over whatever the author had.
        var rig = new Rig();
        rig.Publish(
            ClassRow("logic_timer"),
            KeyvalueNumber("Interval", 2f),
            KeyvalueFlag("StartDisabled", false),
            KeyvalueVector("Offset", new System.Numerics.Vector3(1f, 2f, 3f)));

        PropertyFieldModel interval = rig.Row("Interval").Fields[0];
        interval.BeginEdit();
        interval.Text = "5.5";
        interval.Commit();

        rig.Edits[^1].Id.ShouldBe(PropertyId.EntityKeyvalue);
        rig.Edits[^1].Key.ShouldBe("Interval");
        rig.Edits[^1].Text.ShouldBe("5.5");

        rig.Row("StartDisabled").Flag = true;
        rig.Edits[^1].Key.ShouldBe("StartDisabled");
        rig.Edits[^1].Text.ShouldBe("1");

        PropertyFieldModel y = rig.Row("Offset").Fields[1];
        y.BeginEdit();
        y.Text = "9";
        y.Commit();

        // Three components on the edited side too: PropertyEditor splices at
        // the TOKEN level and writes the value WHOLE when either side is not
        // exactly three parts, which for a one-token string would replace a
        // whole vector with a single number.
        rig.Edits[^1].Key.ShouldBe("Offset");
        rig.Edits[^1].Axes.ShouldBe(PropertyAxes.Y);
        rig.Edits[^1].Text.Split(' ').Length.ShouldBe(3);
        rig.Edits[^1].Text.Split(' ')[1].ShouldBe("9");
    }

    [Fact]
    public void A_non_entity_edit_still_carries_its_typed_value()
    {
        // The entity arm must not swallow every other property: a light's
        // intensity is a float its command reads from Number, and a Text-only
        // edit would reach BuildLight with nothing in it.
        var rig = new Rig();
        rig.Publish(new PropertyRow
        {
            Group = "Light", Name = "Intensity", Id = PropertyId.LightIntensity,
            Key = "", Kind = PropertyKind.Number, Number = 10f, Choices = [],
            PresentCount = 1, SelectionCount = 1,
        });

        PropertyFieldModel field = rig.Rows.Single().Fields[0];
        field.BeginEdit();
        field.Text = "12";
        field.Commit();

        rig.Edits[^1].Id.ShouldBe(PropertyId.LightIntensity);
        rig.Edits[^1].Key.ShouldBe("");
        rig.Edits[^1].Number.ShouldBe(12f);
    }

    // --- the header ---------------------------------------------------------

    [Fact]
    public void The_kind_chip_reads_Entity()
    {
        var rig = new Rig();
        rig.Publish(ClassRow("logic_relay"), KeyvalueText("StartDisabled", "0"));

        rig.Panel.HeaderKind.ShouldBe("Entity");
    }

    [Fact]
    public void A_class_the_catalogue_declares_gets_no_badge()
    {
        var rig = new Rig();
        rig.Panel.Schemas = Catalog(Schema("logic_relay"));

        rig.Publish(ClassRow("logic_relay"));

        rig.Panel.HasUnknownClass.ShouldBeFalse();
        rig.Panel.UnknownClassLabel.ShouldBe("");
    }

    [Fact]
    public void A_class_nothing_declares_gets_a_standing_badge()
    {
        // The rows below it are still this map's own data, editable and
        // preserved; what is missing is the schema that would say which
        // properties exist. Without the badge a mistyped class name looks
        // exactly like a class with nothing declared.
        var rig = new Rig();
        rig.Panel.Schemas = Catalog(Schema("logic_relay"));

        rig.Publish(ClassRow("logic_relayy"), KeyvalueText("StartDisabled", "0"));

        rig.Panel.HasUnknownClass.ShouldBeTrue();
        rig.Panel.UnknownClassLabel.ShouldBe(
            "Unknown class 'logic_relayy' - properties preserved as text");
    }

    [Fact]
    public void The_badge_clears_when_the_selection_moves_off_the_entity()
    {
        var rig = new Rig();
        rig.Panel.Schemas = Catalog(Schema("logic_relay"));
        rig.Publish(ClassRow("nothing_declares_this"));
        rig.Panel.HasUnknownClass.ShouldBeTrue();

        rig.Panel.Apply([], 0);

        rig.Panel.HasUnknownClass.ShouldBeFalse();
    }

    [Fact]
    public void No_session_badges_nothing()
    {
        // A null catalogue means no session, which is not the same as "this
        // class does not exist": every class would be unknown, and the panel
        // would badge a map it has simply not been told about yet.
        var rig = new Rig();
        rig.Panel.Schemas = null;

        rig.Publish(ClassRow("anything_at_all"));

        rig.Panel.HasUnknownClass.ShouldBeFalse();
    }

    // --- the insert menu ----------------------------------------------------

    [Fact]
    public void The_insert_menu_lists_the_PARSED_catalogue_and_nothing_else()
    {
        // Built from EntityCatalog.Shared instead, this returns the classes
        // this PROCESS can construct - which in this test process is none, and
        // in a shipped editor is the engine's own builtins rather than the
        // project's. The catalogue below exists only as bytes; nothing has
        // registered "test_widget" anywhere.
        EntitySchemaCatalog parsed = Catalog(
            Schema("test_widget", "Test widget"),
            Schema("test_beacon"));

        List<EntityInsertItem> items = EntityInsertMenu.Build(parsed);

        items.Select(i => i.ClassName).ShouldBe(["test_beacon", "test_widget"]);
        items.Single(i => i.ClassName == "test_widget").Display.ShouldBe("Test widget");

        // No display name declared, so the class name IS the label - never an
        // empty menu entry.
        items.Single(i => i.ClassName == "test_beacon").Display.ShouldBe("test_beacon");

        EntityCatalog.Shared.TryGetSchema("test_widget", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_brush_class_is_not_offered_by_a_point_insert()
    {
        // A brush class gives behaviour to geometry this insert does not
        // create, so placing one would make a node declaring it is a volume and
        // carrying no volume - invalid on its face and invisible in the
        // viewport for a reason nothing on screen explains.
        EntitySchemaCatalog parsed = Catalog(
            Schema("trigger_once", placement: EntityPlacement.Brush),
            Schema("logic_relay", placement: EntityPlacement.Abstract));

        EntityInsertMenu.Build(parsed)
            .Select(i => i.ClassName)
            .ShouldBe(["logic_relay"]);
    }

    [Fact]
    public void An_entry_carries_the_wire_name_the_menu_will_insert()
    {
        // The LABEL is the display name and the payload is the class name; a
        // menu that inserted what it printed would place "Test widget", which
        // resolves in no catalogue.
        List<EntityInsertItem> items = EntityInsertMenu.Build(Catalog(Schema("test_widget", "Test widget")));

        items[0].Display.ShouldBe("Test widget");
        items[0].ClassName.ShouldBe("test_widget");
    }

    [Fact]
    public void The_shell_model_publishes_and_clears_the_classes_with_the_session()
    {
        // Left standing, the start page's Object menu would offer entities for
        // a project that is closed, and the next project's menu would open
        // showing the previous one's classes.
        var shell = new ShellModel();
        shell.HasEntityClasses.ShouldBeFalse();

        shell.SetEntityClasses(EntityInsertMenu.Build(Catalog(Schema("test_widget"))));
        shell.HasEntityClasses.ShouldBeTrue();
        shell.EntityClasses.Select(i => i.ClassName).ShouldBe(["test_widget"]);

        shell.SetEntityClasses(null);
        shell.EntityClasses.ShouldBeEmpty();
        shell.HasEntityClasses.ShouldBeFalse();
    }

    [Fact]
    public void No_session_offers_no_classes()
    {
        EntityInsertMenu.Build(null).ShouldBeEmpty();
    }
}
