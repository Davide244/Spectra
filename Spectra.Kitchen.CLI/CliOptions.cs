using Spectra.Kitchen.Cooking;
using SpectraEngine.Core.Graphics;

namespace Spectra.Kitchen.CLI;

internal enum CliMode
{
    Run,
    Help,
    Version,
    UsageError,
}

/// <summary>
/// What the tool was asked to do.
/// </summary>
/// <remarks>
/// <b>Four verbs, and one tool rather than three.</b> Map compilation is a cook
/// rule inside <c>scook</c>, not a separate <c>smapc</c>: a whole-project build
/// tool that shells out to siblings has to reproduce their dependency graph to
/// know when to call them. <c>ssc</c> stays separate for the opposite reason, that
/// it compiles ONE file and has a different argument grammar.
/// </remarks>
internal enum CliVerb
{
    Cook,
    Verify,
    Inspect,
    Clean,
}

internal sealed class CliOptions
{
    public required CliVerb Verb { get; init; }

    /// <summary>
    /// The project folder or manifest for <c>cook</c> and <c>clean</c>; the pack
    /// for <c>verify</c> and <c>inspect</c>.
    /// </summary>
    public required string Target { get; init; }

    public string? Output { get; init; }
    public CookProfile Profile { get; init; } = CookProfile.Ship;
    public IReadOnlyList<GraphicsBackend> Targets { get; init; } = [];
    public int Jobs { get; init; } = 1;
    public bool UseCache { get; init; } = true;
    public bool Loose { get; init; }
    public bool Watch { get; init; }
    public bool KeepBrushSource { get; init; }
    public ScriptSourceMode ScriptSource { get; init; } = ScriptSourceMode.Embed;
    public CookEncoder Encoder { get; init; } = CookEncoder.Managed;
    public bool Strict { get; init; }
    public string? ManifestPath { get; init; }
    public bool Quiet { get; init; }
    public bool UseColor { get; init; }

    // Which switches were TYPED, kept separately from their values so the tool can
    // say "this build does not act on your --target" without saying it to everybody
    // who never passed one.
    public bool ProfileGiven { get; init; }
    public bool TargetsGiven { get; init; }
    public bool JobsGiven { get; init; }
    public bool CacheGiven { get; init; }

    /// <summary>The library-side settings this command line asks for.</summary>
    public CookSettings ToCookSettings() => new()
    {
        OutputPath = Output,
        Profile = Profile,
        Targets = Targets.Count > 0 ? Targets : DefaultTargets(),
        Jobs = Jobs,
        UseCache = UseCache,
        // --watch implies --loose: a watch loop exists to feed the editor's
        // cooked-accurate preview, which overlays a tree on the loose files.
        Loose = Loose || Watch,
        KeepBrushSource = KeepBrushSource,
        ScriptSource = ScriptSource,
        Encoder = Encoder,
        Strict = Strict,
        ManifestPath = ManifestPath,
    };

    /// <summary>
    /// The backends a bare invocation cooks for: the same three <c>ssc</c>
    /// defaults to, and for the same reason. Vulkan is excluded until SPIR-V
    /// emission exists, or every default cook would fail.
    /// </summary>
    public static IReadOnlyList<GraphicsBackend> DefaultTargets() =>
        [GraphicsBackend.OpenGL, GraphicsBackend.D3D11, GraphicsBackend.D3D12];

