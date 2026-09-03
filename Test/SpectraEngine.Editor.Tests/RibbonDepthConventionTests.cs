using System.Globalization;
using System.Text.RegularExpressions;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// Guards the rule that a gradient brush can never reach a
/// <c>BrushTransition</c>.
/// </summary>
/// <remarks>
/// <para><b>The shell's depth is its first gradients, and they arrived into a
/// theme that already knows what a badly transitioned brush does.</b>
/// <c>BrushOpacityConventionTests</c> beside this one records the measured
/// consequence of letting a <c>BrushTransition</c> interpolate two quantities
/// it then multiplies. What Avalonia's brush animator does with a PAIR OF
/// GRADIENTS is documented nowhere and is not something to discover in a
/// shipped build, so the answer here is structural: depth is a STATIC layer,
/// only solid fills ramp, and a sheen appears by moving one element's
/// <c>Opacity</c>.</para>
/// <para><b>Three claims, and the middle one is what makes the other two
/// cheap.</b> Every gradient is declared in one file, every gradient is
/// referenced by one of three named consumers, and none of those three
/// transitions a brush. Without the second claim this test would have to model
/// Avalonia's selector matching to work out which style block can reach which
/// element - with it, the reachable set is three selectors and can simply be
/// read.</para>
/// <para>A source convention in the shape of
/// <c>BrushOpacityConventionTests</c> and <c>ComPtrOwnershipConventionTests</c>,
/// because this project references no Avalonia and structurally cannot
/// evaluate a brush.</para>
/// </remarks>
public sealed class RibbonDepthConventionTests
{
    /// <summary>
    /// The only styles allowed to name a gradient. Each is a static decorative
    /// surface: a non-interactive child that fades on Opacity, a hairline, and
    /// the ribbon's own ground.
    /// </summary>
    private static readonly string[] AllowedConsumers =
    [
        "Border.sheen",
        "Border.ribbonrule",
        "Border.ribbonbody",
    ];

