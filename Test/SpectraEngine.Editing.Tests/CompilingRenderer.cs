using Microsoft.Extensions.Logging.Abstractions;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using System;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// A <see cref="Renderer"/> that accepts static-world meshes and keeps nothing
/// but a count. Enough to run <c>Scene.RebuildStaticWorld</c> headlessly, which
/// is what the gizmo's brush tests need: they assert on the scene's
/// <c>LastCompileDirtyCells</c>, never on geometry, so the mesh contents are
/// genuinely irrelevant and pretending otherwise would only add a second
/// FakeMesh to maintain.
/// </summary>
/// <remarks>
/// Textures and shaders still throw, exactly as <see cref="StubRenderer"/> does:
/// reaching one from the editing suite would mean a test grew a graphics
/// dependency it has no business having.
/// </remarks>
internal sealed class CompilingRenderer : Renderer
{
    public CompilingRenderer()
        : base(NullLogger<Renderer>.Instance, new ThrowingShaderCompiler())
    {
    }

    /// <summary>How many meshes the static-world compile has asked for.</summary>
    public int CreatedMeshCount { get; private set; }

    public override GraphicsBackend Backend => GraphicsBackend.OpenGL;

    public override string CurrentPipelineName => "Compiling";

    public override string NextPipeline() => "Compiling";

    public override Mesh CreateMesh(
        ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices, ReadOnlySpan<VertexAttribute> attributes)
    {
        CreatedMeshCount++;
        return new EmptyMesh((uint)indices.Length);
    }

    public override Texture CreateTexture(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        TextureFormat format,
        TextureColorSpace colorSpace,
        TextureFilter filter = TextureFilter.Linear,
        TextureWrap wrap = TextureWrap.Repeat)
        => throw new NotSupportedException("CompilingRenderer creates no textures.");

    // No target of any kind, so a pass is a no-op rather than a throw: these
    // stubs stand in for a renderer during scene and editor tests, which drive
    // no pipeline and so open no passes, but a future one that does should not
    // fail for the wrong reason.
    protected override void BeginPassCore(in PassClear clear)
    {
    }

    protected override void EndPassCore()
    {
    }

    public override ShaderProgram CreateShader(string vertexSource, string fragmentSource)
        => throw new NotSupportedException("CompilingRenderer creates no shaders.");

    public override ShaderProgram CreateShader(PipelineBlob blob)
        => throw new NotSupportedException("CompilingRenderer creates no shaders.");

    private sealed class EmptyMesh : Mesh
    {
        public EmptyMesh(uint indexCount) => IndexCount = indexCount;

        public override void Draw()
        {
        }

        public override void Dispose()
        {
        }
    }

    private sealed class ThrowingShaderCompiler : IShaderCompiler
    {
        public CompiledShaderFile Compile(string source, ReadOnlySpan<GraphicsBackend> targets)
            => throw new NotSupportedException("CompilingRenderer compiles no shaders.");
    }
}
