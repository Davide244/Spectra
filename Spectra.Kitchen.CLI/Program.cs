using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using SpectraEngine.Core;
using SpectraEngine.Core.Projects;

namespace Spectra.Kitchen.CLI;

/// <summary>
/// <c>scook</c>: the cook tool's command line.
/// </summary>
/// <remarks>
/// <para><b>Shaped after <c>ssc</c> deliberately</b>, down to the exit codes and
/// the stderr form. Two tools in one solution that disagree about what exit 1
/// means, or about how a diagnostic is spelled, cost every script that drives
/// them a special case.</para>
/// <para><b>Exit codes: 0 success, 1 cook error, 2 usage error, 3 I/O error.</b>
/// The line between 1 and 3 is who is at fault: a project that is malformed, a
/// rule that failed, a verb that is unbuilt are all the cook's business and exit
/// 1, while a path the filesystem refused is 3.</para>
/// <para><b><see cref="Run"/> takes its writers rather than reaching for
/// <c>Console</c>.</b> That is what lets the CLI be tested at all: a test asserts
/// on the exit code and on the exact stderr line, which is the contract an IDE
/// parses, and spawning a process to get at it would make the fastest tests in the
/// repo the slowest.</para>
/// </remarks>
internal static class Program
{
    private const string ToolName = "scook";

    private const int ExitSuccess = 0;
    private const int ExitCookError = 1;
    private const int ExitUsageError = 2;
    private const int ExitIoError = 3;

    private static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        // Decided before the parse, because help and usage errors are printed
        // before there is a CliOptions to read UseColor off.
        bool noColor = Array.IndexOf(args, "--no-color") >= 0;
        var errStyle = new AnsiStyle(!noColor && ConsoleColor.ShouldUseForStderr());
        var outStyle = new AnsiStyle(!noColor && ConsoleColor.ShouldUseForStdout());

