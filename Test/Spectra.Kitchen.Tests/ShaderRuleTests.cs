using Microsoft.Extensions.Logging.Abstractions;
using Spectra.Kitchen.Cooking;
using Spectra.Kitchen.Diagnostics;
using Spectra.Kitchen.Packs;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// The shader cook: source in, one blob per requested backend out, and nothing
/// for a backend nobody asked for.
/// </summary>
/// <remarks>
/// <para><b>The fixture is the engine's own <c>ShadowDepth</c>, not a toy.</b>
/// It is the shader the renderer instances, so it exercises the whole container
/// - two stages, a vertex input table, and the generated instanced twin with a
/// table of its own - and a toy shader would silently prove only that the
/// header round-trips.</para>
/// <para><b>Every claim here is about what is or is not IN the pack.</b> The
/// failures this rule can have are all silent at runtime: a missing blob makes
/// the engine compile from source and render the right picture, and an extra
/// blob makes a d3d11-only build ship GLSL nobody will ever bind. Neither
/// throws, neither logs, and neither changes a pixel.</para>
/// </remarks>
public class ShaderRuleTests
{
    private const string ShaderPath = "Shaders/ShadowDepth.spectrashade";
    private const string CookedPath = "Shaders/ShadowDepth.specshadecomp";

    [Fact]
    public void A_d3d11_only_pack_carries_no_gl_blob_and_still_loads()
    {
        using var project = new TempProject();
        WriteShader(project);

        var pack = project.Track(new PackSource(NullLogger.Instance, Cook(project, GraphicsBackend.D3D11)));

        pack.TryOpen(CookedPath, out ContentBlob? blob).ShouldBeTrue();
        using (blob)
        {
            // Asked for, and there.
            PipelineBlob d3d11 = ShaderFileReader
                .ReadPipeline(blob.Span, GraphicsBackend.D3D11).ShouldNotBeNull();

            d3d11.VertexData.ShouldNotBeNull();
            d3d11.VertexInputs.ShouldNotBeEmpty();

            // The generated twin survives the cook. Without it a batched shadow
            // draw has no program to run and the pass silently stops batching.
            d3d11.InstancedVertexData.ShouldNotBeNull();
            d3d11.InstancedVertexInputs.ShouldContain(
                element => element.Rate == VertexInputRate.PerInstance);

            // Not asked for, and not there. This is the writer-side filter: the
            // compiler was also only asked for d3d11, and trusting that alone
            // would make the promise a property of another component.
            ShaderFileReader.ReadPipeline(blob.Span, GraphicsBackend.OpenGL).ShouldBeNull();
            ShaderFileReader.ReadBackends(blob.Span).ShouldBe([GraphicsBackend.D3D11]);
        }

        // And the engine's own lookup finds it, which is the whole point of
        // cooking one: the resolver reports a cooked pipeline rather than source
        // text, so nothing compiles at startup.
        var stack = new ContentSourceStack();
        stack.Mount(pack);

        ResolvedShader resolved = BaseShaderResolver.ResolveBuiltIn(
            stack, BaseShaders.ShadowDepthFileName, GraphicsBackend.D3D11, NullLogger.Instance);

        resolved.Cooked.ShouldNotBeNull();
        resolved.Source.ShouldBeNull();

        // A backend the pack was not cooked for degrades to source rather than
        // failing: the picture is right and the run pays for a compile, which is
        // a report at Error and never a black window.
        BaseShaderResolver
            .ResolveBuiltIn(stack, BaseShaders.ShadowDepthFileName, GraphicsBackend.OpenGL, NullLogger.Instance)
            .Cooked.ShouldBeNull();
    }

    [Fact]
    public void The_pipeline_table_is_written_in_the_order_the_targets_were_asked_for()
    {
        using var project = new TempProject();
        WriteShader(project);

        var pack = project.Track(new PackSource(
            NullLogger.Instance, Cook(project, GraphicsBackend.D3D12, GraphicsBackend.OpenGL)));

        pack.TryOpen(CookedPath, out ContentBlob? blob).ShouldBeTrue();
        using (blob)
        {
            // The target order rather than the compiler's registration order, so
            // one command line writes one file. The reverse would still load and
            // would make two runs of the same command disagree byte for byte the
            // day a generator is registered somewhere else.
            ShaderFileReader.ReadBackends(blob.Span)
                .ShouldBe([GraphicsBackend.D3D12, GraphicsBackend.OpenGL]);
        }
    }

