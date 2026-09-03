using System;
using System.IO;
using System.Text;
using SpectraEngine.Core;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;

namespace SpectraShade.Compiler.Tests;

/// <summary>
/// The .specshadecomp container: what survives a trip through disk, what a file
/// from another format version does, and whether the two read paths agree about
/// where the data section begins.
/// </summary>
/// <remarks>
/// <b>Everything here is latent while shaders are compiled in-process.</b> The
/// engine builds its base shaders at startup and hands the blob straight to the
/// renderer, so nothing the container drops has ever been missed. A cooked pack
/// ships the blob instead, and then a dropped vertex input table is a D3D11
/// input layout built from nothing and a missing instanced stage is a batched
/// draw with no program to run it - neither of which throws.
/// </remarks>
public sealed class ShaderFileCodecTests
{
    [Fact]
    public void A_pipeline_written_to_disk_round_trips_its_vertex_inputs_and_instanced_variant()
    {
        // The engine's own shadow pass, because it is the shader the renderer
        // actually instances: it marks uModel, so the compiler attaches a second
        // vertex stage and a second input table to the blob.
        CompiledShaderFile compiled = new SpectraShadeCompiler()
            .Compile(BaseShaders.ShadowDepth, [GraphicsBackend.OpenGL]);

        PipelineBlob source = compiled.GetPipeline(GraphicsBackend.OpenGL).ShouldNotBeNull();
        source.VertexInputs.ShouldNotBeEmpty();
        source.InstancedVertexData.ShouldNotBeNull();
        source.InstancedVertexInputs.ShouldNotBeEmpty();

        byte[] bytes = WriteToBytes(compiled);

        PipelineBlob whole = ShaderFileReader.Read(new MemoryStream(bytes))
            .GetPipeline(GraphicsBackend.OpenGL).ShouldNotBeNull();

        // The path Renderer.LoadCompiledShader takes, which is the one that
        // matters once a pack ships blobs rather than source.
        PipelineBlob partial = ShaderFileReader
            .ReadPipeline(new MemoryStream(bytes), GraphicsBackend.OpenGL)
            .ShouldNotBeNull();

        foreach (PipelineBlob read in new[] { whole, partial })
        {
            read.VertexInputs.ShouldBe(source.VertexInputs);
            read.InstancedVertexInputs.ShouldBe(source.InstancedVertexInputs);
            read.InstancedVertexData.ShouldBe(source.InstancedVertexData);
            read.VertexData.ShouldBe(source.VertexData);
            read.FragmentData.ShouldBe(source.FragmentData);
        }
    }

    [Fact]
    public void A_shader_with_no_instanced_variant_reads_back_without_one()
    {
        // "No variant" has to be an ordinary answer, or every shader that does
        // not want one fails to load.
        PipelineBlob source = Blob(GraphicsBackend.OpenGL, instanced: false);
        byte[] bytes = WriteToBytes(File(source));

        PipelineBlob read = ShaderFileReader
            .ReadPipeline(new MemoryStream(bytes), GraphicsBackend.OpenGL)
            .ShouldNotBeNull();

        read.InstancedVertexData.ShouldBeNull();
        read.InstancedVertexInputs.ShouldBeEmpty();
        read.VertexInputs.ShouldBe(source.VertexInputs);
    }

    [Fact]
    public void A_shader_file_with_a_different_format_version_is_refused_naming_both_versions()
    {
        const ushort bogus = 9;
        byte[] bytes = HeaderOnly(bogus);

        var whole = Should.Throw<InvalidDataException>(
            () => ShaderFileReader.Read(new MemoryStream(bytes)));
        var partial = Should.Throw<InvalidDataException>(
            () => ShaderFileReader.ReadPipeline(new MemoryStream(bytes), GraphicsBackend.OpenGL));

        // Both numbers, or the reader has told the user their file is wrong
        // without saying what would be right.
        foreach (InvalidDataException error in new[] { whole, partial })
        {
            error.Message.ShouldContain(bogus.ToString());
            error.Message.ShouldContain(EngineInfo.ShaderFormatVersion.ToString());
            error.Message.ShouldContain("recook", Case.Insensitive);
        }
    }

