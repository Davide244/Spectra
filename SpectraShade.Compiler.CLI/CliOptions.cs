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

    private static bool ShouldUseColor()
    {
        if (Console.IsErrorRedirected) return false;
        var noColorEnv = Environment.GetEnvironmentVariable("NO_COLOR");
        return string.IsNullOrEmpty(noColorEnv);
    }

    public static void PrintUsage(TextWriter w)
    {
        w.WriteLine("SpectraShade compiler (ssc)");
        w.WriteLine();
        w.WriteLine("Usage:");
        w.WriteLine("  ssc [options] <input.spectrashade>");
        w.WriteLine();
        w.WriteLine("Options:");
        w.WriteLine("  -o, --output <path>        Output .specshadecomp file");
        w.WriteLine("                             (default: <input>.specshadecomp)");
        w.WriteLine("  -t, --target <backend>     Target backend(s). Repeatable or comma-separated.");
        w.WriteLine("                             Values: opengl, vulkan, d3d11, d3d12, all");
        w.WriteLine("                             Default: all");
        w.WriteLine("      --emit-source <dir>    Also emit generated source text per backend");
        w.WriteLine("                             into <dir> (for debugging codegen).");
        w.WriteLine("  -q, --quiet                Suppress non-error output.");
        w.WriteLine("      --no-color             Disable ANSI color output.");
        w.WriteLine("  -h, --help                 Show this help and exit.");
        w.WriteLine("      --version              Print version and exit.");
        w.WriteLine();
        w.WriteLine("Diagnostics are printed on stderr in IDE-parseable form:");
        w.WriteLine("  <file>(<line>,<col>): error|warning|info: <message>");
        w.WriteLine();
        w.WriteLine("Exit codes: 0=success, 1=compile error, 2=usage error, 3=I/O error");
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
