using Microsoft.CodeAnalysis;
using SpectraEngine.Entities.Generator;
using System.Collections.Immutable;

namespace SpectraEngine.Entities.Tests;

/// <summary>
/// The caching oracle: an edit that changes nothing this generator reads must
/// leave its outputs alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only test that can see the failure it exists for.</b> Capturing
/// an <c>ISymbol</c> or a <c>SyntaxNode</c> in the transform stage produces
/// exactly the right source, exactly the right diagnostics and exactly the right
/// behaviour; the whole of the damage is that every model compares unequal to
/// itself, so every downstream step re-runs on every keystroke in the IDE.
/// Nothing fails, nothing warns, and the symptom arrives months later as a
/// solution that has become slow to type in.
/// </para>
/// <para>
/// <b><c>Cached</c> is the only reason these steps ever report, and it means two
/// different things.</b> Roslyn records <c>Cached</c> both when a step was not
/// re-run at all and when it WAS re-run and its comparer found the new value
/// equal to the old, so no single test can tell "the pipeline ignored this edit"
/// from "the model compared equal". The three tests together do it: the third
/// edits the same class body and reports <c>Modified</c>, which proves a body
/// edit really does re-run the transform, so the second one's <c>Cached</c> is a
/// value comparison rather than a step that never ran.
/// </para>
/// </remarks>
public sealed class EntityGeneratorCachingTests
{
    [Fact]
    public void An_edit_to_a_file_with_no_entity_in_it_leaves_every_output_cached()
    {
        SyntaxTree entity = GeneratorHarness.Tree(Fixtures.CachedEntity, "Entity.cs");
        SyntaxTree unrelated = GeneratorHarness.Tree(Fixtures.UnrelatedFile, "Unrelated.cs");
        Compilation before = GeneratorHarness.Compilation(entity, unrelated);

        GeneratorDriver driver = GeneratorHarness.Driver(trackSteps: true)
            .RunGenerators(before, TestContext.Current.CancellationToken);

        // Replaced rather than added, so the second compilation differs from the
        // first in exactly one tree and in nothing else.
        Compilation after = before.ReplaceSyntaxTree(
            unrelated,
            GeneratorHarness.Tree(Fixtures.UnrelatedFileEdited, "Unrelated.cs"));

        GeneratorRunResult result = RunAgain(driver, after);

        ReasonsFor(result, EntityGenerator.TrackingNames.Models)
            .ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached);
        ReasonsFor(result, EntityGenerator.TrackingNames.AllModels)
            .ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached);

        IncrementalStepRunReason[] outputs = OutputReasons(result);
        outputs.ShouldNotBeEmpty();
        outputs.ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached);
    }

    [Fact]
    public void An_edit_to_the_entity_class_that_changes_nothing_it_declares_leaves_every_output_cached()
    {
        // THE ONE THAT PINS THE MODEL. A member the generator does not read is
        // added to the class body, which is an edit the transform genuinely runs
        // again for (the control below proves that). Only value equality can then
        // stop it propagating: a model holding an ISymbol or a SyntaxNode compares
        // unequal to the identical model from the next compilation, and every
        // output downstream re-runs.
        SyntaxTree entity = GeneratorHarness.Tree(Fixtures.CachedEntity, "Entity.cs");
        Compilation before = GeneratorHarness.Compilation(entity);

        GeneratorDriver driver = GeneratorHarness.Driver(trackSteps: true)
            .RunGenerators(before, TestContext.Current.CancellationToken);

        // The spare member is appended after every attributed one, so not a span
        // the model records moves and the two models must be equal member for
        // member.
        Compilation after = before.ReplaceSyntaxTree(
            entity,
            GeneratorHarness.Tree(Fixtures.CachedEntityWithSpareMember, "Entity.cs"));

        GeneratorRunResult result = RunAgain(driver, after);

        ReasonsFor(result, EntityGenerator.TrackingNames.Models)
            .ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached);
        ReasonsFor(result, EntityGenerator.TrackingNames.AllModels)
            .ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached);
        OutputReasons(result).ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached);
    }

    [Fact]
    public void An_edit_to_the_entity_class_that_changes_what_it_declares_is_reported_as_modified()
    {
        // The control, and it does two jobs. Without it the two tests above pass
        // just as happily against a generator that never runs; and it is what
        // makes the second one's Cached mean "the values compared equal" rather
        // than "the transform was skipped", because the edit is the same SHAPE of
        // edit - another member appended to the same class body.
        SyntaxTree entity = GeneratorHarness.Tree(Fixtures.CachedEntity, "Entity.cs");
        Compilation before = GeneratorHarness.Compilation(entity);

        GeneratorDriver driver = GeneratorHarness.Driver(trackSteps: true)
            .RunGenerators(before, TestContext.Current.CancellationToken);

        Compilation after = before.ReplaceSyntaxTree(
            entity,
            GeneratorHarness.Tree(Fixtures.CachedEntityWithExtraKeyvalue, "Entity.cs"));

        GeneratorRunResult result = RunAgain(driver, after);

        ReasonsFor(result, EntityGenerator.TrackingNames.Models)
            .ShouldContain(IncrementalStepRunReason.Modified);
        OutputReasons(result).ShouldContain(IncrementalStepRunReason.Modified);
    }

    private static GeneratorRunResult RunAgain(GeneratorDriver driver, Compilation compilation) =>
        driver.RunGenerators(compilation, TestContext.Current.CancellationToken)
            .GetRunResult()
            .Results
            .Single();

    private static IncrementalStepRunReason[] OutputReasons(GeneratorRunResult result) => result
        .TrackedOutputSteps
        .SelectMany(step => step.Value)
        .SelectMany(step => step.Outputs)
        .Select(output => output.Reason)
        .ToArray();

    private static IncrementalStepRunReason[] ReasonsFor(GeneratorRunResult result, string step)
    {
        result.TrackedSteps.ContainsKey(step).ShouldBeTrue(
            $"The generator declares a step named '{step}' and the run reported none. " +
            $"Tracked: {string.Join(", ", result.TrackedSteps.Keys)}");

        ImmutableArray<IncrementalGeneratorRunStep> runs = result.TrackedSteps[step];
        return runs
            .SelectMany(run => run.Outputs)
            .Select(output => output.Reason)
            .ToArray();
    }
}