    [Fact]
    public void Every_gradient_in_the_shell_is_declared_in_the_token_file()
    {
        // One home, so the reachable set below is knowable by reading rather
        // than by matching selectors. A gradient declared inline in a panel
        // would be outside every claim this file makes.
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(SourceRoot(), "SpectraEngine.Editor"), "*.axaml",
                     SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), "Tokens.axaml", StringComparison.Ordinal))
                continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("GradientBrush", StringComparison.Ordinal)) continue;
                offenders.Add($"{Path.GetFileName(file)}({i + 1}): {lines[i].Trim()}");
            }
        }

        offenders.ShouldBeEmpty(
            "every gradient belongs in Theme/Tokens.axaml, so the set of styles that can reach " +
            "one stays small enough to read");
    }

    [Fact]
    public void A_gradient_is_named_only_by_a_static_decorative_surface()
    {
        IReadOnlyList<string> keys = GradientKeys();
        keys.ShouldNotBeEmpty("the depth vocabulary should exist");

        var offenders = new List<string>();

        foreach ((string selector, string body, string file, int line) in StyleBlocks())
        {
            if (!keys.Any(k => body.Contains($"StaticResource {k}", StringComparison.Ordinal)))
                continue;

            if (AllowedConsumers.Any(c => selector.Contains(c, StringComparison.Ordinal)))
                continue;

            offenders.Add($"{file}({line}): {selector}");
        }

        offenders.ShouldBeEmpty(
            "a gradient may only be assigned by " + string.Join(", ", AllowedConsumers) +
            ": those three carry no BrushTransition, which is what keeps a gradient off an " +
            "interpolated property by construction rather than by review");
    }

    [Fact]
    public void No_style_that_names_a_gradient_transitions_a_brush()
    {
        // The claim the other two exist to make checkable. A gradient reaching
        // a BrushTransition is the failure; this is where it would happen.
        IReadOnlyList<string> keys = GradientKeys();
        var offenders = new List<string>();

        foreach ((string selector, string body, string file, int line) in StyleBlocks())
        {
            bool namesGradient =
                keys.Any(k => body.Contains($"StaticResource {k}", StringComparison.Ordinal));
            bool isConsumer =
                AllowedConsumers.Any(c => selector.Contains(c, StringComparison.Ordinal));

            if (!namesGradient && !isConsumer) continue;
            if (!body.Contains("<BrushTransition", StringComparison.Ordinal)) continue;

            offenders.Add($"{file}({line}): {selector}");
        }

        offenders.ShouldBeEmpty(
            "a style that assigns a gradient, or that targets one of the surfaces gradients are " +
            "assigned to, must not declare a BrushTransition");
    }

    [Fact]
    public void Every_large_glyph_the_markup_asks_for_exists()
    {
        // The second size is a second set of geometry keys, and a StaticResource
        // that resolves to nothing throws at load rather than at build - which
        // for a ribbon page means the window refuses to open with a resource
        // name in the message and no line number.
        string icons = File.ReadAllText(
            Path.Combine(SourceRoot(), "SpectraEngine.Editor", "Theme", "Icons.axaml"));

        var declared = new HashSet<string>(
            Regex.Matches(icons, @"x:Key=""(IconLg[A-Za-z0-9]+)""")
                 .Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);

        declared.ShouldNotBeEmpty("the large icon set should exist");

        var missing = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(SourceRoot(), "SpectraEngine.Editor"), "*.axaml",
                     SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), "Icons.axaml", StringComparison.Ordinal))
                continue;

            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"StaticResource (IconLg[A-Za-z0-9]+)"))
            {
                if (!declared.Contains(m.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
            }
        }

        missing.ShouldBeEmpty("every IconLg* the markup names must be declared in Icons.axaml");
    }

    [Fact]
    public void A_large_glyph_is_authored_on_its_own_thirty_two_box()
    {
        // The whole reason the large set is new artwork rather than the small
        // set enlarged: it is drawn to fill a 32 box directly, so Path.icon-lg
        // carries no transform and there is no second shared scale factor to
        // drift from the first. A geometry that strayed back onto the 16 grid
        // would render at a quarter of the area with nothing failing.
        string icons = File.ReadAllText(
            Path.Combine(SourceRoot(), "SpectraEngine.Editor", "Theme", "Icons.axaml"));

        var offenders = new List<string>();

        foreach (Match m in Regex.Matches(
                     icons, @"x:Key=""(IconLg[A-Za-z0-9]+)"">([^<]*)<"))
        {
            string name = m.Groups[1].Value;
            double max = Regex.Matches(m.Groups[2].Value, @"-?\d+(\.\d+)?")
                              .Select(n => double.Parse(n.Value, CultureInfo.InvariantCulture))
                              .DefaultIfEmpty(0)
                              .Max();

            // 16 would be dead centre of the 32 box, so anything that never
            // exceeds it is a glyph still drawn on the small grid.
            if (max <= 16.0) offenders.Add($"{name}: widest coordinate {max}");
        }

        offenders.ShouldBeEmpty("a large glyph is authored to fill a 32 box, ink 3.5 to 28.5");
    }

    /// <summary>Every gradient key declared in the token file.</summary>
    private static IReadOnlyList<string> GradientKeys()
    {
        string tokens = File.ReadAllText(
            Path.Combine(SourceRoot(), "SpectraEngine.Editor", "Theme", "Tokens.axaml"));

        return Regex.Matches(tokens, @"<LinearGradientBrush x:Key=""([A-Za-z0-9]+)""")
                    .Select(m => m.Groups[1].Value)
                    .ToList();
    }

    /// <summary>
    /// Every <c>&lt;Style Selector="..."&gt;</c> block in the theme, as
    /// (selector, body, file, line).
    /// </summary>
    /// <remarks>
    /// Crude on purpose. A real XAML parse would pull in the framework this
    /// project deliberately does not reference, and the claims above are about
    /// text a person reads.
    /// </remarks>
    private static List<(string Selector, string Body, string File, int Line)> StyleBlocks()
    {
        var blocks = new List<(string, string, string, int)>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(SourceRoot(), "SpectraEngine.Editor"), "*.axaml",
                     SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            string name = Path.GetFileName(file);

            for (int i = 0; i < lines.Length; i++)
            {
                Match open = Regex.Match(lines[i], @"<Style Selector=""(.*?)""\s*>?\s*$");
                if (!open.Success) continue;

                var body = new System.Text.StringBuilder();
                int j = i;
                for (; j < lines.Length; j++)
                {
                    body.AppendLine(lines[j]);
                    if (lines[j].Contains("</Style>", StringComparison.Ordinal)) break;
                }

                blocks.Add((open.Groups[1].Value, body.ToString(), name, i + 1));
                i = j;
            }
        }

        return blocks;
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.slnx").Any() || dir.EnumerateFiles("*.sln").Any())
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not find the solution root above the test binary");
    }
}
