using Spectra.Kitchen.Diagnostics;
using System;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The diagnostic vocabulary: how a code is spelled, which band it names, and the
/// two shapes of the line an IDE parses.
/// </summary>
public class CookDiagnosticTests
{
    [Fact]
    public void A_cook_code_is_four_digits_behind_SC()
    {
        CookDiagnosticId.Cook(1).ToString().ShouldBe("SC0001");
        CookDiagnosticId.Cook(9002).ToString().ShouldBe("SC9002");
        CookDiagnosticId.Cook(1002).Band.ShouldBe(1);
        CookDiagnosticId.Cook(1002).IsCookCode.ShouldBeTrue();
    }

    [Fact]
    public void A_shader_code_is_wrapped_rather_than_renumbered()
    {
        CookDiagnosticId wrapped = CookDiagnosticId.Wrap("SS", 104);

        // A shader error reaching a person through the cooker must be the same
        // code ssc reports and the same code the language server underlines, or
        // searching for an error code stops working the moment the build tool is
        // the one reporting it.
        wrapped.ToString().ShouldBe("SS0104");
        wrapped.IsCookCode.ShouldBeFalse();
    }

    [Fact]
    public void A_number_outside_the_space_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => CookDiagnosticId.Cook(0));
        Should.Throw<ArgumentOutOfRangeException>(() => CookDiagnosticId.Cook(10000));
    }

    [Fact]
    public void Every_band_has_a_name()
    {
        CookDiagnosticCodes.DescribeBand(0).ShouldBe("project and CLI");
        CookDiagnosticCodes.DescribeBand(6).ShouldBe("shader");
        CookDiagnosticCodes.DescribeBand(9).ShouldBe("pack writing");
    }

    [Fact]
    public void No_code_in_use_names_a_retired_number()
    {
        // Retired codes are never reused: a number that meant one thing in a
        // shipped build and another in the next makes every old bug report and
        // every suppression list silently wrong. The list is empty today, and
        // this is what stops the first retirement from being quietly re-issued.
        CookDiagnosticCodes.IsRetired(CookDiagnosticCodes.ProjectNotOpened.Number).ShouldBeFalse();
        CookDiagnosticCodes.IsRetired(CookDiagnosticCodes.InputMissing.Number).ShouldBeFalse();
        CookDiagnosticCodes.IsRetired(CookDiagnosticCodes.PackWriteFailed.Number).ShouldBeFalse();
    }

    [Fact]
    public void A_diagnostic_about_a_file_names_the_file_and_its_position()
    {
        CookDiagnostic about = CookDiagnostic.Error(
            CookDiagnosticCodes.InputMissing, "no such texture", @"C:\game\Assets\wall.spectramat", 4, 12);

        about.ToBuildLine("scook")
            .ShouldBe(@"C:\game\Assets\wall.spectramat(4,12): error SC1002: no such texture");
    }

    [Fact]
    public void A_diagnostic_about_a_file_with_no_position_still_names_the_file()
    {
        CookDiagnostic about = CookDiagnostic.Warning(
            CookDiagnosticCodes.ContentNotCooked, "not cooked", @"C:\game\Game.spectraproj");

        // The file form rather than the tool form: an IDE that cannot find a line
        // opens the file, which is the right answer, where the tool form would
        // lose the path entirely.
        about.ToBuildLine("scook")
            .ShouldBe(@"C:\game\Game.spectraproj: warning SC1005: not cooked");
    }

    [Fact]
    public void A_diagnostic_about_the_run_itself_names_the_tool()
    {
        CookDiagnostic about = CookDiagnostic.Error(CookDiagnosticCodes.VerbNotImplemented, "unbuilt");

        // MSBuild's second canonical shape. Inventing a (1,1) against the project
        // folder would make an IDE try to open a directory as a file.
        about.ToBuildLine("scook").ShouldBe("scook : error SC0002: unbuilt");
    }

    [Fact]
    public void Strict_promotes_a_warning_and_leaves_an_error_alone()
    {
        CookDiagnostic warning = CookDiagnostic.Warning(CookDiagnosticCodes.ContentNotCooked, "hm");
        CookDiagnostic error = CookDiagnostic.Error(CookDiagnosticCodes.RuleFailed, "no");

        warning.AsError().Severity.ShouldBe(CookDiagnosticSeverity.Error);
        warning.AsError().Message.ShouldBe("hm");
        warning.Severity.ShouldBe(CookDiagnosticSeverity.Warning);
        error.AsError().ShouldBeSameAs(error);
    }
}
