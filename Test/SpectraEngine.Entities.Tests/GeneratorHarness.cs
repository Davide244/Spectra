using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SpectraEngine.Entities.Generator;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace SpectraEngine.Entities.Tests;

/// <summary>
/// Builds a compilation in memory and runs <see cref="EntityGenerator"/> over
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every diagnostic this generator reports is one that never fires in this
/// repo's own sources</b>, because the sources are correct. Driving the
/// generator over a fixture compilation is therefore the only way to see any of
/// them at all, and the only way to observe the incremental steps, which is what
/// the caching oracle needs.
/// </para>
/// <para>
/// <b>References come from this process's own trusted-platform list.</b> That
/// list already carries the framework and <c>SpectraEngine.Core</c> (the test
/// project references it through the entities assembly), so the fixture
/// compilation sees exactly the attribute family and runtime types the real
/// build sees, with no reference-assembly package to keep in step.
/// </para>
/// </remarks>
internal static class GeneratorHarness
{
    private static readonly MetadataReference[] References = LoadReferences();

    /// <summary>One parsed fixture file.</summary>
    public static SyntaxTree Tree(string source, string path) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: path);

    /// <summary>A library compilation over <paramref name="trees"/>.</summary>
    public static CSharpCompilation Compilation(params SyntaxTree[] trees) =>
        CSharpCompilation.Create(
            "EntityFixtures",
            trees,
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>A driver holding one entity generator.</summary>
    /// <param name="trackSteps">
    /// Turns on incremental step tracking. Off by default because it makes the
    /// driver retain every intermediate table, which is exactly what a caching
    /// test wants to look at and exactly what nothing else should pay for.
    /// </param>
    public static GeneratorDriver Driver(bool trackSteps = false) =>
        CSharpGeneratorDriver.Create(
            generators: [new EntityGenerator().AsSourceGenerator()],
            additionalTexts: [],
            parseOptions: null,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: trackSteps));

    /// <summary>Runs the generator once over one fixture file.</summary>
    public static GeneratorRun Run(string source) => Run(Tree(source, "Fixture.cs"));

    /// <summary>Runs the generator once over already-parsed fixture files.</summary>
    public static GeneratorRun Run(params SyntaxTree[] trees)
    {
        GeneratorDriver driver = Driver().RunGeneratorsAndUpdateCompilation(
            Compilation(trees), out Compilation output, out _, TestContext.Current.CancellationToken);

        return new GeneratorRun(driver.GetRunResult(), output);
    }

    private static MetadataReference[] LoadReferences()
    {
        string assemblies = (string)(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "");
        var references = new List<MetadataReference>();
        foreach (string path in assemblies.Split(Path.PathSeparator))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        return references.ToArray();
    }
}

/// <summary>What one generator run produced.</summary>
internal sealed class GeneratorRun
{
    private readonly GeneratorDriverRunResult _result;
    private readonly Compilation _output;

    public GeneratorRun(GeneratorDriverRunResult result, Compilation output)
    {
        _result = result;
        _output = output;
    }

    /// <summary>Everything the generator reported.</summary>
    public ImmutableArray<Diagnostic> Diagnostics => _result.Diagnostics;

    /// <summary>The ids the generator reported, in order.</summary>
    public string[] DiagnosticIds => _result.Diagnostics.Select(d => d.Id).ToArray();

    /// <summary>How many files the generator added.</summary>
    public int SourceCount => _result.GeneratedTrees.Length;

    /// <summary>The one file the generator added, which is what most fixtures produce.</summary>
    public string OnlySource()
    {
        _result.GeneratedTrees.Length.ShouldBe(1, Describe());
        return _result.GeneratedTrees[0].GetText().ToString();
    }

    /// <summary>
    /// Errors from compiling the fixture WITH the generated half, which is what
    /// proves the emitter writes code rather than plausible text.
    /// </summary>
    public string[] CompileErrors() => _output
        .GetDiagnostics()
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .Select(d => $"{d.Id} {d.GetMessage()}")
        .ToArray();

    /// <summary>Every reported diagnostic, one per line, for a failure message.</summary>
    public string Describe() => _result.Diagnostics.Length == 0
        ? "(the generator reported nothing)"
        : string.Join(Environment.NewLine, _result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}"));
}
