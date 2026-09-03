using Spectra.Kitchen.CLI;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The command line's contract: the exit code, and the exact shape of a line on
/// stderr.
/// </summary>
/// <remarks>
/// <para><b>Both halves are a contract with something that is not a person.</b>
/// The exit code is read by a build script and the stderr line is parsed by
/// MSBuild and every IDE that wraps it, so "the message is still helpful" is not
/// the property under test: the property is that the line matches the canonical
/// form and carries the code.</para>
/// <para>Driven through <c>Program.Run</c> with its writers rather than by
/// spawning <c>scook</c>, which is what keeps these as fast as the rest of the
/// suite.</para>
/// </remarks>
public class ScookCliTests
{
    // The ANSI introducer, spelled by code point: a raw escape byte in a source
    // file is invisible in every diff it ever appears in.
    private static readonly string Escape = ((char)0x1B).ToString();

    private const int ExitSuccess = 0;
    private const int ExitCookError = 1;
    private const int ExitUsageError = 2;
    private const int ExitIoError = 3;

    // MSBuild's canonical diagnostic form, in its two shapes: an origin that is a
    // file (with or without a position) or the tool's own name.
    private static readonly Regex BuildLine = new(
        @"^(?<origin>.*?)\s*:\s*(?<severity>error|warning|info)\s+(?<code>[A-Z]{2}\d{4}):\s+(?<text>.+)$",
        RegexOptions.Compiled);

    [Fact]
    public void An_unknown_option_is_a_usage_error()
    {
        var run = Invoke("cook", "--frobnicate");

        run.ExitCode.ShouldBe(ExitUsageError);
        run.Stderr.ShouldContain("unknown option: --frobnicate");
        run.Stderr.ShouldContain("--help");
    }

    [Fact]
    public void An_option_missing_its_argument_is_a_usage_error()
    {
        Invoke("cook", ".", "-o").ExitCode.ShouldBe(ExitUsageError);
        Invoke("cook", ".", "--profile").ExitCode.ShouldBe(ExitUsageError);
        Invoke("cook", ".", "--profile", "shipp").ExitCode.ShouldBe(ExitUsageError);
        Invoke("cook", ".", "-j", "0").ExitCode.ShouldBe(ExitUsageError);
        Invoke("cook", ".", "-t", "metal").ExitCode.ShouldBe(ExitUsageError);
        Invoke("verify").ExitCode.ShouldBe(ExitUsageError);

        // Refused rather than ignored: a switch that silently does nothing on
        // the wrong verb leaves the caller believing they got JSON.
        Invoke("cook", ".", "--json").ExitCode.ShouldBe(ExitUsageError);
    }

    [Fact]
    public void A_folder_that_is_not_a_project_is_a_cook_error_on_a_parseable_line()
    {
        using var scratch = new TempFolder();

        var run = Invoke("cook", scratch.Path);

        run.ExitCode.ShouldBe(ExitCookError);

        Match match = MatchSingleDiagnostic(run.Stderr);
        match.Groups["severity"].Value.ShouldBe("error");
        match.Groups["code"].Value.ShouldBe("SC0001");
        match.Groups["origin"].Value.ShouldBe(scratch.Path);
        match.Groups["text"].Value.ShouldContain(".spectraproj");
    }

    [Fact]
    public void A_pack_that_is_not_there_is_an_IO_error_rather_than_a_broken_pack()
    {
        var run = Invoke("verify", "some.spack", "--no-color");

        // Exit 3, not 1, and the line between them is who is at fault: a typo in
        // a path and a material missing its texture want different people looking
        // at them, and the exit code is how a CI step says which happened.
        run.ExitCode.ShouldBe(ExitIoError);
        MatchSingleDiagnostic(run.Stderr).Groups["code"].Value.ShouldBe("SC9003");
    }

