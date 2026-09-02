using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace SpectraEngine.Entities.Generator;

/// <summary>
/// Emits the machinery behind <c>[SpectraEntity]</c>: the keyvalue binder, the
/// input dispatch, the output declarations, a static <c>EntitySchema</c> and the
/// registration into <c>EntityCatalog</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This assembly does not reference the engine, and must not.</b> The
/// attribute family is matched by fully qualified METADATA NAME, so the
/// generator has no compile-time dependency on Core and Core has none on it: the
/// dependency runs one way, exactly as it does for the editing assembly, and a
/// game that references the engine as a library gets the attributes by
/// referencing Core alone.
/// </para>
/// <para>
/// <b>The pipeline is two outputs over one transform, and the split is what
/// keeps the caching per class.</b> Emission runs per model, so an edit to one
/// entity re-emits one file; the duplicate-name check is the only thing that
/// needs to see every class at once and is therefore the only thing behind a
/// <c>Collect</c>.
/// </para>
/// <para>
/// <b>Nothing reads the compilation.</b> A <c>CompilationProvider</c> anywhere in
/// this pipeline would make every model depend on every edit in the project,
/// which is the same caching failure as capturing a symbol and is just as
/// invisible.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class EntityGenerator : IIncrementalGenerator
{
    /// <summary>
    /// The names the incremental steps are tracked under, which is how a test
    /// asserts that an unrelated edit changed nothing.
    /// </summary>
    /// <remarks>
    /// Public because the caching oracle is the only real proof this generator
    /// caches at all: everything still produces correct source when caching is
    /// broken, so the only symptom is an IDE that has quietly become slow.
    /// </remarks>
    public static class TrackingNames
    {
        /// <summary>The per-class value model, after the symbols are dropped.</summary>
        public const string Models = "EntityModels";

        /// <summary>The batch the duplicate-name check reads.</summary>
        public const string AllModels = "AllEntityModels";
    }

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<EntityModel> models = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                EntityModelFactory.EntityAttributeMetadataName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (syntaxContext, token) => EntityModelFactory.Create(syntaxContext, token))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .WithTrackingName(TrackingNames.Models);

        context.RegisterSourceOutput(models, static (production, model) => Produce(production, model));

        context.RegisterSourceOutput(
            models.Collect().WithTrackingName(TrackingNames.AllModels),
            static (production, all) => ReportDuplicates(production, all));
    }

    private static void Produce(SourceProductionContext production, EntityModel model)
    {
        for (int i = 0; i < model.Diagnostics.Count; i++)
            production.ReportDiagnostic(model.Diagnostics[i].ToDiagnostic());

        // A non-partial class cannot be reopened, so there is nothing to add. Every
        // other refusal is per member: the offending keyvalue or input is left out
        // and the rest of the class is still emitted, because an author fixing one
        // field should not have the whole type disappear underneath them.
        if (!model.IsPartial)
            return;

        production.AddSource(EntityEmitter.HintName(model), EntityEmitter.Emit(model));
    }

    private static void ReportDuplicates(SourceProductionContext production, ImmutableArray<EntityModel> models)
    {
        if (models.Length < 2)
            return;

        var seen = new Dictionary<string, EntityModel>(models.Length);
        foreach (EntityModel model in models)
        {
            if (model.ClassName.Length == 0)
                continue;

            if (seen.TryGetValue(model.ClassName, out EntityModel first))
            {
                // Reported on the SECOND declaration, which is the one a reader
                // can delete: the first is as likely as not the one they meant to
                // keep, and a diagnostic on both makes neither actionable.
                production.ReportDiagnostic(DiagnosticInfo.Create(
                    EntityDiagnostics.DuplicateClassName,
                    model.Location,
                    model.ClassName,
                    first.FullTypeName,
                    model.FullTypeName).ToDiagnostic());
                continue;
            }

            seen.Add(model.ClassName, model);
        }
    }
}
