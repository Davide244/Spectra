namespace SpectraEngine.Entities.Tests;

/// <summary>
/// One test per refusal, because each one describes a declaration that would
/// otherwise become a class which compiles and behaves as nothing.
/// </summary>
public sealed class EntityGeneratorDiagnosticTests
{
    [Fact]
    public void A_class_that_is_not_partial_is_refused()
    {
        GeneratorRun run = GeneratorHarness.Run(Fixtures.NotPartial);

        run.DiagnosticIds.ShouldContain("SPE001");
    }

    [Fact]
    public void A_class_that_is_not_partial_has_nothing_emitted_for_it()
    {
        // The one refusal that stops emission entirely: there is no other half to
        // add. Every other one drops the offending member and emits the rest, so
        // an author fixing one field does not watch the whole type disappear.
        GeneratorRun run = GeneratorHarness.Run(Fixtures.NotPartial);

        run.SourceCount.ShouldBe(0);
    }

    [Fact]
    public void Two_classes_claiming_one_class_name_are_refused()
    {
        GeneratorRun run = GeneratorHarness.Run(Fixtures.DuplicateClassName);

        run.DiagnosticIds.ShouldContain("SPE002");

        // Reported once, on the second declaration: the first is as likely as not
        // the one the author meant to keep, and a diagnostic on both is a pair a
        // reader cannot act on.
        run.DiagnosticIds.Count(id => id == "SPE002").ShouldBe(1);
        run.Diagnostics.Single(d => d.Id == "SPE002").GetMessage().ShouldContain("SecondThing");
    }

    [Fact]
    public void A_keyvalue_on_a_type_nothing_is_inferred_from_is_refused()
    {
        // The rule is "require an explicit type rather than guessing", and the
        // message has to say so: an author who reads only the first line must
        // know that stating Type is the fix.
        GeneratorRun run = GeneratorHarness.Run(Fixtures.UnsupportedKeyvalueType);

        run.DiagnosticIds.ShouldContain("SPE003");
        run.Diagnostics.Single(d => d.Id == "SPE003").GetMessage().ShouldContain("State the type explicitly");
    }

    [Fact]
    public void A_keyvalue_whose_stated_type_the_member_cannot_carry_is_refused()
    {
        // Color is read as a Vector3, so a float member cannot hold one. Without
        // this the generated binder would not compile, which is a worse report of
        // the same fact and lands in a file the author cannot edit.
        GeneratorRun run = GeneratorHarness.Run(Fixtures.KeyvalueTypeMismatch);

        run.DiagnosticIds.ShouldContain("SPE006");
    }

    [Fact]
    public void A_keyvalue_the_binder_cannot_assign_to_is_refused()
    {
        GeneratorRun run = GeneratorHarness.Run(Fixtures.KeyvalueNotAssignable);

        run.DiagnosticIds.ShouldContain("SPE007");
    }

    [Fact]
    public void An_input_that_is_not_shaped_the_way_the_dispatch_calls_one_is_refused()
    {
        GeneratorRun run = GeneratorHarness.Run(Fixtures.InvalidInputSignature);

        run.DiagnosticIds.ShouldContain("SPE004");
        run.Diagnostics.Single(d => d.Id == "SPE004").GetMessage()
            .ShouldContain("void Trigger(ref EntityInputContext context)");
    }

    [Fact]
    public void A_keyvalue_named_targetname_is_refused()
    {
        // targetname IS SceneNode.Name. A second field of that name is a fork in
        // the identity: a rename in the scene tree updates one of them, every
        // wire aimed at the other silently stops resolving, and nothing anywhere
        // reports a disagreement.
        GeneratorRun run = GeneratorHarness.Run(Fixtures.ReservedKeyvalueName);

        run.DiagnosticIds.ShouldContain("SPE005");
    }

    [Fact]
    public void A_keyvalue_named_TargetName_in_any_casing_is_refused()
    {
        // Ordinally it is a different key, and to every person who reads it, it
        // is the same idea. The confusion is the damage.
        const string source = """
            using SpectraEngine.Core.Entities;

            namespace TestGame.Entities;

            [SpectraEntity("logic_thing")]
            public sealed partial class LogicThing : Entity
            {
                [Keyvalue("TargetName")]
                public string Who { get; set; } = "";
            }
            """;

        GeneratorHarness.Run(source).DiagnosticIds.ShouldContain("SPE005");
    }

    [Fact]
    public void A_refused_member_does_not_stop_the_rest_of_the_class_being_emitted()
    {
        const string source = """
            using SpectraEngine.Core.Entities;

            namespace TestGame.Entities;

            [SpectraEntity("logic_thing")]
            public sealed partial class LogicThing : Entity
            {
                [Keyvalue("targetname")]
                public string Who { get; set; } = "";

                [Keyvalue("speed", Default = "1")]
                public float Speed { get; set; }
            }
            """;

        GeneratorRun run = GeneratorHarness.Run(source);

        run.DiagnosticIds.ShouldContain("SPE005");

        string generated = run.OnlySource();
        generated.ShouldContain("\"speed\"");
        generated.ShouldNotContain("targetname");
    }
}
