using SpectraEngine.Core.Graphics;

namespace SpectraShade.Compiler.CLI;

internal enum CliMode
{
    Compile,
    Help,
    Version,
    UsageError,
}

internal sealed class CliOptions
{
    public required string Input { get; init; }
    public required IReadOnlyList<GraphicsBackend> Targets { get; init; }
    public string? Output { get; init; }
    public string? EmitSourceDir { get; init; }
    public bool Quiet { get; init; }
    public bool UseColor { get; init; }

    public static ParseResult Parse(string[] args)
    {
        if (args.Length == 0)
            return ParseResult.Usage("no input file (pass --help for usage)");

        string? input = null;
        string? output = null;
        string? emitSourceDir = null;
        bool quiet = false;
        bool noColor = false;
        var targets = new List<GraphicsBackend>();

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
                case "-t":
                case "--target":
                    if (!TryNext(args, ref i, out var t))
                        return ParseResult.Usage($"'{a}' requires a backend");
                    if (!TryParseTargets(t, targets, out var targetErr))
                        return ParseResult.Usage(targetErr);
                    break;
                case "--emit-source":
                    if (!TryNext(args, ref i, out var d))
                        return ParseResult.Usage($"'{a}' requires a directory");
                    emitSourceDir = d;
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
                        return ParseResult.Usage("'--' must be followed by an input path");
                    if (input is not null)
                        return ParseResult.Usage("more than one input file specified");
                    input = args[++i];
                    break;
                default:
                    if (a.StartsWith('-'))
                        return ParseResult.Usage($"unknown option: {a}");
                    if (input is not null)
                        return ParseResult.Usage("more than one input file specified");
                    input = a;
                    break;
            }
        }

        if (input is null)
            return ParseResult.Usage("no input file specified");

        if (targets.Count == 0)
        {
            targets.Add(GraphicsBackend.OpenGL);
            targets.Add(GraphicsBackend.Vulkan);
            targets.Add(GraphicsBackend.D3D11);
            targets.Add(GraphicsBackend.D3D12);
        }

        return ParseResult.ForOptions(new CliOptions
        {
            Input = input,
            Targets = targets,
            Output = output,
            EmitSourceDir = emitSourceDir,
            Quiet = quiet,
            UseColor = !noColor && ShouldUseColor(),
        });
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

        w.WriteLine($"{s.Title}SpectraShade compiler{s.Reset} {s.Dim}(ssc){s.Reset}");
        w.WriteLine();
        w.WriteLine($"{s.Header}Usage:{s.Reset}");
        w.WriteLine($"  {s.Command}ssc{s.Reset} {s.Dim}[options]{s.Reset} {s.Placeholder}<input.spectrashade>{s.Reset}");
        w.WriteLine();
        w.WriteLine($"{s.Header}Options:{s.Reset}");
        WriteOption(w, s, "-o, --output", "<path>",
            "Output .specshadecomp file",
            $"(default: {s.Placeholder}<input>{s.Reset}.specshadecomp)");
        WriteOption(w, s, "-t, --target", "<backend>",
            "Target backend(s). Repeatable or comma-separated.",
            $"Values: {s.Value}opengl, vulkan, d3d11, d3d12, all{s.Reset}",
            $"Default: {s.Value}all{s.Reset}");
        WriteOption(w, s, "    --emit-source", "<dir>",
            "Also emit generated source text per backend",
            "into <dir> (for debugging codegen).");
        WriteOption(w, s, "-q, --quiet", null,
            "Suppress non-error output.");
        WriteOption(w, s, "    --no-color", null,
            "Disable ANSI color output.");
        WriteOption(w, s, "-h, --help", null,
            "Show this help and exit.");
        WriteOption(w, s, "    --version", null,
            "Print version and exit.");
        w.WriteLine();
        w.WriteLine($"{s.Header}Diagnostics{s.Reset} are printed on stderr in IDE-parseable form:");
        w.WriteLine($"  {s.Placeholder}<file>{s.Reset}({s.Placeholder}<line>{s.Reset},{s.Placeholder}<col>{s.Reset}): {s.Error}error{s.Reset}|{s.Warning}warning{s.Reset}|{s.Info}info{s.Reset}: {s.Placeholder}<message>{s.Reset}");
        w.WriteLine();
        w.WriteLine($"{s.Header}Exit codes:{s.Reset} " +
            $"{s.Value}0{s.Reset}=success, " +
            $"{s.Value}1{s.Reset}=compile error, " +
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
    public static ParseResult ForOptions(CliOptions opts) => new(CliMode.Compile, opts, null);
    public static ParseResult Usage(string error) => new(CliMode.UsageError, null, error);
}
