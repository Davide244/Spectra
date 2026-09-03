using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Packs;
using SpectraEngine.Core.Projects;
using System;
using System.Collections.Generic;
using System.IO;

namespace SpectraEngine.Editor.Shell;

/// <summary>What one Validate Cooked run found.</summary>
/// <param name="Succeeded">Whether the project would ship correctly.</param>
/// <param name="Summary">One sentence for the status line.</param>
/// <param name="Diagnostics">Everything the cook and the verify had to say, in order.</param>
internal sealed record CookedValidationReport(
    bool Succeeded, string Summary, IReadOnlyList<CookDiagnostic> Diagnostics);

/// <summary>
/// Cooks the open project and proves the artifact resolves with nothing but
/// itself mounted.
/// </summary>
/// <remarks>
/// <para><b>It cooks every time rather than verifying whatever is in
/// <c>cooked/</c>.</b> A stale pack passing is worse than no answer at all: the
/// question a person asks by clicking this is "will what I have now ship", and a
/// green tick against last week's artifact answers a different one.</para>
/// <para><b>The pack is cooked from what is ON DISK.</b> Unsaved edits are not in
/// it, which is correct - a cook is a build of the committed source tree - and is
/// why the summary says which level was included rather than leaving the reader
/// to assume the one in the viewport was.</para>
/// <para><b>Nothing here touches the scene, so it runs off the UI thread and
/// needs no <c>EnqueueCommand</c>.</b> It reads a <see cref="ProjectLayout"/>,
/// which is a value, and the filesystem; the render thread's ownership of the
/// graph and of every GPU resource is untouched by construction rather than by
/// this method's care.</para>
/// <para><b>The verifier's strictness is the whole point and it lives there, not
/// here.</b> See <see cref="PackVerifier"/>: the pack is mounted ALONE on a
/// strict stack, so a texture the cook did not produce throws instead of
/// resolving out of the loose tree the editor is otherwise sitting on top of.
/// </para>
/// </remarks>
internal static class CookedValidation
{
    /// <summary>Cooks and verifies <paramref name="layout"/>. Any thread but the render one.</summary>
    public static CookedValidationReport Run(ProjectLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var diagnostics = new List<CookDiagnostic>();

        CookResult cooked;
        try
        {
            cooked = new CookSession(layout, new CookSettings()).Run();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CookedValidationReport(false, $"The cook could not run: {ex.Message}", diagnostics);
        }

        diagnostics.AddRange(cooked.Diagnostics);

        if (!cooked.Succeeded || cooked.OutputPath is not { } pack)
        {
            return new CookedValidationReport(
                false,
                $"The cook failed with {cooked.ErrorCount} error(s), so there is no pack to validate.",
                diagnostics);
        }

        PackVerifyResult verified;
        try
        {
            verified = PackVerifier.Verify(pack);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CookedValidationReport(false, $"Could not read '{pack}': {ex.Message}", diagnostics);
        }

        diagnostics.AddRange(verified.Diagnostics);

        string name = Path.GetFileName(pack);
        string summary = verified.Succeeded
            ? $"Cooked content validates: {name}, {verified.EntriesChecked} entries, " +
              $"{verified.ReferencesChecked} reference(s) resolved with only the pack mounted."
            : $"Cooked content is broken: {verified.ErrorCount} error(s) in {name}. " +
              "The running editor hides these, because it resolves the loose files instead.";

        return new CookedValidationReport(verified.Succeeded, summary, diagnostics);
    }
}
