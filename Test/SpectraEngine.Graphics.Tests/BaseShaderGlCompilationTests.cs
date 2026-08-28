using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// Compiles each built-in <see cref="BaseShaders"/> through SpectraShade →
/// GLSL → glCompileShader on a real OpenGL context. Snapshot tests would only
/// confirm the generator's output didn't change; these confirm the driver
/// actually accepts what we emit.
/// </summary>
[Collection(GlRendererCollection.Name)]
public sealed class BaseShaderGlCompilationTests
{
    private readonly GlRendererFixture _fixture;

    public BaseShaderGlCompilationTests(GlRendererFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Lit_compiles_in_opengl()
    {
        // OpenGLRenderer.Initialize already compiles BaseShaders.Lit through
        // the full pipeline — if the driver had rejected the generated GLSL,
        // the fixture's constructor would have thrown.
        _fixture.Renderer.DefaultShader.ShouldNotBeNull();
    }

    [Fact]
    public void The_deferred_shaders_compile_in_opengl()
    {
        // The semantic analyser does no name resolution and no type checking, so
        // a misspelled builtin emits literally and is first rejected here, by
        // the driver. These two carry the most arithmetic in the engine: a
        // five-output G-buffer write and a Cook-Torrance BRDF with two helper
        // functions and an early return.
        _fixture.Renderer.CreateShaderFromSource(BaseShaders.GBufferFill).ShouldNotBeNull();
        _fixture.Renderer.CreateShaderFromSource(BaseShaders.DeferredLight).ShouldNotBeNull();
    }

    [Fact]
    public void The_generated_instanced_stages_compile_in_opengl()
    {
        // Every base shader that marks a [PerInstance] uniform gets a second
        // vertex stage nobody wrote. A rewrite producing invalid GLSL would
        // otherwise be found by a driver at run time, in whichever pass first
        // drew a batch.
        foreach (string source in new[] { BaseShaders.ShadowDepth, BaseShaders.GBufferFill })
        {
            CompiledShaderFile compiled = new SpectraShadeCompiler()
                .Compile(source, [GraphicsBackend.OpenGL]);
            PipelineBlob blob = compiled.GetPipeline(GraphicsBackend.OpenGL).ShouldNotBeNull();

            blob.InstancedVertexData.ShouldNotBeNull();
            _fixture.Renderer.CreateShaderFromSource(source).ShouldNotBeNull();
            _fixture.Renderer.TryCreateInstancedShaderFromSource(source).ShouldNotBeNull();
        }
    }

    [Fact]
    public void Lit_recompiles_explicitly()
    {
        // A second, explicit compile via the public surface — guards against
        // a future regression where DefaultShader is set by some path that
        // sidesteps CreateShaderFromSource.
        var shader = _fixture.Renderer.CreateShaderFromSource(BaseShaders.Lit);
        shader.ShouldNotBeNull();
    }
}