    [Fact]
    public void Two_clean_cooks_of_a_shader_in_two_processes_are_byte_identical()
    {
        ScookProcess.Require();

        using var project = new TempProject();
        WriteShader(project);

        // Two PROCESSES, because .NET randomises the string hash seed per
        // process: a dictionary iteration order that leaked into a generated
        // stage would be stable inside one test host and different between two
        // runs of the tool, which is the failure reported as "CI says the pack
        // changed and nothing changed".
        byte[] first = CookOutOfProcess(project, "shader-a");
        byte[] second = CookOutOfProcess(project, "shader-b");

        second.ShouldBe(first);
    }

    [Fact]
    public void A_pack_whose_shaders_all_carry_the_same_backends_verifies_clean()
    {
        using var project = new TempProject();
        WriteShader(project);
        project.WriteAsset("Shaders/Lit.spectrashade", BaseShaders.Lit);

        PackVerifyResult result = PackVerifier.Verify(
            Cook(project, GraphicsBackend.D3D11, GraphicsBackend.OpenGL));

        result.Succeeded.ShouldBeTrue(Describe(result));
        result.EntriesChecked.ShouldBe(2);
    }

    [Fact]
    public void The_verifier_fails_a_pack_one_shader_short_and_names_the_backend_and_the_shader()
    {
        // Built by hand rather than cooked, because one cook gives every shader
        // the same target list: the failure being caught is a shader that came
        // out short of the others, which no single cook can produce.
        using var project = new TempProject();
        string pack = Path.Combine(project.Root, "mixed.spack");

        var writer = new PackWriter();
        writer.Add(
            "Shaders/Complete.specshadecomp",
            PackEntryKind.Shader,
            CompileToBytes(GraphicsBackend.D3D11, GraphicsBackend.OpenGL));
        writer.Add(
            "Shaders/Short.specshadecomp",
            PackEntryKind.Shader,
            CompileToBytes(GraphicsBackend.D3D11));
        writer.WriteToFile(pack);

        PackVerifyResult result = PackVerifier.Verify(pack);

        result.Succeeded.ShouldBeFalse();

        CookDiagnostic missing = result.Diagnostics.Single(d => d.IsError);
        missing.Id.ToString().ShouldBe("SC6002");
        missing.Message.ShouldContain("Shaders/Short.specshadecomp");
        missing.Message.ShouldContain("opengl");

        // And not against the complete one, or the diagnostic says nothing about
        // which shader to go and look at.
        missing.Message.ShouldNotContain("Complete");
    }

    [Fact]
    public void A_named_target_list_is_authoritative_where_the_pack_alone_cannot_be()
    {
        using var project = new TempProject();
        WriteShader(project);

        string pack = Cook(project, GraphicsBackend.D3D11);

        // The union over the pack's own shaders is {d3d11}, so on its own the
        // pack is self-consistent and passes. That is the honest limit of a
        // container that does not record what it was cooked for.
        PackVerifier.Verify(pack).Succeeded.ShouldBeTrue();

        // Told what was wanted, it fails - which is why scook verify forwards
        // --target rather than defaulting to one.
        PackVerifyResult asked = PackVerifier.Verify(
            pack, logger: null, targets: [GraphicsBackend.D3D11, GraphicsBackend.D3D12]);

        asked.Succeeded.ShouldBeFalse();

        CookDiagnostic missing = asked.Diagnostics.Single(d => d.IsError);
        missing.Id.ToString().ShouldBe("SC6002");
        missing.Message.ShouldContain("d3d12");
        missing.Message.ShouldContain("Shaders/ShadowDepth.specshadecomp");
    }

    [Fact]
    public void A_shader_the_compiler_refuses_reports_its_message_and_emits_nothing()
    {
        using var project = new TempProject();
        project.WriteAsset(ShaderPath, "this is not a shader at all {{{\n");

        CookResult result = new CookSession(
            project.Layout, new CookSettings { UseCache = false }).Run();

        result.Succeeded.ShouldBeFalse();

        // Under SC6001 rather than an invented SS number: the compiler's
        // Diagnostic carries a severity, a message and a span and no code at all,
        // so wrapping one would mint a number ssc and the language server do not
        // agree with.
        result.Diagnostics.ShouldContain(d => d.IsError && d.Id.ToString() == "SC6001");

        // And the source file is named, so the line is one an IDE can jump to.
        result.Diagnostics
            .First(d => d.Id.ToString() == "SC6001")
            .File.ShouldBe(ShaderPath);

        // A failed cook writes no pack at all, so there is nothing to inspect;
        // what matters is that the shader did not silently become a raw copy of
        // its own source.
        result.Assets
            .Single(a => a.SourcePath == ShaderPath)
            .Outputs.ShouldBeEmpty();
    }