        try
        {
            var parse = CliOptions.Parse(args);
            switch (parse.Mode)
            {
                case CliMode.Help:
                    CliOptions.PrintUsage(stdout, outStyle.Enabled);
                    return ExitSuccess;
                case CliMode.Version:
                    stdout.WriteLine(
                        $"{outStyle.Title}{ToolName}{outStyle.Reset} " +
                        $"{outStyle.Value}{EngineInfo.VersionString}{outStyle.Reset} " +
                        $"{outStyle.Dim}(pack format v{EngineInfo.PackFormatVersion}){outStyle.Reset}");
                    return ExitSuccess;
                case CliMode.UsageError:
                    stderr.WriteLine($"{errStyle.Error}error{errStyle.Reset}: {parse.Error}");
                    stderr.WriteLine($"Run '{errStyle.Command}{ToolName} --help{errStyle.Reset}' for usage.");
                    return ExitUsageError;
            }

            var opts = parse.Options!;
            var writer = new DiagnosticWriter(stderr, ToolName, opts.UseColor);

            return opts.Verb switch
            {
                CliVerb.Cook => RunCook(opts, stdout, writer, outStyle),
                CliVerb.Clean => RunClean(opts, stdout, writer, outStyle),
                _ => RunUnbuilt(opts, writer),
            };
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"{errStyle.Error}fatal{errStyle.Reset}: {ex.Message}");
            return ExitIoError;
        }
    }

    private static int RunCook(CliOptions opts, TextWriter stdout, DiagnosticWriter writer, AnsiStyle style)
    {
        if (opts.Watch)
        {
            // Refused rather than degraded to a single cook. A --watch that cooks
            // once and exits reports success for a loop that is not running, which
            // is the failure this tool refuses everywhere else.
            writer.Write(CookDiagnostic.Error(
                CookDiagnosticCodes.VerbNotImplemented,
                "--watch is not built yet: the incremental cache behind it exists, but there is no file " +
                "watcher driving it. Run a plain cook, which is now incremental, or --loose for a cooked tree."));
            return ExitCookError;
        }

        if (!TryOpenProject(opts.Target, writer, out ProjectLayout? layout, out int failure))
            return failure;

        if (!opts.Quiet) ReportUnimplementedOptions(opts, writer);

        var session = new CookSession(layout, opts.ToCookSettings());
        CookResult result = session.Run();
        writer.WriteAll(result.Diagnostics);

        if (!result.Succeeded) return ExitCookError;

        if (!opts.Quiet)
        {
            // The cache count is only printed when something was actually skipped:
            // "0 from cache" on a first cook is a number that answers a question
            // nobody asked, and it would appear on every clean cook forever.
            string cached = result.CacheHits > 0 ? $", {result.CacheHits} from cache" : string.Empty;

            // The worker count follows the same rule, and it is the count the cook
            // ACTUALLY ran at: printing "1 worker" on every default cook is noise,
            // and printing the -j that was typed would hide the clamp, which is the
            // one thing somebody asking about it wants to see.
            string workers = result.Workers > 1 ? $", {result.Workers} workers" : string.Empty;

            stdout.WriteLine(
                $"{style.Success}{ToolName}{style.Reset}: wrote {style.Path}{result.OutputPath}{style.Reset} " +
                $"{style.Dim}({result.EntryCount} entries, {result.PayloadBytes} bytes, " +
                $"profile {CookManifest.ToWire(opts.Profile)}{cached}{workers}, " +
                $"{result.WarningCount} warning(s)){style.Reset}");
        }

        return ExitSuccess;
    }

    private static int RunClean(CliOptions opts, TextWriter stdout, DiagnosticWriter writer, AnsiStyle style)
    {
        if (!TryOpenProject(opts.Target, writer, out ProjectLayout? layout, out int failure))
            return failure;

        string target = Path.GetFullPath(opts.Output ?? layout.CookedPath);

        if (!IsSafeToDelete(layout, target, out string why))
        {
            writer.Write(CookDiagnostic.Error(
                CookDiagnosticCodes.UnsafeCleanTarget,
                $"Refusing to delete '{target}': {why}"));
            return ExitCookError;
        }

        // The cache goes with the output, and that is what makes the verb true. A
        // clean that left the cache behind would have the next cook rebuild the
        // artifact from cached payloads, so "clean then cook" would not be a clean
        // cook and the one thing people run clean FOR would not happen.
        string cache = Path.Combine(Path.GetFullPath(layout.Root), CookCache.DirectoryName);

        if (!Directory.Exists(target) && !Directory.Exists(cache))
        {
            if (!opts.Quiet)
                stdout.WriteLine($"{ToolName}: nothing to clean at {style.Path}{target}{style.Reset}");
            return ExitSuccess;
        }

        foreach (string directory in new[] { target, cache })
        {
            if (!Directory.Exists(directory)) continue;

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writer.Write(CookDiagnostic.Error(
                    CookDiagnosticCodes.OutputNotWritable, $"Could not delete '{directory}': {ex.Message}"));
                return ExitIoError;
            }
        }

        if (!opts.Quiet)
            stdout.WriteLine($"{style.Success}{ToolName}{style.Reset}: cleaned {style.Path}{target}{style.Reset}");

        return ExitSuccess;
    }

    // A verb that silently does nothing teaches within one session that this
    // tool's verbs are decorative. Both of these therefore say what they will do,
    // and exit non-zero so no script mistakes one for a pass.
    private static int RunUnbuilt(CliOptions opts, DiagnosticWriter writer)
    {
        string message = opts.Verb switch
        {
            CliVerb.Verify =>
                "'verify' is not built yet. It will check that every entry decodes, every reference resolves " +
                "inside the pack, the digest matches, and (with --keep-brush-source) that the baked geometry " +
                "recompiles bit-identically from the retained brushes.",
            CliVerb.Inspect =>
                "'inspect' is not built yet. It will list a pack's entries with their ids, names, sizes, " +
                "kinds and codecs.",
            _ => $"'{CliOptions.ToWire(opts.Verb)}' is not built yet.",
        };

        writer.Write(CookDiagnostic.Error(CookDiagnosticCodes.VerbNotImplemented, message, opts.Target));
        return ExitCookError;
    }

    private static bool TryOpenProject(
        string target, DiagnosticWriter writer, out ProjectLayout layout, out int failure)
    {
        try
        {
            layout = ProjectLayout.Open(target);
            failure = ExitSuccess;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ProjectFormatException)
        {
            // A folder that exists and is not a project is a PROJECT error rather
            // than an I/O one: the filesystem answered every question it was
            // asked, and the answer was that this is not a Spectra project.
            writer.Write(CookDiagnostic.Error(CookDiagnosticCodes.ProjectNotOpened, ex.Message, target));
            layout = null!;
            failure = ExitCookError;
            return false;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            writer.Write(CookDiagnostic.Error(
                CookDiagnosticCodes.ProjectNotOpened, $"Could not open '{target}': {ex.Message}", target));
            layout = null!;
            failure = ExitIoError;
            return false;
        }
    }

    // Reported straight rather than through the session, so --strict does not turn
    // "this build ignores your -j8" into a failed build: the request is legitimate
    // and the cook it asked for still happened.
    private static void ReportUnimplementedOptions(CliOptions opts, DiagnosticWriter writer)
    {
        if (opts.TargetsGiven)
            Say(writer, "--target selects nothing yet: the shader cook rule is not built.");

        if (opts.ProfileGiven && opts.Profile != CookProfile.Ship)
            Say(writer, $"--profile {CookManifest.ToWire(opts.Profile)} is recorded and no rule varies by profile yet.");

        if (opts.KeepBrushSource)
            Say(writer, "--keep-brush-source does nothing yet: the map cook rule is not built.");

        if (opts.ScriptSource == ScriptSourceMode.Strip)
            Say(writer, "--script-source strip does nothing yet: the script cook rule is not built.");

        if (opts.Encoder == CookEncoder.Native)
            Say(writer, "--encoder native does nothing yet: the image cook rule is not built.");
    }

    private static void Say(DiagnosticWriter writer, string message) =>
        writer.Write(CookDiagnostic.Warning(CookDiagnosticCodes.OptionNotImplemented, message));

    // The one destructive thing this tool does, so the guard is explicit rather
    // than trusting that -o always names cook output.
    private static bool IsSafeToDelete(ProjectLayout layout, string target, out string why)
    {
        string root = Path.GetFullPath(layout.Root);

        if (PathsEqual(target, root))
        {
            why = "it is the project folder itself.";
            return false;
        }

        if (root.StartsWith(WithSeparator(target), StringComparison.OrdinalIgnoreCase))
        {
            why = "it contains the project folder.";
            return false;
        }

        foreach (string authored in new[] { layout.AssetsPath, layout.MapsPath, layout.ScriptsPath })
        {
            if (!PathsEqual(target, authored)) continue;

            why = "it holds authored content, and a clean only ever removes derived output.";
            return false;
        }

        why = string.Empty;
        return true;
    }

    // Case-insensitively even on a case-sensitive filesystem: for a delete guard,
    // the conservative direction is to refuse more, not fewer.
    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string WithSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
}
