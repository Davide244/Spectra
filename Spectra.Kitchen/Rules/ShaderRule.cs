using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Diagnostics;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// Compiles a <c>.spectrashade</c> source file into a <c>.specshadecomp</c>
/// blob per requested backend.
/// </summary>
/// <remarks>
/// <para><b>The compiler runs IN PROCESS, and that is the reason the Kitchen is
/// bound by AOT-safe dependencies at all.</b> The editor hosts this library to
/// produce cooked-accurate preview, and a cook that can only shell out to
/// <c>ssc</c> cannot be hosted: it would need a tool on <c>PATH</c>, a temp file
/// per shader and a process per shader, and the preview would then be measuring
/// a different compiler build from the one the editor is linked against.</para>
/// <para><b>The target filter is applied on the WRITER side, not merely by
/// asking the compiler for fewer backends.</b> Both are done, and the second is
/// what makes the promise hold: a pack cooked for d3d11 must not carry a GL
/// blob, and the only bytes that can carry one are the bytes this rule emits.
/// Trusting the compiler to return exactly what it was asked for makes the
/// promise a property of another component, which is precisely the shape of
/// claim that stops being true without anything failing.</para>
/// <para><b>A compiler diagnostic reports under a code the COOKER owns, and does
/// not invent an <c>SS####</c>.</b> <see cref="CookDiagnosticId.Wrap"/> exists so
/// a shader error reaching a person through the cooker carries the same code
/// <c>ssc</c> and the language server would print - and SpectraShade's
/// <see cref="Diagnostic"/> carries a severity, a message and a span, with no
/// number anywhere in the compiler. Minting one here would put a code on the
/// compiler's behalf that no other tool agrees with, which is the exact failure
/// wrapping exists to avoid. The message text is carried verbatim under
/// <c>SC6001</c>; when the compiler numbers its diagnostics, this becomes a
/// <c>Wrap</c> per diagnostic and the message stops moving.</para>
/// <para><b>Determinism comes for free and is asserted anyway.</b> The compile is
/// a pure function of the source text and the target list: one lex, one parse,
/// one analyze, then one generator per target walked in the order the targets
/// were given. Nothing here reads a clock, a path or a dictionary's iteration
/// order, and <c>ShaderRuleTests</c> cooks the same file twice and compares
/// bytes, because "obviously pure" is what every non-deterministic build step was
/// believed to be first.</para>
/// </remarks>
public sealed class ShaderRule : IRule
{
    /// <summary>What this rule cooks, dot included.</summary>
    public const string SourceExtension = BaseShaders.SourceExtension;

    /// <summary>What it emits, dot included.</summary>
    public const string CookedExtension = CompiledShaderFile.FileExtension;

    /// <inheritdoc/>
    public RuleKind Kind => RuleKind.Shader;

    /// <inheritdoc/>
    /// <remarks>
    /// Raise this whenever the bytes this rule emits for one source can change.
    /// The COMPILER's own output moving is carried by the <c>shaderFormat</c>
    /// tool version in the cache key rather than by this number, so a codegen
    /// change that leaves the container alone still needs one of the two moved
    /// by hand - see <see cref="CookCacheKey"/>.
    /// </remarks>
    public int Version => 1;

    /// <inheritdoc/>
    /// <remarks>
    /// The target list, and only that. A shader's cooked bytes are the same
    /// under every profile and every encoder, so a <c>--profile fast</c> run must
    /// not recompile a project's shaders.
    /// </remarks>
    public CookSettingKeys SettingsRead => CookSettingKeys.Targets;

    /// <summary>
    /// The content path the cooked form of <paramref name="sourcePath"/> is
    /// emitted at.
    /// </summary>
    /// <remarks>
    /// Exposed because the engine's lookup has to name the same string
    /// (<c>BaseShaders.CookedContentPath</c>) and a verify has to recognise one.
    /// A disagreement between the two spellings is not an error anywhere: the
    /// lookup simply misses and a shipped build quietly compiles from source.
    /// </remarks>
    public static string CookedPathFor(string sourcePath) =>
        Path.ChangeExtension(sourcePath, CookedExtension);

