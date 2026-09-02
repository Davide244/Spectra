using System.Threading.Tasks;

namespace SpectraEngine.Entities.Tests;

/// <summary>
/// What the generator actually writes, pinned as text.
/// </summary>
/// <remarks>
/// <b>A snapshot is the right oracle here because the output is a DOCUMENT.</b>
/// Asserting that some substring is present would pass on source that is subtly
/// reordered, differently qualified, or missing a whole member; the generated
/// half of a class is read by people debugging their own entities, so its shape
/// is part of what this generator promises.
/// </remarks>
public sealed class EntityGeneratorSnapshotTests
{
    [Fact]
    public Task Generated_source_for_a_representative_entity_matches_snapshot()
    {
        GeneratorRun run = GeneratorHarness.Run(Fixtures.RepresentativeEntity);

        run.Diagnostics.ShouldBeEmpty(run.Describe());
        return Verify(run.OnlySource());
    }

    [Fact]
    public void The_generated_source_compiles()
    {
        // The snapshot proves the text is what was intended; this proves the text
        // is CODE. An emitter bug - a missing global:: qualification, an argument
        // in the wrong position, a case label that collides - produces a file that
        // looks perfectly reasonable in a diff and fails at the consumer.
        GeneratorRun run = GeneratorHarness.Run(Fixtures.RepresentativeEntity);

        run.CompileErrors().ShouldBeEmpty();
    }

    [Fact]
    public void An_entity_with_no_keyvalues_and_no_inputs_still_gets_a_schema_and_a_registration()
    {
        // The two switches are emitted only when they have something to switch
        // on, because an override that only calls base is noise in a file people
        // read. The schema and the registration are not optional: a class with no
        // keyvalues is still a class a map can place.
        const string source = """
            using SpectraEngine.Core.Entities;

            namespace TestGame.Entities;

            [SpectraEntity("logic_auto")]
            public sealed partial class LogicAuto : Entity
            {
            }
            """;

        GeneratorRun run = GeneratorHarness.Run(source);

        run.Diagnostics.ShouldBeEmpty(run.Describe());
        run.CompileErrors().ShouldBeEmpty();

        string generated = run.OnlySource();
        generated.ShouldContain("CreateSpectraSchema");
        generated.ShouldContain("ModuleInitializer");
        generated.ShouldNotContain("ParseKeyValue");
        generated.ShouldNotContain("AcceptInput");
    }
}
