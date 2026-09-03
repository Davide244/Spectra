namespace SpectraEngine.Editor.Tests;

/// <summary>
/// Guards the rule that a shell brush states its translucency in its colour's
/// ALPHA and never on the brush's <c>Opacity</c> property.
/// </summary>
/// <remarks>
/// <para><b>This is a correctness rule, not a style one, because every one of
/// these fills is animated.</b> A <c>BrushTransition</c> interpolates the
/// <c>Color</c> and the <c>Opacity</c> as two independent quantities and the
/// renderer then MULTIPLIES them. A fade from <c>Transparent</c> (opacity 1) to
/// white at opacity 0.08 therefore runs an effective alpha of
/// <c>t * (1 - 0.92t)</c>, which peaks at 0.272 a little past halfway and
/// settles at 0.08: every hover in the shell flared to roughly three and a half
/// times its intended fill and fell back, on the way in and again on the way
/// out.</para>
/// <para>Measured tick by tick against the real theme rather than reasoned
/// about, and the reading is in the commit that fixed it. With the alpha in the
/// colour there is one interpolated quantity and the overshoot is
/// arithmetically impossible.</para>
/// <para>The same file already refuses opacity for TEXT colours, for a
/// different reason (opacity on a white brush breaks Windows subpixel gamma).
/// One rule now covers both.</para>
/// <para>Enforced as a source convention in the shape of
/// <c>ComPtrOwnershipConventionTests</c>, because the tests project references
/// no Avalonia and structurally cannot evaluate a brush.</para>
/// </remarks>
public sealed class BrushOpacityConventionTests
{
    [Fact]
    public void No_theme_brush_carries_an_Opacity_property()
    {
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(SourceRoot(), "SpectraEngine.Editor"), "*.axaml",
                     SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.Contains("SolidColorBrush", StringComparison.Ordinal)) continue;
                if (!line.Contains("Opacity=", StringComparison.Ordinal)) continue;

                offenders.Add($"{Path.GetFileName(file)}({i + 1}): {line.Trim()}");
            }
        }

        offenders.ShouldBeEmpty(
            "a brush must state its alpha in its colour; Opacity on the brush is interpolated " +
            "separately from the colour and multiplied with it, so an animated fill overshoots " +
            "its target and falls back");
    }

    [Fact]
    public void No_transitioned_fill_rests_on_Transparent()
    {
        // Transparent is #00000000, so a fade to a translucent white walks the
        // RGB channels from black to white and the fill passes through grey on
        // its way in. Same hue at zero alpha moves one number.
        string controls = Path.Combine(SourceRoot(), "SpectraEngine.Editor", "Theme", "Controls.axaml");
        string text = File.ReadAllText(controls);

        var offenders = new List<string>();
        foreach (System.Text.RegularExpressions.Match style in
                 System.Text.RegularExpressions.Regex.Matches(
                     text, """<Style Selector="([^"]+)"\s*>(.*?)</Style>""",
                     System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            string body = style.Groups[2].Value;
            if (!body.Contains("BrushTransition", StringComparison.Ordinal)) continue;
            if (!body.Contains("Property=\"Background\"", StringComparison.Ordinal)) continue;
            if (!body.Contains("Value=\"Transparent\"", StringComparison.Ordinal)) continue;

            offenders.Add(style.Groups[1].Value.Trim());
        }

        offenders.ShouldBeEmpty(
            "a style that animates Background must rest on the same hue at zero alpha " +
            "(#00FFFFFF), not on Transparent, which is transparent BLACK");
    }

    // The same walk ContentRoot uses: the nearest ancestor holding a solution
    // file is the repo root. These tests only ever run out of the repo.
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("no solution file above the test binary");
    }
}
