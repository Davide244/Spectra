using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core;
using SpectraEngine.Core.Assets.Sources;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.IO;
using System.Text;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// Where a built-in shader comes from: a cooked blob, a source file the content
/// stack holds, or the copy embedded in the engine assembly.
/// </summary>
/// <remarks>
/// <b>Every one of these failures renders the right picture.</b> A cooked blob
/// that is not found makes the engine compile from source and draw exactly the
/// same frame, at the cost of the compiler front end the cook exists to remove;
/// a source override that is not found falls through to the embedded copy and
/// draws the frame the author was trying to change. Nothing throws and nothing
/// looks wrong, so the resolution ORDER is only ever observable here.
/// </remarks>
public class BaseShaderResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"spectra_shaders_{Guid.NewGuid():N}");

    public BaseShaderResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void With_no_content_source_a_built_in_comes_from_the_engines_own_copy()
    {
        // The floor, and the behaviour every build had before packs existed: a
        // renderer nobody handed a content stack still gets its programs. Which
        // copy answers depends on whether the source tree is there - the file in
        // a developer build, the embedded resource in a deployed one - and the
        // two are the same text, which is what makes the choice invisible.
        ResolvedShader resolved = Resolve(content: null);

        resolved.Cooked.ShouldBeNull();
        resolved.Source.ShouldBe(BaseShaders.Lit);
    }

    [Fact]
    public void A_source_the_content_stack_holds_overrides_the_embedded_copy_and_is_watchable()
    {
        const string authored = "// a project's own lit shader\n";
        WriteContent("Shaders/Lit.spectrashade", authored);

        ResolvedShader resolved = Resolve(LooseStack());

        resolved.Cooked.ShouldBeNull();
        resolved.Source.ShouldBe(authored);

        // A loose file has a watch path, so hot-reload keeps working for a
        // project that authored its own shader. A packed one has none and is
        // simply not watched, which is the rule for every other asset.
        resolved.WatchPath.ShouldNotBeNull();
        Path.GetFullPath(resolved.WatchPath).ShouldBe(
            Path.GetFullPath(Path.Combine(_root, "Shaders", "Lit.spectrashade")));
    }

    [Fact]
    public void A_cooked_blob_beats_the_source_beside_it()
    {
        WriteContent("Shaders/Lit.spectrashade", "// never compiled\n");
        WriteContent("Shaders/Lit.specshadecomp", CookedBytes(GraphicsBackend.D3D11));

        ResolvedShader resolved = Resolve(LooseStack(), GraphicsBackend.D3D11);

        // The whole point of cooking one. If this order ever inverts, a shipped
        // build compiles every shader at startup with the answer sitting in the
        // file next to it and nothing anywhere says so.
        resolved.Source.ShouldBeNull();
        resolved.Cooked.ShouldNotBeNull().Backend.ShouldBe(GraphicsBackend.D3D11);

        // No watch path on a cooked blob: the thing to watch would be the source
        // it was built from, and re-reading the blob when that changes would
        // serve the old shader under a new timestamp.
        resolved.WatchPath.ShouldBeNull();
    }

    [Fact]
    public void A_cooked_file_with_no_blob_for_this_backend_falls_back_to_source()
    {
        const string authored = "// compiled at runtime instead\n";
        WriteContent("Shaders/Lit.spectrashade", authored);
        WriteContent("Shaders/Lit.specshadecomp", CookedBytes(GraphicsBackend.D3D11));

        // A pack cooked for a target list this run is not in. It is reported at
        // Error and then degraded, because refusing to render over it turns a
        // mis-targeted pack into a black window while the source path still
        // produces the right frame.
        ResolvedShader resolved = Resolve(LooseStack(), GraphicsBackend.OpenGL);

        resolved.Cooked.ShouldBeNull();
        resolved.Source.ShouldBe(authored);
    }

    [Fact]
    public void An_unreadable_cooked_file_degrades_rather_than_throwing()
    {
        const string authored = "// still renders\n";
        WriteContent("Shaders/Lit.spectrashade", authored);
        WriteContent("Shaders/Lit.specshadecomp", [1, 2, 3, 4, 5, 6, 7, 8]);

        // Content failures never reach the draw loop. The cooker is where a
        // payload like this is fatal - see PackVerifier's shader arm.
        ResolvedShader resolved = Resolve(LooseStack());

        resolved.Cooked.ShouldBeNull();
        resolved.Source.ShouldBe(authored);
    }

    [Fact]
    public void A_source_saved_with_a_byte_order_mark_is_decoded_without_it()
    {
        const string authored = "// saved by an editor that writes a BOM\n";

        var bytes = new byte[3 + Encoding.UTF8.GetByteCount(authored)];
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        Encoding.UTF8.GetBytes(authored, bytes.AsSpan(3));

        WriteContent("Shaders/Lit.spectrashade", bytes);

        // A content source hands out bytes, so nothing strips this on the way
        // past. Left in, U+FEFF sits in front of the first token and the lexer
        // reports a syntax error on line one of a file that looks ordinary.
        Resolve(LooseStack()).Source.ShouldBe(authored);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Failing a test on its own cleanup helps nobody.
        }
    }

    private ResolvedShader Resolve(
        IContentSource? content, GraphicsBackend backend = GraphicsBackend.OpenGL) =>
        BaseShaderResolver.ResolveBuiltIn(
            content, BaseShaders.LitFileName, backend, NullLogger.Instance);

    private ContentSourceStack LooseStack()
    {
        var stack = new ContentSourceStack();
        stack.Mount(new LooseFileSource(NullLogger.Instance, _root));
        return stack;
    }

    private void WriteContent(string relative, string text) =>
        WriteContent(relative, Encoding.UTF8.GetBytes(text));

    private void WriteContent(string relative, byte[] bytes)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    // A real .specshadecomp, written by the engine's own writer: a hand-built
    // byte array here would prove the resolver reads what this test thinks the
    // format is rather than what the writer emits.
    private static byte[] CookedBytes(GraphicsBackend backend)
    {
        var file = new CompiledShaderFile
        {
            FormatVersion = EngineInfo.ShaderFormatVersion,
            Stages = ShaderStageFlags.Vertex | ShaderStageFlags.Fragment,
            Pipelines =
            [
                new PipelineBlob
                {
                    Backend = backend,
                    Format = ShaderDataFormat.SourceText,
                    Stages = ShaderStageFlags.Vertex | ShaderStageFlags.Fragment,
                    VertexData = Encoding.UTF8.GetBytes("vertex"),
                    FragmentData = Encoding.UTF8.GetBytes("fragment"),
                    VertexInputs = [new VertexInputElement("position", 0, 1, 3, VertexInputRate.PerVertex)],
                },
            ],
        };

        using var bytes = new MemoryStream();
        ShaderFileWriter.Write(bytes, file);
        return bytes.ToArray();
    }
}
