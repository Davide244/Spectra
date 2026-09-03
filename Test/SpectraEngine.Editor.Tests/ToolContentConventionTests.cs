namespace SpectraEngine.Editor.Tests;

/// <summary>
/// Guards the rule that a dock tool's content and its DataContext are assigned
/// together, because assigning only the content is not an error anywhere and
/// the symptom is a pane of blank controls.
/// </summary>
/// <remarks>
/// <para>Dock hands a tool's content presenter its own DataContext, and a
/// floated tool leaves the window's logical tree entirely, so a content control
/// that relies on inheritance resolves every binding against an object carrying
/// none of the properties it names.</para>
/// <para><b>Avalonia reports nothing for that.</b> A failed binding leaves the
/// target property at its own default, so <c>Text</c> goes empty, an
/// <c>ItemsSource</c> goes empty, and <c>IsVisible</c> stays TRUE. That is how
/// the viewport header strip came to show every debug overlay chip at once with
/// every value beside them blank: six panels set the DataContext inline and the
/// seventh host, added when the viewport became dockable, did not.</para>
/// <para>The tests project cannot evaluate a binding at all (it references no
/// Avalonia), which is exactly why this ships as a source convention in the
/// shape of <c>ComPtrOwnershipConventionTests</c>: enforce the rule where it
/// would actually be broken.</para>
/// </remarks>
public sealed class ToolContentConventionTests
{
    [Fact]
    public void Every_dock_tool_takes_its_content_through_the_one_call_that_also_sets_the_DataContext()
    {
        string shell = Path.Combine(SourceRoot(), "SpectraEngine.Editor");
        Directory.Exists(shell).ShouldBeTrue($"expected the editor sources under {shell}");

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(shell, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (IsComment(line)) continue;

                // A direct write to <something>Tool.Content bypasses the pairing.
                int assign = line.IndexOf("Tool.Content", StringComparison.Ordinal);
                if (assign < 0) continue;
                if (line.IndexOf('=', assign) < 0) continue;

                offenders.Add($"{Path.GetFileName(file)}({i + 1}): {line.Trim()}");
            }
        }

        offenders.ShouldBeEmpty(
            "a dock tool's Content must be assigned through SetToolContent, which sets the " +
            "DataContext with it; Dock supplies its own DataContext to tool content and a failed " +
            "binding leaves IsVisible at true rather than raising anything");
    }

    [Fact]
    public void The_pairing_lives_in_exactly_one_place()
    {
        // If the helper is ever inlined back to its call sites this test fails
        // rather than the guard above quietly passing over zero call sites.
        string window = Path.Combine(SourceRoot(), "SpectraEngine.Editor", "MainWindow.axaml.cs");
        string text = File.ReadAllText(window);

        text.ShouldContain("private void SetToolContent(",
            customMessage: "the one call that pairs a tool's content with its DataContext must exist");

        int calls = 0;
        int at = 0;
        while ((at = text.IndexOf("SetToolContent(", at, StringComparison.Ordinal)) >= 0)
        {
            calls++;
            at += "SetToolContent(".Length;
        }

        // Seven tools plus the declaration itself.
        calls.ShouldBe(8,
            "every dock tool in the window goes through the pairing; a new tool must join it");
    }

    private static bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal)
            || trimmed.StartsWith("///", StringComparison.Ordinal);
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
