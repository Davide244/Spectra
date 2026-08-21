using SpectraEngine.Core.Graphics.Shaders;

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
    public void Lit_recompiles_explicitly()
    {
        // A second, explicit compile via the public surface — guards against
        // a future regression where DefaultShader is set by some path that
        // sidesteps CreateShaderFromSource.
        var shader = _fixture.Renderer.CreateShaderFromSource(BaseShaders.Lit);
        shader.ShouldNotBeNull();
    }
}
