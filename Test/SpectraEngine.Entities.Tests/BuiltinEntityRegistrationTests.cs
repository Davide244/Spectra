using SpectraEngine.Core.Entities;

namespace SpectraEngine.Entities.Tests;

/// <summary>
/// The anchor and the generated schemas: what a host gets by referencing this
/// assembly and calling one method.
/// </summary>
/// <remarks>
/// <b>This is the one test class that touches
/// <see cref="EntityCatalog.Shared"/>, and reading it FREEZES it.</b> Every other
/// test here builds its own catalogue for exactly that reason. The registrations
/// themselves happen in generated module initializers, which run when the
/// assembly is loaded, so they are already in place before any test can freeze
/// anything.
/// </remarks>
public sealed class BuiltinEntityRegistrationTests
{
    [Fact]
    public void The_anchor_puts_every_built_in_class_in_the_shared_catalogue()
    {
        BuiltinEntities.EnsureRegistered();

        EntityCatalog.Shared.TryCreate("logic_relay", out Entity? relay).ShouldBeTrue();
        EntityCatalog.Shared.TryCreate("logic_timer", out Entity? timer).ShouldBeTrue();
        EntityCatalog.Shared.TryCreate("math_counter", out Entity? counter).ShouldBeTrue();

        relay.ShouldBeOfType<LogicRelay>();
        timer.ShouldBeOfType<LogicTimer>();
        counter.ShouldBeOfType<MathCounter>();
    }

    [Fact]
    public void The_anchor_lists_exactly_the_classes_it_claims_to()
    {
        // The check inside EnsureRegistered is what makes reading Schemas
        // observable, so the JIT cannot elide the static initializer that keeps
        // these three types alive in a trimmed build.
        BuiltinEntities.Schemas.Count.ShouldBe(BuiltinEntities.ClassCount);
        BuiltinEntities.EnsureRegistered();
    }

    [Fact]
    public void The_relays_generated_schema_describes_what_the_class_declares()
    {
        EntitySchema schema = LogicRelay.SpectraSchema;

        schema.ClassName.ShouldBe("logic_relay");
        LogicRelay.SpectraClassName.ShouldBe("logic_relay");

        // Derived from the wire name because the class states no Display: an
        // editor showing "logic_relay" in a palette is a tool that has not been
        // finished.
        schema.DisplayName.ShouldBe("Logic Relay");
        schema.Group.ShouldBe("Logic");
        schema.Placement.ShouldBe(EntityPlacement.Abstract);
        schema.Origin.ShouldBe(EntityOrigin.EngineCSharp);

        schema.Inputs.ShouldBe(["Trigger", "Enable", "Disable", "Toggle"]);
        schema.Outputs.ShouldBe([LogicRelay.OnTrigger]);

        schema.Keyvalues.Count.ShouldBe(1);
        KeyvalueDescriptor startDisabled = schema.Keyvalues[0];
        startDisabled.Name.ShouldBe("startdisabled");
        startDisabled.Display.ShouldBe("Start disabled");
        startDisabled.Type.ShouldBe(KeyvalueType.Bool);
        startDisabled.Default.ShouldBe("0");
        startDisabled.HasMin.ShouldBeFalse();
        startDisabled.HasMax.ShouldBeFalse();
    }

    [Fact]
    public void The_timers_generated_schema_carries_the_bound_the_class_declares()
    {
        EntitySchema schema = LogicTimer.SpectraSchema;
        KeyvalueDescriptor refire = schema.Keyvalues.Single(k => k.Name == "refiretime");

        refire.Type.ShouldBe(KeyvalueType.Float);
        refire.HasMin.ShouldBeTrue();
        refire.Min.ShouldBe(LogicTimer.MinimumInterval);

        // NaN for "no bound", which is why HasMax is asked rather than comparing
        // Max to anything: NaN is unequal to itself.
        refire.HasMax.ShouldBeFalse();
    }

    [Fact]
    public void Keyvalues_are_in_declaration_order_which_is_the_order_a_panel_lays_them_out()
    {
        MathCounter.SpectraSchema.Keyvalues
            .Select(keyvalue => keyvalue.Name)
            .ShouldBe(["startvalue", "min", "max"]);
    }

    [Fact]
    public void The_counters_three_outputs_are_all_declared()
    {
        MathCounter.SpectraSchema.Outputs
            .ShouldBe([MathCounter.OutValue, MathCounter.OnHitMax, MathCounter.OnHitMin]);
    }

    [Fact]
    public void An_output_constant_spells_its_own_member_name()
    {
        // There is no second spelling for the two to disagree about: the schema
        // takes the member's NAME, and the constant a fire site names has to be
        // the same string or a wire authored against one would never resolve.
        LogicRelay.OnTrigger.ShouldBe(nameof(LogicRelay.OnTrigger));
        LogicTimer.OnTimer.ShouldBe(nameof(LogicTimer.OnTimer));
        MathCounter.OutValue.ShouldBe(nameof(MathCounter.OutValue));
        MathCounter.OnHitMax.ShouldBe(nameof(MathCounter.OnHitMax));
        MathCounter.OnHitMin.ShouldBe(nameof(MathCounter.OnHitMin));
    }
}