    [Fact]
    public void Verify_passes_a_cooked_pack_and_fails_one_missing_a_texture()
    {
        using var project = new TempProject();
        project.WriteAsset("Textures/wall_brick.png", TempProject.Png(8, 8, seed: 1));
        project.WriteAsset(
            "Materials/wall.spectramat",
            "shader = lit\ntexture uDiffuse = Textures/wall_brick.png\n");

        Invoke("cook", project.Root, "-q").ExitCode.ShouldBe(ExitSuccess);
        string pack = Directory.GetFiles(project.CookedPath, "*.spack").Single();

        var passed = Invoke("verify", pack);
        passed.ExitCode.ShouldBe(ExitSuccess);
        passed.Stderr.ShouldBeEmpty();
        passed.Stdout.ShouldContain("1 reference(s) resolved");

        // Remove the texture and recook: the cook is still happy, because there is
        // no material rule and nothing in a cook reads what a raw-copied
        // .spectramat names. Verify is the step that notices - and it resolves the
        // slot through the same .simage redirection the engine uses, so what it
        // reports is what a running game would actually fail to find.
        File.Delete(Path.Combine(project.Layout.AssetsPath, "Textures", "wall_brick.png"));
        Invoke("cook", project.Root, "-q").ExitCode.ShouldBe(ExitSuccess);

        var failed = Invoke("verify", pack);
        failed.ExitCode.ShouldBe(ExitCookError);
        MatchSingleDiagnostic(failed.Stderr).Groups["code"].Value.ShouldBe("SC5001");
    }

    [Fact]
    public void Inspect_prints_the_header_and_every_entry_in_both_forms()
    {
        using var project = new TempProject();
        project.WriteAsset("Textures/wall_brick.png", TempProject.Png(8, 8, seed: 1));

        Invoke("cook", project.Root, "-q").ExitCode.ShouldBe(ExitSuccess);
        string pack = Directory.GetFiles(project.CookedPath, "*.spack").Single();

        var text = Invoke("inspect", pack);
        text.ExitCode.ShouldBe(ExitSuccess);

        // The COOKED name, which is also this surface's only statement that the
        // image rule ran at all: a pack whose textures still said .png would be a
        // pack of raw copies, and it would mount and render perfectly.
        text.Stdout.ShouldContain("Textures/wall_brick.simage");
        text.Stdout.ShouldNotContain("Textures/wall_brick.png");
        text.Stdout.ShouldContain("sorted, names");
        text.Stdout.ShouldContain("1 entries");

        // The form for anything that is not a person: the table's columns widen
        // to whatever the longest name in that particular pack is, so parsing it
        // means parsing a layout that changes per file.
        var json = Invoke("inspect", pack, "--json");
        json.ExitCode.ShouldBe(ExitSuccess);
        json.Stdout.ShouldContain("\"scookInspect\": 1");
        json.Stdout.ShouldContain("\"name\":\"Textures/wall_brick.simage\"");
        json.Stdout.ShouldContain("\"codec\":\"none\"");
        json.Stdout.ShouldNotContain("\r\n");
    }

    [Fact]
    public void Watch_is_refused_rather_than_degraded_to_one_cook()
    {
        using var project = new TempProject();

        var run = Invoke("cook", project.Root, "--watch");

        // Cooking once and exiting would report success for a loop that is not
        // running, which is worse than refusing.
        run.ExitCode.ShouldBe(ExitCookError);
        MatchSingleDiagnostic(run.Stderr).Groups["code"].Value.ShouldBe("SC0002");
    }

    [Fact]
    public void A_cook_writes_a_pack_into_the_projects_cooked_folder()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/strings.bin", TempProject.Bytes(40));

        var run = Invoke("cook", project.Root);

        run.ExitCode.ShouldBe(ExitSuccess);

