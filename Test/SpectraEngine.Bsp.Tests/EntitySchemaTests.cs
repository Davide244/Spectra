using SpectraEngine.Core.Entities;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The schema vocabulary's two rules that a reader gets wrong by writing the
/// obvious thing: NaN bounds, and reserved flag bits.
/// </summary>
public sealed class EntitySchemaTests
{
    private static KeyvalueDescriptor Speed(float min = float.NaN, float max = float.NaN, uint flags = 0) =>
        new("speed", "Speed", "How fast the door moves.", "100", KeyvalueType.Float,
            KeyvalueWidget.Auto, min, max, flags, KeyvalueDescriptor.NoChoices);

    [Fact]
    public void An_unbounded_descriptor_reports_no_bounds()
    {
        // NaN is unequal to itself, so the obvious `Min == float.NaN` test says
        // "bounded" for every descriptor ever written and then clamps against a
        // NaN, which yields NaN. HasMin/HasMax are the comparison.
        KeyvalueDescriptor unbounded = Speed();

        unbounded.HasMin.ShouldBeFalse();
        unbounded.HasMax.ShouldBeFalse();
#pragma warning disable CS1718 // Comparison made to same variable: that is the point.
        (unbounded.Min == unbounded.Min).ShouldBeFalse();
#pragma warning restore CS1718

        KeyvalueDescriptor bounded = Speed(min: 0f, max: 600f);
        bounded.HasMin.ShouldBeTrue();
        bounded.HasMax.ShouldBeTrue();
    }

    [Fact]
    public void A_zero_bound_is_a_real_bound()
    {
        // The failure a sentinel of 0 would have: "at least zero" is the most
        // common bound anyone writes.
        Speed(min: 0f).HasMin.ShouldBeTrue();
    }

    [Fact]
    public void The_defined_flag_bits_are_kept_and_the_reserved_ones_are_dropped()
    {
        // Bits 3 to 7 are claimed by later work. A definition produced by a newer
        // tool must lose what this engine does not understand rather than acting
        // on it by accident.
        uint written = KeyvalueFlags.ReadOnly | KeyvalueFlags.RequiresRestart | (1u << 5);

        KeyvalueFlags.Mask(written).ShouldBe(KeyvalueFlags.ReadOnly | KeyvalueFlags.RequiresRestart);

        KeyvalueDescriptor descriptor = Speed(flags: KeyvalueFlags.Mask(written));
        descriptor.IsReadOnly.ShouldBeTrue();
        descriptor.RequiresRestart.ShouldBeTrue();
        descriptor.IsHiddenInEditor.ShouldBeFalse();
    }

    [Fact]
    public void The_widget_vocabulary_is_closed()
    {
        KeyvalueWidget.IsDefined(KeyvalueWidget.Auto).ShouldBeTrue();
        KeyvalueWidget.IsDefined(KeyvalueWidget.Flags).ShouldBeTrue();
        KeyvalueWidget.IsDefined(6).ShouldBeFalse();
    }

    [Fact]
    public void A_schema_carries_its_keyvalues_in_declaration_order()
    {
        // Declaration order is layout order in a property panel and record order
        // in an exported schema, so it is data rather than presentation.
        var schema = new EntitySchema(
            "func_door",
            displayName: "Door",
            group: "Brush Entities",
            placement: EntityPlacement.Brush,
            origin: EntityOrigin.Luau,
            keyvalues: [Speed(), Speed(min: 0f)],
            inputs: ["Open", "Close"],
            outputs: ["OnFullyOpen"]);

        schema.ClassName.ShouldBe("func_door");
        schema.Placement.ShouldBe(EntityPlacement.Brush);
        schema.Origin.ShouldBe(EntityOrigin.Luau);
        schema.Keyvalues.Count.ShouldBe(2);
        schema.Keyvalues[1].HasMin.ShouldBeTrue();
        schema.Inputs[0].ShouldBe("Open");
        schema.Outputs.Count.ShouldBe(1);
    }

    [Fact]
    public void A_schema_with_no_class_name_is_refused()
    {
        // It could not be registered, looked up or written; refused here rather
        // than three layers down where the message names none of that.
        Should.Throw<ArgumentException>(() => new EntitySchema(""));
    }

    [Fact]
    public void A_schema_declaring_nothing_carries_empty_lists_rather_than_nulls()
    {
        var schema = new EntitySchema("info_player_start");

        schema.Keyvalues.Count.ShouldBe(0);
        schema.Inputs.Count.ShouldBe(0);
        schema.Outputs.Count.ShouldBe(0);
        schema.DisplayName.ShouldBe("");
    }
}