    public static ParseResult Parse(string[] args)
    {
        CliVerb? verb = null;
        string? target = null;
        string? output = null;
        string? manifest = null;
        var profile = CookProfile.Ship;
        var scriptSource = ScriptSourceMode.Embed;
        var encoder = CookEncoder.Managed;
        var targets = new List<GraphicsBackend>();
        int jobs = 1;
        bool useCache = true;
        bool loose = false, watch = false, keepBrush = false, strict = false, quiet = false, noColor = false;
        bool profileGiven = false, jobsGiven = false, cacheGiven = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h":
                case "--help":
                case "/?":
                    return ParseResult.ForMode(CliMode.Help);
                case "--version":
                    return ParseResult.ForMode(CliMode.Version);
                case "-o":
                case "--output":
                    if (!TryNext(args, ref i, out var o))
                        return ParseResult.Usage($"'{a}' requires a path");
                    output = o;
                    break;
                case "--profile":
                    if (!TryNext(args, ref i, out var p))
                        return ParseResult.Usage($"'{a}' requires a profile");
                    if (!TryParseProfile(p, out profile))
                        return ParseResult.Usage($"unknown profile '{p}'. Valid: ship, fast, preview");
                    profileGiven = true;
                    break;
                case "-t":
                case "--target":
                    if (!TryNext(args, ref i, out var t))
                        return ParseResult.Usage($"'{a}' requires a backend");
                    if (!TryParseTargets(t, targets, out var targetErr))
                        return ParseResult.Usage(targetErr);
                    break;
                case "-j":
                case "--jobs":
                    if (!TryNext(args, ref i, out var j))
                        return ParseResult.Usage($"'{a}' requires a worker count");
                    // Invariant, because a console is typed by a person who expects
                    // a plain number and a value that parses on one machine and not
                    // another is the worst kind of bug report to receive.
                    if (!int.TryParse(j, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out jobs) || jobs < 1)
                    {
                        return ParseResult.Usage($"'{a}' requires a worker count of 1 or more, not '{j}'");
                    }
                    jobsGiven = true;
                    break;
                case "--cache":
                    useCache = true;
                    cacheGiven = true;
                    break;
                case "--no-cache":
                    useCache = false;
                    cacheGiven = true;
                    break;
                case "--loose":
                    loose = true;
                    break;
                case "--watch":
                    watch = true;
                    break;
                case "--keep-brush-source":
                    keepBrush = true;
                    break;
                case "--script-source":
                    if (!TryNext(args, ref i, out var s))
                        return ParseResult.Usage($"'{a}' requires embed or strip");
                    if (!TryParseScriptSource(s, out scriptSource))
                        return ParseResult.Usage($"unknown script source mode '{s}'. Valid: embed, strip");
                    break;
                case "--encoder":
                    if (!TryNext(args, ref i, out var e))
                        return ParseResult.Usage($"'{a}' requires managed or native");
                    if (!TryParseEncoder(e, out encoder))
                        return ParseResult.Usage($"unknown encoder '{e}'. Valid: managed, native");
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--manifest":
                    if (!TryNext(args, ref i, out var m))
                        return ParseResult.Usage($"'{a}' requires a path");
                    manifest = m;
                    break;
                case "-q":
                case "--quiet":
                    quiet = true;
                    break;
                case "--no-color":
                    noColor = true;
                    break;
                case "--":
                    if (i + 1 >= args.Length)
                        return ParseResult.Usage("'--' must be followed by a path");
                    if (target is not null)
                        return ParseResult.Usage("more than one path specified");
                    target = args[++i];
                    break;
                default:
                    if (a.StartsWith('-'))
                        return ParseResult.Usage($"unknown option: {a}");

                    // The first bare word is the verb when it names one. A folder
                    // genuinely called "cook" is reachable as './cook' or after
                    // '--', which is the same escape every tool of this shape uses.
                    if (verb is null && target is null && TryParseVerb(a, out var parsedVerb))
                    {
                        verb = parsedVerb;
                        break;
                    }

                    if (target is not null)
                        return ParseResult.Usage("more than one path specified");
                    target = a;
                    break;
            }
        }

        CliVerb effective = verb ?? CliVerb.Cook;

        if (target is null)
        {
            // A pack has no sensible default and a project does: running the tool
            // inside the folder you are working in is the common case, and it is
            // what every build tool of this shape does.
            if (effective is CliVerb.Verify or CliVerb.Inspect)
                return ParseResult.Usage($"'{ToWire(effective)}' requires a path to a pack");

            target = Directory.GetCurrentDirectory();
        }

        return ParseResult.ForOptions(new CliOptions
        {
            Verb = effective,
            Target = target,
            Output = output,
            Profile = profile,
            Targets = targets,
            Jobs = jobs,
            UseCache = useCache,
            Loose = loose,
            Watch = watch,
            KeepBrushSource = keepBrush,
            ScriptSource = scriptSource,
            Encoder = encoder,
            Strict = strict,
            ManifestPath = manifest,
            Quiet = quiet,
            UseColor = !noColor && ShouldUseColor(),
            ProfileGiven = profileGiven,
            TargetsGiven = targets.Count > 0,
            JobsGiven = jobsGiven,
            CacheGiven = cacheGiven,
        });
    }

    public static string ToWire(CliVerb verb) => verb switch
    {
        CliVerb.Cook => "cook",
        CliVerb.Verify => "verify",
        CliVerb.Inspect => "inspect",
        CliVerb.Clean => "clean",
        _ => "cook",
    };

    // Hand-written, never Enum.Parse: reflection over enum names is what trimming
    // removes, so the parse would work in every debug run and fail in a published
    // one.
    private static bool TryParseVerb(string value, out CliVerb verb)
    {
        switch (value)
        {
            case "cook": verb = CliVerb.Cook; return true;
            case "verify": verb = CliVerb.Verify; return true;
            case "inspect": verb = CliVerb.Inspect; return true;
            case "clean": verb = CliVerb.Clean; return true;
            default: verb = CliVerb.Cook; return false;
        }
    }

    private static bool TryParseProfile(string value, out CookProfile profile)
    {
        switch (value)
        {
            case "ship": profile = CookProfile.Ship; return true;
            case "fast": profile = CookProfile.Fast; return true;
            case "preview": profile = CookProfile.Preview; return true;
            default: profile = CookProfile.Ship; return false;
        }
    }

    private static bool TryParseScriptSource(string value, out ScriptSourceMode mode)
    {
        switch (value)
        {
            case "embed": mode = ScriptSourceMode.Embed; return true;
            case "strip": mode = ScriptSourceMode.Strip; return true;
            default: mode = ScriptSourceMode.Embed; return false;
        }
    }

    private static bool TryParseEncoder(string value, out CookEncoder encoder)
    {
        switch (value)
        {
            case "managed": encoder = CookEncoder.Managed; return true;
            case "native": encoder = CookEncoder.Native; return true;
            default: encoder = CookEncoder.Managed; return false;
        }
    }

    private static bool TryNext(string[] args, ref int i, out string value)
    {
        if (i + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }
        value = args[++i];
        return true;
    }

    // ssc's grammar exactly, including the aliases: a person who has typed
    // '-t dx11' at one of these tools should not discover the other wants
    // something else.
    private static bool TryParseTargets(string value, List<GraphicsBackend> into, out string error)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var raw in parts)
        {
            var token = raw.ToLowerInvariant();
            if (token == "all")
            {
                AddUnique(into, GraphicsBackend.OpenGL);
                AddUnique(into, GraphicsBackend.Vulkan);
                AddUnique(into, GraphicsBackend.D3D11);
                AddUnique(into, GraphicsBackend.D3D12);
                continue;
            }

            var backend = token switch
            {
                "opengl" or "gl" or "glsl" => (GraphicsBackend?)GraphicsBackend.OpenGL,
                "vulkan" or "vk" or "spirv" => GraphicsBackend.Vulkan,
                "d3d11" or "dx11" or "hlsl11" => GraphicsBackend.D3D11,
                "d3d12" or "dx12" or "hlsl12" => GraphicsBackend.D3D12,
                _ => null,
            };
            if (backend is null)
            {
                error = $"unknown target backend '{raw}'. Valid: opengl, vulkan, d3d11, d3d12, all";
                return false;
            }
            AddUnique(into, backend.Value);
        }
        error = string.Empty;
        return true;
    }

    private static void AddUnique(List<GraphicsBackend> list, GraphicsBackend item)
    {
        if (!list.Contains(item))
            list.Add(item);
    }

    private static bool ShouldUseColor() => ConsoleColor.ShouldUseForStderr();

    public static void PrintUsage(TextWriter w, bool color)
    {
        var s = new AnsiStyle(color);

        w.WriteLine($"{s.Title}Spectra cook{s.Reset} {s.Dim}(scook){s.Reset}");
        w.WriteLine();
        w.WriteLine($"{s.Header}Usage:{s.Reset}");
        w.WriteLine($"  {s.Command}scook{s.Reset} {s.Dim}[cook]{s.Reset} {s.Dim}[options]{s.Reset} {s.Placeholder}<projectDir>{s.Reset}");
        w.WriteLine($"  {s.Command}scook verify{s.Reset} {s.Placeholder}<pack>{s.Reset}");
        w.WriteLine($"  {s.Command}scook inspect{s.Reset} {s.Placeholder}<pack>{s.Reset}");
        w.WriteLine($"  {s.Command}scook clean{s.Reset} {s.Dim}[options]{s.Reset} {s.Placeholder}<projectDir>{s.Reset}");
        w.WriteLine();
        w.WriteLine($"{s.Header}Verbs:{s.Reset}");
        WriteOption(w, s, "cook", null,
            "Cook a project into a pack. The default verb.");
        WriteOption(w, s, "verify", null,
            $"{s.Warning}Not built yet.{s.Reset} Will check every entry, reference",
            "and digest in a cooked pack.");
        WriteOption(w, s, "inspect", null,
            $"{s.Warning}Not built yet.{s.Reset} Will list a pack's entries, sizes,",
            "codecs and names.");
        WriteOption(w, s, "clean", null,
            "Delete a project's cook output and its cook cache.");
        w.WriteLine();
        w.WriteLine($"{s.Header}Options:{s.Reset}");
        WriteOption(w, s, "-o, --output", "<path>",
            "Where output goes",
            $"(default: the project's {s.Value}cooked/{s.Reset} folder)");
        WriteOption(w, s, "    --profile", "<name>",
            $"Values: {s.Value}ship, fast, preview{s.Reset}",
            $"Default: {s.Value}ship{s.Reset}");
        WriteOption(w, s, "-t, --target", "<backend>",
            "Backend(s) shaders are cooked for. Comma-separated.",
            $"Values: {s.Value}opengl, vulkan, d3d11, d3d12, all{s.Reset}",
            $"Default: {s.Value}opengl, d3d11, d3d12{s.Reset} {s.Dim}(as ssc){s.Reset}");
        WriteOption(w, s, "-j, --jobs", "<n>",
            $"Worker count. {s.Value}-j1{s.Reset} is the determinism-oracle mode.");
        WriteOption(w, s, "    --cache", null,
            $"Use the cook cache in {s.Value}.spectra-cook/{s.Reset} (default).");
        WriteOption(w, s, "    --no-cache", null,
            "Neither read nor write the cook cache: re-cook",
            "everything and leave what is cached alone.");
        WriteOption(w, s, "    --loose", null,
            "Emit a cooked directory tree instead of a pack.");
        WriteOption(w, s, "    --watch", null,
            "Re-cook on change. Implies --loose.");
        WriteOption(w, s, "    --keep-brush-source", null,
            "Keep authored brushes in a cooked map, so a verify",
            "can recompile them and compare.");
        WriteOption(w, s, "    --script-source", "<mode>",
            $"Values: {s.Value}embed, strip{s.Reset}. Default: {s.Value}embed{s.Reset}");
        WriteOption(w, s, "    --encoder", "<name>",
            $"Values: {s.Value}managed, native{s.Reset}. Default: {s.Value}managed{s.Reset}");
        WriteOption(w, s, "    --strict", null,
            "Treat warnings as errors.");
        WriteOption(w, s, "    --manifest", "<path>",
            "Write a JSON manifest of every asset, its id, its",
            "inputs and its output hash. This is what CI diffs.");
        WriteOption(w, s, "-q, --quiet", null,
            "Suppress non-error output.");
        WriteOption(w, s, "    --no-color", null,
            $"Disable ANSI color output {s.Dim}(NO_COLOR is honoured){s.Reset}.");
        WriteOption(w, s, "-h, --help", null,
            "Show this help and exit.");
        WriteOption(w, s, "    --version", null,
            "Print version and exit.");
        w.WriteLine();
        w.WriteLine($"{s.Header}Diagnostics{s.Reset} are printed on stderr in IDE-parseable form:");
        w.WriteLine($"  {s.Placeholder}<file>{s.Reset}({s.Placeholder}<line>{s.Reset},{s.Placeholder}<col>{s.Reset}): {s.Error}error{s.Reset}|{s.Warning}warning{s.Reset}|{s.Info}info{s.Reset} {s.Value}SC####{s.Reset}: {s.Placeholder}<message>{s.Reset}");
        w.WriteLine($"  {s.Dim}Bands: 0xxx project/CLI, 1xxx discovery, 2xxx image, 3xxx model, 4xxx audio,{s.Reset}");
        w.WriteLine($"  {s.Dim}       5xxx material, 6xxx shader, 7xxx map, 8xxx script, 9xxx pack.{s.Reset}");
        w.WriteLine($"  {s.Dim}A shader error keeps its own SS#### code rather than being renumbered.{s.Reset}");
        w.WriteLine();
        w.WriteLine($"{s.Header}Exit codes:{s.Reset} " +
            $"{s.Value}0{s.Reset}=success, " +
            $"{s.Value}1{s.Reset}=cook error, " +
            $"{s.Value}2{s.Reset}=usage error, " +
            $"{s.Value}3{s.Reset}=I/O error");
    }

    private static void WriteOption(TextWriter w, AnsiStyle s, string flags, string? arg, params string[] description)
    {
        const int descCol = 27;
        var argText = arg is null ? string.Empty : $" {s.Placeholder}{arg}{s.Reset}";
        var preLen = 2 + flags.Length + (arg is null ? 0 : 1 + arg.Length);
        var pad = preLen < descCol ? new string(' ', descCol - preLen) : "  ";
        w.WriteLine($"  {s.Flag}{flags}{s.Reset}{argText}{pad}{description[0]}");
        var contIndent = new string(' ', descCol);
        for (int i = 1; i < description.Length; i++)
            w.WriteLine($"{contIndent}{description[i]}");
    }
}

internal readonly struct ParseResult
{
    public CliMode Mode { get; }
    public CliOptions? Options { get; }
    public string? Error { get; }

    private ParseResult(CliMode mode, CliOptions? options, string? error)
    {
        Mode = mode;
        Options = options;
        Error = error;
    }

    public static ParseResult ForMode(CliMode mode) => new(mode, null, null);
    public static ParseResult ForOptions(CliOptions opts) => new(CliMode.Run, opts, null);
    public static ParseResult Usage(string error) => new(CliMode.UsageError, null, error);
}