        // -o defaults to ProjectFormat.CookedFolder, the constant that has been in
        // the layout since it was written and has never had a consumer.
        string[] packs = Directory.GetFiles(project.CookedPath, "*.spack");
        packs.Length.ShouldBe(1);
        run.Stdout.ShouldContain(packs[0]);
    }

    [Fact]
    public void Quiet_says_nothing_on_a_successful_cook()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/strings.bin", TempProject.Bytes(40));

        var run = Invoke("cook", project.Root, "-q");

        run.ExitCode.ShouldBe(ExitSuccess);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldBeEmpty();
    }

    [Fact]
    public void An_option_this_build_does_not_act_on_says_so_without_failing_the_cook()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/strings.bin", TempProject.Bytes(40));

        // --keep-brush-source, because the map rule is genuinely unbuilt.
        // This case used to be spelled with -t, which stopped being an example
        // of anything the moment the shader rule landed and started acting on it.
        var run = Invoke("cook", project.Root, "--keep-brush-source", "--strict");

        // A warning rather than an error even under --strict: the request is
        // legitimate and the cook it asked for still happened. It is reported by
        // the CLI rather than through the session for exactly that reason.
        run.ExitCode.ShouldBe(ExitSuccess);

        Match match = MatchSingleDiagnostic(run.Stderr);
        match.Groups["severity"].Value.ShouldBe("warning");
        match.Groups["code"].Value.ShouldBe("SC0003");
        match.Groups["origin"].Value.ShouldBe("scook");
    }

    [Fact]
    public void A_worker_count_is_acted_on_rather_than_warned_about()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/strings.bin", TempProject.Bytes(40));

        var run = Invoke("cook", project.Root, "-j", "8");

        // This used to be the tool's own example of a switch it accepted and
        // ignored. A tool that keeps apologising for something it now does is
        // worse than one that never said anything, because the warning is what a
        // reader would believe over the behaviour.
        run.ExitCode.ShouldBe(ExitSuccess);
        run.Stderr.ShouldBeEmpty();
    }

    [Fact]
    public void The_summary_reports_the_workers_the_cook_ran_at_not_the_ones_asked_for()
    {
        using var project = new TempProject();
        for (int i = 0; i < 3; i++)
            project.WriteAsset($"Data/t{i}.bin", TempProject.Bytes(16, seed: (byte)i));

        // Three assets under -j8 is three workers, and saying "8" would hide the
        // clamp, which is the one thing somebody asking why -j16 is no faster
        // needs to see.
        Invoke("cook", project.Root, "-j", "8").Stdout.ShouldContain("3 workers");

        // One worker is the resting state of every default cook, so it is not
        // printed at all: a number that never changes is not information.
        using var single = new TempProject();
        single.WriteAsset("Data/one.bin", TempProject.Bytes(16));
        Invoke("cook", single.Root).Stdout.ShouldNotContain("worker");
    }

    [Fact]
    public void Clean_removes_the_cook_output_and_refuses_anything_else()
    {
        using var project = new TempProject();
        project.WriteAsset("Data/strings.bin", TempProject.Bytes(40));

        Invoke("cook", project.Root).ExitCode.ShouldBe(ExitSuccess);
        Directory.Exists(project.CookedPath).ShouldBeTrue();

        Invoke("clean", project.Root).ExitCode.ShouldBe(ExitSuccess);
        Directory.Exists(project.CookedPath).ShouldBeFalse();

        // The one destructive thing the tool does, so the guard is explicit: a
        // clean only ever removes derived output.
        var refused = Invoke("clean", project.Root, "-o", project.Layout.AssetsPath);
        refused.ExitCode.ShouldBe(ExitCookError);
        MatchSingleDiagnostic(refused.Stderr).Groups["code"].Value.ShouldBe("SC0005");
        Directory.Exists(project.Layout.AssetsPath).ShouldBeTrue();

        var refusedRoot = Invoke("clean", project.Root, "-o", project.Root);
        refusedRoot.ExitCode.ShouldBe(ExitCookError);
        Directory.Exists(project.Root).ShouldBeTrue();
    }

    [Fact]
    public void Help_and_version_exit_zero_and_name_the_tool()
    {
        var help = Invoke("--help");
        help.ExitCode.ShouldBe(ExitSuccess);
        help.Stdout.ShouldContain("scook");
        help.Stdout.ShouldContain("0=success, 1=cook error, 2=usage error, 3=I/O error");

        var version = Invoke("--version");
        version.ExitCode.ShouldBe(ExitSuccess);
        version.Stdout.ShouldStartWith("scook ");
    }

    [Fact]
    public void No_color_leaves_no_escape_sequences_anywhere()
    {
        using var scratch = new TempFolder();

        var run = Invoke("cook", scratch.Path, "--no-color");

        run.Stderr.ShouldNotContain(Escape);
        Invoke("--help", "--no-color").Stdout.ShouldNotContain(Escape);
    }

    private static Match MatchSingleDiagnostic(string stderr)
    {
        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

        lines.Length.ShouldBe(1, $"expected exactly one diagnostic line, got: {stderr}");

        Match match = BuildLine.Match(lines[0]);
        match.Success.ShouldBeTrue($"not an MSBuild-parseable diagnostic: {lines[0]}");
        return match;
    }

    private static Run Invoke(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // --no-color on every invocation, because whether a test host redirects
        // its streams is not something a test should depend on. Prepended rather
        // than appended: appended, it is swallowed as the argument of a trailing
        // option, which is exactly what the dangling-option cases pass.
        string[] withNoColor = args.Contains("--no-color") ? args : ["--no-color", .. args];
        int exit = Program.Run(withNoColor, stdout, stderr);

        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private readonly record struct Run(int ExitCode, string Stdout, string Stderr);

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spectra_cli_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