    [Fact]
    public void A_file_this_engine_wrote_declares_this_engines_format_version()
    {
        // The mirror of the refusal above: the strict check is only safe while
        // the writer and the constant cannot disagree.
        byte[] bytes = WriteToBytes(File(Blob(GraphicsBackend.OpenGL, instanced: true)));

        ShaderFileReader.Read(new MemoryStream(bytes))
            .FormatVersion.ShouldBe(EngineInfo.ShaderFormatVersion);
    }

    [Fact]
    public void ReadPipeline_and_Read_agree_on_the_data_section_start()
    {
        // Three, because the two paths derive that origin differently and a
        // divergence only moves a blob that is not the first one.
        CompiledShaderFile file = File(
            Blob(GraphicsBackend.OpenGL, instanced: false),
            Blob(GraphicsBackend.D3D11, instanced: true),
            Blob(GraphicsBackend.D3D12, instanced: true));

        byte[] bytes = WriteToBytes(file);

        PipelineBlob whole = ShaderFileReader.Read(new MemoryStream(bytes))
            .GetPipeline(GraphicsBackend.D3D12).ShouldNotBeNull();
        PipelineBlob partial = ShaderFileReader
            .ReadPipeline(new MemoryStream(bytes), GraphicsBackend.D3D12)
            .ShouldNotBeNull();

        partial.VertexData.ShouldBe(whole.VertexData);
        partial.FragmentData.ShouldBe(whole.FragmentData);
        partial.InstancedVertexData.ShouldBe(whole.InstancedVertexData);
        partial.VertexInputs.ShouldBe(whole.VertexInputs);
        partial.InstancedVertexInputs.ShouldBe(whole.InstancedVertexInputs);

        // And the third blob is genuinely the third one, not the first read
        // twice from a data section start both paths got equally wrong.
        partial.VertexData.ShouldBe(StageData(GraphicsBackend.D3D12, "vertex"));
    }

    [Fact]
    public void The_span_reader_and_the_stream_reader_agree_byte_for_byte()
    {
        // Three pipelines, so the comparison covers a blob at a non-zero offset:
        // the two parsers derive the data section's origin separately, and a
        // divergence between them cannot move the first blob.
        CompiledShaderFile file = File(
            Blob(GraphicsBackend.OpenGL, instanced: false),
            Blob(GraphicsBackend.D3D11, instanced: true),
            Blob(GraphicsBackend.D3D12, instanced: true));

        byte[] bytes = WriteToBytes(file);

        foreach (GraphicsBackend backend in new[]
                 { GraphicsBackend.OpenGL, GraphicsBackend.D3D11, GraphicsBackend.D3D12 })
        {
            PipelineBlob stream = ShaderFileReader
                .ReadPipeline(new MemoryStream(bytes), backend).ShouldNotBeNull();
            PipelineBlob span = ShaderFileReader
                .ReadPipeline(bytes.AsSpan(), backend).ShouldNotBeNull();

            // Two parsers over one layout: they exist because a stream seeks and
            // a mapped pack view is already there, and this is the only thing
            // keeping them in step. A divergence is a stage read out of the
            // middle of somebody else's bytes rather than an exception.
            span.Backend.ShouldBe(stream.Backend);
            span.Format.ShouldBe(stream.Format);
            span.Stages.ShouldBe(stream.Stages);
            span.VertexData.ShouldBe(stream.VertexData);
            span.FragmentData.ShouldBe(stream.FragmentData);
            span.GeometryData.ShouldBe(stream.GeometryData);
            span.ComputeData.ShouldBe(stream.ComputeData);
            span.VertexInputs.ShouldBe(stream.VertexInputs);
            span.InstancedVertexData.ShouldBe(stream.InstancedVertexData);
            span.InstancedVertexInputs.ShouldBe(stream.InstancedVertexInputs);
        }
    }

