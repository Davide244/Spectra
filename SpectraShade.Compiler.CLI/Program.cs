using SpectraEngine.Core;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler;
using SpectraShade.Compiler.Analysis;
using SpectraShade.Compiler.CodeGen;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.CLI;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitCompileError = 1;
    private const int ExitUsageError = 2;
    private const int ExitIoError = 3;

    private static int Main(string[] args)
    {
        try
        {
            var parse = CliOptions.Parse(args);
            switch (parse.Mode)
            {
                case CliMode.Help:
                    CliOptions.PrintUsage(Console.Out);
                    return ExitSuccess;
                case CliMode.Version:
                    Console.Out.WriteLine($"ssc {EngineInfo.VersionString} (shader format v{EngineInfo.ShaderFormatVersion})");
                    return ExitSuccess;
                case CliMode.UsageError:
                    Console.Error.WriteLine($"error: {parse.Error}");
                    Console.Error.WriteLine("Run 'ssc --help' for usage.");
                    return ExitUsageError;
            }

            return Run(parse.Options!);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex.Message}");
            return ExitIoError;
        }
    }

    private static int Run(CliOptions opts)
    {
        if (!File.Exists(opts.Input))
        {
            Console.Error.WriteLine($"error: input file not found: {opts.Input}");
            return ExitIoError;
        }

        string source;
        try
        {
            source = File.ReadAllText(opts.Input);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"error: failed to read '{opts.Input}': {ex.Message}");
            return ExitIoError;
        }

        var writer = new DiagnosticWriter(Console.Error, opts.Input, opts.UseColor);

        var lexer = new Lexer(source, opts.Input);
        var tokens = lexer.Tokenize();

        var parser = new Parser(tokens);
        var unit = parser.Parse();
        writer.WriteAll(parser.Diagnostics);

        if (HasErrors(parser.Diagnostics))
            return ExitCompileError;

        var analyzer = new SemanticAnalyzer();
        bool ok = analyzer.Analyze(unit);
        writer.WriteAll(analyzer.Diagnostics);

        if (!ok || HasErrors(analyzer.Diagnostics))
            return ExitCompileError;

        var generators = BuildGenerators();
        var pipelines = new List<PipelineBlob>(opts.Targets.Count);
        var allStages = ShaderStageFlags.None;

        foreach (var target in opts.Targets)
        {
            if (!generators.TryGetValue(target, out var gen))
            {
                Console.Error.WriteLine($"error: no code generator available for backend '{target}'");
                return ExitCompileError;
            }

            PipelineBlob blob;
            try
            {
                blob = gen.Generate(unit);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{opts.Input}: error: codegen failed for {target}: {ex.Message}");
                return ExitCompileError;
            }

            pipelines.Add(blob);
            allStages |= blob.Stages;

            if (opts.EmitSourceDir is not null)
                EmitSourceText(opts.EmitSourceDir, opts.Input, blob);
        }

        var compiled = new CompiledShaderFile
        {
            FormatVersion = EngineInfo.ShaderFormatVersion,
            Stages = allStages,
            Pipelines = pipelines,
        };

        var outputPath = opts.Output ?? Path.ChangeExtension(opts.Input, ".specshadecomp");
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var stream = File.Create(outputPath);
            ShaderFileWriter.Write(stream, compiled);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"error: failed to write '{outputPath}': {ex.Message}");
            return ExitIoError;
        }

        if (!opts.Quiet)
        {
            var stageList = string.Join(", ", pipelines.Select(p => $"{p.Backend}:{p.Stages}"));
            Console.Out.WriteLine($"ssc: wrote {outputPath} ({pipelines.Count} pipeline(s): {stageList})");
        }

        return ExitSuccess;
    }

    private static bool HasErrors(IReadOnlyList<Diagnostic> diags)
    {
        for (int i = 0; i < diags.Count; i++)
            if (diags[i].Severity == DiagnosticSeverity.Error)
                return true;
        return false;
    }

    private static Dictionary<GraphicsBackend, ICodeGenerator> BuildGenerators()
    {
        var dict = new Dictionary<GraphicsBackend, ICodeGenerator>
        {
            [GraphicsBackend.OpenGL] = new GlslGenerator(),
            [GraphicsBackend.D3D11] = new HlslGenerator(GraphicsBackend.D3D11),
            [GraphicsBackend.D3D12] = new HlslGenerator(GraphicsBackend.D3D12),
            [GraphicsBackend.Vulkan] = new SpirVGenerator(),
        };
        return dict;
    }

    private static void EmitSourceText(string dir, string input, PipelineBlob blob)
    {
        if (blob.Format != ShaderDataFormat.SourceText)
            return;

        Directory.CreateDirectory(dir);
        var baseName = Path.GetFileNameWithoutExtension(input);
        var backendTag = blob.Backend.ToString().ToLowerInvariant();

        WriteStage(dir, baseName, backendTag, "vert", blob.VertexData);
        WriteStage(dir, baseName, backendTag, "frag", blob.FragmentData);
        WriteStage(dir, baseName, backendTag, "geom", blob.GeometryData);
        WriteStage(dir, baseName, backendTag, "comp", blob.ComputeData);
    }

    private static void WriteStage(string dir, string baseName, string backend, string stage, byte[]? data)
    {
        if (data is null) return;
        var path = Path.Combine(dir, $"{baseName}.{backend}.{stage}.txt");
        File.WriteAllBytes(path, data);
    }
}