    /// <summary>Whether <paramref name="contentPath"/> is a shader source file.</summary>
    public static bool Handles(string contentPath) =>
        contentPath.EndsWith(SourceExtension, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void Cook(IRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string source = DecodeUtf8(context.Read(context.SourcePath));

        // Deduplicated, because the compiler registers one generator per backend
        // and a repeated target would emit two entries for it - a file whose
        // pipeline table has a duplicate, where ReadPipeline takes the first and
        // the second is dead weight nothing will ever look at.
        GraphicsBackend[] targets = DistinctTargets(context.Targets);
        if (targets.Length == 0)
        {
            context.Report(CookDiagnostic.Warning(
                CookDiagnosticCodes.ShaderNoTargets,
                $"'{context.SourcePath}' was cooked with no target backends, so it produced no blob and is " +
                "not in this pack. Name at least one --target.",
                context.SourcePath));

            return;
        }

        CompiledShaderFile compiled;
        try
        {
            compiled = new SpectraShadeCompiler().Compile(source, targets);
        }
        catch (ShaderCompilationException ex)
        {
            foreach (Diagnostic diagnostic in ex.Diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Info) continue;

                context.Report(Translate(diagnostic, context.SourcePath));
            }

            // The exception's own message is not reported on top: it is a join of
            // the same diagnostics, and reporting both prints every shader error
            // twice in a build log whose whole contract is one parseable line per
            // problem.
            return;
        }
        catch (NotImplementedException ex)
        {
            // SpirVGenerator. A target with no code generator is a legitimate
            // request the toolchain cannot serve yet, so it names itself rather
            // than arriving as SC1004 "the Shader rule failed".
            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.ShaderBackendUnsupported,
                $"'{context.SourcePath}' could not be cooked for every requested target: {ex.Message}",
                context.SourcePath));

            return;
        }

        CompiledShaderFile filtered = KeepOnly(compiled, targets, context);
        if (filtered.Pipelines.Count == 0) return;

        using var bytes = new MemoryStream();
        ShaderFileWriter.Write(bytes, filtered);

        context.Emit(CookedPathFor(context.SourcePath), bytes.ToArray(), PackEntryKind.Shader);
    }

    // The writer-side filter. It also REPORTS a requested backend the compile did
    // not produce, because the alternative is a pack that is one blob short and
    // mounts perfectly: the engine then falls back to compiling that shader from
    // source at runtime, which is the exact cost cooking exists to remove and
    // shows up as nothing at all in a build log.
    private static CompiledShaderFile KeepOnly(
        CompiledShaderFile compiled, GraphicsBackend[] targets, IRuleContext context)
    {
        var kept = new List<PipelineBlob>(targets.Length);
        var stages = ShaderStageFlags.None;

        // Walked in the TARGET order rather than the compiler's, so the pipeline
        // table's order is the order the cook was asked for and two runs with the
        // same command line write the same bytes.
        for (int i = 0; i < targets.Length; i++)
        {
            PipelineBlob? blob = compiled.GetPipeline(targets[i]);
            if (blob is null)
            {
                context.Report(CookDiagnostic.Error(
                    CookDiagnosticCodes.ShaderBackendMissing,
                    $"'{context.SourcePath}' was cooked for {CookSettingsDigest.ToWire(targets[i])} and the " +
                    "compiler produced no pipeline for it.",
                    context.SourcePath));

                continue;
            }

            kept.Add(blob);
            stages |= blob.Stages;
        }

        return new CompiledShaderFile
        {
            FormatVersion = compiled.FormatVersion,
            Stages = stages,
            Pipelines = kept,
        };
    }

    private static GraphicsBackend[] DistinctTargets(IReadOnlyList<GraphicsBackend> targets)
    {
        var distinct = new List<GraphicsBackend>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            if (!distinct.Contains(targets[i])) distinct.Add(targets[i]);
        }

        return [.. distinct];
    }

    private static CookDiagnostic Translate(Diagnostic diagnostic, string sourcePath)
    {
        // Lines and columns are one-based in the build-line format and the lexer
        // reports them the same way, so they travel through unchanged; a span
        // with no position degrades to the whole-file form rather than being
        // invented as (1,1), which would send an editor to the wrong place with
        // more confidence than reporting nothing.
        int line = diagnostic.Span.Start.Line;
        int column = diagnostic.Span.Start.Column;

        return diagnostic.Severity == DiagnosticSeverity.Error
            ? CookDiagnostic.Error(CookDiagnosticCodes.ShaderCompileFailed, diagnostic.Message, sourcePath, line, column)
            : CookDiagnostic.Warning(CookDiagnosticCodes.ShaderCompileFailed, diagnostic.Message, sourcePath, line, column);
    }

    // A shader source is text and a rule reads bytes. An editor that saved one
    // with a BOM would otherwise put U+FEFF in front of the first token, and the
    // lexer reports that as a syntax error on line one of a file that looks
    // perfectly ordinary.
    private static string DecodeUtf8(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        if (span.Length >= 3 && span[..3].SequenceEqual(bom)) span = span[3..];

        return Encoding.UTF8.GetString(span);
    }
}