    [Fact]
    public void The_span_reader_answers_null_for_a_backend_the_file_does_not_carry()
    {
        // The same ordinary answer the stream reader gives, because the engine
        // uses it to decide between a cooked blob and compiling from source: an
        // exception here would turn a pack cooked for another target list into a
        // crash rather than a fallback.
        byte[] bytes = WriteToBytes(File(Blob(GraphicsBackend.D3D11, instanced: true)));

        ShaderFileReader.ReadPipeline(bytes.AsSpan(), GraphicsBackend.OpenGL).ShouldBeNull();
        ShaderFileReader.ReadPipeline(new MemoryStream(bytes), GraphicsBackend.OpenGL).ShouldBeNull();
    }

    [Fact]
    public void The_backend_listing_reads_the_table_and_nothing_else()
    {
        CompiledShaderFile file = File(
            Blob(GraphicsBackend.D3D11, instanced: true),
            Blob(GraphicsBackend.OpenGL, instanced: false));

        // Table order, not sorted: a verify reports what the file says in the
        // order the file says it, so its diagnostics do not reorder themselves
        // because an enum's numbering changed.
        ShaderFileReader.ReadBackends(WriteToBytes(file))
            .ShouldBe([GraphicsBackend.D3D11, GraphicsBackend.OpenGL]);
    }

    [Fact]
    public void A_truncated_file_is_refused_rather_than_read_short()
    {
        byte[] bytes = WriteToBytes(File(Blob(GraphicsBackend.OpenGL, instanced: true)));

        // Half a file. The span parser is looking at a fixed extent, so it can
        // say so; BinaryReader.ReadBytes would hand back a shorter array and the
        // caller would build a shader program out of half a stage.
        Should.Throw<InvalidDataException>(
            () => ShaderFileReader.ReadPipeline(bytes.AsSpan(0, bytes.Length / 2), GraphicsBackend.OpenGL));
    }

    // --- helpers -------------------------------------------------------------

    private static byte[] WriteToBytes(CompiledShaderFile file)
    {
        using var stream = new MemoryStream();
        ShaderFileWriter.Write(stream, file);
        return stream.ToArray();
    }

    private static CompiledShaderFile File(params PipelineBlob[] pipelines) => new()
    {
        FormatVersion = EngineInfo.ShaderFormatVersion,
        Stages = ShaderStageFlags.Vertex | ShaderStageFlags.Fragment,
        Pipelines = pipelines,
    };

    // Per-backend stage bytes of differing length, so a blob read from the wrong
    // offset cannot accidentally match the right one.
    private static byte[] StageData(GraphicsBackend backend, string stage) =>
        Encoding.UTF8.GetBytes($"{backend}:{stage}:{new string('x', (int)backend * 7)}");

    private static PipelineBlob Blob(GraphicsBackend backend, bool instanced) => new()
    {
        Backend = backend,
        Format = ShaderDataFormat.SourceText,
        Stages = ShaderStageFlags.Vertex | ShaderStageFlags.Fragment,
        VertexData = StageData(backend, "vertex"),
        FragmentData = StageData(backend, "fragment"),
        VertexInputs =
        [
            new VertexInputElement("position", 0, 1, 3, VertexInputRate.PerVertex),
            new VertexInputElement("normal", 1, 1, 3, VertexInputRate.PerVertex),
            new VertexInputElement("uv", 2, 1, 2, VertexInputRate.PerVertex),
        ],
        InstancedVertexData = instanced ? StageData(backend, "instanced") : null,
        InstancedVertexInputs = instanced
            ?
            [
                new VertexInputElement("position", 0, 1, 3, VertexInputRate.PerVertex),
                new VertexInputElement("normal", 1, 1, 3, VertexInputRate.PerVertex),
                new VertexInputElement("uv", 2, 1, 2, VertexInputRate.PerVertex),
                new VertexInputElement("uModel", 3, 4, 4, VertexInputRate.PerInstance),
            ]
            : [],
    };

    // Written by hand rather than through ShaderFileWriter: a test that takes
    // its header from the code under test cannot catch that header changing.
    private static byte[] HeaderOnly(ushort formatVersion) =>
    [
        (byte)'S', (byte)'S', (byte)'C', (byte)'O',
        (byte)(formatVersion & 0xFF), (byte)(formatVersion >> 8),
        (byte)(ShaderStageFlags.Vertex | ShaderStageFlags.Fragment),
        0,
    ];
}