    [Fact]
    public void A_shader_is_cooked_rather_than_copied()
    {
        using var project = new TempProject();
        WriteShader(project);

        CookResult result = new CookSession(
            project.Layout, new CookSettings { UseCache = false }).Run();

        result.Succeeded.ShouldBeTrue();

        CookedAsset asset = result.Assets.Single();
        asset.Rule.ShouldBe(Rules.RuleKind.Shader);

        // The source itself is NOT in the pack: a shipped game reads the blob,
        // and shipping the source beside it would be handing every player the
        // input to a compiler the build was meant to have run for them.
        CookedOutput output = asset.Outputs.Single();
        output.Path.ShouldBe(CookedPath);
    }

    [Fact]
    public void A_cached_shader_cook_produces_the_bytes_the_clean_one_did()
    {
        using var project = new TempProject();
        WriteShader(project);

        // The cache stores emitted bytes and replays them, so a rule whose output
        // it never captured correctly is a pack that changes on the second run of
        // an unchanged project.
        string cold = Path.Combine(project.Root, "cold");
        string warm = Path.Combine(project.Root, "warm");

        new CookSession(project.Layout, new CookSettings { OutputPath = cold }).Run()
            .Succeeded.ShouldBeTrue();

        CookResult second = new CookSession(project.Layout, new CookSettings { OutputPath = warm }).Run();

        second.Succeeded.ShouldBeTrue();
        second.CacheHits.ShouldBe(1);

        ReadPack(cold).ShouldBe(ReadPack(warm));
    }

    [Fact]
    public void Changing_the_target_list_re_cooks_the_shader()
    {
        using var project = new TempProject();
        WriteShader(project);

        new CookSession(
                project.Layout,
                new CookSettings { OutputPath = Path.Combine(project.Root, "one"), Targets = [GraphicsBackend.D3D11] })
            .Run()
            .Succeeded.ShouldBeTrue();

        CookResult second = new CookSession(
                project.Layout,
                new CookSettings
                {
                    OutputPath = Path.Combine(project.Root, "two"),
                    Targets = [GraphicsBackend.D3D11, GraphicsBackend.OpenGL],
                })
            .Run();

        second.Succeeded.ShouldBeTrue();

        // The rule declares CookSettingKeys.Targets, so the key moves with the
        // list. Without the declaration the second run is a cache hit serving a
        // d3d11-only blob for a cook that asked for two backends, and nothing
        // reports it.
        second.CacheHits.ShouldBe(0);
    }

    // --- helpers -------------------------------------------------------------

    private static void WriteShader(TempProject project) =>
        project.WriteAsset(ShaderPath, BaseShaders.ShadowDepth);

    private static string Cook(TempProject project, params GraphicsBackend[] targets)
    {
        CookResult result = new CookSession(
            project.Layout,
            new CookSettings { UseCache = false, Targets = targets }).Run();

        result.Succeeded.ShouldBeTrue(Describe(result.Diagnostics));
        return result.OutputPath!;
    }

    private static byte[] CookOutOfProcess(TempProject project, string label)
    {
        string output = Path.Combine(project.Root, label);

        ScookProcess.Result run = ScookProcess.Run("cook", project.Root, "-o", output, "--no-cache");
        run.ExitCode.ShouldBe(0, $"scook failed: {run.Stderr}");

        return File.ReadAllBytes(Directory.GetFiles(output, "*.spack").Single());
    }

    private static byte[] ReadPack(string outputDirectory) =>
        File.ReadAllBytes(Directory.GetFiles(outputDirectory, "*.spack").Single());

    private static byte[] CompileToBytes(params GraphicsBackend[] targets)
    {
        CompiledShaderFile compiled = new SpectraShadeCompiler().Compile(BaseShaders.ShadowDepth, targets);

        using var bytes = new MemoryStream();
        ShaderFileWriter.Write(bytes, compiled);
        return bytes.ToArray();
    }

    private static string Describe(PackVerifyResult result) => Describe(result.Diagnostics);

    private static string Describe(IReadOnlyList<CookDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static d => d.ToString()));
}
