using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using System;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// A <see cref="Renderer"/> that creates nothing. The editing suite only needs
/// a renderer for its framebuffer latch (the viewport the editor input adapter
/// reads), so every GPU entry point throws — reaching one would mean a test
/// grew a graphics dependency it has no business having.
/// </summary>
internal sealed class StubRenderer : Renderer
{
    public StubRenderer()
        : base(NullLogger<Renderer>.Instance, new ThrowingShaderCompiler())
    {
    }

    // Arbitrary: nothing on the editing path branches on the backend.
    public override GraphicsBackend Backend => GraphicsBackend.OpenGL;

    public override string CurrentPipelineName => "Stub";

    public override string NextPipeline() => "Stub";

    public override Mesh CreateMesh(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
        => throw new NotSupportedException("StubRenderer creates no GPU resources.");

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter = TextureFilter.Linear,
        TextureWrap wrap = TextureWrap.Repeat)
        => throw new NotSupportedException("StubRenderer creates no GPU resources.");

    // No target of any kind, so a pass is a no-op rather than a throw: these
    // stubs stand in for a renderer during scene and editor tests, which drive
    // no pipeline and so open no passes, but a future one that does should not
    // fail for the wrong reason.
    protected override void BeginPassCore(RenderTarget? target, in PassClear clear)
    {
    }

    protected override void EndPassCore(RenderTarget? target)
    {
    }

    public override RenderTarget CreateRenderTarget(in RenderTargetDesc desc)
        => throw new NotSupportedException($"{GetType().Name} creates no GPU resources.");

    public override ShaderProgram CreateShader(string vertexSource, string fragmentSource)
        => throw new NotSupportedException("StubRenderer creates no shaders.");

    public override ShaderProgram CreateShader(PipelineBlob blob)
        => throw new NotSupportedException("StubRenderer creates no shaders.");

    private sealed class ThrowingShaderCompiler : IShaderCompiler
    {
        public CompiledShaderFile Compile(string source, ReadOnlySpan<GraphicsBackend> targets)
            => throw new NotSupportedException("StubRenderer compiles no shaders.");
    }
}
